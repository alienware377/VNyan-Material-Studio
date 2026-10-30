using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace MaterialStudio
{
    /// <summary>
    /// What a shader's own editor GUI would do when a value changes: flip keywords, apply
    /// render presets, fix blend state. At runtime there is no material editor, so without
    /// this a "Normal map" toggle would change a number and nothing on screen.
    /// </summary>
    public static class KeywordRules
    {
        /// <summary>Call after writing a number property through the UI.</summary>
        public static void AfterNumberChanged(Material m, ShaderLayout L, PropInfo p, float value)
        {
            if (m == null || p == null) return;
            // A locked Poiyomi shader is compiled for ONE keyword set. Flipping a keyword there
            // makes Unity draw its nearest variant, which can switch whole groups of effects off.
            // The value itself is baked in, so leave keywords alone (it applies on unlock/export).
            if (RecipeEngine.IsLockedPoiyomi(m)) return;
            if (p.keyword != null)
            {
                bool on = p.keywordOnWhenZero ? value < 0.5f : value >= 0.5f;
                SetKeyword(m, p.keyword, on);
            }
            if (p.enumKeywords != null)
            {
                int sel = Mathf.RoundToInt(value);
                for (int k = 0; k < p.enumKeywords.Length; k++) SetKeyword(m, p.enumKeywords[k], k == sel);
            }
            if (p.actions != null) RunThryActions(m, p, value);
            FamilyFixup(m, L, p.name);
        }

        /// <summary>Call after a texture or color changes (Standard/MToon key off those too).</summary>
        public static void AfterOtherChanged(Material m, ShaderLayout L, string propName)
        {
            FamilyFixup(m, L, propName);
        }

        /// <summary>
        /// Sets every toggle/enum keyword from the current property values. Used after a
        /// shader swap or a Poiyomi unlock, where the keywords on the material belong to
        /// the old shader.
        /// </summary>
        public static void RebuildKeywords(Material m, ShaderLayout L)
        {
            if (m == null || L == null) return;
            if (RecipeEngine.IsLockedPoiyomi(m)) return;   // see AfterNumberChanged
            foreach (PropInfo p in L.all)
            {
                if (p.keyword == null && p.enumKeywords == null) continue;
                if (!m.HasProperty(p.name)) continue;
                float v = p.type == PropType.Int ? RecipeEngine.GetIntProp(m, p.name) : m.GetFloat(p.name);
                if (p.keyword != null) SetKeyword(m, p.keyword, p.keywordOnWhenZero ? v < 0.5f : v >= 0.5f);
                if (p.enumKeywords != null)
                {
                    int sel = Mathf.RoundToInt(v);
                    for (int k = 0; k < p.enumKeywords.Length; k++) SetKeyword(m, p.enumKeywords[k], k == sel);
                }
            }
            FamilyFixup(m, L, null);
        }

        public static void SetKeyword(Material m, string kw, bool on)
        {
            if (string.IsNullOrEmpty(kw)) return;
            if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw);
        }

        static void RunThryActions(Material m, PropInfo p, float value)
        {
            foreach (ThryAction a in p.actions)
            {
                if (Mathf.Abs(a.value - value) > 0.001f) continue;
                foreach (var kv in a.sets)
                {
                    string key = kv.Key, val = kv.Value;
                    if (key == "render_queue")
                    {
                        int q;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out q)) m.renderQueue = q;
                    }
                    else if (key == "render_type") m.SetOverrideTag("RenderType", val);
                    else
                    {
                        float f;
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                            RecipeEngine.SetNumber(m, key, f);
                    }
                }
            }
        }

        // ------------------------------------------------------------ per family

        public static void FamilyFixup(Material m, ShaderLayout L, string changed)
        {
            ShaderFamily f = L != null ? L.family : ShaderLayout.FamilyOf(m.shader, m);
            try
            {
                switch (f)
                {
                    case ShaderFamily.Standard: StandardFixup(m, changed == "_Mode"); break;
                    case ShaderFamily.MToon: MToonValidate(m, changed == "_BlendMode"); break;
                    case ShaderFamily.MToon10: MToon10Validate(m); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[MaterialStudio] keyword fixup for " + f + " failed: " + e.Message);
            }
        }

        // Unity's StandardShaderGUI.SetupMaterialWithBlendMode + SetMaterialKeywords.
        static void StandardFixup(Material m, bool modeChanged)
        {
            if (modeChanged && m.HasProperty("_Mode"))
            {
                int mode = Mathf.RoundToInt(m.GetFloat("_Mode"));
                switch (mode)
                {
                    case 0:
                        m.SetOverrideTag("RenderType", "");
                        SetBlend(m, 1, 0, 1);
                        SetKeyword(m, "_ALPHATEST_ON", false); SetKeyword(m, "_ALPHABLEND_ON", false); SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                        m.renderQueue = -1;
                        break;
                    case 1:
                        m.SetOverrideTag("RenderType", "TransparentCutout");
                        SetBlend(m, 1, 0, 1);
                        SetKeyword(m, "_ALPHATEST_ON", true); SetKeyword(m, "_ALPHABLEND_ON", false); SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                        m.renderQueue = 2450;
                        break;
                    case 2:
                        m.SetOverrideTag("RenderType", "Transparent");
                        SetBlend(m, 5, 10, 0);
                        SetKeyword(m, "_ALPHATEST_ON", false); SetKeyword(m, "_ALPHABLEND_ON", true); SetKeyword(m, "_ALPHAPREMULTIPLY_ON", false);
                        m.renderQueue = 3000;
                        break;
                    case 3:
                        m.SetOverrideTag("RenderType", "Transparent");
                        SetBlend(m, 1, 10, 0);
                        SetKeyword(m, "_ALPHATEST_ON", false); SetKeyword(m, "_ALPHABLEND_ON", false); SetKeyword(m, "_ALPHAPREMULTIPLY_ON", true);
                        m.renderQueue = 3000;
                        break;
                }
            }
            bool spec = m.shader != null && m.shader.name.Contains("Specular");
            SetKeyword(m, "_NORMALMAP", HasTex(m, "_BumpMap") || HasTex(m, "_DetailNormalMap"));
            if (spec) SetKeyword(m, "_SPECGLOSSMAP", HasTex(m, "_SpecGlossMap"));
            else SetKeyword(m, "_METALLICGLOSSMAP", HasTex(m, "_MetallicGlossMap"));
            SetKeyword(m, "_PARALLAXMAP", HasTex(m, "_ParallaxMap"));
            SetKeyword(m, "_DETAIL_MULX2", HasTex(m, "_DetailAlbedoMap") || HasTex(m, "_DetailNormalMap"));
            if (m.HasProperty("_EmissionColor"))
            {
                Color ec = m.GetColor("_EmissionColor");
                bool emit = ec.maxColorComponent > 0.1f / 255f;
                SetKeyword(m, "_EMISSION", emit);
                m.globalIlluminationFlags = emit ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                                                 : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            if (m.HasProperty("_SmoothnessTextureChannel"))
                SetKeyword(m, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", Mathf.RoundToInt(m.GetFloat("_SmoothnessTextureChannel")) == 1);
            if (m.HasProperty("_SpecularHighlights"))
                SetKeyword(m, "_SPECULARHIGHLIGHTS_OFF", m.GetFloat("_SpecularHighlights") < 0.5f);
            if (m.HasProperty("_GlossyReflections"))
                SetKeyword(m, "_GLOSSYREFLECTIONS_OFF", m.GetFloat("_GlossyReflections") < 0.5f);
        }

        static void SetBlend(Material m, int src, int dst, int zwrite)
        {
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", src);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", dst);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", zwrite);
        }

        static bool HasTex(Material m, string p) { return m.HasProperty(p) && m.GetTexture(p) != null; }

        // MToon 0.x keeps its render state in sync through MToon.Utils.ValidateProperties.
        static MethodInfo mtoonValidate;
        static bool mtoonSearched;

        static void MToonValidate(Material m, bool blendChanged)
        {
            if (!mtoonSearched)
            {
                mtoonSearched = true;
                Type t = FindType("MToon.Utils");
                if (t != null)
                    mtoonValidate = t.GetMethod("ValidateProperties", BindingFlags.Public | BindingFlags.Static,
                        null, new Type[] { typeof(Material), typeof(bool) }, null);
            }
            if (mtoonValidate != null) mtoonValidate.Invoke(null, new object[] { m, blendChanged });
        }

        static Type mtoon10Type;
        static bool mtoon10Searched;

        static void MToon10Validate(Material m)
        {
            if (!mtoon10Searched)
            {
                mtoon10Searched = true;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = a.GetTypes(); } catch (ReflectionTypeLoadException e) { ts = e.Types; } catch { continue; }
                    if (ts == null) continue;
                    foreach (Type t in ts)
                        if (t != null && t.Name == "MToonValidator") { mtoon10Type = t; break; }
                    if (mtoon10Type != null) break;
                }
            }
            if (mtoon10Type == null) return;
            object v = Activator.CreateInstance(mtoon10Type, new object[] { m });
            MethodInfo val = mtoon10Type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (val != null) val.Invoke(v, null);
        }

        static Type FindType(string full)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(full); } catch { }
                if (t != null) return t;
            }
            return null;
        }
    }
}
