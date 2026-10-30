using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Turns an avatar instantiated from an AssetBundle into one made of real project
    /// assets, so it can be built into a new bundle. Bundle objects live in memory only;
    /// without this the new bundle would reference nothing. Meshes are deep-copied
    /// (BoneWeight1, so &gt;4 influences survive), textures are read back from the GPU in
    /// the right colour space, materials are copied and re-pointed at the same-named
    /// shader in this project, and every other referenced asset (VRM objects, avatar,
    /// clips) is copied and re-linked.
    /// </summary>
    public class AssetPersist
    {
        public readonly string dir;
        public readonly List<string> notes;
        public readonly Dictionary<UnityEngine.Object, UnityEngine.Object> map = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
        public readonly Dictionary<string, Texture> textureByOriginalName = new Dictionary<string, Texture>();
        readonly HashSet<Texture> normalUse = new HashSet<Texture>();
        int shaderMisses;
        public readonly List<string> missingShaders = new List<string>();

        public AssetPersist(string assetDir, List<string> notes)
        {
            dir = assetDir;
            this.notes = notes;
            MSBatch.EnsureFolder(dir + "/Meshes");
            MSBatch.EnsureFolder(dir + "/Materials");
            MSBatch.EnsureFolder(dir + "/Textures");
            MSBatch.EnsureFolder(dir + "/Data");
        }

        // NOT AssetDatabase.Contains: it answers true for objects loaded from an AssetBundle,
        // which have no file behind them (measured: Contains=True, path=''). A real project
        // asset is one with a path.
        public static bool IsAsset(UnityEngine.Object o) { return o != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)); }

        public void PersistAll(GameObject root)
        {
            // note which textures are used as normal maps before copying any of them
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials) NoteNormalUse(m);

            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue;
                FixReferences(c);
            }
            notes.Add("Copied into the project: " + Count<Mesh>() + " meshes, " + Count<Material>() + " materials, " + Count<Texture>() + " textures, " +
                      (map.Count - Count<Mesh>() - Count<Material>() - Count<Texture>()) + " other assets.");
            if (shaderMisses > 0)
                notes.Add("WARNING: " + shaderMisses + " material(s) use a shader that is not in this Unity project; add its package (Packages tab) or they will render pink. Nothing was swapped to MToon.");
        }

        int Count<T>() where T : UnityEngine.Object
        {
            int n = 0;
            foreach (var kv in map) if (kv.Value is T) n++;
            return n;
        }

        void NoteNormalUse(Material m)
        {
            if (m == null || m.shader == null) return;
            Shader sh = m.shader;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                string pn = sh.GetPropertyName(i);
                Texture t = m.GetTexture(pn);
                if (t == null) continue;
                string l = pn.ToLowerInvariant();
                if ((sh.GetPropertyFlags(i) & UnityEngine.Rendering.ShaderPropertyFlags.Normal) != 0 || l.Contains("bump") || l.Contains("normal"))
                    normalUse.Add(t);
            }
        }

        /// <summary>Walks a component/asset's serialized references and replaces bundle objects with project copies.</summary>
        public void FixReferences(UnityEngine.Object owner)
        {
            var so = new SerializedObject(owner);
            SerializedProperty p = so.GetIterator();
            bool changed = false;
            while (p.Next(true))
            {
                if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                UnityEngine.Object o = p.objectReferenceValue;
                if (o == null || o is GameObject || o is Component || o is MonoScript) continue;
                if (IsAsset(o)) continue;
                UnityEngine.Object copy = Persist(o);
                if (copy != null && copy != o) { p.objectReferenceValue = copy; changed = true; }
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        public UnityEngine.Object Persist(UnityEngine.Object o)
        {
            if (o == null || IsAsset(o)) return o;
            UnityEngine.Object done;
            if (map.TryGetValue(o, out done)) return done;
            UnityEngine.Object copy = null;
            try
            {
                if (o is Mesh) copy = CopyMesh((Mesh)o);
                else if (o is Material) copy = CopyMaterial((Material)o);
                else if (o is Texture2D) copy = SaveTexture((Texture2D)o);
                else if (o is Shader) copy = ShaderBundleBuilder.FindShader(((Shader)o).name);
                else if (o is Cubemap) copy = SaveCubemap((Cubemap)o);
                else if (o is Texture) { notes.Add("Texture '" + o.name + "' (" + o.GetType().Name + ") cannot be read back; it was left out."); copy = null; }
                else if (o is AudioClip || o is Font) { notes.Add(o.GetType().Name + " '" + o.name + "' cannot be copied out of a bundle; it was left out."); copy = null; }
                else if (o is Avatar) copy = CopyAvatar((Avatar)o);
                else if (o is RuntimeAnimatorController)
                {
                    // An AnimatorController's states live in sub-objects a plain copy loses, so a
                    // half-copied controller would be worse than none. Say so instead.
                    notes.Add("Animator controller '" + o.name + "' cannot be rebuilt from a .vsfavatar; it was left out (VSF custom animations on this avatar will not play). Export from the avatar's Unity project to keep it.");
                    copy = null;
                }
                else copy = CopyGeneric(o);
            }
            catch (Exception e)
            {
                notes.Add("Could not copy " + o.GetType().Name + " '" + o.name + "': " + e.Message);
                copy = null;
            }
            map[o] = copy;
            return copy;
        }

        Mesh CopyMesh(Mesh src)
        {
            Mesh copy = UnityEngine.Object.Instantiate(src);
            copy.name = src.name;
            AssetDatabase.CreateAsset(copy, MSBatch.NamedPath(dir + "/Meshes", src.name, ".asset"));
            map[src] = copy;
            return copy;
        }

        public Material CopyMaterial(Material src)
        {
            Material copy = UnityEngine.Object.Instantiate(src);
            copy.name = src.name;
            // assigning a shader resets the render queue to the shader's default (measured:
            // eye highlight 2951 -> 2000, brows 3000 -> 2000), so carry it over by hand
            int queue = src.renderQueue;
            if (src.shader != null)
            {
                Shader local = ShaderBundleBuilder.FindShader(src.shader.name);
                if (local != null) { copy.shader = local; copy.renderQueue = queue; }
                else
                {
                    // locked Poiyomi: rebind to the shader it was locked from; the exporter locks it again
                    string orig = RecipeEngine.PoiyomiOriginalShader(src);
                    Shader o = orig.Length > 0 ? ShaderBundleBuilder.FindShader(orig) : null;
                    if (o != null)
                    {
                        int q = src.renderQueue;
                        copy.shader = o;
                        copy.renderQueue = q;
                        if (copy.HasProperty("_ShaderOptimizerEnabled")) copy.SetFloat("_ShaderOptimizerEnabled", 0f);
                    }
                    else
                    {
                        shaderMisses++;
                        string miss = orig.Length > 0 ? orig : src.shader.name;
                        if (!missingShaders.Contains(miss)) missingShaders.Add(miss);
                        notes.Add("Missing shader '" + miss + "' for material " + src.name + ".");
                    }
                }
            }
            map[src] = copy;
            // textures
            Shader sh = copy.shader;
            if (sh != null)
            {
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    string pn = sh.GetPropertyName(i);
                    Texture t = copy.GetTexture(pn);
                    if (t == null || IsAsset(t)) continue;
                    copy.SetTexture(pn, Persist(t) as Texture);
                }
            }
            AssetDatabase.CreateAsset(copy, MSBatch.NamedPath(dir + "/Materials", src.name, ".mat"));
            return copy;
        }

        public GameObject avatarRoot;

        Avatar CopyAvatar(Avatar src)
        {
            Avatar a = null;
            try { a = UnityEngine.Object.Instantiate(src); } catch { a = null; }
            if ((a == null || !a.isValid) && src.isHuman && avatarRoot != null)
                a = AvatarBuilder.BuildHumanAvatar(avatarRoot, src.humanDescription);
            if (a == null) { notes.Add("The humanoid Avatar could not be copied."); return null; }
            a.name = src.name;
            AssetDatabase.CreateAsset(a, MSBatch.NamedPath(dir + "/Data", src.name, ".asset"));
            map[src] = a;
            return a;
        }

        UnityEngine.Object CopyGeneric(UnityEngine.Object src)
        {
            UnityEngine.Object copy;
            if (src is ScriptableObject)
            {
                // A copy made with Instantiate keeps the bundle's script (no GUID, dead once
                // saved), and swapping m_Script on a ScriptableObject destroys it. A fresh
                // CreateInstance gets the project's script; then copy the data fields across.
                if (ProjectScript(src.GetType()) == null)
                    notes.Add("WARNING: the script for " + src.GetType().FullName + " '" + src.name + "' is not in this Unity project; that data will be missing.");
                copy = ScriptableObject.CreateInstance(src.GetType());
                EditorUtility.CopySerializedManagedFieldsOnly(src, copy);
            }
            else copy = UnityEngine.Object.Instantiate(src);
            copy.name = src.name;
            string ext = src is AnimationClip ? ".anim" : ".asset";
            AssetDatabase.CreateAsset(copy, MSBatch.NamedPath(dir + "/Data", src.name, ext));
            map[src] = copy;
            // ScriptableObjects (VRM BlendShapeAvatar -> clips, meta -> thumbnail) reference more bundle objects
            if (copy is ScriptableObject) FixReferences(copy);
            return copy;
        }

        static Dictionary<Type, MonoScript> scriptByType;

        /// <summary>The project's own MonoScript for a class (null if the class has no script asset).</summary>
        public static MonoScript ProjectScript(Type t)
        {
            if (t == null) return null;
            if (scriptByType == null)
            {
                scriptByType = new Dictionary<Type, MonoScript>();
                foreach (MonoScript ms in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    if (ms == null || !IsAsset(ms)) continue;
                    Type c = null;
                    try { c = ms.GetClass(); } catch { }
                    if (c != null && !scriptByType.ContainsKey(c)) scriptByType[c] = ms;
                }
            }
            MonoScript s;
            scriptByType.TryGetValue(t, out s);
            return s;
        }

        /// <summary>
        /// Points a MonoBehaviour's or ScriptableObject's m_Script at the project's MonoScript.
        /// Objects from an AssetBundle point at the bundle's in-memory copy of the script, which
        /// has no GUID: saving them writes a dead reference ("Could not extract GUID") and the
        /// export loads with "missing script" there. That lost every VRM component (meta,
        /// humanoid, blendshapes, spring bones, look-at), so the avatar stood in a T-pose.
        /// </summary>
        public static bool RelinkScript(UnityEngine.Object o)
        {
            if (!(o is MonoBehaviour) && !(o is ScriptableObject)) return true;
            var so = new SerializedObject(o);
            SerializedProperty p = so.FindProperty("m_Script");
            if (p == null) return true;
            var cur = p.objectReferenceValue as MonoScript;
            if (cur != null && IsAsset(cur)) return true;
            MonoScript ms = ProjectScript(o.GetType());
            if (ms == null) return false;
            p.objectReferenceValue = ms;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        /// <summary>True when the object's script is a real project asset (so it survives saving).</summary>
        public static bool ScriptIsAsset(UnityEngine.Object o)
        {
            if (!(o is MonoBehaviour) && !(o is ScriptableObject)) return true;
            var p = new SerializedObject(o).FindProperty("m_Script");
            return p == null || IsAsset(p.objectReferenceValue);
        }

        /// <summary>
        /// Bundle textures are GPU-only. Read them back through a RenderTexture in the
        /// texture's own colour space (sRGB stays sRGB, data stays linear) and import the
        /// PNG with matching settings. Normal maps are decoded from either layout
        /// (DXT5nm AG or plain RG) and re-imported as normal maps.
        /// </summary>
        Texture2D SaveTexture(Texture2D src)
        {
            bool srgb = false;
            try { srgb = GraphicsFormatUtility.IsSRGBFormat(src.graphicsFormat); } catch { }
            bool normal = normalUse.Contains(src);
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var flat = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, !srgb);
                flat.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                flat.Apply();
                if (normal) DecodeNormals(flat);
                byte[] png = flat.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(flat);

                string path = MSBatch.NamedPath(dir + "/Textures", src.name, ".png");
                File.WriteAllBytes(path, png);
                AssetDatabase.ImportAsset(path);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp != null)
                {
                    imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    imp.sRGBTexture = srgb && !normal;
                    imp.mipmapEnabled = src.mipmapCount > 1;
                    imp.wrapMode = src.wrapMode;
                    imp.filterMode = src.filterMode;
                    imp.anisoLevel = src.anisoLevel;
                    imp.alphaIsTransparency = false;
                    imp.maxTextureSize = NextPow2(Mathf.Max(src.width, src.height));
                    imp.SaveAndReimport();
                }
                Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (t != null && !textureByOriginalName.ContainsKey(src.name)) textureByOriginalName[src.name] = t;
                map[src] = t;
                return t;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Bundle cubemaps (reflections, matcaps) are compressed and GPU-only, and Unity will not
        /// convert them directly. Each face is drawn by a tiny helper shader that samples the
        /// cube by direction, read back, and saved as a plain .cubemap asset (HDR keeps half
        /// floats). Face row order differs between APIs, so the copy is checked against the
        /// original in 14 directions and redone flipped if it does not match.
        /// </summary>
        Cubemap SaveCubemap(Cubemap src)
        {
            Shader helper = Shader.Find("Hidden/MaterialStudio/CubeFace");
            if (helper == null) { notes.Add("Cubemap '" + src.name + "' skipped: the Material Studio cube helper shader is missing from the project."); return null; }
            TextureFormat sf = src.format;
            bool hdr = sf == TextureFormat.BC6H || sf == TextureFormat.RGBAHalf || sf == TextureFormat.RGBAFloat
                    || sf == TextureFormat.RGB9e5Float || sf == TextureFormat.RHalf || sf == TextureFormat.RGHalf;
            var mat = new Material(helper);
            mat.SetTexture("_Cube", src);
            Cubemap best = null;
            float bestErr = float.MaxValue;
            for (int flip = 0; flip < 2; flip++)
            {
                Cubemap c = RenderCube(src, mat, hdr, flip);
                float err = CompareCubes(src, c, mat);
                MSBatch.Log("cube '" + src.name + "' " + src.format + " " + src.width + " hdr=" + hdr + " flip=" + flip + " err=" + err.ToString("0.000"));
                if (err < bestErr) { if (best != null) UnityEngine.Object.DestroyImmediate(best); best = c; bestErr = err; }
                else UnityEngine.Object.DestroyImmediate(c);
                if (bestErr < 0.03f) break;
            }
            UnityEngine.Object.DestroyImmediate(mat);
            if (best == null) return null;
            if (bestErr >= 0.03f) notes.Add("Cubemap '" + src.name + "' copied, but it differs from the original (error " + bestErr.ToString("0.000") + "); check its reflections.");
            // back to the compression it shipped with (uncompressed half-float cubes are ~48 MB each)
            try
            {
                TextureFormat target = hdr ? TextureFormat.BC6H : src.format;
                if (target == TextureFormat.BC6H || target == TextureFormat.DXT1 || target == TextureFormat.DXT5 || target == TextureFormat.BC7)
                    EditorUtility.CompressCubemapTexture(best, target, 100);
            }
            catch (Exception e) { notes.Add("Cubemap '" + src.name + "' kept uncompressed (" + e.Message + ")."); }
            best.name = src.name;
            best.wrapMode = src.wrapMode;
            best.filterMode = src.filterMode;
            AssetDatabase.CreateAsset(best, MSBatch.NamedPath(dir + "/Textures", src.name, ".cubemap"));
            if (!textureByOriginalName.ContainsKey(src.name)) textureByOriginalName[src.name] = best;
            map[src] = best;
            return best;
        }

        static Cubemap RenderCube(Cubemap src, Material mat, bool hdr, int flip)
        {
            int size = src.width;
            // non-HDR: an sRGB target re-encodes exactly like the sRGB cubemap that stores it
            var rt = RenderTexture.GetTemporary(size, size, 0, hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32,
                hdr ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            bool mips = src.mipmapCount > 1;
            // explicit formats: the TextureFormat constructor gives a LINEAR 8-bit cube, which
            // would brighten the sRGB-encoded faces read back above
            var outCube = new Cubemap(size, hdr ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_SRGB,
                mips ? UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain : UnityEngine.Experimental.Rendering.TextureCreationFlags.None);
            var tmp = new Texture2D(size, size, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, false, hdr);
            try
            {
                mat.SetFloat("_Probe", 0f);
                mat.SetFloat("_Flip", flip);
                for (int f = 0; f < 6; f++)
                {
                    mat.SetFloat("_Face", f);
                    Graphics.Blit(null, rt, mat);
                    RenderTexture.active = rt;
                    tmp.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    tmp.Apply();
                    outCube.SetPixels(tmp.GetPixels(), (CubemapFace)f);
                }
                outCube.Apply(mips);
                return outCube;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(tmp);
            }
        }

        /// <summary>Mean colour difference between two cubemaps over 14 directions.</summary>
        static float CompareCubes(Cubemap a, Cubemap b, Material mat)
        {
            Vector3[] dirs =
            {
                new Vector3(1, 0.3f, 0.2f), new Vector3(-1, 0.2f, -0.3f), new Vector3(0.2f, 1, 0.3f), new Vector3(-0.3f, -1, 0.2f),
                new Vector3(0.3f, 0.2f, 1), new Vector3(-0.2f, -0.3f, -1), new Vector3(1, 1, 1), new Vector3(-1, 1, -1),
                new Vector3(1, -1, -1), new Vector3(-1, -1, 1), new Vector3(0.7f, 0.1f, -0.6f), new Vector3(-0.4f, 0.8f, 0.5f),
                new Vector3(0.1f, -0.7f, 0.7f), new Vector3(-0.8f, -0.2f, -0.5f),
            };
            var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var px = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
            RenderTexture prev = RenderTexture.active;
            float err = 0f;
            try
            {
                mat.SetFloat("_Probe", 1f);
                foreach (Vector3 d in dirs)
                {
                    mat.SetVector("_Dir", d);
                    mat.SetTexture("_Cube", a);
                    Color ca = Probe(mat, rt, px);
                    mat.SetTexture("_Cube", b);
                    Color cb = Probe(mat, rt, px);
                    float scale = Mathf.Max(0.05f, Mathf.Max(ca.r, Mathf.Max(ca.g, ca.b)));
                    err += (Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b)) / (3f * scale);
                }
            }
            finally
            {
                mat.SetTexture("_Cube", a);
                mat.SetFloat("_Probe", 0f);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(px);
            }
            return err / dirs.Length;
        }

        static Color Probe(Material mat, RenderTexture rt, Texture2D px)
        {
            Graphics.Blit(null, rt, mat);
            RenderTexture.active = rt;
            px.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            px.Apply();
            return px.GetPixel(0, 0);
        }

        static void DecodeNormals(Texture2D t)
        {
            Color32[] px = t.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                // Unity's UnpackNormal trick covers both layouts: x = r * a
                float x = (px[i].r / 255f) * (px[i].a / 255f) * 2f - 1f;
                float y = px[i].g / 255f * 2f - 1f;
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                px[i] = new Color32((byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f),
                                    (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 255);
            }
            t.SetPixels32(px);
            t.Apply();
        }

        static int NextPow2(int v)
        {
            int p = 32;
            while (p < v && p < 8192) p <<= 1;
            return p;
        }
    }
}
