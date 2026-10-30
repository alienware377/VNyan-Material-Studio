using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Batch-mode entry point the VNyan plugin calls:
    ///   Unity.exe -batchmode -quit -projectPath P -executeMethod MaterialStudio.EditorTools.MSBatch.Run -msjob job.json
    /// The job says what to do ("catalog", "shaders", "export"); the result goes to job.resultPath.
    /// </summary>
    public static class MSBatch
    {
        public static void Run()
        {
            var res = new MSResult();
            MSJob job = null;
            try
            {
                string jobPath = Arg("-msjob");
                if (string.IsNullOrEmpty(jobPath) || !File.Exists(jobPath)) throw new Exception("No -msjob file given.");
                job = JsonUtility.FromJson<MSJob>(File.ReadAllText(jobPath));
                if (job == null) throw new Exception("The job file could not be read.");
                Log("job '" + job.kind + "' on Unity " + Application.unityVersion);
                res.ok = true;
                string kind = " " + job.kind + " ";
                if (kind.Contains(" catalog ")) ShaderBundleBuilder.WriteCatalog(job.catalogPath, res);
                if (res.ok && kind.Contains(" shaders ")) ShaderBundleBuilder.Build(job, res);
                if (res.ok && kind.Contains(" export ")) VsfExporter.Export(job, res);
                if (res.ok && kind.Contains(" describe ")) Describe(job.sourcePath, res);
                if (res.ok && kind.Contains(" lock ")) LockBuilder.Build(job, res);
            }
            catch (Exception e)
            {
                res.ok = false;
                res.message = e.GetType().Name + ": " + e.Message;
                Debug.LogError("[MS] " + e);
            }
            try
            {
                if (job != null && !string.IsNullOrEmpty(job.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(job.resultPath));
                    File.WriteAllText(job.resultPath, JsonUtility.ToJson(res, true));
                }
            }
            catch (Exception e) { Debug.LogError("[MS] could not write result: " + e.Message); }
            Log(res.ok ? "OK " + res.output : "FAILED " + res.message);
            EditorApplication.Exit(res.ok ? 0 : 1);
        }

        public static void Log(string s) { Debug.Log("[MS] " + s); }

        /// <summary>Diagnostics: list every renderer slot, material and shader in a .vsfavatar.</summary>
        static void Describe(string path, MSResult res)
        {
            AssetBundle.UnloadAllAssetBundles(true);
            AssetBundle b = AssetBundle.LoadFromFile(path);
            if (b == null) { res.ok = false; res.message = "Cannot open " + path; return; }
            try
            {
                GameObject prefab = b.LoadAsset<GameObject>("VSFAvatar");
                if (prefab == null) { res.ok = false; res.message = "No VSFAvatar inside."; return; }
                foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] ms = r.sharedMaterials;
                    for (int i = 0; i < ms.Length; i++)
                        res.notes.Add(RecipePaths.PathOf(r.transform, prefab.transform) + "|" + i + "|" + (ms[i] != null ? ms[i].name : "null") + "|" +
                                      (ms[i] != null && ms[i].shader != null ? ms[i].shader.name : "") + "|" +
                                      (ms[i] != null ? ms[i].GetTag("OriginalShader", false, "") : "") + "|" +
                                      (ms[i] != null && ms[i].HasProperty("_Color") ? ms[i].GetColor("_Color").ToString() : "-"));
                }
            }
            finally { b.Unload(true); }
        }

        static string Arg(string name)
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }

        public static void EnsureFolder(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// A free asset path whose file name IS <paramref name="name"/>, so CreateAsset keeps the
        /// object's name (it renames a main asset to its file name). VNyan plugins find meshes
        /// and materials by name: "Mesh.008.baked" coming out as "Mesh_008_baked" broke them.
        /// Clashing names go in numbered subfolders instead of getting a " 1" suffix.
        /// </summary>
        public static string NamedPath(string folder, string name, string ext)
        {
            EnsureFolder(folder);
            if (!FileSafe(name)) return AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Sanitize(name) + ext);
            string p = folder + "/" + name + ext;
            for (int k = 1; File.Exists(p) || Directory.Exists(p); k++)
            {
                EnsureFolder(folder + "/" + k);
                p = folder + "/" + k + "/" + name + ext;
            }
            return p;
        }

        static bool FileSafe(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 120) return false;
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            // Windows drops trailing dots/spaces; Unity hides names starting with '.' or ending with '~'
            if (n[0] == '.' || n[0] == ' ' || n.EndsWith(".", StringComparison.Ordinal) || n.EndsWith(" ", StringComparison.Ordinal) || n.EndsWith("~", StringComparison.Ordinal)) return false;
            string u = n.ToUpperInvariant();
            int dot = u.IndexOf('.');
            string stem = dot >= 0 ? u.Substring(0, dot) : u;
            foreach (string r in new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" })
                if (stem == r) return false;
            return true;
        }

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unnamed";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
