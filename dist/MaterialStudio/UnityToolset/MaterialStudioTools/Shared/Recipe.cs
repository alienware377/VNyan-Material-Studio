using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaterialStudio
{
    // ============================================================================
    //  Material Studio recipe
    //
    //  One file per avatar holding every live material edit made in VNyan. It is the
    //  hand-off format between the runtime plugin, the editor-side exporter, and any
    //  other exporter that wants to carry the edits (Weight Studio, Warudo Importer).
    //
    //  Plain JsonUtility types only: this file is compiled into the VNyan plugin AND
    //  into the Unity editor toolset (2019.4), so it must not touch Newtonsoft or any
    //  runtime-only API. Keep the copies identical.
    //
    //  Addressing: a material is found by the renderer path (relative to the avatar
    //  root, root name excluded) and the material slot index. VNyan names every loaded
    //  avatar "VSFAvatarTemporary(Clone)" while the editor prefab has its own name, so
    //  the root never takes part in a path.
    // ============================================================================

    [Serializable]
    public class Recipe
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string avatarKey = "";      // file-name-safe identity used for the recipe name
        public string avatarFile = "";     // full path of the source .vsfavatar when known
        public string signature = "";      // mesh signature (name#verts list) for matching
        public string savedAt = "";
        public List<MatEdit> materials = new List<MatEdit>();

        public MatEdit Find(string id)
        {
            for (int i = 0; i < materials.Count; i++)
                if (materials[i] != null && materials[i].id == id) return materials[i];
            return null;
        }

        public string ToJson() { return JsonIO.ToJson(this); }

        public static Recipe FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Recipe r = JsonIO.FromJson<Recipe>(json);
            if (r == null) return null;
            if (r.materials == null) r.materials = new List<MatEdit>();
            for (int i = 0; i < r.materials.Count; i++)
                if (r.materials[i] != null) r.materials[i].Normalize();
            return r;
        }
    }

    [Serializable]
    public class SlotRef
    {
        public string path = "";   // renderer path under the avatar root ("" = the root itself)
        public int slot;           // index into sharedMaterials

        public SlotRef() { }
        public SlotRef(string p, int s) { path = p; slot = s; }
        public override string ToString() { return path + "|" + slot; }
    }

    /// <summary>
    /// The edits for one material. Values are stored as differences from the material
    /// the avatar shipped with, except when the shader was changed: then the full
    /// property set of the new shader is written so the result never depends on what
    /// the two shaders happen to share.
    /// </summary>
    [Serializable]
    public class MatEdit
    {
        public string id = "";              // first slot that used it: "path|slot"
        public string originalName = "";
        public string originalShader = "";
        public List<SlotRef> slots = new List<SlotRef>();
        public bool unique;                 // split off a shared material for these slots only

        public string shader = "";          // "" = shader unchanged
        public bool fullSet;                // true when every property below is a full snapshot
        public bool unlockedPoiyomi;        // was a locked Poiyomi material, now its original shader
        public int renderQueue = -2;        // -2 = unchanged
        public bool keywordsSet;            // keywords below are the full enabled set
        public List<string> keywords = new List<string>();

        public List<NamedFloat> floats = new List<NamedFloat>();
        public List<NamedColor> colors = new List<NamedColor>();
        public List<NamedVector> vectors = new List<NamedVector>();
        public List<TexEdit> textures = new List<TexEdit>();
        public List<NamedString> tags = new List<NamedString>();

        public void Normalize()
        {
            if (slots == null) slots = new List<SlotRef>();
            if (keywords == null) keywords = new List<string>();
            if (floats == null) floats = new List<NamedFloat>();
            if (colors == null) colors = new List<NamedColor>();
            if (vectors == null) vectors = new List<NamedVector>();
            if (textures == null) textures = new List<TexEdit>();
            if (tags == null) tags = new List<NamedString>();
        }

        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrEmpty(shader) && renderQueue == -2 && !keywordsSet && !unique
                    && floats.Count == 0 && colors.Count == 0 && vectors.Count == 0
                    && textures.Count == 0 && tags.Count == 0;
            }
        }
    }

    [Serializable] public class NamedFloat  { public string n; public float v;   public NamedFloat() { }  public NamedFloat(string n, float v)    { this.n = n; this.v = v; } }
    [Serializable] public class NamedColor  { public string n; public Color v;   public NamedColor() { }  public NamedColor(string n, Color v)    { this.n = n; this.v = v; } }
    [Serializable] public class NamedVector { public string n; public Vector4 v; public NamedVector() { } public NamedVector(string n, Vector4 v) { this.n = n; this.v = v; } }
    [Serializable] public class NamedString { public string n; public string v;  public NamedString() { } public NamedString(string n, string v) { this.n = n; this.v = v; } }

    /// <summary>
    /// A texture slot edit. <c>src</c> says where the texture comes from:
    ///   ""                 the slot was cleared (no texture)
    ///   "keep"             texture unchanged, only tiling/offset changed
    ///   "file:&lt;rel&gt;"       a PNG/JPG copied into the recipe's folder (path relative to it)
    ///   "avatar:&lt;name&gt;"    another texture the avatar already has, by texture name
    /// </summary>
    [Serializable]
    public class TexEdit
    {
        public string n;
        public string src = "keep";
        public bool linear;                 // load as linear data (normal maps, masks)
        public bool stSet;                  // scale/offset below apply
        public Vector2 scale = Vector2.one;
        public Vector2 offset = Vector2.zero;
    }

    /// <summary>Path helpers shared by the runtime and the editor so both address alike.</summary>
    public static class RecipePaths
    {
        // A segment is the object's name; when earlier siblings share that name it becomes
        // "name[#k]" (k = how many came before), so duplicate-named meshes stay apart.
        public static string PathOf(Transform t, Transform root)
        {
            if (t == null || t == root) return "";
            var parts = new List<string>();
            Transform c = t;
            while (c != null && c != root) { parts.Add(Segment(c)); c = c.parent; }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        static string Segment(Transform c)
        {
            Transform p = c.parent;
            if (p == null) return c.name;
            int k = 0;
            for (int i = 0; i < p.childCount; i++)
            {
                Transform s = p.GetChild(i);
                if (s == c) break;
                if (s.name == c.name) k++;
            }
            return k == 0 ? c.name : c.name + "[#" + k + "]";
        }

        public static Transform Resolve(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            if (path.IndexOf("[#", System.StringComparison.Ordinal) < 0)
            {
                Transform t = root.Find(path);
                if (t != null) return t;
            }
            // Walk by segments: handles "[#k]" duplicates and names that contain '/'.
            string[] segs = path.Split('/');
            Transform cur = root;
            for (int i = 0; i < segs.Length && cur != null; i++)
            {
                string name = segs[i];
                int want = 0;
                int at = name.LastIndexOf("[#", System.StringComparison.Ordinal);
                if (at > 0 && name.EndsWith("]", System.StringComparison.Ordinal))
                {
                    int k;
                    if (int.TryParse(name.Substring(at + 2, name.Length - at - 3), out k)) { want = k; name = name.Substring(0, at); }
                }
                Transform next = null;
                int seen = 0;
                for (int c = 0; c < cur.childCount; c++)
                {
                    if (cur.GetChild(c).name != name) continue;
                    if (seen++ == want) { next = cur.GetChild(c); break; }
                }
                cur = next;
            }
            return cur;
        }

        public static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "avatar";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
            string r = sb.ToString().Trim('.');
            return r.Length == 0 ? "avatar" : r;
        }
    }
}
