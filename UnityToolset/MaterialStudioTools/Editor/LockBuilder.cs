using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// "Lock &amp; optimize" for materials that live in VNyan. A running player cannot run
    /// Poiyomi's optimizer (it writes and compiles a new shader), so each live material is
    /// rebuilt here on its unlocked shader with the same values, locked by Poiyomi's own
    /// ShaderOptimizer (via VsfExporter.LockPoiyomi), and the locked materials go into a bundle
    /// the plugin loads. VNyan then swaps each live material onto its locked shader, keeping
    /// its own textures.
    /// </summary>
    public static class LockBuilder
    {
        const string Root = "Assets/MaterialStudioTools_Lock";

        public static void Build(MSJob job, MSResult res)
        {
            if (job.locks == null || job.locks.Count == 0) { res.notes.Add("Nothing to lock."); return; }
            string dir = Root + "/" + MSBatch.Sanitize(job.bundleName);
            if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            MSBatch.EnsureFolder(dir);

            // A filled texture slot only has to be "filled" for the lock to keep that feature.
            var dummy = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            dummy.SetPixels32(px);
            dummy.Apply();
            AssetDatabase.CreateAsset(dummy, dir + "/filled.asset");

            var holder = new GameObject("MaterialStudioLockHolder");
            var mats = new List<Material>();
            var ids = new List<string>();
            try
            {
                foreach (LockRequest req in job.locks)
                {
                    Shader s = ShaderBundleBuilder.FindShader(req.shader);
                    if (s == null) { res.notes.Add("Not in the Unity project: " + req.shader + " (add your Poiyomi package on Packages)."); continue; }
                    var m = new Material(s) { name = "lock_" + mats.Count };
                    foreach (NamedFloat f in req.floats) if (m.HasProperty(f.n)) m.SetFloat(f.n, f.v);
                    foreach (NamedColor c in req.colors) if (m.HasProperty(c.n)) m.SetColor(c.n, c.v);
                    foreach (NamedVector v in req.vectors) if (m.HasProperty(v.n)) m.SetVector(v.n, v.v);
                    foreach (TexEdit t in req.textures)
                    {
                        if (!m.HasProperty(t.n)) continue;
                        m.SetTexture(t.n, t.src == "has" ? dummy : null);
                        if (t.stSet) { m.SetTextureScale(t.n, t.scale); m.SetTextureOffset(t.n, t.offset); }
                    }
                    foreach (NamedString tg in req.tags) if (!string.IsNullOrEmpty(tg.n)) m.SetOverrideTag(tg.n, tg.v ?? "");
                    m.shaderKeywords = req.keywords.ToArray();
                    if (m.HasProperty("_ShaderOptimizerEnabled")) m.SetFloat("_ShaderOptimizerEnabled", 0f);
                    m.renderQueue = req.renderQueue;
                    AssetDatabase.CreateAsset(m, dir + "/" + m.name + ".mat");
                    mats.Add(m);
                    ids.Add(req.id);
                }
                if (mats.Count == 0) { res.ok = false; res.message = "None of the materials could be rebuilt for locking."; return; }

                // VsfExporter.LockPoiyomi works on the materials under a GameObject's renderers.
                var mr = holder.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats.ToArray();
                VsfExporter.LockPoiyomi(holder, res.notes, dir);
                AssetDatabase.SaveAssets();

                var manifest = new LockManifest { bundleFile = job.bundleName, builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
                var names = new List<string>();
                for (int i = 0; i < mats.Count; i++)
                {
                    Material m = mats[i];
                    if (m.shader == null || !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal))
                    {
                        res.notes.Add("Poiyomi did not lock material " + ids[i] + "; it stays unlocked.");
                        continue;
                    }
                    manifest.entries.Add(new LockedEntry { id = ids[i], material = m.name, shader = m.shader.name });
                    names.Add(AssetDatabase.GetAssetPath(m));
                }
                if (names.Count == 0) { res.ok = false; res.message = "Poiyomi's optimizer did not lock anything (see the Unity log)."; return; }

                var build = new AssetBundleBuild { assetBundleName = job.bundleName, assetNames = names.ToArray() };
                string tmp = Path.Combine(Path.GetTempPath(), "MaterialStudioLockBuild");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                AssetBundleManifest abm = BuildPipeline.BuildAssetBundles(tmp, new[] { build },
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows);
                string built = Path.Combine(tmp, job.bundleName);
                if (abm == null || !File.Exists(built)) { res.ok = false; res.message = "Unity did not produce the locked-shader bundle."; return; }
                Directory.CreateDirectory(job.outDir);
                string dst = Path.Combine(job.outDir, job.bundleName);
                File.Copy(built, dst, true);
                File.WriteAllText(dst + ".json", JsonUtility.ToJson(manifest, false));
                res.output = job.bundleName;
                res.notes.Add("Locked and optimised " + manifest.entries.Count + " Poiyomi material(s).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
                if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            }
        }
    }
}
