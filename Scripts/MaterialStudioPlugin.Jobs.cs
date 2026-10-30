using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    public partial class MaterialStudioPlugin
    {
        static string CatalogPath { get { return Path.Combine(ShaderCatalog.Dir, "workspace_catalog.json"); } }
        string WorkProject { get { return cfg.useSeedDirectly ? cfg.seedProject : cfg.workspace; } }
        Text jobLog;
        float nextLogPaint;
        string shaderPickSearch = "";

        // ------------------------------------------------------------------ log pane

        void TickJobsUi()
        {
            if (jobLog == null || (currentTab != 2 && currentTab != 3)) return;
            if (Time.unscaledTime < nextLogPaint) return;
            nextLogPaint = Time.unscaledTime + 0.5f;
            PaintLog();
        }

        void PaintLog()
        {
            if (jobLog == null) return;
            int from = Math.Max(0, runner.log.Count - 14);
            var lines = new List<string>();
            lines.Add("Status: " + runner.status);
            for (int i = from; i < runner.log.Count; i++) lines.Add(runner.log[i]);
            string s = string.Join("\n", lines.ToArray());
            if (s.Length > 12000) s = s.Substring(s.Length - 12000);    // a Text past ~16k chars corrupts other windows' glyphs
            jobLog.text = s;
        }

        RectTransform TabScroll(RectTransform P, float logH, out float contentW)
        {
            Ui.Clear(P);
            ScrollRect sr;
            RectTransform c = Ui.Scroll("TabScroll", P, 6, 6, RightW - 12, P.rect.height - logH - 18, out sr);
            Image lb = Ui.Box("LogBox", P, 6, P.rect.height - logH - 8, RightW - 12, logH, Ui.PanelDark);
            jobLog = Ui.Label("Log", lb.transform, "", 6, 4, RightW - 24, logH - 8, 10, TextAnchor.LowerLeft);
            jobLog.verticalOverflow = VerticalWrapMode.Truncate;
            contentW = RightW - 28;
            PaintLog();
            return c;
        }

        Text Section(RectTransform c, string title, float y, float w)
        {
            Text t = Ui.Label("H_" + title, c, title, 6, y, w, 22, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            t.color = Ui.Accent;
            return t;
        }

        // ------------------------------------------------------------------ Packages tab

        void BuildPackagesTab(RectTransform P)
        {
            float w;
            RectTransform c = TabScroll(P, 130, out w);
            float y = 4f;
            Section(c, "Unity project used for shaders and export", y, w); y += 24f;

            Toggle direct = Ui.Check("Toggle_Direct", c, "Use my VSF SDK project directly (no private copy)", 8, y, 420, 22, cfg.useSeedDirectly, delegate (bool v) { cfg.useSeedDirectly = v; SaveConfig(); RebuildTab(); });
            tips.Chip((RectTransform)direct.transform, "Direct", "Off (recommended): Material Studio makes its own private Unity project by copying the SDK parts of your VSF SDK project, so your project is never touched. " +
                "On: it works inside your project instead (adds an Assets/MaterialStudioTools folder and imports packages there). Either way, close that project in Unity while a job runs.", false);
            y += 28f;
            Ui.Label("SeedL", c, "VSF SDK project:", 8, y, 120, 22, 11);
            Text sp = Ui.Label("SeedP", c, string.IsNullOrEmpty(cfg.seedProject) ? "(not picked)" : cfg.seedProject, 130, y, w - 250, 22, 11);
            sp.color = UnityDriver.IsUnityProject(cfg.seedProject) ? Ui.Ink : Ui.Warn;
            Button pk = Ui.Button("Button_PickSeed", c, "Pick folder", w - 110, y, 104, 22, delegate
            {
                string f = NativeFileDialog.PickFolder("Pick your VSF SDK Unity project (the folder with Assets and ProjectSettings)", cfg.seedProject);
                if (string.IsNullOrEmpty(f)) return;
                if (!UnityDriver.IsUnityProject(f)) { SetStatus("That folder is not a Unity project (needs Assets and ProjectSettings)."); return; }
                cfg.seedProject = f;
                cfg.seedFolders.Clear();
                foreach (string a in UnityDriver.AssetFolders(f)) if (UnityDriver.IsDefaultSeedFolder(a)) cfg.seedFolders.Add(a);
                cfg.workspaceSeeded = false;
                SaveConfig();
                RebuildTab();
            }, 11);
            tips.Chip((RectTransform)pk.transform, "PickSeed", "The Unity project you export avatars with (VSF SDK + UniVRM). Its SDK folders are copied so exported avatars keep their VRM components.", false);
            y += 28f;

            if (!cfg.useSeedDirectly && UnityDriver.IsUnityProject(cfg.seedProject))
            {
                Text hint = Ui.Label("SeedHint", c, "Copy these Assets folders into the private project (SDK, UniVRM, physics, shaders your avatars use):", 8, y, w, 20, 11);
                hint.color = Ui.TextDim;
                y += 22f;
                List<string> folders = UnityDriver.AssetFolders(cfg.seedProject);
                float x = 8f;
                foreach (string f in folders)
                {
                    string ff = f;
                    Ui.Check("Seed_" + f, c, f, x, y, 196, 20, cfg.seedFolders.Contains(f), delegate (bool on)
                    {
                        if (on && !cfg.seedFolders.Contains(ff)) cfg.seedFolders.Add(ff);
                        if (!on) cfg.seedFolders.Remove(ff);
                        cfg.workspaceSeeded = false;
                        SaveConfig();
                    });
                    x += 200f;
                    if (x + 196f > w) { x = 8f; y += 22f; }
                }
                if (x > 8f) y += 22f;
                Ui.Label("WsL", c, "Private project: " + cfg.workspace + (UnityDriver.IsUnityProject(cfg.workspace) ? (cfg.workspaceSeeded ? "  (ready)" : "  (will refresh)") : "  (made on first build)"), 8, y, w, 20, 10).color = Ui.TextDim;
                y += 26f;
            }

            Section(c, "Shader packages", y, w); y += 24f;
            if (cfg.packages.Count == 0)
            {
                Text none = Ui.Label("NoPk", c, "None yet. Add Poiyomi, lilToon or any shader .unitypackage (or a folder of shaders).", 8, y, w, 20, 11);
                none.color = Ui.TextDim;
                y += 22f;
            }
            for (int i = 0; i < cfg.packages.Count; i++)
            {
                PackageEntry pe = cfg.packages[i];
                bool imported = !string.IsNullOrEmpty(pe.importedStamp) && pe.importedStamp == Stamp(pe);
                Ui.Label("Pk" + i, c, Path.GetFileName(pe.path.TrimEnd('\\', '/')) + (pe.isFolder ? "  (folder)" : ""), 8, y, w - 230, 20, 11);
                Text st = Ui.Label("PkS" + i, c, !File.Exists(pe.path) && !Directory.Exists(pe.path) ? "missing" : (imported ? "imported" : "imports on next build"), w - 220, y, 140, 20, 10);
                st.color = imported ? Ui.Good : Ui.Warn;
                Ui.Button("PkX" + i, c, "Remove", w - 72, y, 66, 20, delegate { cfg.packages.Remove(pe); SaveConfig(); RebuildTab(); }, 10);
                y += 22f;
            }
            Button ap = Ui.Button("Button_AddPkg", c, "Add .unitypackage", 8, y, 150, 24, delegate
            {
                string f = NativeFileDialog.OpenFile("Pick a shader .unitypackage", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads", "Unity package (*.unitypackage)", "*.unitypackage");
                if (string.IsNullOrEmpty(f)) return;
                AddPackage(f, false);
            });
            tips.Chip((RectTransform)ap.transform, "AddPkg", "Pick a shader's .unitypackage (Poiyomi, lilToon, or any custom shader). It is imported into the Unity project on the next Build and its shaders show up below.", false);
            Button af = Ui.Button("Button_AddFolder", c, "Add shader folder", 164, y, 150, 24, delegate
            {
                string f = NativeFileDialog.PickFolder("Pick a folder with .shader files (and their .meta files)", cfg.seedProject);
                if (string.IsNullOrEmpty(f)) return;
                AddPackage(f, true);
            });
            tips.Chip((RectTransform)af.transform, "AddFolder", "A folder of shaders already unpacked (for example Assets/_PoiyomiShaders from another project). It is copied into the Unity project's Assets.", false);
            y += 32f;

            Section(c, "Shaders to build", y, w); y += 24f;
            List<CatalogShader> cats = catalog.workspace != null ? catalog.workspace.shaders : new List<CatalogShader>();
            HashSet<string> needed = NeededShaders();
            if (cats.Count == 0)
            {
                Text n = Ui.Label("NoCat", c, "Press Build once: it imports your packages and lists their shaders here. Shaders this avatar needs (unlocked Poiyomi, swapped shaders) are always included.", 8, y, w, 34, 11, TextAnchor.UpperLeft);
                n.color = Ui.TextDim;
                y += 36f;
            }
            else
            {
                Ui.Label("FindL", c, "Find", 8, y, 36, 22, 11);
                InputField sf = Ui.Input("Input_PickSearch", c, shaderPickSearch, 46, y, 200, 22, InputField.ContentType.Standard, null);
                sf.onValueChanged.AddListener(delegate (string s) { shaderPickSearch = s; });
                Ui.Button("Button_ApplyFind", c, "Filter", 250, y, 60, 22, delegate { RebuildTab(); }, 11);
                Ui.Button("Button_PickNone", c, "Untick all", 316, y, 80, 22, delegate { cfg.wantedShaders.Clear(); SaveConfig(); RebuildTab(); }, 11);
                y += 26f;
                string q = (shaderPickSearch ?? "").Trim().ToLowerInvariant();
                foreach (CatalogShader cs in cats)
                {
                    if (cs == null || string.IsNullOrEmpty(cs.name)) continue;
                    if (q.Length > 0 && cs.name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                    string nm = cs.name;
                    bool need = needed.Contains(nm);
                    string label = nm + (need ? "   (needed by this avatar)" : "") + (cs.hasErrors ? "   (has compile errors)" : "");
                    Toggle t = Ui.Check("Want", c, label, 8, y, w - 16, 20, need || cfg.wantedShaders.Contains(nm), delegate (bool on)
                    {
                        if (on && !cfg.wantedShaders.Contains(nm)) cfg.wantedShaders.Add(nm);
                        if (!on) cfg.wantedShaders.Remove(nm);
                        SaveConfig();
                    });
                    if (need) t.interactable = false;
                    if (cs.hasErrors) t.GetComponentInChildren<Text>().color = Ui.Warn;
                    y += 22f;
                }
            }
            y += 6f;
            Button bb = Ui.Button("Button_Build", c, runner.Busy ? "Working..." : "Build shader bundle", 8, y, 180, 28, StartShaderBuild);
            tips.Chip((RectTransform)bb.transform, "Build", "Runs Unity in the background: sets up the project, imports new packages, then compiles the ticked shaders (with the exact features your materials use) into a bundle VNyan loads right away. " +
                "First run takes several minutes. VNyan stays usable.", false);
            Ui.Button("Button_Cancel", c, "Cancel", 194, y, 80, 28, delegate { runner.Cancel(); RebuildTab(); });
            y += 36f;
            Ui.SetContentHeight(c, y);
        }

        static string Stamp(PackageEntry pe)
        {
            try
            {
                if (pe.isFolder) return Directory.Exists(pe.path) ? "dir:" + Directory.GetLastWriteTimeUtc(pe.path).Ticks : "";
                var fi = new FileInfo(pe.path);
                return fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "";
            }
            catch { return ""; }
        }

        void AddPackage(string path, bool folder)
        {
            foreach (PackageEntry pe in cfg.packages)
                if (string.Equals(pe.path, path, StringComparison.OrdinalIgnoreCase)) { SetStatus("Already in the list."); return; }
            cfg.packages.Add(new PackageEntry { path = path, isFolder = folder });
            SaveConfig();
            SetStatus("Added " + Path.GetFileName(path.TrimEnd('\\', '/')) + ". Press Build to import it.");
            RebuildTab();
        }

        /// <summary>Shaders this avatar needs from the bundle: unlocked/locked Poiyomi originals and swapped-in bundle shaders.</summary>
        HashSet<string> NeededShaders()
        {
            var set = new HashSet<string>();
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null || e.mat.shader == null) continue;
                string orig = RecipeEngine.PoiyomiOriginalShader(e.mat);
                if (orig.Length > 0) set.Add(orig);
                if (catalog.IsBundleShader(e.mat.shader)) set.Add(e.mat.shader.name);
            }
            return set;
        }

        List<ShaderRequest> BuildRequests()
        {
            var map = new Dictionary<string, ShaderRequest>();
            Func<string, ShaderRequest> get = delegate (string n)
            {
                ShaderRequest r;
                if (!map.TryGetValue(n, out r)) { r = new ShaderRequest { name = n }; map[n] = r; }
                return r;
            };
            foreach (string n in cfg.wantedShaders) get(n);
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null || e.mat.shader == null) continue;
                string orig = RecipeEngine.PoiyomiOriginalShader(e.mat);
                bool fromBundle = catalog.IsBundleShader(e.mat.shader);
                string[] wantKw = DesiredKeywords(e.mat);
                bool missing = pendingKw.ContainsKey(e.mat) || !VariantKnown(e.mat.shader, wantKw);
                if (orig.Length == 0 && !fromBundle && !missing) continue;
                ShaderRequest r = get(orig.Length > 0 ? orig : e.mat.shader.name);
                // the material's numbers let the editor's own property drawers pick keywords
                var fs = new FloatSet();
                Shader sh = e.mat.shader;
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    PropType t = RecipeEngine.KindOf(sh, i);
                    if (t != PropType.Float && t != PropType.Int) continue;
                    string pn = sh.GetPropertyName(i);
                    fs.f.Add(new NamedFloat(pn, t == PropType.Int ? RecipeEngine.GetIntProp(e.mat, pn) : e.mat.GetFloat(pn)));
                }
                r.materials.Add(fs);
                if (fromBundle || missing)
                {
                    r.variants.AddRange(VariantsToCompile(e.mat, wantKw));
                    requestedKey[e.mat] = new KeywordSet(WantedKeywords(sh, wantKw)).Key();
                }
            }
            return new List<ShaderRequest>(map.Values);
        }

        /// <summary>Workspace setup + package imports + toolset, as runner steps. False when it cannot start.</summary>
        bool QueuePrepare(string project, string exe, bool forExport)
        {
            var notes2 = new List<string>();
            if (cfg.useSeedDirectly)
            {
                if (!UnityDriver.IsUnityProject(project)) { runner.Log("Pick your VSF SDK project on the Packages tab first."); return false; }
            }
            else if (!cfg.workspaceSeeded || !UnityDriver.IsUnityProject(project))
            {
                if (UnityDriver.IsUnityProject(cfg.seedProject))
                {
                    string seed = cfg.seedProject;
                    var folders = new List<string>(cfg.seedFolders);
                    runner.Enqueue(new UnityStep
                    {
                        title = "Copying SDK folders into the private project",
                        project = project,
                        work = delegate (List<string> n) { return UnityDriver.SeedWorkspace(seed, project, folders, n); },
                        onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { cfg.workspaceSeeded = true; SaveConfig(); } }
                    });
                }
                else
                {
                    if (forExport) { runner.Log("Exporting needs the VSF SDK: pick your VSF SDK project on the Packages tab first."); return false; }
                    if (!UnityDriver.IsUnityProject(project))
                        runner.Enqueue(new UnityStep
                        {
                            title = "Creating the private Unity project", exe = exe, project = project, createProject = true, graphics = false,
                            logPath = Path.Combine(UnityDriver.JobsDir, "create.log"),
                        });
                }
            }
            foreach (PackageEntry pe in cfg.packages)
            {
                if (pe.importedStamp == Stamp(pe) && pe.importedStamp.Length > 0) continue;
                PackageEntry p = pe;
                if (pe.isFolder)
                {
                    runner.Enqueue(new UnityStep
                    {
                        title = "Copying " + Path.GetFileName(pe.path.TrimEnd('\\', '/')),
                        project = project,
                        work = delegate (List<string> n)
                        {
                            string dst = Path.Combine(Path.Combine(project, "Assets"), Path.GetFileName(p.path.TrimEnd('\\', '/')));
                            UnityDriver.CopyTree(p.path, dst, null);
                            return true;
                        },
                        onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { p.importedStamp = Stamp(p); SaveConfig(); } }
                    });
                }
                else
                {
                    if (!File.Exists(pe.path)) { runner.Log("Package missing: " + pe.path); continue; }
                    runner.Enqueue(new UnityStep
                    {
                        title = "Importing " + Path.GetFileName(pe.path), exe = exe, project = project, graphics = false,
                        extraArgs = "-importPackage \"" + pe.path + "\"",
                        logPath = Path.Combine(UnityDriver.JobsDir, "import.log"),
                        onDone = delegate (UnityStep s, MSResult r) { if (r.ok) { p.importedStamp = Stamp(p); SaveConfig(); } }
                    });
                }
            }
            runner.Enqueue(new UnityStep
            {
                title = "Installing the Material Studio editor scripts",
                project = project,
                work = delegate (List<string> n) { return UnityDriver.InstallToolset(project, n); }
            });
            foreach (string s in notes2) runner.Log(s);
            return true;
        }

        /// <summary>
        /// First-time help: find a VSF SDK project in Unity Hub's project list (the most recently
        /// opened one wins), so Lock and Build work without a trip to the Packages tab.
        /// </summary>
        bool AutoPickSeedProject()
        {
            if (UnityDriver.IsUnityProject(cfg.seedProject)) return true;
            try
            {
                string hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine("UnityHub", "projects-v1.json"));
                if (!File.Exists(hub)) return false;
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(hub), "\"path\":\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!UnityDriver.IsUnityProject(p) || !Directory.Exists(Path.Combine(p, Path.Combine("Assets", "VSF SDK")))) continue;
                    string lib = Path.Combine(p, "Library");
                    DateTime t = Directory.Exists(lib) ? Directory.GetLastWriteTimeUtc(lib) : Directory.GetLastWriteTimeUtc(p);
                    if (t > bestTime) { bestTime = t; best = p; }
                }
                if (best == null) return false;
                cfg.seedProject = best;
                cfg.seedFolders.Clear();
                foreach (string a in UnityDriver.AssetFolders(best)) if (UnityDriver.IsDefaultSeedFolder(a)) cfg.seedFolders.Add(a);
                cfg.workspaceSeeded = false;
                SaveConfig();
                runner.Log("Using your VSF SDK project " + best + " (change it on the Packages tab). Copying: " + string.Join(", ", cfg.seedFolders.ToArray()));
                return true;
            }
            catch (Exception e) { runner.Log("Could not look up Unity Hub projects: " + e.Message); return false; }
        }

        string PickEditor(string project)
        {
            var n = new List<string>();
            string probe = UnityDriver.IsUnityProject(project) ? project : cfg.seedProject;
            string exe = UnityDriver.EditorFor(probe, cfg.unityExe, n);
            foreach (string s in n) runner.Log(s);
            if (exe == null) runner.Log("No Unity editor found. Install Unity 2019.4.31f1 (the VSF SDK version) with Unity Hub, or pick Unity.exe on Settings.");
            return exe;
        }

        bool CheckNotOpen(string project)
        {
            if (UnityDriver.IsUnityProject(project) && UnityDriver.ProjectIsOpen(project))
            {
                runner.Log("That Unity project is open in the Unity editor. Close it there first (Unity allows one editor per project).");
                return false;
            }
            return true;
        }

        void StartShaderBuild() { StartShaderBuild(false); }

        void StartShaderBuild(bool automatic)
        {
            if (!automatic) SetJob("Building the shader bundle", "Compiles the ticked shaders plus the features your materials use", "new shaders loaded");
            if (runner.Busy) { SetStatus("A job is already running."); return; }
            AutoPickSeedProject();
            string project = WorkProject;
            if (string.IsNullOrEmpty(project)) { runner.Log("Pick your VSF SDK project first."); RebuildTab(); return; }
            string exe = PickEditor(project);
            if (exe == null || !CheckNotOpen(project)) { RebuildTab(); return; }
            if (!QueuePrepare(project, exe, false)) { RebuildTab(); return; }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string jobDir = Path.Combine(UnityDriver.JobsDir, "build_" + stamp);
            Directory.CreateDirectory(jobDir);
            var job = new MSJob
            {
                kind = "catalog shaders",
                resultPath = Path.Combine(jobDir, "result.json"),
                catalogPath = CatalogPath,
                shaders = BuildRequests(),
                bundleName = "ms_shaders_" + stamp,
                outDir = ShaderCatalog.BundleDir,
            };
            string jobPath = Path.Combine(jobDir, "job.json");
            File.WriteAllText(jobPath, JsonIO.ToJson(job, true));
            runner.Enqueue(new UnityStep
            {
                title = job.shaders.Count > 0 ? "Compiling " + job.shaders.Count + " shader(s)" : "Listing the project's shaders",
                exe = exe, project = project, graphics = true,
                extraArgs = "-executeMethod MaterialStudio.EditorTools.MSBatch.Run -msjob \"" + jobPath + "\"",
                logPath = Path.Combine(jobDir, "unity.log"),
                resultPath = job.resultPath,
                onDone = delegate (UnityStep s, MSResult r)
                {
                    catalog.LoadWorkspaceCatalog(CatalogPath);
                    if (r.ok && !string.IsNullOrEmpty(r.output)) LoadNewBundle(Path.Combine(ShaderCatalog.BundleDir, r.output));
                    catalog.Refresh(reg.Shaders());
                    FinishVariantBuild(r.ok && !string.IsNullOrEmpty(r.output));
                    RefreshAll();
                }
            });
            SetStatus("Building in the background...");
            RebuildTab();
        }

        /// <summary>Swap in a freshly built shader bundle and re-point materials at its shaders.</summary>
        void LoadNewBundle(string path)
        {
            var log = new List<string>();
            var oldUsers = new List<KeyValuePair<Material, string>>();
            foreach (MatEntry e in reg.entries)
                if (e.mat != null && catalog.IsBundleShader(e.mat.shader)) oldUsers.Add(new KeyValuePair<Material, string>(e.mat, e.mat.shader.name));
            if (!catalog.LoadBundleFile(path, log)) { foreach (string l in log) runner.Log(l); return; }
            try { File.WriteAllText(ShaderCatalog.ActiveFile, Path.GetFileName(path)); } catch { }
            foreach (string l in log) runner.Log(l);
            catalog.Refresh(reg.Shaders());
            int moved = 0;
            foreach (var kv in oldUsers)
            {
                Shader ns = catalog.Find(kv.Value);
                if (ns != null && ns != kv.Key.shader) { int q = kv.Key.renderQueue; kv.Key.shader = ns; kv.Key.renderQueue = q; moved++; }
            }
            ShaderLayout.ClearCache();
            if (moved > 0) runner.Log("Moved " + moved + " material(s) onto the new build.");
            runner.Log("New shaders are ready: pick them on the Shader tab" +
                       (reg.entries.Exists(delegate (MatEntry e) { return e.mat != null && RecipeEngine.IsLockedPoiyomi(e.mat); }) ? ", or Unlock your Poiyomi materials." : "."));
        }

        // ------------------------------------------------------------------ Export tab

        void BuildExportTab(RectTransform P)
        {
            float w;
            RectTransform c = TabScroll(P, 150, out w);
            float y = 4f;
            Section(c, "Export a .vsfavatar with your edits baked in", y, w); y += 26f;

            Toggle a = Ui.Check("Src_Vsf", c, "From this avatar's .vsfavatar file", 8, y, 300, 22, cfg.exportSourceKind != "prefab", delegate (bool on) { cfg.exportSourceKind = on ? "vsfavatar" : "prefab"; SaveConfig(); RebuildTab(); });
            tips.Chip((RectTransform)a.transform, "SrcVsf", "Rebuilds the avatar from the .vsfavatar file inside the private Unity project. Needs the private project set up from your VSF SDK project (Packages tab) and the avatar's shaders added there.", false);
            y += 24f;
            if (cfg.exportSourceKind != "prefab")
            {
                Text f = Ui.Label("VsfP", c, string.IsNullOrEmpty(cfg.exportVsf) ? "(unknown: browse to it)" : cfg.exportVsf, 30, y, w - 140, 34, 11, TextAnchor.UpperLeft);
                f.color = File.Exists(cfg.exportVsf) ? Ui.Ink : Ui.Warn;
                Ui.Button("Button_BrowseVsf", c, "Browse", w - 100, y, 90, 22, delegate
                {
                    string p = NativeFileDialog.OpenFile("Pick the avatar's .vsfavatar", StartDir(cfg.exportVsf, cfg.exportOut), "VSF avatar (*.vsfavatar)", "*.vsfavatar");
                    if (string.IsNullOrEmpty(p)) return;
                    cfg.exportVsf = p;
                    RememberAvatarPath(p);
                    SaveConfig();
                    RebuildTab();
                }, 11);
                y += 38f;
            }
            Toggle b = Ui.Check("Src_Prefab", c, "From the avatar's prefab in its Unity project", 8, y, 340, 22, cfg.exportSourceKind == "prefab", delegate (bool on) { cfg.exportSourceKind = on ? "prefab" : "vsfavatar"; SaveConfig(); RebuildTab(); });
            tips.Chip((RectTransform)b.transform, "SrcPrefab", "Best quality when you still have the project you made the avatar in: the edits are applied to COPIES of its materials and exported with the VSF SDK's own settings. Your prefab and materials are not changed.", false);
            y += 24f;
            if (cfg.exportSourceKind == "prefab")
            {
                Ui.Label("ProjL", c, "Project:", 30, y, 70, 22, 11);
                Ui.Label("ProjP", c, string.IsNullOrEmpty(cfg.exportProject) ? "(pick)" : cfg.exportProject, 100, y, w - 210, 22, 11);
                Ui.Button("Button_PickProj", c, "Pick", w - 100, y, 90, 22, delegate
                {
                    string p = NativeFileDialog.PickFolder("Pick the avatar's Unity project", cfg.exportProject);
                    if (!string.IsNullOrEmpty(p)) { cfg.exportProject = p; SaveConfig(); RebuildTab(); }
                }, 11);
                y += 26f;
                Ui.Label("PrefL", c, "Prefab:", 30, y, 70, 22, 11);
                InputField pf = Ui.Input("Input_Prefab", c, cfg.exportPrefab, 100, y, w - 210, 22, InputField.ContentType.Standard, delegate (string s) { cfg.exportPrefab = s.Trim(); SaveConfig(); });
                ((Text)pf.placeholder).text = "Assets/.../MyAvatar.prefab (Enter to keep)";
                Ui.Button("Button_PickPrefab", c, "Browse", w - 100, y, 90, 22, delegate
                {
                    string start = UnityDriver.IsUnityProject(cfg.exportProject) ? Path.Combine(cfg.exportProject, "Assets") : "";
                    string p = NativeFileDialog.OpenFile("Pick the avatar prefab", start, "Prefab (*.prefab)", "*.prefab");
                    if (string.IsNullOrEmpty(p)) return;
                    string proj = cfg.exportProject;
                    if (UnityDriver.IsUnityProject(proj) && p.StartsWith(proj, StringComparison.OrdinalIgnoreCase))
                        cfg.exportPrefab = p.Substring(proj.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                    else { SetStatus("Pick a prefab inside the project folder."); return; }
                    SaveConfig(); RebuildTab();
                }, 11);
                y += 30f;
            }

            Section(c, "Output", y, w); y += 24f;
            string defDir = DefaultOutDir();
            Ui.Label("OutL", c, "Folder:", 8, y, 60, 22, 11);
            Ui.Label("OutP", c, string.IsNullOrEmpty(cfg.exportOut) ? defDir + "  (default)" : cfg.exportOut, 70, y, w - 180, 34, 11, TextAnchor.UpperLeft);
            Ui.Button("Button_PickOut", c, "Pick", w - 100, y, 90, 22, delegate
            {
                string p = NativeFileDialog.PickFolder("Where should the new .vsfavatar go?", string.IsNullOrEmpty(cfg.exportOut) ? defDir : cfg.exportOut);
                if (!string.IsNullOrEmpty(p)) { cfg.exportOut = p; SaveConfig(); RebuildTab(); }
            }, 11);
            y += 38f;
            Ui.Label("NameL", c, "File:", 8, y, 60, 22, 11);
            InputField nf = Ui.Input("Input_OutName", c, cfg.exportName, 70, y, 280, 22, InputField.ContentType.Standard, delegate (string s) { cfg.exportName = s.Trim(); SaveConfig(); });
            ((Text)nf.placeholder).text = DefaultOutName();
            y += 30f;

            Section(c, "Options", y, w); y += 24f;
            Toggle t1 = Ui.Check("Toggle_LiveWeights", c, "Include Weight Studio's live weight paint", 8, y, w - 20, 22, cfg.applyLiveWeights, delegate (bool v) { cfg.applyLiveWeights = v; SaveConfig(); });
            tips.Chip((RectTransform)t1.transform, "LiveW", "Writes the bone weights you painted with Weight Studio (including >4 influences) into the export, exactly as VNyan shows them now. Weights from Bone Weight Smoother or JigglyPhys are left out on purpose: those plugins redo their work on every load, so baking them in would apply them twice. Only meshes that really differ are changed; the log lists them.", false);
            y += 24f;
            Toggle t2 = Ui.Check("Toggle_Relock", c, "Keep Poiyomi locked where it is locked now (recommended)", 8, y, w - 20, 22, cfg.relockPoiyomi, delegate (bool v) { cfg.relockPoiyomi = v; SaveConfig(); });
            tips.Chip((RectTransform)t2.transform, "Relock", "Each Poiyomi material comes out locked or unlocked exactly as it is in VNyan right now (Lock & optimise / Unlock on the Shader tab). Off: everything is exported unlocked. Needs the matching Poiyomi version in the Unity project.", false);
            y += 24f;
            Toggle t3 = Ui.Check("Toggle_LoadAfter", c, "Load the new avatar in VNyan when done", 8, y, w - 20, 22, cfg.loadAfterExport, delegate (bool v) { cfg.loadAfterExport = v; SaveConfig(); });
            tips.Chip((RectTransform)t3.transform, "LoadAfter", "Loads the exported file through VNyan's normal avatar loading, so you can check it straight away.", false);
            y += 30f;

            Button ex = Ui.Button("Button_Export", c, runner.Busy ? "Working..." : "Export .vsfavatar", 8, y, 180, 30, StartExport);
            tips.Chip((RectTransform)ex.transform, "Export", "Saves your edits, snapshots live weights, then has Unity rebuild the avatar with everything applied, using the same settings as the VSF SDK's own exporter. VNyan and VSeeFace both load the result.", false);
            Ui.Button("Button_CancelEx", c, "Cancel", 194, y, 80, 30, delegate { runner.Cancel(); RebuildTab(); });
            y += 40f;

            Section(c, "Other exporters", y, w); y += 24f;
            Text info = Ui.Label("OtherInfo", c,
                "Weight Studio's .vbx / VRM exports read the live materials, so your edits are already in them. " +
                "Its Unity window, and the Warudo Importer's exporter, read this model's saved edits from: " +
                (string.IsNullOrEmpty(avatarKey) ? "(no avatar)" : RecipePath), 8, y, w - 16, 56, 11, TextAnchor.UpperLeft);
            info.color = Ui.TextDim;
            y += 60f;
            Ui.SetContentHeight(c, y);
        }

        /// <summary>A folder a file dialog can open at; never throws (an empty path used to crash Browse).</summary>
        static string StartDir(params string[] hints)
        {
            foreach (string h in hints)
            {
                if (string.IsNullOrEmpty(h)) continue;
                try
                {
                    if (Directory.Exists(h)) return h;
                    string d = Path.GetDirectoryName(h);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
                }
                catch { }
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        /// <summary>VNyan only tells us the avatar's FILE NAME; remember where the user found it.</summary>
        void RememberAvatarPath(string full)
        {
            string n = Path.GetFileName(full);
            cfg.avatarPaths.RemoveAll(delegate (NamedString x) { return x == null || string.Equals(x.n, n, StringComparison.OrdinalIgnoreCase); });
            cfg.avatarPaths.Add(new NamedString(n, full));
        }

        /// <summary>Turns a bare file name into a full path: remembered picks, then the export folders.</summary>
        string ResolveAvatarFile(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return "";
            try
            {
                if (Path.IsPathRooted(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;
                string n = Path.GetFileName(nameOrPath);
                foreach (NamedString x in cfg.avatarPaths)
                    if (x != null && string.Equals(x.n, n, StringComparison.OrdinalIgnoreCase) && File.Exists(x.v)) return x.v;
                foreach (string dir in new[] { cfg.exportOut, StartDir(cfg.exportVsf) })
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string c = Path.Combine(dir, n);
                    if (File.Exists(c)) { RememberAvatarPath(c); return c; }
                }
            }
            catch { }
            return "";
        }

        string DefaultOutDir()
        {
            if (cfg.exportSourceKind != "prefab" && !string.IsNullOrEmpty(cfg.exportVsf))
            {
                try { return Path.GetDirectoryName(cfg.exportVsf); } catch { }
            }
            return Path.Combine(ShaderCatalog.Dir, "Exports");
        }

        string DefaultOutName()
        {
            string b = cfg.exportSourceKind == "prefab" ? Path.GetFileNameWithoutExtension(cfg.exportPrefab ?? "") : Path.GetFileNameWithoutExtension(cfg.exportVsf ?? "");
            if (string.IsNullOrEmpty(b)) b = string.IsNullOrEmpty(avatarKey) ? "avatar" : avatarKey;
            return b + "_edited.vsfavatar";
        }

        void StartExport()
        {
            if (runner.Busy) { SetStatus("A job is already running."); return; }
            if (boundAvatar == null) { runner.Log("Load the avatar in VNyan first."); RebuildTab(); return; }
            SaveRecipe();
            SaveConfig();
            bool fromPrefab = cfg.exportSourceKind == "prefab";
            string project = fromPrefab ? cfg.exportProject : WorkProject;
            string source = fromPrefab ? cfg.exportPrefab : cfg.exportVsf;
            if (fromPrefab && (!UnityDriver.IsUnityProject(project) || string.IsNullOrEmpty(source)))
            { runner.Log("Pick the avatar's Unity project and its prefab first."); RebuildTab(); return; }
            if (!fromPrefab && (string.IsNullOrEmpty(source) || !File.Exists(source)))
            { runner.Log("The avatar's .vsfavatar file was not found; Browse to it."); RebuildTab(); return; }

            string exe = PickEditor(project);
            if (exe == null || !CheckNotOpen(project)) { RebuildTab(); return; }

            string outDir = string.IsNullOrEmpty(cfg.exportOut) ? DefaultOutDir() : cfg.exportOut;
            string name = string.IsNullOrEmpty(cfg.exportName) ? DefaultOutName() : cfg.exportName;
            if (!name.EndsWith(".vsfavatar", StringComparison.OrdinalIgnoreCase)) name += ".vsfavatar";
            string outPath = Path.Combine(outDir, name);
            if (!fromPrefab && string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            { runner.Log("The output would overwrite the source file VNyan has loaded; pick another name."); RebuildTab(); return; }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string jobDir = Path.Combine(UnityDriver.JobsDir, "export_" + stamp);
            Directory.CreateDirectory(jobDir);
            string live = "";
            if (cfg.applyLiveWeights)
            {
                var n = new List<string>();
                try
                {
                    live = Path.Combine(jobDir, "live.vbx");
                    int count = LiveSnapshot.Write(live, boundAvatar, n);
                    runner.Log("Live weights snapshot: " + count + " mesh(es).");
                }
                catch (Exception e) { runner.Log("Live weights snapshot failed (" + e.Message + "); exporting without it."); live = ""; }
                foreach (string s in n) runner.Log(s);
            }

            if (fromPrefab)
            {
                runner.Enqueue(new UnityStep
                {
                    title = "Installing the Material Studio editor scripts", project = project,
                    work = delegate (List<string> n) { return UnityDriver.InstallToolset(project, n); }
                });
            }
            else if (!QueuePrepare(project, exe, true)) { RebuildTab(); return; }


            var job = new MSJob
            {
                kind = "export",
                resultPath = Path.Combine(jobDir, "result.json"),
                sourceKind = fromPrefab ? "prefab" : "vsfavatar",
                sourcePath = source,
                recipePath = File.Exists(RecipePath) ? RecipePath : "",
                recipeDir = RecipeTexDir,
                liveVbxPath = live,
                outPath = outPath,
                relockPoiyomi = cfg.relockPoiyomi,
                applyLiveWeights = cfg.applyLiveWeights && live.Length > 0,
            };
            string jobPath = Path.Combine(jobDir, "job.json");
            File.WriteAllText(jobPath, JsonIO.ToJson(job, true));
            SetJob("Exporting " + name, "Unity rebuilds the avatar with your edits baked in; you can keep editing", "saved to " + outPath);
            runner.Enqueue(new UnityStep
            {
                title = "Exporting " + name,
                exe = exe, project = project, graphics = true,
                extraArgs = "-executeMethod MaterialStudio.EditorTools.MSBatch.Run -msjob \"" + jobPath + "\"",
                logPath = Path.Combine(jobDir, "unity.log"),
                resultPath = job.resultPath,
                onDone = delegate (UnityStep s, MSResult r)
                {
                    if (!r.ok) return;
                    string produced = string.IsNullOrEmpty(r.output) ? outPath : r.output;
                    runner.Log("Wrote " + produced);
                    if (cfg.loadAfterExport) LoadIntoVNyan(produced);
                }
            });
            SetStatus("Exporting in the background...");
            RebuildTab();
        }

        /// <summary>Loads a .vsfavatar through VNyan's own loader (unmangled UiManager methods).</summary>
        void LoadIntoVNyan(string path)
        {
            try
            {
                Type t = Type.GetType("UiManager, Assembly-CSharp");
                if (t == null) { runner.Log("VNyan's loader was not found; load the file with Load Avatar."); return; }
                UnityEngine.Object ui = FindObjectOfType(t);
                MethodInfo m = t.GetMethod("LoadAvatarDelayed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)
                            ?? t.GetMethod("LoadVSFAvatar", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (ui == null || m == null) { runner.Log("VNyan's loader was not found; load the file with Load Avatar."); return; }
                m.Invoke(ui, new object[] { path });
                runner.Log("Asked VNyan to load the new avatar.");
            }
            catch (Exception e) { runner.Log("Loading in VNyan failed: " + e.Message); }
        }
    }
}
