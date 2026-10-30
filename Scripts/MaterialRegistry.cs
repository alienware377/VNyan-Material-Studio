using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaterialStudio
{
    /// <summary>One distinct material on the avatar and every renderer slot that uses it.</summary>
    public class MatEntry
    {
        public string id;                    // "path|slot" of the first slot (stable across loads)
        public Material mat;                 // the live material object
        public MatSnapshot orig;             // how it shipped (frozen at first sight this session)
        public readonly List<SlotRef> slots = new List<SlotRef>();
        public readonly List<Renderer> renderers = new List<Renderer>();
        public bool unique;                  // split off a shared material by the user
        public bool unlockedPoiyomi;
        public string originalName;
        public string originalShader;

        public string Display
        {
            get
            {
                string sh = mat != null && mat.shader != null ? ShortShader(mat.shader.name) : "?";
                return (mat != null ? mat.name : "(none)") + "  [" + sh + "]";
            }
        }

        public static string ShortShader(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            if (n.StartsWith("Hidden/Locked/", StringComparison.Ordinal)) return "Locked " + Leaf(n.Substring(14), 2);
            return Leaf(n, 2);
        }

        static string Leaf(string n, int parts)
        {
            string[] p = n.Split('/');
            if (p.Length <= parts) return n;
            // a locked Poiyomi name ends in a GUID-ish segment; skip it
            int end = p.Length;
            if (end > 1 && p[end - 1].Length >= 24 && p[end - 1].IndexOf(' ') < 0) end--;
            int start = Math.Max(0, end - parts);
            return string.Join("/", p, start, end - start);
        }
    }

    /// <summary>
    /// Finds every material on the avatar, keeps the as-shipped snapshot of each (for
    /// diffing and Reset), and resolves recipe addresses back to live objects.
    /// </summary>
    public class MaterialRegistry
    {
        public GameObject avatar;
        public readonly List<MatEntry> entries = new List<MatEntry>();
        readonly Dictionary<Material, MatEntry> byMat = new Dictionary<Material, MatEntry>();

        // Session-wide: VNyan reloads a cached avatar with the SAME material objects, which
        // by then carry our edits. Remember the first snapshot per object so "original"
        // stays original across rebinds.
        static readonly Dictionary<Material, MatSnapshot> firstSeen = new Dictionary<Material, MatSnapshot>();
        // Materials we created (Make unique) map back to the material they were cut from.
        static readonly Dictionary<Material, Material> cloneOf = new Dictionary<Material, Material>();

        public void Bind(GameObject root)
        {
            avatar = root;
            entries.Clear();
            byMat.Clear();
            if (root == null) return;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < rs.Length; r++)
            {
                Renderer rend = rs[r];
                if (!(rend is SkinnedMeshRenderer) && !(rend is MeshRenderer)) continue;
                Material[] mats = rend.sharedMaterials;
                string path = RecipePaths.PathOf(rend.transform, root.transform);
                for (int s = 0; s < mats.Length; s++)
                {
                    Material m = mats[s];
                    if (m == null) continue;
                    MatEntry e;
                    if (!byMat.TryGetValue(m, out e))
                    {
                        e = new MatEntry { id = path + "|" + s, mat = m };
                        Material source;
                        e.unique = cloneOf.TryGetValue(m, out source);
                        e.orig = SnapshotOf(e.unique ? source : m);
                        e.originalName = (e.unique ? source : m).name;
                        e.originalShader = e.orig.shaderName;
                        byMat[m] = e;
                        entries.Add(e);
                    }
                    e.slots.Add(new SlotRef(path, s));
                    if (!e.renderers.Contains(rend)) e.renderers.Add(rend);
                }
            }
        }

        static MatSnapshot SnapshotOf(Material m)
        {
            MatSnapshot s;
            if (m != null && firstSeen.TryGetValue(m, out s)) return s;
            s = MatSnapshot.Capture(m);
            if (m != null) firstSeen[m] = s;
            return s;
        }

        public MatEntry EntryFor(Material m)
        {
            MatEntry e;
            return m != null && byMat.TryGetValue(m, out e) ? e : null;
        }

        public MatEntry EntryById(string id)
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].id == id) return entries[i];
            return null;
        }

        public Material MaterialAt(SlotRef sr)
        {
            if (avatar == null || sr == null) return null;
            Transform t = RecipePaths.Resolve(avatar.transform, sr.path);
            Renderer r = t != null ? t.GetComponent<Renderer>() : null;
            if (r == null) return null;
            Material[] mats = r.sharedMaterials;
            return sr.slot >= 0 && sr.slot < mats.Length ? mats[sr.slot] : null;
        }

        /// <summary>
        /// Gives the listed slots their own copy of the material. Returns the copy. The
        /// copy remembers its source, so its "original" is the source's original.
        /// </summary>
        public Material SplitOff(Material src, IList<SlotRef> slots)
        {
            if (src == null || avatar == null) return null;
            var copy = new Material(src);
            copy.name = src.name + " (unique)";
            Material root;
            cloneOf[copy] = cloneOf.TryGetValue(src, out root) ? root : src;
            for (int i = 0; i < slots.Count; i++)
            {
                Transform t = RecipePaths.Resolve(avatar.transform, slots[i].path);
                Renderer r = t != null ? t.GetComponent<Renderer>() : null;
                if (r == null) continue;
                Material[] mats = r.sharedMaterials;
                if (slots[i].slot < 0 || slots[i].slot >= mats.Length) continue;
                mats[slots[i].slot] = copy;
                r.sharedMaterials = mats;
            }
            return copy;
        }

        /// <summary>Undo a split: point the slots back at the material it was cut from.</summary>
        public bool Merge(Material copy)
        {
            Material src;
            if (copy == null || avatar == null || !cloneOf.TryGetValue(copy, out src)) return false;
            Renderer[] rs = avatar.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs)
            {
                Material[] mats = r.sharedMaterials;
                bool hit = false;
                for (int s = 0; s < mats.Length; s++) if (mats[s] == copy) { mats[s] = src; hit = true; }
                if (hit) r.sharedMaterials = mats;
            }
            return true;
        }

        public IEnumerable<Shader> Shaders()
        {
            var set = new HashSet<Shader>();
            foreach (MatEntry e in entries)
            {
                if (e.mat != null && e.mat.shader != null) set.Add(e.mat.shader);
                if (e.orig != null && e.orig.shader != null) set.Add(e.orig.shader);
            }
            return set;
        }

        /// <summary>Every texture the avatar shipped with, by name (for "avatar:" sources).</summary>
        public Dictionary<string, Texture> AvatarTextures()
        {
            var d = new Dictionary<string, Texture>();
            foreach (MatEntry e in entries)
            {
                if (e.orig == null) continue;
                foreach (var kv in e.orig.textures)
                    if (kv.Value != null && !d.ContainsKey(kv.Value.name)) d[kv.Value.name] = kv.Value;
                if (e.mat == null || e.mat.shader == null) continue;
                Shader sh = e.mat.shader;
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (RecipeEngine.KindOf(sh, i) != PropType.Texture) continue;
                    Texture t = e.mat.GetTexture(sh.GetPropertyName(i));
                    if (t != null && !d.ContainsKey(t.name)) d[t.name] = t;
                }
            }
            return d;
        }

        /// <summary>A short list of mesh name#vertexcount; identical across loads of one file.</summary>
        public static string Signature(GameObject root)
        {
            if (root == null) return "";
            var list = new List<string>();
            foreach (SkinnedMeshRenderer r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null && r.sharedMesh != null) list.Add(r.sharedMesh.name + "#" + r.sharedMesh.vertexCount);
            list.Sort(StringComparer.Ordinal);
            return string.Join(";", list.ToArray());
        }
    }
}
