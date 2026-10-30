using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Lists the project's shaders and compiles the requested ones into an AssetBundle the
    /// VNyan plugin loads at runtime. shader_feature variants are only compiled when a
    /// material in the bundle uses them, so the bundle carries one small material per
    /// keyword combination the user's materials need (picked by the shader's own property
    /// drawers, exactly as the material inspector would).
    /// </summary>
    public static class ShaderBundleBuilder
    {
        const string TempRoot = "Assets/MaterialStudioTools_Build";

        public static void WriteCatalog(string path, MSResult res)
        {
            var cat = new WorkspaceCatalog { unityVersion = Application.unityVersion, builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            foreach (string guid in AssetDatabase.FindAssets("t:Shader"))
            {
                string ap = AssetDatabase.GUIDToAssetPath(guid);
                if (!ap.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (ap.StartsWith(TempRoot, StringComparison.Ordinal)) continue;
                Shader s = AssetDatabase.LoadAssetAtPath<Shader>(ap);
                if (s == null || s.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                string[] parts = ap.Split('/');
                cat.shaders.Add(new CatalogShader { name = s.name, assetPath = ap, source = parts.Length > 1 ? parts[1] : "", hasErrors = HasErrors(s) });
            }
            cat.shaders.Sort(delegate (CatalogShader a, CatalogShader b) { return string.CompareOrdinal(a.name, b.name); });
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(cat, false));
            }
            res.notes.Add("The project has " + cat.shaders.Count + " shader(s).");
        }

        static bool HasErrors(Shader s)
        {
            try
            {
                MethodInfo m = typeof(ShaderUtil).GetMethod("ShaderHasError", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Shader) }, null);
                if (m != null) return (bool)m.Invoke(null, new object[] { s });
            }
            catch { }
            return false;
        }

        /// <summary>Keywords the shader declares (internal API, read by reflection; null when unknown).</summary>
        public static HashSet<string> DeclaredKeywords(Shader s)
        {
            var set = new HashSet<string>();
            bool any = false;
            foreach (string n in new[] { "GetShaderGlobalKeywords", "GetShaderLocalKeywords" })
            {
                try
                {
                    MethodInfo m = typeof(ShaderUtil).GetMethod(n, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Shader) }, null);
                    if (m == null) continue;
                    var kws = m.Invoke(null, new object[] { s }) as string[];
                    if (kws == null) continue;
                    any = true;
                    foreach (string k in kws) set.Add(k);
                }
                catch { }
            }
            return any ? set : null;
        }

        public static Shader FindShader(string name)
        {
            Shader s = Shader.Find(name);
            if (s != null) return s;
            foreach (string guid in AssetDatabase.FindAssets("t:Shader"))
            {
                Shader x = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (x != null && x.name == name) return x;
            }
            return null;
        }

        public static void Build(MSJob job, MSResult res)
        {
            if (job.shaders == null || job.shaders.Count == 0) { res.notes.Add("No shaders ticked, nothing to compile."); return; }
            string dir = TempRoot + "/" + MSBatch.Sanitize(job.bundleName);
            if (AssetDatabase.IsValidFolder(TempRoot)) AssetDatabase.DeleteAsset(TempRoot);
            MSBatch.EnsureFolder(dir);

            var assetNames = new List<string>();
            var manifest = new ShaderBundleManifest { bundleFile = job.bundleName, unityVersion = Application.unityVersion, builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            int matIndex = 0;
            foreach (ShaderRequest req in job.shaders)
            {
                if (req == null || string.IsNullOrEmpty(req.name)) continue;
                Shader s = FindShader(req.name);
                if (s == null) { res.notes.Add("Not in this project: " + req.name + " (add its package and build again)."); continue; }
                if (HasErrors(s)) res.notes.Add("Warning: " + req.name + " has compile errors in this Unity version.");
                string sp = AssetDatabase.GetAssetPath(s);
                if (!string.IsNullOrEmpty(sp) && sp.StartsWith("Assets/", StringComparison.Ordinal) && !assetNames.Contains(sp)) assetNames.Add(sp);

                HashSet<string> declared = DeclaredKeywords(s);
                var seen = new HashSet<string>();
                var bs = new BundledShader { name = s.name, source = SourceOf(sp) };

                // 1) defaults  2) each material's numbers through the shader's drawers  3) explicit keyword sets
                var mats = new List<Material>();
                mats.Add(new Material(s));
                foreach (FloatSet fs in req.materials)
                {
                    var m = new Material(s);
                    foreach (NamedFloat nf in fs.f) if (m.HasProperty(nf.n)) m.SetFloat(nf.n, nf.v);
                    try { MaterialEditor.ApplyMaterialPropertyDrawers(m); }
                    catch (Exception e) { res.notes.Add("Property drawers failed on " + s.name + ": " + e.Message); }
                    mats.Add(m);
                }
                foreach (KeywordSet ks in req.variants)
                {
                    var m = new Material(s);
                    m.shaderKeywords = ks.k.ToArray();
                    mats.Add(m);
                }
                foreach (Material m in mats)
                {
                    var kws = new List<string>();
                    foreach (string k in m.shaderKeywords) if (declared == null || declared.Contains(k)) kws.Add(k);
                    var set = new KeywordSet(kws);
                    if (!seen.Add(set.Key())) { UnityEngine.Object.DestroyImmediate(m); continue; }
                    m.shaderKeywords = set.k.ToArray();
                    bs.variants.Add(set);
                    string mp = dir + "/v" + (matIndex++) + ".mat";
                    AssetDatabase.CreateAsset(m, mp);
                    assetNames.Add(mp);
                }
                manifest.shaders.Add(bs);
                MSBatch.Log("queued " + s.name + " with " + bs.variants.Count + " keyword set(s)");
            }
            AssetDatabase.SaveAssets();
            if (manifest.shaders.Count == 0) { res.ok = false; res.message = "None of the requested shaders are in the project."; return; }

            var build = new AssetBundleBuild { assetBundleName = job.bundleName, assetNames = assetNames.ToArray() };
            string tmp = Path.Combine(Path.GetTempPath(), "MaterialStudioShaderBuild");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            AssetBundleManifest abm = BuildPipeline.BuildAssetBundles(tmp, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows);
            string built = Path.Combine(tmp, job.bundleName);
            if (abm == null || !File.Exists(built)) { res.ok = false; res.message = "Unity did not produce the shader bundle (see the Unity log)."; return; }

            Directory.CreateDirectory(job.outDir);
            string dst = Path.Combine(job.outDir, job.bundleName);
            File.Copy(built, dst, true);
            File.WriteAllText(dst + ".json", JsonUtility.ToJson(manifest, false));
            res.output = job.bundleName;
            res.notes.Add("Compiled " + manifest.shaders.Count + " shader(s) into " + job.bundleName + " (" + (new FileInfo(dst).Length / 1024) + " KB).");
            AssetDatabase.DeleteAsset(TempRoot);
        }

        static string SourceOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            string[] p = assetPath.Split('/');
            return p.Length > 1 ? p[1] : "";
        }
    }
}
