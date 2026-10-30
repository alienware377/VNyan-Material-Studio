using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaterialStudio
{
    /// <summary>
    /// Shader feature switches (Poiyomi's section toggles, Standard's normal map/emission, ...)
    /// pick a compiled VARIANT of the shader by keyword set. A shader inside the avatar (or in
    /// our bundle) only has the combinations it was compiled with, and its keywordSpace lists
    /// ONLY the keywords of those variants (measured: Poiyomi 8.0 copy knew 36 of its 60+). Ask
    /// for anything else and Unity silently draws the nearest variant: a switch that does
    /// nothing, or one that drops a whole group of effects.
    ///
    /// So: work out the FEATURE keywords a material wants (from the shader's own toggle and
    /// enum properties, not its keyword space), check that exact set exists, swap to a
    /// same-named bundle copy that has it, or compile it in the background. While compiling, the
    /// material keeps the last combination that rendered correctly, and gets the new one the
    /// moment it exists.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        readonly Dictionary<Shader, HashSet<string>> shippedSets = new Dictionary<Shader, HashSet<string>>();
        readonly Dictionary<Shader, HashSet<string>> featureKw = new Dictionary<Shader, HashSet<string>>();
        readonly Dictionary<Material, string[]> lastGood = new Dictionary<Material, string[]>();
        readonly Dictionary<Material, string[]> pendingKw = new Dictionary<Material, string[]>();
        readonly HashSet<Material> compileFailed = new HashSet<Material>();
        // what the last compile was asked for per material, and the feature keywords the user flipped
        readonly Dictionary<Material, string> requestedKey = new Dictionary<Material, string>();
        readonly Dictionary<Material, List<string>> touchedKw = new Dictionary<Material, List<string>>();
        float variantBuildAt = -1f;
        readonly List<MatEntry> missingVariant = new List<MatEntry>();

        /// <summary>Keywords the shader's own switches and dropdowns can turn on.</summary>
        HashSet<string> FeatureKeywordsOf(Shader s)
        {
            HashSet<string> set;
            if (featureKw.TryGetValue(s, out set)) return set;
            set = new HashSet<string>();
            ShaderLayout L = ShaderLayout.For(s);
            if (L != null)
                foreach (PropInfo p in L.all)
                {
                    if (p.keyword != null) set.Add(p.keyword);
                    if (p.enumKeywords != null) foreach (string k in p.enumKeywords) set.Add(k);
                }
            featureKw[s] = set;
            return set;
        }

        static HashSet<string> SpaceOf(Shader s)
        {
            var h = new HashSet<string>();
            try { h.UnionWith(s.keywordSpace.keywordNames); } catch { }
            return h;
        }

        /// <summary>The keywords that matter for the variant: compiled ones plus the shader's own feature keywords (no leftovers).</summary>
        string[] WantedKeywords(Shader s, string[] keywords)
        {
            if (s == null || keywords == null) return new string[0];
            HashSet<string> space = SpaceOf(s), feat = FeatureKeywordsOf(s);
            var outp = new List<string>();
            foreach (string k in keywords)
                if (!string.IsNullOrEmpty(k) && (space.Contains(k) || feat.Contains(k))) outp.Add(k);
            return outp.ToArray();
        }

        void IndexShippedVariants()
        {
            shippedSets.Clear();
            featureKw.Clear();
            foreach (MatEntry e in reg.entries)
            {
                if (e.orig == null || e.orig.shader == null) continue;
                HashSet<string> set;
                if (!shippedSets.TryGetValue(e.orig.shader, out set)) { set = new HashSet<string>(); shippedSets[e.orig.shader] = set; }
                set.Add(new KeywordSet(WantedKeywords(e.orig.shader, e.orig.keywords)).Key());
                // the shipped look always rendered: a safe stand-in while something compiles
                if (e.mat != null && !lastGood.ContainsKey(e.mat) && e.mat.shader == e.orig.shader) lastGood[e.mat] = e.orig.keywords;
            }
        }

        bool VariantKnown(Shader s, string[] keywords)
        {
            if (s == null) return true;
            if (s.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal)) return true;   // locked: compiled as one set
            string[] want = WantedKeywords(s, keywords);
            HashSet<string> space = SpaceOf(s);
            if (space.Count > 0)
                foreach (string k in want) if (!space.Contains(k)) return false;   // no variant has it at all
            string key = new KeywordSet(want).Key();
            if (catalog.IsBundleShader(s)) return catalog.HasVariantKey(s, key);
            HashSet<string> set;
            if (!shippedSets.TryGetValue(s, out set)) return true;   // not an avatar shader we can judge
            return set.Contains(key);
        }

        /// <summary>Finds materials asking for a missing combination and fixes what it can right away.</summary>
        void CheckVariants()
        {
            variantWarning = "";
            missingVariant.Clear();
            if (!cfg.enabled) { variantBuildAt = -1f; return; }   // switched off: shipped materials, nothing to compile
            int swapped = 0;
            foreach (MatEntry e in reg.entries)
            {
                Material m = e.mat;
                if (m == null || m.shader == null) continue;
                string[] wantKw = m.shaderKeywords;
                if (VariantKnown(m.shader, wantKw))
                {
                    lastGood[m] = wantKw;
                    pendingKw.Remove(m);
                    continue;
                }
                // a same-named shader from our bundle that has this exact combination?
                Shader b = catalog.FindBundleShader(m.shader.name);
                if (b != null && b != m.shader && VariantKnown(b, wantKw))
                {
                    int q = m.renderQueue;
                    m.shader = b;
                    m.renderQueue = q;
                    lastGood[m] = wantKw;
                    pendingKw.Remove(m);
                    swapped++;
                    continue;
                }
                // keep drawing the last combination that worked until the new one exists
                string[] good;
                if (lastGood.TryGetValue(m, out good) && good != null)
                {
                    pendingKw[m] = wantKw;
                    m.shaderKeywords = good;
                }
                if (compileFailed.Contains(m)) continue;   // Unity could not build it; wait for the next edit
                missingVariant.Add(e);
            }
            if (swapped > 0) Debug.Log(LOG + "moved " + swapped + " material(s) onto compiled shader copies that have their features.");
            if (missingVariant.Count == 0) { variantBuildAt = -1f; return; }

            var names = new List<string>();
            foreach (MatEntry e in missingVariant) names.Add(e.mat.name);
            Debug.Log(LOG + "feature combination not compiled yet for: " + string.Join(", ", names.ToArray()));
            if (cfg.autoBuildVariants)
            {
                variantWarning = "Compiling the feature you switched for " + missingVariant.Count + " material(s) in the background (Unity, about half a minute; " +
                                 "longer the very first time). It switches over by itself.";
                variantBuildAt = Time.unscaledTime + 2f;   // collect a few clicks into one compile
                SetStatus("Compiling the feature you switched (see the bar under the window).");
            }
            else
                variantWarning = missingVariant.Count + " material(s) want a feature this shader copy was not compiled with. " +
                                 "Packages > Build compiles it (or turn on automatic compiling in Settings).";
        }

        /// <summary>The keywords a material really wants (pending ones win over what it shows meanwhile).</summary>
        string[] DesiredKeywords(Material m)
        {
            string[] k;
            return m != null && pendingKw.TryGetValue(m, out k) ? k : (m != null ? m.shaderKeywords : new string[0]);
        }

        /// <summary>After a compile: hand every waiting material its real keywords, then re-check.</summary>
        void FinishVariantBuild(bool ok)
        {
            foreach (var kv in new List<KeyValuePair<Material, string[]>>(pendingKw))
                if (kv.Key != null) kv.Key.shaderKeywords = kv.Value;
            pendingKw.Clear();
            // Only give up on a material when the compile was asked for EXACTLY what it wants now.
            // Edits made while Unity was compiling simply get the next compile.
            var failed = new List<MatEntry>();
            foreach (MatEntry e in reg.entries)
            {
                Material m = e.mat;
                if (m == null || m.shader == null || VariantKnown(m.shader, m.shaderKeywords)) continue;
                Shader b = catalog.FindBundleShader(m.shader.name);
                if (b != null && VariantKnown(b, m.shaderKeywords)) continue;
                string asked, now = new KeywordSet(WantedKeywords(m.shader, m.shaderKeywords)).Key();
                if (ok && requestedKey.TryGetValue(m, out asked) && asked == now) failed.Add(e);
            }
            foreach (MatEntry e in failed) compileFailed.Add(e.mat);
            CheckVariants();
            if (failed.Count > 0)
            {
                var names = new List<string>();
                foreach (MatEntry e in failed) names.Add(e.mat.name);
                Debug.Log(LOG + "compiled, but the combination is still missing for: " + string.Join(", ", names.ToArray()));
                variantWarning = "Unity could not compile that combination for " + string.Join(", ", names.ToArray()) +
                                 "; it keeps the last look that worked. See the Packages tab log.";
            }
        }

        /// <summary>A feature switch was flipped: remember it (compiles include its on and off versions) and retry failures.</summary>
        void NoteFeatureToggle(Material m, PropInfo p)
        {
            if (m == null || p == null) return;
            compileFailed.Remove(m);
            var kws = new List<string>();
            if (p.keyword != null) kws.Add(p.keyword);
            if (p.enumKeywords != null) kws.AddRange(p.enumKeywords);
            if (kws.Count == 0) return;
            List<string> t;
            if (!touchedKw.TryGetValue(m, out t)) { t = new List<string>(); touchedKw[m] = t; }
            foreach (string k in kws) { t.Remove(k); t.Insert(0, k); }   // most recent first
            if (t.Count > 12) t.RemoveRange(12, t.Count - 12);
        }

        /// <summary>
        /// The keyword sets one compile should produce for a material: what it wants now plus
        /// every on/off mix of the (up to 5 most recent) switches the user has been flipping, so
        /// flipping them again is instant. Enum keywords count as one switch each option.
        /// </summary>
        List<KeywordSet> VariantsToCompile(Material m, string[] want)
        {
            var outp = new List<KeywordSet>();
            string[] w = WantedKeywords(m.shader, want);
            outp.Add(new KeywordSet(w));
            List<string> t;
            if (!touchedKw.TryGetValue(m, out t) || t.Count == 0) return outp;
            var flips = new List<string>();
            foreach (string k in t) { if (flips.Count >= 5) break; if (!flips.Contains(k)) flips.Add(k); }
            var baseSet = new HashSet<string>(w);
            foreach (string k in flips) baseSet.Remove(k);
            int n = flips.Count;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                var set = new List<string>(baseSet);
                for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) set.Add(flips[i]);
                var ks = new KeywordSet(set);
                if (!outp.Exists(delegate (KeywordSet x) { return x.Key() == ks.Key(); })) outp.Add(ks);
            }
            return outp;
        }

        /// <summary>From Update: start the debounced background compile when nothing else runs.</summary>
        void TickVariantBuild()
        {
            if (variantBuildAt < 0f || Time.unscaledTime < variantBuildAt || runner.Busy) return;
            variantBuildAt = -1f;
            if (missingVariant.Count == 0) return;
            SetJob("Compiling the switched feature", VariantJobDetail(), "switched over");
            StartShaderBuild(true);
        }
    }
}
