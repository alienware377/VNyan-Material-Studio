using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Applies a Material Studio recipe (the live edits made in VNyan) to an avatar in the
    /// Unity editor. Used by the Material Studio exporter, and callable by any other
    /// exporter through reflection, with no compile-time dependency:
    ///
    ///   Type t = (find "MaterialStudio.EditorTools.RecipeApplier" in loaded assemblies);
    ///   int n = (int)t.GetMethod("ApplyRecipeFile").Invoke(null,
    ///       new object[] { avatarRoot, recipeJsonPath, "Assets/SomeFolder", true, notesList });
    ///
    /// With copyMaterials = true every material it touches is first copied into assetDir,
    /// so the project's own materials are never modified.
    /// </summary>
    public static class RecipeApplier
    {
        public static int ApplyRecipeFile(GameObject root, string recipePath, string assetDir, bool copyMaterials, List<string> notes)
        {
            if (notes == null) notes = new List<string>();
            if (root == null || string.IsNullOrEmpty(recipePath) || !File.Exists(recipePath)) { notes.Add("No recipe to apply."); return 0; }
            Recipe r = Recipe.FromJson(File.ReadAllText(recipePath));
            if (r == null) { notes.Add("The recipe file could not be read."); return 0; }
            string texDir = Path.Combine(Path.GetDirectoryName(recipePath), Path.GetFileNameWithoutExtension(recipePath));
            return Apply(root, r, texDir, assetDir, copyMaterials, null, notes);
        }

        /// <summary>
        /// <paramref name="texByOriginalName"/> maps texture names as they were in the
        /// source bundle to their project copies (from <see cref="AssetPersist"/>), for
        /// "avatar:" texture sources. May be null.
        /// </summary>
        public static int Apply(GameObject root, Recipe r, string texDir, string assetDir, bool copyMaterials,
                                Dictionary<string, Texture> texByOriginalName, List<string> notes)
        {
            MSBatch.EnsureFolder(assetDir + "/Materials");
            MSBatch.EnsureFolder(assetDir + "/Textures");
            var copies = new Dictionary<Material, Material>();
            var fileTex = new Dictionary<string, Texture>();
            int applied = 0;

            Func<Material, Material> own = delegate (Material m)
            {
                if (m == null) return null;
                if (!copyMaterials) return m;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.StartsWith(assetDir + "/", StringComparison.Ordinal)) return m;
                Material c;
                if (copies.TryGetValue(m, out c)) return c;
                c = new Material(m) { name = m.name };
                AssetDatabase.CreateAsset(c, MSBatch.NamedPath(assetDir + "/Materials", m.name, ".mat"));
                copies[m] = c;
                ReplaceEverywhere(root, m, c);
                return c;
            };

            // Unique copies must start from the material as it SHIPPED, not after another
            // entry's edits landed on it, so freeze every source first.
            var shipped = new Dictionary<Material, MatSnapshot>();
            foreach (MatEdit e in r.materials)
            {
                if (e == null || e.slots == null || e.slots.Count == 0) continue;
                Material src = MaterialAt(root, e.slots[0]);
                if (src != null && !shipped.ContainsKey(src)) shipped[src] = MatSnapshot.Capture(src);
            }

            foreach (MatEdit e in r.materials)
            {
                if (e == null || e.slots == null || e.slots.Count == 0) continue;
                Material m = MaterialAt(root, e.slots[0]);
                if (m == null) { notes.Add("No material at " + e.slots[0] + " in this avatar; skipped."); continue; }
                if (e.unique)
                {
                    // a separate copy for exactly these slots
                    Material u = new Material(m) { name = m.name + " (unique)" };
                    MatSnapshot snap;
                    Material key = m;
                    foreach (var kv in copies) if (kv.Value == m) key = kv.Key;
                    if (shipped.TryGetValue(key, out snap)) snap.Restore(u);
                    AssetDatabase.CreateAsset(u, MSBatch.NamedPath(assetDir + "/Materials", u.name, ".mat"));
                    foreach (SlotRef s in e.slots) SetMaterialAt(root, s, u);
                    m = u;
                }
                else m = own(m);

                RecipeEngine.Apply(m, e,
                    delegate (string sn) { return ShaderBundleBuilder.FindShader(sn); },
                    delegate (TexEdit te) { return ResolveTexture(te, root, texDir, assetDir, texByOriginalName, fileTex, m, notes); },
                    notes);
                EditorUtility.SetDirty(m);
                applied++;
            }
            AssetDatabase.SaveAssets();
            notes.Add("Applied VNyan edits to " + applied + " material(s).");
            return applied;
        }

        /// <summary>Copies every material on the avatar into assetDir (so nothing original is edited).</summary>
        public static int CopyAllMaterials(GameObject root, string assetDir)
        {
            MSBatch.EnsureFolder(assetDir + "/Materials");
            var copies = new Dictionary<Material, Material>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] ms = r.sharedMaterials;
                bool hit = false;
                for (int i = 0; i < ms.Length; i++)
                {
                    Material m = ms[i];
                    if (m == null) continue;
                    string p = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(p) && p.StartsWith(assetDir + "/", StringComparison.Ordinal)) continue;
                    Material c;
                    if (!copies.TryGetValue(m, out c))
                    {
                        c = new Material(m) { name = m.name };
                        AssetDatabase.CreateAsset(c, MSBatch.NamedPath(assetDir + "/Materials", m.name, ".mat"));
                        copies[m] = c;
                    }
                    ms[i] = c;
                    hit = true;
                }
                if (hit) r.sharedMaterials = ms;
            }
            AssetDatabase.SaveAssets();
            return copies.Count;
        }

        static Texture ResolveTexture(TexEdit te, GameObject root, string texDir, string assetDir,
                                      Dictionary<string, Texture> byName, Dictionary<string, Texture> fileTex, Material m, List<string> notes)
        {
            if (te.src.StartsWith("avatar:", StringComparison.Ordinal))
            {
                string n = te.src.Substring(7);
                Texture t;
                if (byName != null && byName.TryGetValue(n, out t)) return t;
                // search the avatar's own materials for a texture with that name
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    foreach (Material mm in r.sharedMaterials)
                    {
                        if (mm == null || mm.shader == null) continue;
                        Shader sh = mm.shader;
                        for (int i = 0; i < sh.GetPropertyCount(); i++)
                        {
                            if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                            Texture x = mm.GetTexture(sh.GetPropertyName(i));
                            if (x != null && x.name == n) return x;
                        }
                    }
                return null;
            }
            if (te.src.StartsWith("file:", StringComparison.Ordinal))
            {
                string rel = te.src.Substring(5);
                Texture t;
                if (fileTex.TryGetValue(rel, out t)) return t;
                string full = Path.Combine(texDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return null;
                string dst = AssetDatabase.GenerateUniqueAssetPath(assetDir + "/Textures/" + Path.GetFileName(full));
                File.Copy(full, dst, true);
                AssetDatabase.ImportAsset(dst);
                var imp = AssetImporter.GetAtPath(dst) as TextureImporter;
                bool normal = false;
                if (m != null && m.shader != null)
                {
                    int idx = m.shader.FindPropertyIndex(te.n);
                    if (idx >= 0) normal = (m.shader.GetPropertyFlags(idx) & UnityEngine.Rendering.ShaderPropertyFlags.Normal) != 0;
                }
                if (imp != null)
                {
                    imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    imp.sRGBTexture = !te.linear && !normal;
                    imp.maxTextureSize = 8192;
                    imp.SaveAndReimport();
                }
                t = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
                fileTex[rel] = t;
                return t;
            }
            return null;
        }

        public static Material MaterialAt(GameObject root, SlotRef sr)
        {
            Transform t = RecipePaths.Resolve(root.transform, sr.path);
            Renderer r = t != null ? t.GetComponent<Renderer>() : null;
            if (r == null) return null;
            Material[] ms = r.sharedMaterials;
            return sr.slot >= 0 && sr.slot < ms.Length ? ms[sr.slot] : null;
        }

        static void SetMaterialAt(GameObject root, SlotRef sr, Material m)
        {
            Transform t = RecipePaths.Resolve(root.transform, sr.path);
            Renderer r = t != null ? t.GetComponent<Renderer>() : null;
            if (r == null) return;
            Material[] ms = r.sharedMaterials;
            if (sr.slot < 0 || sr.slot >= ms.Length) return;
            ms[sr.slot] = m;
            r.sharedMaterials = ms;
        }

        static void ReplaceEverywhere(GameObject root, Material from, Material to)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] ms = r.sharedMaterials;
                bool hit = false;
                for (int i = 0; i < ms.Length; i++) if (ms[i] == from) { ms[i] = to; hit = true; }
                if (hit) r.sharedMaterials = ms;
            }
        }
    }
}
