using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaterialStudio
{
    /// <summary>
    /// A frozen copy of one material's state: shader, queue, keywords, RenderType tag and
    /// every property value of its shader. Used as the "original" to diff against and to
    /// reset to. Shared by the runtime and the editor toolset.
    /// </summary>
    public class MatSnapshot
    {
        public Shader shader;
        public string shaderName = "";
        public int renderQueue;
        public string[] keywords = new string[0];
        public string renderType = "";
        public string originalShaderTag = "";   // Poiyomi lock: the shader it was locked from
        public string originalKeywordsTag = ""; // Poiyomi lock: the keywords it was compiled with
        public readonly Dictionary<string, float> floats = new Dictionary<string, float>();
        public readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
        public readonly Dictionary<string, Vector4> vectors = new Dictionary<string, Vector4>();
        public readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
        public readonly Dictionary<string, Vector4> texST = new Dictionary<string, Vector4>();
        // Poiyomi/Thry "keep live when locked" marks: override tag "<property>Animated" = "1"/"2"
        public readonly Dictionary<string, string> animTags = new Dictionary<string, string>();

        public static MatSnapshot Capture(Material m)
        {
            var s = new MatSnapshot();
            if (m == null) return s;
            s.shader = m.shader;
            s.shaderName = m.shader != null ? m.shader.name : "";
            s.renderQueue = m.renderQueue;
            s.keywords = m.shaderKeywords != null ? (string[])m.shaderKeywords.Clone() : new string[0];
            s.renderType = m.GetTag("RenderType", false, "");
            s.originalShaderTag = m.GetTag("OriginalShader", false, "");
            s.originalKeywordsTag = m.GetTag("OriginalKeywords", false, "");
            Shader sh = m.shader;
            if (sh == null) return s;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = sh.GetPropertyName(i);
                if (!m.HasProperty(name)) continue;
                string at = m.GetTag(name + "Animated", false, "");
                if (at.Length > 0) s.animTags[name] = at;
                try
                {
                    switch (RecipeEngine.KindOf(sh, i))
                    {
                        case PropType.Color: s.colors[name] = m.GetColor(name); break;
                        case PropType.Vector: s.vectors[name] = m.GetVector(name); break;
                        case PropType.Float: s.floats[name] = m.GetFloat(name); break;
                        case PropType.Int: s.floats[name] = RecipeEngine.GetIntProp(m, name); break;
                        case PropType.Texture:
                            s.textures[name] = m.GetTexture(name);
                            Vector2 sc = m.GetTextureScale(name), of = m.GetTextureOffset(name);
                            s.texST[name] = new Vector4(sc.x, sc.y, of.x, of.y);
                            break;
                    }
                }
                catch { }
            }
            return s;
        }

        /// <summary>Puts a material back exactly the way this snapshot saw it.</summary>
        public void Restore(Material m)
        {
            if (m == null) return;
            if (shader != null && m.shader != shader) m.shader = shader;
            foreach (var kv in floats) RecipeEngine.SetNumber(m, kv.Key, kv.Value);
            foreach (var kv in colors) if (m.HasProperty(kv.Key)) m.SetColor(kv.Key, kv.Value);
            foreach (var kv in vectors) if (m.HasProperty(kv.Key)) m.SetVector(kv.Key, kv.Value);
            foreach (var kv in textures)
            {
                if (!m.HasProperty(kv.Key)) continue;
                m.SetTexture(kv.Key, kv.Value);
                Vector4 st;
                if (texST.TryGetValue(kv.Key, out st))
                {
                    m.SetTextureScale(kv.Key, new Vector2(st.x, st.y));
                    m.SetTextureOffset(kv.Key, new Vector2(st.z, st.w));
                }
            }
            m.shaderKeywords = (string[])keywords.Clone();
            if (m.shader != null)
            {
                int pc = m.shader.GetPropertyCount();
                for (int i = 0; i < pc; i++)
                {
                    string pn = m.shader.GetPropertyName(i);
                    string want;
                    if (!animTags.TryGetValue(pn, out want)) want = "";
                    if (m.GetTag(pn + "Animated", false, "") != want) m.SetOverrideTag(pn + "Animated", want);
                }
            }
            if (m.GetTag("OriginalShader", false, "") != originalShaderTag) m.SetOverrideTag("OriginalShader", originalShaderTag);
            if (m.GetTag("OriginalKeywords", false, "") != originalKeywordsTag) m.SetOverrideTag("OriginalKeywords", originalKeywordsTag);
            // GetTag falls back to the shader's own tag; writing that back as an override would
            // pin it across a later shader switch, so only override a real difference
            if (m.GetTag("RenderType", false, "") != renderType) m.SetOverrideTag("RenderType", renderType);
            m.renderQueue = renderQueue;
        }
    }

    public enum PropType { Color, Vector, Float, Int, Texture }

    public static class RecipeEngine
    {
        const float Eps = 1e-5f;

        /// <summary>
        /// Property kind, robust across Unity versions. 2021.1+ has a real Integer type
        /// (enum value 5) that 2019.4 does not know about, so it is compared by number.
        /// Legacy "Int" in a shader file is reported as Float everywhere.
        /// </summary>
        public static PropType KindOf(Shader sh, int i)
        {
            ShaderPropertyType t = sh.GetPropertyType(i);
            switch ((int)t)
            {
                case 0: return PropType.Color;
                case 1: return PropType.Vector;
                case 2: return PropType.Float;
                case 3: return PropType.Float;   // Range
                case 4: return PropType.Texture;
                case 5: return PropType.Int;
                default: return PropType.Float;
            }
        }

        public static float GetIntProp(Material m, string name)
        {
#if UNITY_2021_1_OR_NEWER
            return m.GetInteger(name);
#else
            return m.GetInt(name);
#endif
        }

        /// <summary>Sets a Float/Range/Int property whichever way the shader declares it.</summary>
        public static void SetNumber(Material m, string name, float v)
        {
            if (m == null || !m.HasProperty(name)) return;
            Shader sh = m.shader;
            int idx = sh != null ? sh.FindPropertyIndex(name) : -1;
            if (idx >= 0 && KindOf(sh, idx) == PropType.Int)
            {
#if UNITY_2021_1_OR_NEWER
                m.SetInteger(name, Mathf.RoundToInt(v));
#else
                m.SetInt(name, Mathf.RoundToInt(v));
#endif
            }
            else m.SetFloat(name, v);
        }

        static bool Same(float a, float b) { return Mathf.Abs(a - b) <= Eps * Mathf.Max(1f, Mathf.Abs(a)); }
        static bool Same(Color a, Color b) { return Same(a.r, b.r) && Same(a.g, b.g) && Same(a.b, b.b) && Same(a.a, b.a); }
        static bool Same(Vector4 a, Vector4 b) { return Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z) && Same(a.w, b.w); }

        static bool SameKeywords(string[] a, string[] b)
        {
            var sa = new HashSet<string>(a ?? new string[0]);
            var sb = new HashSet<string>(b ?? new string[0]);
            return sa.SetEquals(sb);
        }

        /// <summary>
        /// Builds the recipe entry for a material compared to its original snapshot. Returns
        /// null when nothing differs. <paramref name="texSource"/> names where a non-original
        /// texture came from (see <see cref="TexEdit"/>); return null to skip that slot.
        /// </summary>
        public static MatEdit Diff(MatSnapshot orig, Material cur, Func<Texture, string> texSource)
        {
            if (orig == null || cur == null) return null;
            var e = new MatEdit();
            Shader sh = cur.shader;
            // Same name counts as unchanged: the recipe addresses shaders by name, so a copy of
            // the same shader from another bundle is indistinguishable to every consumer.
            bool shaderChanged = sh != null && sh != orig.shader && sh.name != orig.shaderName;
            e.fullSet = shaderChanged;
            if (shaderChanged) e.shader = sh.name;
            if (cur.renderQueue != orig.renderQueue || shaderChanged) e.renderQueue = cur.renderQueue;

            string[] kw = cur.shaderKeywords ?? new string[0];
            if (shaderChanged || !SameKeywords(kw, orig.keywords))
            {
                e.keywordsSet = true;
                e.keywords.AddRange(kw);
            }
            string origTag = cur.GetTag("OriginalShader", false, "");
            if (origTag != orig.originalShaderTag) e.tags.Add(new NamedString("OriginalShader", origTag));
            // A locked Poiyomi reports no keywords at all; the set its shader was compiled with
            // lives in this tag, and the exporter needs it to lock the same features in again.
            string okw = cur.GetTag("OriginalKeywords", false, "");
            bool lockedNow = IsLockedPoiyomi(cur);
            if (lockedNow ? okw.Length > 0 && (shaderChanged || okw != orig.originalKeywordsTag)
                          : orig.originalKeywordsTag.Length > 0 && okw != orig.originalKeywordsTag)
                e.tags.Add(new NamedString("OriginalKeywords", okw));
            string rt = cur.GetTag("RenderType", false, "");
            if (shaderChanged ? rt.Length > 0 : rt != orig.renderType)
                e.tags.Add(new NamedString("RenderType", rt));

            if (sh == null) return e.IsEmpty ? null : e;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = sh.GetPropertyName(i);
                if (!cur.HasProperty(name)) continue;
                string curAnim = cur.GetTag(name + "Animated", false, ""), origAnim;
                if (!orig.animTags.TryGetValue(name, out origAnim)) origAnim = "";
                if (curAnim != origAnim) e.tags.Add(new NamedString(name + "Animated", curAnim));
                try
                {
                    switch (KindOf(sh, i))
                    {
                        case PropType.Color:
                        {
                            Color c = cur.GetColor(name), o;
                            if (shaderChanged || !orig.colors.TryGetValue(name, out o) || !Same(c, o))
                                e.colors.Add(new NamedColor(name, c));
                            break;
                        }
                        case PropType.Vector:
                        {
                            Vector4 v = cur.GetVector(name), o;
                            if (shaderChanged || !orig.vectors.TryGetValue(name, out o) || !Same(v, o))
                                e.vectors.Add(new NamedVector(name, v));
                            break;
                        }
                        case PropType.Float:
                        case PropType.Int:
                        {
                            float f = KindOf(sh, i) == PropType.Int ? GetIntProp(cur, name) : cur.GetFloat(name), o;
                            if (shaderChanged || !orig.floats.TryGetValue(name, out o) || !Same(f, o))
                                e.floats.Add(new NamedFloat(name, f));
                            break;
                        }
                        case PropType.Texture:
                        {
                            Texture t = cur.GetTexture(name), ot;
                            Vector2 sc = cur.GetTextureScale(name), of = cur.GetTextureOffset(name);
                            Vector4 st = new Vector4(sc.x, sc.y, of.x, of.y), ost;
                            bool hadOrig = orig.textures.TryGetValue(name, out ot);
                            // after a shader switch every slot is written, empty ones and default
                            // tiling included: the old shader's same-named values must not come back
                            bool texDiff = shaderChanged || !hadOrig || ot != t;
                            bool stDiff = shaderChanged || !orig.texST.TryGetValue(name, out ost) || !Same(st, ost);
                            if (!texDiff && !stDiff) break;
                            var te = new TexEdit { n = name };
                            if (texDiff)
                            {
                                string src = t == null ? "" : (texSource != null ? texSource(t) : null);
                                if (src == null) src = "avatar:" + t.name;
                                te.src = src;
                            }
                            else te.src = "keep";
                            if (stDiff) { te.stSet = true; te.scale = sc; te.offset = of; }
                            e.textures.Add(te);
                            break;
                        }
                    }
                }
                catch { }
            }
            return e.IsEmpty ? null : e;
        }

        /// <summary>
        /// Applies a recipe entry onto a material. The caller resolves shaders by name and
        /// textures by <see cref="TexEdit.src"/>; either resolver may return null, in which
        /// case that part is skipped and reported through <paramref name="notes"/>.
        /// Returns true when the entry came from a locked Poiyomi without usable keywords, so the
        /// caller should rebuild the keywords from the values.
        /// </summary>
        public static bool Apply(Material m, MatEdit e, Func<string, Shader> shaderResolver,
                                 Func<TexEdit, Texture> texResolver, List<string> notes)
        {
            if (m == null || e == null) return false;
            bool lockedCapture = false;
            if (!string.IsNullOrEmpty(e.shader) && (m.shader == null || m.shader.name != e.shader))
            {
                Shader s = shaderResolver != null ? shaderResolver(e.shader) : null;
                if (s != null) SwapShaderKeepTags(m, s);
                // a recipe saved on a LOCKED Poiyomi names its generated Hidden/Locked shader; the
                // editor has the unlocked one already (and locks it again), so nothing is lost
                else if (e.shader.StartsWith("Hidden/Locked/", StringComparison.Ordinal) && m.HasProperty("_ShaderOptimizerEnabled")) lockedCapture = true;
                else if (notes != null)
                    notes.Add("Shader '" + e.shader + "' is not available here, kept '" +
                              (m.shader != null ? m.shader.name : "none") + "' on " + m.name + ".");
            }

            for (int i = 0; i < e.floats.Count; i++)
            {
                // the lock flag belongs to the locked shader: this material stays unlocked until it is locked again
                if (lockedCapture && e.floats[i].n == "_ShaderOptimizerEnabled") continue;
                SetNumber(m, e.floats[i].n, e.floats[i].v);
            }
            if (lockedCapture) m.SetFloat("_ShaderOptimizerEnabled", 0f);
            for (int i = 0; i < e.colors.Count; i++)
                if (m.HasProperty(e.colors[i].n)) m.SetColor(e.colors[i].n, e.colors[i].v);
            for (int i = 0; i < e.vectors.Count; i++)
                if (m.HasProperty(e.vectors[i].n)) m.SetVector(e.vectors[i].n, e.vectors[i].v);
            for (int i = 0; i < e.textures.Count; i++)
            {
                TexEdit te = e.textures[i];
                if (te == null || !m.HasProperty(te.n)) continue;
                if (te.src != "keep")
                {
                    if (te.src == "") m.SetTexture(te.n, null);
                    else
                    {
                        Texture t = texResolver != null ? texResolver(te) : null;
                        if (t != null) m.SetTexture(te.n, t);
                        else if (notes != null) notes.Add("Texture '" + te.src + "' for " + te.n + " on " + m.name + " was not found.");
                    }
                }
                if (te.stSet)
                {
                    m.SetTextureScale(te.n, te.scale);
                    m.SetTextureOffset(te.n, te.offset);
                }
            }
            string okw = "";
            for (int i = 0; i < e.tags.Count; i++)
            {
                if (e.tags[i] == null || string.IsNullOrEmpty(e.tags[i].n)) continue;
                string tn = e.tags[i].n;
                // the lock tags describe the locked shader; on the unlocked one they would make it
                // look locked, so only remember the keywords the lock was compiled with
                if (lockedCapture && (tn == "OriginalShader" || tn == "OriginalShaderGUID" || tn == "OriginalKeywords"))
                {
                    if (tn == "OriginalKeywords") okw = e.tags[i].v ?? "";
                    continue;
                }
                m.SetOverrideTag(tn, e.tags[i].v ?? "");
            }
            bool rebuild = false;
            if (lockedCapture)
            {
                // a locked material's keyword list is always empty, and an enum-only tag says
                // nothing, so fall back to the values
                string[] k = SplitKeywords(okw);
                if (k.Length > 0 && HealthyOriginalKeywords(okw, m.shader)) m.shaderKeywords = k;
                else
                {
                    rebuild = true;
                    if (notes != null)
                        notes.Add(m.name + (k.Length == 0 ? ": locked in VNyan by an older Material Studio" : ": its lock recorded unusable keywords (" + okw + ")") +
                                  ", keywords rebuilt from values.");
                }
            }
            else if (e.keywordsSet) m.shaderKeywords = e.keywords.ToArray();
            if (e.renderQueue != -2) m.renderQueue = e.renderQueue;
            return rebuild;
        }

        /// <summary>"A B  C" -> [A, B, C] (Thry stores keyword lists space separated).</summary>
        public static string[] SplitKeywords(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Keywords a shader's KeywordEnum properties produce ("_LIGHTINGMODE_CLOTH"...). Unity
        /// sets those again by itself whenever a shader is assigned, so a keyword list made of
        /// nothing else says nothing about which features a lock was compiled with.
        /// </summary>
        public static HashSet<string> EnumKeywords(Shader s)
        {
            var set = new HashSet<string>();
            if (s == null) return set;
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string[] attrs;
                try { attrs = s.GetPropertyAttributes(i); } catch { continue; }
                if (attrs == null) continue;
                foreach (string a in attrs)
                {
                    if (a == null || !a.StartsWith("KeywordEnum(", StringComparison.Ordinal) || !a.EndsWith(")", StringComparison.Ordinal)) continue;
                    string pn = s.GetPropertyName(i);
                    foreach (string v in a.Substring(12, a.Length - 13).Split(','))
                    {
                        string t = v.Trim();
                        if (t.Length > 0) set.Add((pn + "_" + t).Replace(' ', '_').ToUpperInvariant());
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// True when a Thry "OriginalKeywords" tag can be trusted as the set a lock was compiled
        /// with: not empty and not only KeywordEnum keywords.
        /// </summary>
        public static bool HealthyOriginalKeywords(string okw, Shader full)
        {
            string[] k = SplitKeywords(okw);
            if (k.Length == 0) return false;
            HashSet<string> en = EnumKeywords(full);
            foreach (string x in k) if (!en.Contains(x)) return true;
            return false;
        }

        /// <summary>
        /// Switches the shader like Thry's lock and unlock do, keeping the render queue, the lock
        /// tags and a real RenderType override (MToon's Transparent) that assigning a shader drops.
        /// </summary>
        public static void SwapShaderKeepTags(Material m, Shader s)
        {
            if (m == null || s == null) return;
            var keep = new List<NamedString>();
            string rt = m.GetTag("RenderType", false, "");
            if (rt.Length > 0 && rt != ShaderTag(m.shader, "RenderType")) keep.Add(new NamedString("RenderType", rt));
            foreach (string t in new[] { "OriginalShader", "OriginalShaderGUID", "OriginalKeywords", "thry_rename_suffix" })
                keep.Add(new NamedString(t, m.GetTag(t, false, "")));
            if (m.shader != null)
            {
                int n = m.shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    string at = m.shader.GetPropertyName(i) + "Animated";
                    keep.Add(new NamedString(at, m.GetTag(at, false, "")));
                }
            }
            int q = m.renderQueue;
            m.shader = s;
            foreach (NamedString t in keep)
                if (m.GetTag(t.n, false, "") != t.v) m.SetOverrideTag(t.n, t.v);
            m.renderQueue = q;
        }

        /// <summary>A shader's own tag value (what a material without an override reports).</summary>
        static string ShaderTag(Shader s, string tag)
        {
            if (s == null) return "";
            Material probe = null;
            try
            {
                probe = new Material(s);
                return probe.GetTag(tag, false, "");
            }
            catch { return ""; }
            finally { if (probe != null) UnityEngine.Object.DestroyImmediate(probe); }
        }

        /// <summary>
        /// Locked Poiyomi stores the shader it was locked from in an override tag; the
        /// locked shader itself is "Hidden/Locked/...". Returns "" when not locked.
        /// </summary>
        public static string PoiyomiOriginalShader(Material m)
        {
            if (m == null || m.shader == null) return "";
            string orig = m.GetTag("OriginalShader", false, "");
            if (orig.Length == 0) return "";
            return m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal) ? orig : "";
        }

        public static bool IsLockedPoiyomi(Material m) { return PoiyomiOriginalShader(m).Length > 0; }
    }
}
