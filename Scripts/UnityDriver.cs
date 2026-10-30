using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MaterialStudio
{
    /// <summary>
    /// Drives a Unity EDITOR in batch mode. A running player cannot compile shaders or
    /// build AssetBundles, so both "add shaders from a .unitypackage" and "export a
    /// .vsfavatar" go through the editor, exactly like the Warudo Importer's exporter.
    /// </summary>
    public static class UnityDriver
    {
        public static string PluginDir
        {
            get
            {
                string root = Path.GetDirectoryName(Application.dataPath);   // <VNyan>/VNyan_Data -> <VNyan>
                return Path.Combine(root, Path.Combine("Items", Path.Combine("Assemblies", "MaterialStudio")));
            }
        }

        public static string ToolsetSource { get { return Path.Combine(PluginDir, Path.Combine("UnityToolset", "MaterialStudioTools")); } }
        public static bool ToolsetAvailable { get { return File.Exists(Path.Combine(ToolsetSource, Path.Combine("Editor", "MSBatch.cs"))); } }

        public static string DefaultWorkspace { get { return Path.Combine(ShaderCatalog.Dir, "Workspace"); } }
        public static string JobsDir { get { return Path.Combine(ShaderCatalog.Dir, "Jobs"); } }

        public static bool IsUnityProject(string dir)
        {
            return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Assets"))
                && Directory.Exists(Path.Combine(dir, "ProjectSettings"));
        }

        public static string ProjectVersion(string dir)
        {
            try
            {
                string p = Path.Combine(dir, Path.Combine("ProjectSettings", "ProjectVersion.txt"));
                if (!File.Exists(p)) return null;
                foreach (string line in File.ReadAllLines(p))
                    if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                        return line.Substring("m_EditorVersion:".Length).Trim();
            }
            catch { }
            return null;
        }

        /// <summary>A project open in the Unity editor holds Temp/UnityLockfile open.</summary>
        public static bool ProjectIsOpen(string dir)
        {
            string lockf = Path.Combine(dir, Path.Combine("Temp", "UnityLockfile"));
            if (!File.Exists(lockf)) return false;
            try { using (new FileStream(lockf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch { return true; }
        }

        /// <summary>Every installed editor: Unity Hub's folders plus the user's override.</summary>
        public static List<string> FindEditors(string userOverride)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(userOverride) && File.Exists(userOverride)) list.Add(userOverride);
            var roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Path.Combine("Unity", Path.Combine("Hub", "Editor"))),
                @"C:\Program Files\Unity\Hub\Editor",
            };
            try
            {
                // Hub's "install location" setting, when the user moved it
                string sec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine("UnityHub", "secondaryInstallPath.json"));
                if (File.Exists(sec))
                {
                    string s = File.ReadAllText(sec).Trim().Trim('"').Replace("\\\\", "\\");
                    if (s.Length > 0) roots.Add(s);
                }
            }
            catch { }
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string d in Directory.GetDirectories(root))
                {
                    string exe = Path.Combine(d, Path.Combine("Editor", "Unity.exe"));
                    if (File.Exists(exe) && !list.Contains(exe)) list.Add(exe);
                }
            }
            return list;
        }

        public static string EditorVersion(string exe)
        {
            // <...>/<version>/Editor/Unity.exe
            try { return Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(exe))); } catch { return ""; }
        }

        /// <summary>The editor matching the project, else the newest of the same major.minor.</summary>
        public static string EditorFor(string projectDir, string userOverride, List<string> notes)
        {
            List<string> eds = FindEditors(userOverride);
            if (!string.IsNullOrEmpty(userOverride) && File.Exists(userOverride)) return userOverride;
            string want = ProjectVersion(projectDir);
            if (want != null)
            {
                foreach (string e in eds) if (EditorVersion(e) == want) return e;
                string mm = MajorMinor(want);
                string best = null;
                foreach (string e in eds)
                    if (MajorMinor(EditorVersion(e)) == mm && (best == null || string.CompareOrdinal(EditorVersion(e), EditorVersion(best)) > 0)) best = e;
                if (best != null)
                {
                    notes.Add("Unity " + want + " is not installed; using " + EditorVersion(best) + ".");
                    return best;
                }
                notes.Add("This project needs Unity " + want + " (or another " + mm + ".x). Install it with Unity Hub, or pick Unity.exe on the Settings tab.");
                return null;
            }
            // new workspace: the VSF SDK editor line, so the export also loads in VSeeFace
            foreach (string e in eds) if (EditorVersion(e).StartsWith("2019.4", StringComparison.Ordinal)) return e;
            return eds.Count > 0 ? eds[0] : null;
        }

        static string MajorMinor(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            string[] p = v.Split('.');
            return p.Length >= 2 ? p[0] + "." + p[1] : v;
        }

        // ------------------------------------------------------------ workspace

        /// <summary>
        /// Makes the private workspace by copying the SDK side of an existing VSF SDK project
        /// (ProjectSettings, Packages manifest and the ticked Assets folders, .meta files
        /// included so script GUIDs match and the avatar's VRM components bind).
        /// </summary>
        public static bool SeedWorkspace(string seedProject, string workspace, IList<string> assetFolders, List<string> notes)
        {
            try
            {
                if (!IsUnityProject(seedProject)) { notes.Add("The seed folder is not a Unity project."); return false; }
                Directory.CreateDirectory(workspace);
                CopyTree(Path.Combine(seedProject, "ProjectSettings"), Path.Combine(workspace, "ProjectSettings"), null);
                string man = Path.Combine(seedProject, Path.Combine("Packages", "manifest.json"));
                if (File.Exists(man))
                {
                    Directory.CreateDirectory(Path.Combine(workspace, "Packages"));
                    File.Copy(man, Path.Combine(workspace, Path.Combine("Packages", "manifest.json")), true);
                }
                string lockf = Path.Combine(seedProject, Path.Combine("Packages", "packages-lock.json"));
                if (File.Exists(lockf)) File.Copy(lockf, Path.Combine(workspace, Path.Combine("Packages", "packages-lock.json")), true);
                // local file: packages next to the seed project would break; report them
                if (File.Exists(man) && File.ReadAllText(man).IndexOf("\"file:", StringComparison.Ordinal) >= 0)
                    notes.Add("The seed project uses local (file:) packages; they may not resolve in the workspace.");
                Directory.CreateDirectory(Path.Combine(workspace, "Assets"));
                foreach (string f in assetFolders)
                {
                    string src = Path.Combine(Path.Combine(seedProject, "Assets"), f);
                    if (!Directory.Exists(src)) continue;
                    CopyTree(src, Path.Combine(Path.Combine(workspace, "Assets"), f), null);
                    if (File.Exists(src + ".meta")) File.Copy(src + ".meta", Path.Combine(Path.Combine(workspace, "Assets"), f + ".meta"), true);
                }
                notes.Add("Workspace seeded from " + seedProject + " (" + assetFolders.Count + " Assets folders).");
                return true;
            }
            catch (Exception e) { notes.Add("Seeding the workspace failed: " + e.Message); return false; }
        }

        public static void CopyTree(string from, string to, string[] onlyExt)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from))
            {
                if (onlyExt != null && Array.IndexOf(onlyExt, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            }
            foreach (string d in Directory.GetDirectories(from))
                CopyTree(d, Path.Combine(to, Path.GetFileName(d)), onlyExt);
        }

        /// <summary>Copies the editor toolset (scripts only) into the project.</summary>
        public static bool InstallToolset(string project, List<string> notes)
        {
            if (!ToolsetAvailable) { notes.Add("The editor toolset is missing (expected " + ToolsetSource + "). Reinstall Material Studio."); return false; }
            try
            {
                string dst = Path.Combine(Path.Combine(project, "Assets"), "MaterialStudioTools");
                CopyTree(ToolsetSource, dst, new[] { ".cs", ".asmdef", ".meta", ".shader" });
                return true;
            }
            catch (Exception e) { notes.Add("Could not copy the toolset into " + project + ": " + e.Message); return false; }
        }

        /// <summary>A small top-level Assets folder list for the seed checklist.</summary>
        public static List<string> AssetFolders(string project)
        {
            var l = new List<string>();
            try
            {
                foreach (string d in Directory.GetDirectories(Path.Combine(project, "Assets")))
                    l.Add(Path.GetFileName(d));
            }
            catch { }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        /// <summary>Folders a VSF SDK export needs: SDK, UniVRM, and the usual avatar runtimes.</summary>
        public static bool IsDefaultSeedFolder(string name)
        {
            string l = name.ToLowerInvariant();
            return l == "vrm" || l == "vrmshaders" || l == "unigltf" || l == "vsf sdk" || l.Contains("vsf_sdk")
                || l.Contains("dynamicbone") || l.Contains("magicacloth") || l.Contains("poiyomi") || l.Contains("liltoon")
                || l == "vrm10" || l == "univrm";
        }
    }

    /// <summary>One batch-mode editor run.</summary>
    public class UnityStep
    {
        public string title;
        public string exe;
        public string project;
        public string extraArgs = "";      // after -projectPath
        public string logPath;
        public string resultPath;          // MSResult JSON, when the step runs MSBatch
        public bool graphics = true;       // texture read-back needs a GPU device
        public bool createProject;         // -createProject instead of -projectPath
        public Action<UnityStep, MSResult> onDone;

        // A non-Unity step (file copies) runs on a worker thread so VNyan never stalls.
        public Func<List<string>, bool> work;
        public volatile bool workDone;
        public bool workOk;
        public readonly List<string> workNotes = new List<string>();

        public Process process;
        public DateTime started;
        public int exitCode = -1;
        public MSResult result;

        public string Args()
        {
            var sb = new StringBuilder();
            sb.Append("-batchmode -quit");
            if (!graphics) sb.Append(" -nographics");
            sb.Append(createProject ? " -createProject \"" : " -projectPath \"").Append(project.TrimEnd('\\', '/')).Append('"');
            if (!string.IsNullOrEmpty(extraArgs)) sb.Append(' ').Append(extraArgs);
            sb.Append(" -logFile \"").Append(logPath).Append('"');
            return sb.ToString();
        }

        public string Tail(int lines)
        {
            try
            {
                if (!File.Exists(logPath)) return "";
                var kept = new List<string>();
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.IndexOf("[MS]", StringComparison.Ordinal) < 0 && line.IndexOf("error", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        kept.Add(line.Length > 220 ? line.Substring(0, 220) : line);
                        if (kept.Count > lines) kept.RemoveAt(0);
                    }
                }
                return string.Join("\n", kept.ToArray());
            }
            catch { return ""; }
        }
    }

    /// <summary>Runs steps one after another; the plugin polls <see cref="Tick"/> every frame.</summary>
    public class UnityRunner
    {
        readonly Queue<UnityStep> queue = new Queue<UnityStep>();
        public UnityStep current;
        public readonly List<string> log = new List<string>();
        public string status = "idle";
        public bool Busy { get { return current != null || queue.Count > 0; } }
        public Action onFinishedAll;

        public void Enqueue(UnityStep s) { queue.Enqueue(s); }

        public void Cancel()
        {
            queue.Clear();
            if (current != null && current.process != null && !current.process.HasExited)
            {
                try { current.process.Kill(); } catch { }
            }
            Log("Cancelled.");
            current = null;
            status = "cancelled";
        }

        public void Log(string s)
        {
            log.Add(s);
            if (log.Count > 200) log.RemoveAt(0);
            Debug.Log("[MaterialStudio] " + s);
        }

        public void Tick()
        {
            if (current == null)
            {
                if (queue.Count == 0) return;
                current = queue.Dequeue();
                Start(current);
                return;
            }
            if (current.work != null)
            {
                if (!current.workDone) { status = current.title + " (" + (int)(DateTime.Now - current.started).TotalSeconds + " s)"; return; }
                lock (current.workNotes) foreach (string n in current.workNotes) Log("  " + n);
                current.exitCode = current.workOk ? 0 : 1;
                Finish(true);
                return;
            }
            if (current.process == null) { Finish(false); return; }
            if (!current.process.HasExited)
            {
                status = current.title + " (" + (int)(DateTime.Now - current.started).TotalSeconds + " s)";
                return;
            }
            current.exitCode = current.process.ExitCode;
            Finish(true);
        }

        void Start(UnityStep s)
        {
            if (s.work != null)
            {
                s.started = DateTime.Now;
                Log(s.title + "...");
                UnityStep st = s;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    var tmp = new List<string>();
                    try { st.workOk = st.work(tmp); }
                    catch (Exception e) { tmp.Add("Failed: " + e.Message); st.workOk = false; }
                    lock (st.workNotes) st.workNotes.AddRange(tmp);
                    st.workDone = true;
                });
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.logPath));
                if (File.Exists(s.logPath)) File.Delete(s.logPath);
                if (!string.IsNullOrEmpty(s.resultPath) && File.Exists(s.resultPath)) File.Delete(s.resultPath);
            }
            catch { }
            var psi = new ProcessStartInfo(s.exe, s.Args());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (Directory.Exists(s.project)) psi.WorkingDirectory = s.project;
            s.started = DateTime.Now;
            Log(s.title + "... (Unity " + UnityDriver.EditorVersion(s.exe) + ")");
            try { s.process = Process.Start(psi); }
            catch (Exception e) { Log("Could not start Unity: " + e.Message); s.process = null; }
        }

        void Finish(bool ran)
        {
            UnityStep s = current;
            current = null;
            if (ran && !string.IsNullOrEmpty(s.resultPath) && File.Exists(s.resultPath))
            {
                try { s.result = JsonIO.FromJson<MSResult>(File.ReadAllText(s.resultPath)); } catch { }
            }
            bool ok = ran && (s.result != null ? s.result.ok : s.exitCode == 0);
            if (s.result != null)
            {
                foreach (string n in s.result.notes) Log("  " + n);
                if (!string.IsNullOrEmpty(s.result.message)) Log("  " + s.result.message);
            }
            if (!ok)
            {
                Log(s.title + " FAILED (exit " + s.exitCode + "). Unity log: " + s.logPath);
                string tail = s.Tail(8);
                if (tail.Length > 0) Log(tail);
                queue.Clear();     // later steps depend on this one
                status = "failed: " + s.title;
            }
            else
            {
                Log(s.title + " done in " + (int)(DateTime.Now - s.started).TotalSeconds + " s.");
                status = queue.Count > 0 ? "working" : "done";
            }
            try { if (s.onDone != null) s.onDone(s, ok ? (s.result ?? new MSResult { ok = true }) : (s.result ?? new MSResult { ok = false })); }
            catch (Exception e) { Log("After-step handler failed: " + e.Message); }
            if (queue.Count == 0 && current == null && onFinishedAll != null) onFinishedAll();
        }
    }
}
