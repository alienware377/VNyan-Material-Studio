using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Keeps a material's keywords and override tags exact across shader swaps, and gives a
    /// Poiyomi material the keyword set its lock needs. Thry's optimizer compiles a feature into
    /// a locked shader only when its keyword is on at lock time, and assigning a shader in the
    /// editor re-runs the property drawers (keywords change) and drops the RenderType override
    /// and the custom render queue. Unlocking mirrors Thry's own unlock: the keywords come back
    /// from the "OriginalKeywords" tag, or from the values when that tag cannot be trusted.
    /// </summary>
    public static class PoiyomiKeywords
    {
        /// <summary>Every override tag the material itself sets (not the shader's own tags).</summary>
        public static Dictionary<string, string> CaptureTags(Material m)
        {
            var d = new Dictionary<string, string>();
            if (m == null) return d;
            SerializedProperty map = null;
            try
            {
                var so = new SerializedObject(m);
                map = so.FindProperty("stringTagMap") ?? so.FindProperty("m_StringTagMap");
                if (map != null && map.isArray)
                {
                    for (int i = 0; i < map.arraySize; i++)
                    {
                        SerializedProperty e = map.GetArrayElementAtIndex(i);
                        SerializedProperty k = e.FindPropertyRelative("first"), v = e.FindPropertyRelative("second");
                        if (k != null && v != null && !string.IsNullOrEmpty(k.stringValue)) d[k.stringValue] = v.stringValue ?? "";
                    }
                    return d;
                }
            }
            catch { }
            // fallback: the tags this toolset and Thry care about
            var keys = new List<string> { "RenderType", "OriginalShader", "OriginalShaderGUID", "OriginalKeywords", "thry_rename_suffix" };
            if (m.shader != null)
                for (int i = 0; i < m.shader.GetPropertyCount(); i++) keys.Add(m.shader.GetPropertyName(i) + "Animated");
            foreach (string k in keys)
            {
                string v = m.GetTag(k, false, "");
                if (v.Length > 0) d[k] = v;
            }
            return d;
        }

        public static void RestoreTags(Material m, Dictionary<string, string> tags)
        {
            if (m == null || tags == null) return;
            foreach (var kv in tags) m.SetOverrideTag(kv.Key, kv.Value ?? "");
        }

        /// <summary>The material's own render queue (-1 = the shader's), not the effective one.</summary>
        public static int RawQueue(Material m)
        {
            try
            {
                SerializedProperty p = new SerializedObject(m).FindProperty("m_CustomRenderQueue");
                if (p != null) return p.intValue;
            }
            catch { }
            return m.renderQueue;
        }

        /// <summary>
        /// Assigns a shader and puts back the override tags, the render queue and exactly the
        /// given keywords (the assignment itself re-runs the drawers, which can change them).
        /// </summary>
        public static void AssignShaderExact(Material m, Shader s, string[] keywords)
        {
            if (m == null || s == null) return;
            string[] kw = keywords != null ? (string[])keywords.Clone() : new string[0];
            Dictionary<string, string> tags = CaptureTags(m);
            int q = RawQueue(m);
            m.shader = s;
            RestoreTags(m, tags);
            m.renderQueue = q;
            m.shaderKeywords = kw;
        }

        /// <summary>
        /// Locked Poiyomi back onto its full shader, the way Thry's unlock does it: the keywords
        /// come from the OriginalKeywords tag when it is healthy; otherwise the material keeps its
        /// own and the shader's drawers rebuild them from the values.
        /// </summary>
        public static void Rebind(Material m, Shader full, List<string> notes)
        {
            if (m == null || full == null) return;
            string okw = m.GetTag("OriginalKeywords", false, "");
            bool healthy = RecipeEngine.HealthyOriginalKeywords(okw, full);
            AssignShaderExact(m, full, healthy ? RecipeEngine.SplitKeywords(okw) : m.shaderKeywords);
            if (m.HasProperty("_ShaderOptimizerEnabled")) m.SetFloat("_ShaderOptimizerEnabled", 0f);
            if (!healthy)
            {
                Derive(m, null);
                if (notes != null)
                    notes.Add(m.name + ": its lock did not record usable keywords (" + (okw.Length == 0 ? "none" : okw) +
                              "), so they were rebuilt from values (" + Clean(m.shaderKeywords).Length + " keyword(s)).");
            }
        }

        /// <summary>
        /// Runs the shader's property drawers (Toggle, ThryToggle, KeywordEnum...) on top of the
        /// current keywords, like Thry's "Fix Keywords". Keywords no drawer owns are kept.
        /// </summary>
        public static void Derive(Material m, List<string> notes)
        {
            if (m == null || m.shader == null) return;
            var before = new HashSet<string>(Clean(m.shaderKeywords));
            try { MaterialEditor.ApplyMaterialPropertyDrawers(m); }
            catch (Exception e) { if (notes != null) notes.Add(m.name + ": rebuilding keywords failed (" + e.Message + ")."); return; }
            var after = new HashSet<string>(Clean(m.shaderKeywords));
            if (notes == null) return;
            var added = new List<string>(); var removed = new List<string>();
            foreach (string k in after) if (!before.Contains(k)) added.Add(k);
            foreach (string k in before) if (!after.Contains(k)) removed.Add(k);
            added.Sort(StringComparer.Ordinal); removed.Sort(StringComparer.Ordinal);
            if (added.Count + removed.Count > 0)
                notes.Add(m.name + ": keywords worked out from its values" + (added.Count > 0 ? ", added " + string.Join(", ", added.ToArray()) : "") +
                          (removed.Count > 0 ? ", removed " + string.Join(", ", removed.ToArray()) : "") + ".");
        }

        /// <summary>
        /// The keywords the material's values switch on, worked out on a scratch copy on the
        /// full shader <paramref name="full"/> (its property drawers, from no keywords at all).
        /// It ignores the keyword list a lock is given, so it shows when that list misses a
        /// feature. Returns null when the drawers could not run.
        /// </summary>
        public static HashSet<string> ImpliedByValues(Material m, Shader full)
        {
            if (m == null || full == null) return null;
            Material scratch = null;
            try
            {
                scratch = new Material(m);
                scratch.shader = full;
                // the lock button's drawer must not see a locked value
                if (scratch.HasProperty("_ShaderOptimizerEnabled")) scratch.SetFloat("_ShaderOptimizerEnabled", 0f);
                scratch.shaderKeywords = new string[0];
                MaterialEditor.ApplyMaterialPropertyDrawers(scratch);
                return new HashSet<string>(Clean(scratch.shaderKeywords));
            }
            catch { return null; }
            finally { if (scratch != null) UnityEngine.Object.DestroyImmediate(scratch); }
        }

        /// <summary>
        /// Keywords the values of <paramref name="m"/> switch on that <paramref name="have"/>
        /// lacks, counting only keywords the full shader declares (a lock drops the rest
        /// anyway). Null when this cannot be worked out.
        /// </summary>
        public static List<string> MissingFromValues(Material m, Shader full, IEnumerable<string> have)
        {
            HashSet<string> implied = ImpliedByValues(m, full);
            if (implied == null) return null;
            HashSet<string> declared = Declared(full);
            var h = new HashSet<string>(have ?? new string[0]);
            var miss = new List<string>();
            foreach (string k in implied)
                if (!h.Contains(k) && (declared.Count == 0 || declared.Contains(k))) miss.Add(k);
            miss.Sort(StringComparer.Ordinal);
            return miss;
        }

        /// <summary>Every keyword the shader declares (internal ShaderUtil calls, read by reflection).</summary>
        public static HashSet<string> Declared(Shader s)
        {
            var set = new HashSet<string>();
            if (s == null) return set;
            foreach (string mn in new[] { "GetShaderGlobalKeywords", "GetShaderLocalKeywords" })
            {
                MethodInfo mi = typeof(ShaderUtil).GetMethod(mn, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (mi == null) continue;
                try
                {
                    var r = mi.Invoke(null, new object[] { s }) as string[];
                    if (r != null) foreach (string k in r) if (!string.IsNullOrEmpty(k)) set.Add(k);
                }
                catch { }
            }
            return set;
        }

        static readonly Regex DefineRx = new Regex(@"^\s*#define\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline);
        static readonly Regex BareDefineRx = new Regex(@"^[ \t]*#define[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*\r?$", RegexOptions.Multiline);

        /// <summary>Names #defined in a generated (locked) shader file.</summary>
        public static HashSet<string> DefinesIn(Shader locked)
        {
            var set = new HashSet<string>();
            string p = locked != null ? AssetDatabase.GetAssetPath(locked) : "";
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return set;
            foreach (Match mt in DefineRx.Matches(File.ReadAllText(p))) set.Add(mt.Groups[1].Value);
            return set;
        }

        /// <summary>
        /// Keywords the generated shader still has code for. Thry leaves out the #define of a
        /// keyword whose code the lock already removed (a feature switched off by value, like
        /// rim lighting with _RimStyle set), so only these must be defined.
        /// </summary>
        public static HashSet<string> StillUsed(Shader locked, IEnumerable<string> keywords)
        {
            var set = new HashSet<string>();
            string p = locked != null ? AssetDatabase.GetAssetPath(locked) : "";
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return set;
            var texts = new List<string>();
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(p)))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".shader" && ext != ".cginc" && ext != ".hlsl" && ext != ".hlslinc") continue;
                // the #define lines Thry added do not count as use
                texts.Add(BareDefineRx.Replace(File.ReadAllText(f), ""));
            }
            foreach (string k in keywords)
                foreach (string t in texts)
                    if (Regex.IsMatch(t, @"\b" + Regex.Escape(k) + @"\b")) { set.Add(k); break; }
            return set;
        }

        public static string[] Clean(string[] kw)
        {
            var l = new List<string>();
            if (kw != null) foreach (string k in kw) if (!string.IsNullOrEmpty(k) && !l.Contains(k)) l.Add(k);
            return l.ToArray();
        }
    }
}
