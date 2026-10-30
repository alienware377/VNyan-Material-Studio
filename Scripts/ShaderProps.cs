using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaterialStudio
{
    public enum PropUi { Slider, Number, IntNumber, Toggle, Enum, Color, Vector, Texture }

    /// <summary>One shader property, decoded from the runtime Shader API into what the UI needs.</summary>
    public class PropInfo
    {
        public int index;
        public string name;
        public string label;
        public PropType type;
        public PropUi ui;
        public ShaderPropertyFlags flags;
        public string[] attrs = new string[0];
        public float min, max;
        public bool hdr, isNormal, noScaleOffset, linearData;
        public bool hiddenInInspector;
        public string keyword;               // toggle keyword (null when none)
        public bool keywordOnWhenZero;       // ToggleOff
        public string[] enumNames;
        public float[] enumValues;
        public string[] enumKeywords;        // KeywordEnum: one keyword per option
        public List<ThryAction> actions;     // Thry on_value_actions
        public int depth;
        public string groupPath = "";

        public string Help
        {
            get
            {
                string s = label + "\n(" + name + ", " + type + ")";
                if (attrs.Length > 0) s += "\n[" + string.Join("] [", attrs) + "]";
                if (keyword != null) s += "\nTurns shader keyword " + keyword + (keywordOnWhenZero ? " on when OFF" : " on/off") + ".";
                if (enumKeywords != null) s += "\nEach option switches its own shader keyword.";
                if (actions != null && actions.Count > 0) s += "\nPicking an option also sets the related blend/queue values (Thry preset).";
                return s;
            }
        }
    }

    public class ThryAction
    {
        public float value;
        public readonly List<KeyValuePair<string, string>> sets = new List<KeyValuePair<string, string>>();
    }

    public class PropGroup
    {
        public string title;
        public int depth;
        public string key;                   // stable id for fold state
        public PropInfo toggle;              // Thry section enable toggle (reference_property)
        public readonly List<PropInfo> props = new List<PropInfo>();
        public readonly List<PropGroup> children = new List<PropGroup>();
        public PropGroup parent;

        public int CountDeep()
        {
            int n = props.Count;
            for (int i = 0; i < children.Count; i++) n += children[i].CountDeep();
            return n;
        }
    }

    public enum ShaderFamily { Unknown, Poiyomi, PoiyomiLocked, LilToon, MToon, MToon10, Standard, UnityBasic, Thry }

    /// <summary>Parsed layout of a shader: every editable property, grouped like the shader's own GUI.</summary>
    public class ShaderLayout
    {
        public Shader shader;
        public ShaderFamily family;
        public bool thry;
        public readonly List<PropInfo> all = new List<PropInfo>();
        public readonly Dictionary<string, PropInfo> byName = new Dictionary<string, PropInfo>();
        public readonly List<PropGroup> roots = new List<PropGroup>();

        static readonly Dictionary<Shader, ShaderLayout> cache = new Dictionary<Shader, ShaderLayout>();

        public static ShaderLayout For(Shader sh)
        {
            if (sh == null) return null;
            ShaderLayout l;
            if (cache.TryGetValue(sh, out l) && l != null) return l;
            l = Build(sh);
            cache[sh] = l;
            return l;
        }

        public static void ClearCache() { cache.Clear(); }

        public static ShaderFamily FamilyOf(Shader sh, Material m)
        {
            if (sh == null) return ShaderFamily.Unknown;
            string n = sh.name;
            string lower = n.ToLowerInvariant();
            if (m != null && RecipeEngine.IsLockedPoiyomi(m)) return ShaderFamily.PoiyomiLocked;
            if (lower.Contains("poiyomi")) return lower.StartsWith("hidden/locked") ? ShaderFamily.PoiyomiLocked : ShaderFamily.Poiyomi;
            if (lower.Contains("liltoon") || lower.StartsWith("_liltoon") || lower.StartsWith("hidden/liltoon")) return ShaderFamily.LilToon;
            if (n == "VRM/MToon") return ShaderFamily.MToon;
            if (lower.Contains("mtoon10")) return ShaderFamily.MToon10;
            if (lower.Contains("mtoon")) return ShaderFamily.MToon;
            if (n == "Standard" || n == "Standard (Specular setup)" || n == "Autodesk Interactive") return ShaderFamily.Standard;
            if (lower.StartsWith("unlit/") || lower.StartsWith("legacy shaders/") || lower.StartsWith("mobile/")
                || lower.StartsWith("particles/") || lower.StartsWith("sprites/") || lower.StartsWith("ui/")
                || lower.StartsWith("vrm/unlit") || lower.StartsWith("unigltf/")) return ShaderFamily.UnityBasic;
            return ShaderFamily.Unknown;
        }

        static ShaderLayout Build(Shader sh)
        {
            var L = new ShaderLayout { shader = sh, family = FamilyOf(sh, null) };
            int n = sh.GetPropertyCount();

            // Thry (Poiyomi and friends) marks its sections with m_start_/m_end_ properties.
            for (int i = 0; i < n && !L.thry; i++)
            {
                string pn = sh.GetPropertyName(i);
                if (pn.StartsWith("m_start_", StringComparison.Ordinal) || pn.EndsWith("Category", StringComparison.Ordinal) && pn.StartsWith("m_", StringComparison.Ordinal))
                    L.thry = true;
            }
            if (L.thry && L.family == ShaderFamily.Unknown) L.family = ShaderFamily.Thry;

            var general = new PropGroup { title = "General", depth = 0, key = "general" };
            L.roots.Add(general);
            PropGroup cur = general;
            var stack = new List<PropGroup>();
            bool anyHeader = false;

            for (int i = 0; i < n; i++)
            {
                string pn = sh.GetPropertyName(i);
                string desc = sh.GetPropertyDescription(i) ?? "";
                string label = desc;
                string opts = "";
                int dash = desc.IndexOf("--", StringComparison.Ordinal);
                if (dash >= 0) { label = desc.Substring(0, dash); opts = desc.Substring(dash + 2); }
                label = label.Trim();
                if (label.Length == 0) label = pn;
                string[] attrs;
                try { attrs = sh.GetPropertyAttributes(i) ?? new string[0]; } catch { attrs = new string[0]; }

                if (L.thry)
                {
                    if (pn.StartsWith("m_", StringComparison.Ordinal) && pn.EndsWith("Category", StringComparison.Ordinal))
                    {
                        stack.Clear();
                        cur = new PropGroup { title = label, depth = 0, key = pn };
                        L.roots.Add(cur);
                        continue;
                    }
                    if (pn.StartsWith("m_start_", StringComparison.Ordinal) || pn.StartsWith("s_start_", StringComparison.Ordinal))
                    {
                        var g = new PropGroup { title = label, depth = cur.depth + 1, key = pn, parent = cur };
                        g.toggle = null;
                        string refProp = ThryOption(opts, "reference_property");
                        if (refProp != null) g.title = label; // toggle resolved after the loop
                        if (refProp != null) pendingToggles.Add(new KeyValuePair<PropGroup, string>(g, refProp));
                        cur.children.Add(g);
                        stack.Add(cur);
                        cur = g;
                        continue;
                    }
                    if (pn.StartsWith("m_end_", StringComparison.Ordinal) || pn.StartsWith("s_end_", StringComparison.Ordinal))
                    {
                        if (stack.Count > 0) { cur = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); }
                        continue;
                    }
                    if (IsStructural(pn)) continue;
                }
                else
                {
                    string header = AttrArg(attrs, "Header");
                    if (header != null)
                    {
                        anyHeader = true;
                        cur = new PropGroup { title = header, depth = 0, key = "h_" + header };
                        L.roots.Add(cur);
                    }
                }

                var p = Decode(sh, i, pn, label, attrs, opts);
                if (p == null) continue;
                p.depth = cur.depth;
                p.groupPath = cur.key;
                cur.props.Add(p);
                L.all.Add(p);
                L.byName[pn] = p;
            }

            // Thry section enable toggles: show them on the section header instead of as a row.
            for (int i = 0; i < pendingToggles.Count; i++)
            {
                PropInfo tp;
                // some sections reference a colour-theme dropdown, not a switch: only real toggles
                if (L.byName.TryGetValue(pendingToggles[i].Value, out tp) && tp.ui == PropUi.Toggle) pendingToggles[i].Key.toggle = tp;
            }
            pendingToggles.Clear();

            // No grouping from the shader itself: group by the first word of the property name
            // so a 600-property lilToon list is still browsable.
            if (!L.thry && !anyHeader && general.props.Count > 24) RegroupByPrefix(L, general);

            // Drop empty groups (keeps the tree short).
            for (int i = L.roots.Count - 1; i >= 0; i--) Prune(L.roots[i]);
            for (int i = L.roots.Count - 1; i >= 0; i--)
                if (L.roots[i].CountDeep() == 0) L.roots.RemoveAt(i);
            return L;
        }

        static readonly List<KeyValuePair<PropGroup, string>> pendingToggles = new List<KeyValuePair<PropGroup, string>>();

        static void Prune(PropGroup g)
        {
            for (int i = g.children.Count - 1; i >= 0; i--)
            {
                Prune(g.children[i]);
                if (g.children[i].CountDeep() == 0 && g.children[i].toggle == null) g.children.RemoveAt(i);
            }
        }

        static bool IsStructural(string pn)
        {
            return pn.StartsWith("m_", StringComparison.Ordinal) || pn.StartsWith("g_", StringComparison.Ordinal)
                || pn.StartsWith("s_", StringComparison.Ordinal) || pn.StartsWith("shader_", StringComparison.Ordinal)
                || pn.StartsWith("footer_", StringComparison.Ordinal) || pn.StartsWith("Instancing", StringComparison.Ordinal);
        }

        static void RegroupByPrefix(ShaderLayout L, PropGroup general)
        {
            var order = new List<string>();
            var map = new Dictionary<string, PropGroup>();
            foreach (PropInfo p in general.props)
            {
                string w = FirstWord(p.name);
                PropGroup g;
                if (!map.TryGetValue(w, out g))
                {
                    g = new PropGroup { title = w, depth = 0, key = "w_" + w };
                    map[w] = g;
                    order.Add(w);
                }
                g.props.Add(p);
            }
            general.props.Clear();
            var misc = new PropGroup { title = "Other", depth = 0, key = "w_other" };
            L.roots.Remove(general);
            L.roots.Add(general);   // keep "General" first for the few loose ones
            foreach (string w in order)
            {
                PropGroup g = map[w];
                if (g.props.Count < 3) { misc.props.AddRange(g.props); continue; }
                L.roots.Add(g);
            }
            if (misc.props.Count > 0) L.roots.Add(misc);
            foreach (PropGroup g in L.roots) foreach (PropInfo p in g.props) p.groupPath = g.key;
        }

        static string FirstWord(string pn)
        {
            string s = pn.TrimStart('_');
            if (s.StartsWith("Use", StringComparison.Ordinal) && s.Length > 3) s = s.Substring(3);
            int end = 1;
            while (end < s.Length && !char.IsUpper(s[end]) && !char.IsDigit(s[end]) && s[end] != '_') end++;
            string w = s.Substring(0, Math.Min(end, s.Length));
            return w.Length == 0 ? "Other" : w;
        }

        static PropInfo Decode(Shader sh, int i, string pn, string label, string[] attrs, string opts)
        {
            ShaderPropertyFlags flags = sh.GetPropertyFlags(i);
            if ((flags & ShaderPropertyFlags.PerRendererData) != 0) return null;
            // Thry/Poiyomi bookkeeping (lock switch, "you forgot to lock", help boxes): the lock is
            // a real button in the header instead
            if (pn == "_ShaderOptimizerEnabled" || pn == "_ForgotToLockMaterial" || pn == "_LockTooltip") return null;
            for (int a = 0; a < attrs.Length; a++)
            {
                string an = AttrName(attrs[a]);
                if (an == "Helpbox" || an == "ThryShaderOptimizerLockButton") return null;
            }
            var p = new PropInfo
            {
                index = i, name = pn, label = label, attrs = attrs, flags = flags,
                type = RecipeEngine.KindOf(sh, i),
                hiddenInInspector = (flags & ShaderPropertyFlags.HideInInspector) != 0,
                hdr = (flags & ShaderPropertyFlags.HDR) != 0,
                isNormal = (flags & ShaderPropertyFlags.Normal) != 0,
                noScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0,
            };
            string lower = pn.ToLowerInvariant();
            p.linearData = p.isNormal || lower.Contains("bump") || lower.Contains("normal") || lower.Contains("mask")
                           || lower.Contains("metallic") || lower.Contains("smooth") || lower.Contains("occlusion")
                           || lower.Contains("height") || lower.Contains("parallax") || lower.Contains("noise");
            for (int a = 0; a < attrs.Length; a++) if (attrs[a] == "Normal") p.isNormal = true;

            switch (p.type)
            {
                case PropType.Color: p.ui = PropUi.Color; return p;
                case PropType.Vector: p.ui = PropUi.Vector; return p;
                case PropType.Texture: p.ui = PropUi.Texture; return p;
            }

            // numbers: toggles, enums, sliders
            if ((int)sh.GetPropertyType(i) == 3)
            {
                Vector2 r = sh.GetPropertyRangeLimits(i);
                p.min = r.x; p.max = r.y;
                p.ui = PropUi.Slider;
            }
            else p.ui = p.type == PropType.Int ? PropUi.IntNumber : PropUi.Number;

            for (int a = 0; a < attrs.Length; a++)
            {
                string at = attrs[a];
                string head = AttrName(at);
                string arg = AttrInner(at);
                switch (head)
                {
                    case "Toggle":
                    case "MaterialToggle":
                        p.ui = PropUi.Toggle;
                        p.keyword = string.IsNullOrEmpty(arg) ? pn.ToUpperInvariant() + "_ON" : arg.Trim();
                        break;
                    case "ToggleOff":
                        p.ui = PropUi.Toggle;
                        p.keyword = string.IsNullOrEmpty(arg) ? pn.ToUpperInvariant() + "_OFF" : arg.Trim();
                        p.keywordOnWhenZero = true;
                        break;
                    case "ThryToggle":
                    {
                        p.ui = PropUi.Toggle;
                        string kw = FirstArg(arg);
                        if (!string.IsNullOrEmpty(kw) && kw != "true" && kw != "false") p.keyword = kw;
                        break;
                    }
                    case "ToggleUI":
                    case "ThryToggleUI":
                    case "lilToggle":
                    case "lilToggleLeft":
                        p.ui = PropUi.Toggle;
                        break;
                    case "IntRange":
                        p.ui = PropUi.Slider;
                        break;
                    case "KeywordEnum":
                    {
                        string[] names = SplitArgs(arg);
                        p.ui = PropUi.Enum;
                        p.enumNames = names;
                        p.enumValues = new float[names.Length];
                        p.enumKeywords = new string[names.Length];
                        for (int k = 0; k < names.Length; k++)
                        {
                            p.enumValues[k] = k;
                            p.enumKeywords[k] = (pn + "_" + names[k]).ToUpperInvariant().Replace(' ', '_');
                        }
                        break;
                    }
                    case "Enum":
                    case "ThryWideEnum":
                    case "lilEnum":
                        if (DecodeEnum(arg, p)) p.ui = PropUi.Enum;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(opts) && opts.IndexOf("on_value_actions", StringComparison.Ordinal) >= 0)
                p.actions = ParseThryActions(opts);
            return p;
        }

        static bool DecodeEnum(string arg, PropInfo p)
        {
            string[] parts = SplitArgs(arg);
            if (parts.Length == 1)
            {
                // Enum(UnityEngine.Rendering.CullMode): a C# enum type
                Type t = FindType(parts[0]);
                if (t == null || !t.IsEnum) return false;
                string[] names = Enum.GetNames(t);
                Array vals = Enum.GetValues(t);
                p.enumNames = names;
                p.enumValues = new float[names.Length];
                for (int k = 0; k < names.Length; k++) p.enumValues[k] = Convert.ToSingle(vals.GetValue(k));
                return true;
            }
            if (parts.Length < 2 || parts.Length % 2 != 0) return false;
            int cnt = parts.Length / 2;
            p.enumNames = new string[cnt];
            p.enumValues = new float[cnt];
            for (int k = 0; k < cnt; k++)
            {
                p.enumNames[k] = parts[k * 2].Trim();
                float v;
                if (!float.TryParse(parts[k * 2 + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
                p.enumValues[k] = v;
            }
            return true;
        }

        static Type FindType(string name)
        {
            name = name.Trim();
            Type t = Type.GetType(name);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(name); } catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        static readonly Regex ActionBlock = new Regex(@"\{\s*value\s*:\s*([-0-9.]+)\s*,\s*actions\s*:\s*\[(.*?)\]\s*\}", RegexOptions.Singleline);
        static readonly Regex SetProp = new Regex(@"type\s*:\s*SET_PROPERTY\s*,\s*data\s*:\s*([^=}\s]+)\s*=\s*([^}\s]*)", RegexOptions.Singleline);

        static List<ThryAction> ParseThryActions(string opts)
        {
            var list = new List<ThryAction>();
            foreach (Match m in ActionBlock.Matches(opts))
            {
                float v;
                if (!float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                var a = new ThryAction { value = v };
                foreach (Match s in SetProp.Matches(m.Groups[2].Value))
                    a.sets.Add(new KeyValuePair<string, string>(s.Groups[1].Value.Trim(), s.Groups[2].Value.Trim()));
                if (a.sets.Count > 0) list.Add(a);
            }
            return list.Count > 0 ? list : null;
        }

        static string ThryOption(string opts, string key)
        {
            if (string.IsNullOrEmpty(opts)) return null;
            Match m = Regex.Match(opts, key + @"\s*:\s*([A-Za-z0-9_]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ------------------------------------------------------------ attribute helpers

        public static string AttrName(string at)
        {
            int p = at.IndexOf('(');
            return (p < 0 ? at : at.Substring(0, p)).Trim();
        }

        public static string AttrInner(string at)
        {
            int p = at.IndexOf('(');
            int q = at.LastIndexOf(')');
            if (p < 0 || q <= p) return "";
            return at.Substring(p + 1, q - p - 1);
        }

        static string AttrArg(string[] attrs, string name)
        {
            for (int i = 0; i < attrs.Length; i++)
                if (AttrName(attrs[i]) == name) return AttrInner(attrs[i]).Trim();
            return null;
        }

        static string FirstArg(string arg)
        {
            string[] a = SplitArgs(arg);
            return a.Length > 0 ? a[0].Trim() : null;
        }

        static string[] SplitArgs(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return new string[0];
            string[] raw = arg.Split(',');
            var outp = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string s = raw[i].Trim();
                if (s.Length > 0) outp.Add(s);
            }
            return outp.ToArray();
        }
    }
}
