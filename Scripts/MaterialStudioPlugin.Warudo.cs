using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MaterialStudio.Warudo;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    /// <summary>
    /// The .warudo export source. Warudo mods are built with a newer Unity than the VSF SDK
    /// (2021.3 and up), and an AssetBundle only opens in the Unity version that built it or a
    /// newer one, so these exports run in a second private project on a newer editor: one at
    /// least as new as the mod and no newer than VNyan's own line, so VNyan loads the result.
    /// That project is set up from the same VSF SDK project as the first one, or from the
    /// UniVRM package shipped with the plugin when there is none.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        const string UniVrmPackageName = "UniVRM-0.66.0_39d5.unitypackage";
        static string UniVrmPackage { get { return Path.Combine(UnityDriver.PluginDir, Path.Combine("UnityToolset", Path.Combine("UniVRM", UniVrmPackageName))); } }
        static string WarudoCacheDir { get { return Path.Combine(ShaderCatalog.Dir, "WarudoCache"); } }

        // ------------------------------------------------------------------ editor choice

        /// <summary>The oldest Unity a mod can need when its own file does not say (Warudo mods start at 2021.3).</summary>
        static string ModNeeds(string modVersion) { return string.IsNullOrEmpty(modVersion) ? "2021.3.0f1" : modVersion; }

        /// <summary>True when Unity can open what this Unity version built and VNyan can load it (up to VNyan's own year.minor).</summary>
        static bool VNyanCanLoad(string version)
        {
            string vn = Application.unityVersion;
            return WarudoInfo.CompareVersions(version, vn) <= 0 || WarudoInfo.SameLine(version, vn);
        }

        /// <summary>
        /// An installed editor that can open the mod (same version or newer) and whose files
        /// VNyan can load. The one the .warudo project is on wins (no upgrade, no rebuild), then
        /// the newest. Null when there is none.
        /// </summary>
        string EditorForWarudo(string modVersion, List<string> notes)
        {
            string need = ModNeeds(modVersion);
            string vn = Application.unityVersion;
            if (!VNyanCanLoad(need))
            {
                notes.Add("This mod was made with Unity " + need + ", which is newer than VNyan's own " + vn + ", so VNyan could not load it as a .vsfavatar.");
                return null;
            }
            string projectOn = UnityDriver.ProjectVersion(cfg.warudoWorkspace);
            string best = null;
            foreach (string e in UnityDriver.FindEditors(cfg.unityExe))
            {
                string v = UnityDriver.EditorVersion(e);
                if (WarudoInfo.CompareVersions(v, need) < 0 || !VNyanCanLoad(v)) continue;
                if (v == projectOn) return e;
                if (best == null || WarudoInfo.CompareVersions(v, UnityDriver.EditorVersion(best)) > 0) best = e;
            }
            if (best == null)
                notes.Add("No installed Unity can open this mod: it needs Unity " + need + " or newer, up to VNyan's own " + vn +
                          ". Install Unity " + vn + " (the button on the Export tab opens Unity Hub for it).");
            return best;
        }

        /// <summary>The build id Unity Hub needs to install VNyan's exact Unity version, from VNyan's own player.</summary>
        static string VNyanChangeset()
        {
            try
            {
                string player = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UnityPlayer.dll");
                string pv = FileVersionInfo.GetVersionInfo(player).ProductVersion ?? "";   // "2022.3.62f3 (96770f904ca7)"
                int a = pv.IndexOf('('), b = pv.IndexOf(')');
                if (a >= 0 && b > a + 1) return pv.Substring(a + 1, b - a - 1).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>Opens Unity Hub on the install page for VNyan's Unity version, or the download page without Hub.</summary>
        void OfferUnityInstall()
        {
            string ver = Application.unityVersion;
            string cs = VNyanChangeset();
            bool hub = false;
            try { using (var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("unityhub")) hub = k != null; } catch { }
            try
            {
                if (hub && cs.Length > 0)
                {
                    Process.Start(new ProcessStartInfo("unityhub://" + ver + "/" + cs) { UseShellExecute = true });
                    runner.Log("Unity Hub is opening its install window for Unity " + ver + ". The default choices are enough. " +
                               "Unity needs its free licence, so sign in to Unity Hub once if it asks. When the install is done, press Export again.");
                }
                else
                {
                    Process.Start(new ProcessStartInfo("https://unity.com/download") { UseShellExecute = true });
                    runner.Log("Install Unity Hub from the page that just opened, sign in (the free licence is enough), then install Unity " + ver +
                               " from its Installs page and press Export again.");
                }
            }
            catch (Exception e) { runner.Log("Could not open Unity Hub: " + e.Message + ". Install Unity " + ver + " with Unity Hub by hand."); }
            RebuildTab();
        }

        // ------------------------------------------------------------------ the second project

        /// <summary>
        /// Steps that make the .warudo project ready: create it with the chosen editor, copy the
        /// VSF SDK project's folders in (or import the bundled UniVRM), import shader packages,
        /// add the editor scripts, and one setup run that also lets Unity finish its first import.
        /// </summary>
        bool QueuePrepareWarudo(string project, string exe)
        {
            string ver = UnityDriver.EditorVersion(exe);
            string have = UnityDriver.ProjectVersion(project);
            if (have != null && WarudoInfo.CompareVersions(have, ver) > 0)
            {
                // a project cannot go back to an older editor; it is ours, so make it again
                if (!IsOwnFolder(project)) { runner.Log("The .warudo project " + project + " was made with Unity " + have + "; pick that editor on Settings."); return false; }
                runner.Log("The .warudo project was made with Unity " + have + "; making it again for Unity " + ver + ".");
                runner.Enqueue(new UnityStep
                {
                    title = "Clearing the old .warudo project", project = project,
                    work = delegate (List<string> n) { Directory.Delete(project, true); return true; }
                });
                foreach (PackageEntry pe in cfg.packages) pe.importedStampWarudo = "";
                cfg.warudoWorkspaceVersion = "";
                have = null;
            }
            // decided now, before any step runs: a clearing step queued above deletes the folder later
            bool fresh = have == null;
            bool ready = !fresh && cfg.warudoWorkspaceVersion.Length > 0;
            if (!ready)
            {
                if (fresh)
                    runner.Enqueue(new UnityStep
                    {
                        title = "Creating the Unity " + ver + " project for .warudo exports", exe = exe, project = project, createProject = true, graphics = false,
                        logPath = Path.Combine(UnityDriver.JobsDir, "create_warudo.log"),
                    });
                runner.Enqueue(new UnityStep
                {
                    title = "Adding Unity's own UI and test packages", project = project,
                    work = delegate (List<string> n) { return UnityDriver.AddEditorPackages(project, exe, n); }
                });
                string seed = cfg.seedProject;
                var folders = new List<string>(cfg.seedFolders);
                bool seedHasVrm = UnityDriver.IsUnityProject(seed) && folders.Exists(delegate (string f)
                    { return string.Equals(f, "VRM", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(Path.Combine(seed, "Assets"), f)); });
                if (UnityDriver.IsUnityProject(seed) && folders.Count > 0)
                    runner.Enqueue(new UnityStep
                    {
                        title = "Copying your VSF SDK project's folders into the .warudo project", project = project,
                        work = delegate (List<string> n) { return CopySeedFolders(seed, project, folders, n); }
                    });
                if (!seedHasVrm && (fresh || !Directory.Exists(Path.Combine(Path.Combine(project, "Assets"), "VRM"))))
                {
                    if (!File.Exists(UniVrmPackage)) { runner.Log("The bundled UniVRM package is missing (" + UniVrmPackage + "). Reinstall Material Studio."); runner.Cancel(); return false; }
                    runner.Enqueue(new UnityStep
                    {
                        title = "Importing UniVRM", exe = exe, project = project, graphics = false,
                        extraArgs = "-importPackage \"" + UniVrmPackage + "\"",
                        logPath = Path.Combine(UnityDriver.JobsDir, "import_warudo.log"),
                    });
                }
            }
            foreach (PackageEntry pe in cfg.packages)
            {
                if (pe.importedStampWarudo == Stamp(pe) && pe.importedStampWarudo.Length > 0) continue;
                PackageEntry p = pe;
                if (pe.isFolder)
                    runner.Enqueue(new UnityStep
                    {
                        title = "Copying " + Path.GetFileName(pe.path.TrimEnd('\\', '/')), project = project,
                        work = delegate (List<string> n)
                        {
                            // replaces a same-named folder the VSF SDK project brought (an older
                            // Poiyomi, say): mixing two versions' scripts would not compile
                            string fname = Path.GetFileName(p.path.TrimEnd('\\', '/'));
                            if (fname.Length == 0) { n.Add("A whole drive cannot be added as a shader folder: " + p.path); return false; }
                            string dst = Path.Combine(Path.Combine(project, "Assets"), fname);
                            if (Directory.Exists(dst) && IsOwnFolder(project))
                            {
                                Directory.Delete(dst, true);
                                if (File.Exists(dst + ".meta")) File.Delete(dst + ".meta");
                                n.Add("Replaced the project's " + Path.GetFileName(dst) + " folder with this one.");
                            }
                            UnityDriver.CopyTree(p.path, dst, null);
                            if (File.Exists(p.path.TrimEnd('\\', '/') + ".meta")) File.Copy(p.path.TrimEnd('\\', '/') + ".meta", dst + ".meta", true);
                            return true;
                        },
                        onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { p.importedStampWarudo = Stamp(p); SaveConfig(); } }
                    });
                else if (File.Exists(pe.path))
                    runner.Enqueue(new UnityStep
                    {
                        title = "Importing " + Path.GetFileName(pe.path), exe = exe, project = project, graphics = false,
                        extraArgs = "-importPackage \"" + pe.path + "\"",
                        logPath = Path.Combine(UnityDriver.JobsDir, "import_warudo.log"),
                        onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { p.importedStampWarudo = Stamp(p); SaveConfig(); } }
                    });
                else runner.Log("Package missing: " + pe.path);
            }
            runner.Enqueue(new UnityStep
            {
                title = "Installing the Material Studio editor scripts", project = project,
                work = delegate (List<string> n) { return UnityDriver.InstallToolset(project, n) && InstallVrcStubs(project, n); }
            });
            if (!ready)
            {
                string jobDir = Path.Combine(UnityDriver.JobsDir, "setup_warudo");
                Directory.CreateDirectory(jobDir);
                var job = new MSJob { kind = "setup", resultPath = Path.Combine(jobDir, "result.json") };
                string jobPath = Path.Combine(jobDir, "job.json");
                File.WriteAllText(jobPath, JsonIO.ToJson(job, true));
                runner.Enqueue(new UnityStep
                {
                    title = "Setting up the .warudo project (first time only, can take several minutes)", exe = exe, project = project, graphics = false,
                    extraArgs = "-executeMethod MaterialStudio.EditorTools.MSBatch.Run -msjob \"" + jobPath + "\"",
                    logPath = Path.Combine(jobDir, "unity.log"), resultPath = job.resultPath,
                    onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { cfg.warudoWorkspaceVersion = ver; SaveConfig(); } }
                });
            }
            return true;
        }

        /// <summary>
        /// Data-only stand-ins for VRChat's PhysBone classes, so a mod's PhysBone settings can be
        /// read and turned into DynamicBone. Left out when the project has the real VRChat SDK.
        /// </summary>
        static bool InstallVrcStubs(string project, List<string> notes)
        {
            string src = Path.Combine(UnityDriver.PluginDir, Path.Combine("UnityToolset", "VRCStubs"));
            if (!Directory.Exists(src)) { notes.Add("The PhysBone readers are missing from the plugin folder; PhysBones become DynamicBone by bone names only."); return true; }
            string assets = Path.Combine(project, "Assets");
            foreach (string d in Directory.GetDirectories(assets))
                if (Path.GetFileName(d).IndexOf("VRCSDK", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string man = Path.Combine(project, Path.Combine("Packages", "manifest.json"));
            if (File.Exists(man) && File.ReadAllText(man).IndexOf("com.vrchat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            UnityDriver.CopyTree(src, Path.Combine(assets, "MaterialStudioVRCStubs"), new[] { ".cs", ".asmdef", ".meta" });
            return true;
        }

        /// <summary>Only folders Material Studio made itself are ever deleted.</summary>
        static bool IsOwnFolder(string dir)
        {
            try { return Path.GetFullPath(dir).StartsWith(Path.GetFullPath(ShaderCatalog.Dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// The ticked Assets folders of the VSF SDK project, with their .meta files so script GUIDs
        /// match. A folder already in the project is left alone: it is either from an earlier try
        /// or a newer version from the shader packages (Poiyomi 9 over the seed's Poiyomi 8).
        /// </summary>
        static bool CopySeedFolders(string seed, string project, List<string> folders, List<string> notes)
        {
            string assets = Path.Combine(project, "Assets");
            Directory.CreateDirectory(assets);
            var copied = new List<string>();
            foreach (string f in folders)
            {
                string src = Path.Combine(Path.Combine(seed, "Assets"), f);
                if (!Directory.Exists(src) || Directory.Exists(Path.Combine(assets, f))) continue;
                UnityDriver.CopyTree(src, Path.Combine(assets, f), null);
                if (File.Exists(src + ".meta")) File.Copy(src + ".meta", Path.Combine(assets, f + ".meta"), true);
                copied.Add(f);
            }
            notes.Add(copied.Count > 0 ? "Copied " + string.Join(", ", copied.ToArray()) + " from " + seed + "." : "The VSF SDK folders are already in the project.");
            return true;
        }

        /// <summary>
        /// Unpacks the mod (its AssetBundle can be most of a GB, so on a worker thread), keeps only
        /// this mod in the cache, and writes the export job with what the editor needs from it.
        /// </summary>
        UnityStep UnpackWarudoStep(string source, MSJob job, string jobDir, string jobPath)
        {
            return new UnityStep
            {
                title = "Unpacking " + Path.GetFileName(source),
                project = jobDir,
                work = delegate (List<string> n)
                {
                    WarudoContainer c = WarudoContainer.Open(source, WarudoCacheDir);
                    foreach (string d in Directory.GetDirectories(WarudoCacheDir))
                        if (!string.Equals(Path.GetFullPath(d), Path.GetFullPath(c.cacheDir), StringComparison.OrdinalIgnoreCase))
                            try { Directory.Delete(d, true); } catch { }
                    job.warudoBundlePath = c.bundlePath;
                    job.modName = c.DisplayName;
                    job.modAuthor = c.author;
                    job.modVersion = c.modVersion;
                    job.modUnityVersion = c.unityVersion;
                    if (c.coverJpeg != null && c.coverJpeg.Length > 0)
                    {
                        job.modCoverPath = Path.Combine(jobDir, "cover.jpg");
                        File.WriteAllBytes(job.modCoverPath, c.coverJpeg);
                    }
                    File.WriteAllText(jobPath, JsonIO.ToJson(job, true));
                    n.Add("Unpacked " + c.DisplayName + " by " + (c.author.Length > 0 ? c.author : "unknown") + " (made with Unity " + c.unityVersion + ").");
                    return true;
                }
            };
        }

        // ------------------------------------------------------------------ Export tab

        float BuildWarudoSource(RectTransform c, float y, float w)
        {
            Text f = Ui.Label("WarudoP", c, string.IsNullOrEmpty(cfg.exportWarudo) ? "(browse to the .warudo file)" : cfg.exportWarudo, 30, y, w - 140, 34, 11, TextAnchor.UpperLeft);
            f.color = File.Exists(cfg.exportWarudo) ? Ui.Ink : Ui.Warn;
            Ui.Button("Button_BrowseWarudo", c, "Browse", w - 100, y, 90, 22, delegate
            {
                string p = NativeFileDialog.OpenFile("Pick the Warudo mod (.warudo)", StartDir(cfg.exportWarudo, cfg.exportOut), "Warudo mod (*.warudo)", "*.warudo");
                if (string.IsNullOrEmpty(p)) return;
                if (!string.Equals(p, cfg.exportWarudo, StringComparison.OrdinalIgnoreCase)) { cfg.exportName = ""; if (outNameField != null) outNameField.text = ""; }
                cfg.exportWarudo = p;
                SaveConfig();
                RebuildTab();
            }, 11);
            y += 38f;
            if (File.Exists(cfg.exportWarudo))
            {
                WarudoInfo mod = WarudoInfo.Read(cfg.exportWarudo);
                string text;
                bool bad = false;
                if (mod.error.Length > 0) { text = "Could not read this file: " + mod.error; bad = true; }
                else
                {
                    var n = new List<string>();
                    string exe = EditorForWarudo(mod.unityVersion, n);
                    text = "Made with Unity " + mod.unityVersion + (mod.author.Length > 0 ? " by " + mod.author : "") + ". ";
                    if (exe != null) text += "Exports with Unity " + UnityDriver.EditorVersion(exe) + ".";
                    else { text += n.Count > 0 ? n[0] : "No installed Unity can open it."; bad = true; }
                }
                Text info = Ui.Label("WarudoInfo", c, text, 30, y, w - 40, 34, 10, TextAnchor.UpperLeft);
                info.color = bad ? Ui.Warn : Ui.TextDim;
                y += 36f;
                // installing helps unless the mod is newer than VNyan itself
                if (bad && mod.error.Length == 0 && VNyanCanLoad(ModNeeds(mod.unityVersion)))
                {
                    Button ib = Ui.Button("Button_InstallUnity", c, "Install Unity " + Application.unityVersion + " with Unity Hub", 30, y, 300, 24, OfferUnityInstall, 11);
                    tips.Chip((RectTransform)ib.transform, "InstallUnity", "Opens Unity Hub's install window for the Unity version VNyan itself runs on (free; the Personal licence is enough). Without Unity Hub, opens its download page first. Unity cannot come with this plugin: its licence does not allow passing it on.", false);
                    y += 30f;
                }
            }
            Toggle ph = Ui.Check("Toggle_WarudoPhysics", c, "Turn the mod's bone physics into DynamicBone (like the Warudo Importer)", 30, y, w - 40, 22, cfg.warudoPhysics, delegate (bool v) { cfg.warudoPhysics = v; SaveConfig(); });
            tips.Chip((RectTransform)ph.transform, "WarudoPhysics", "Warudo turns VRChat PhysBones and similar into DynamicBone when it loads a mod; this does the same, so hair and clothes move in VNyan. Needs DynamicBone in your VSF SDK project. Bones the mod's own VRM spring bones already move are left alone.", false);
            y += 28f;
            return y;
        }

        // ------------------------------------------------------------------ identity

        /// <summary>
        /// The Warudo Importer loads a mod under the name "&lt;mod&gt;.warudo.vsfavatar". Returns the
        /// .warudo file behind such a name, or "" when the avatar did not come from one.
        /// </summary>
        string WarudoFileFor(string avatarSource)
        {
            const string tail = ".warudo.vsfavatar";
            if (string.IsNullOrEmpty(avatarSource) || !avatarSource.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return "";
            string file = avatarSource.Substring(0, avatarSource.Length - ".vsfavatar".Length);
            string name = Path.GetFileName(file);
            try
            {
                if (Path.IsPathRooted(file) && File.Exists(file))
                {
                    // the importer's key may come back lower-cased; show the real name
                    string[] hit = Directory.GetFiles(Path.GetDirectoryName(file), name);
                    return hit.Length > 0 ? hit[0] : file;
                }
                // only a file name: the importer remembers the last file it opened
                string settings = Path.Combine(Application.persistentDataPath, "WarudoImporter");
                if (File.Exists(settings))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(settings), "\"path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    string p = m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
                    if (p.Length > 0 && string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
                }
                if (string.Equals(Path.GetFileName(cfg.exportWarudo), name, StringComparison.OrdinalIgnoreCase) && File.Exists(cfg.exportWarudo)) return cfg.exportWarudo;
            }
            catch { }
            return "";
        }
    }
}
