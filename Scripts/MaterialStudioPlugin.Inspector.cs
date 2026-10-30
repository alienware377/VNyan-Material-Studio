using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    public partial class MaterialStudioPlugin
    {
        const float LabelW = 170f;
        const float CtlX = 182f;
        const float RowH = 28f, RowPitch = 30f;

        string propSearch = "";
        readonly HashSet<string> openGroups = new HashSet<string>();
        readonly List<Action> rowRefreshers = new List<Action>();
        RectTransform propContent;
        ScrollRect propScroll;
        GameObject popup;

        // ------------------------------------------------------------------ tab

        void BuildPropertiesTab(RectTransform P)
        {
            Ui.Clear(P);
            ClosePopup();
            rowRefreshers.Clear();
            float pw = RightW;
            MatEntry e = Primary;
            if (boundAvatar == null) { Ui.Label("Empty", P, "Load an avatar in VNyan first.", 10, 10, pw - 20, 24, 12); return; }
            if (e == null || e.mat == null) { Ui.Label("Empty", P, "Pick a material on the left.", 10, 10, pw - 20, 24, 12); return; }
            Material m = e.mat;
            ShaderLayout L = ShaderLayout.For(m.shader);
            bool locked = RecipeEngine.IsLockedPoiyomi(m);

            string head = m.name + (selection.Count > 1 ? "   (+" + (selection.Count - 1) + " more selected)" : "");
            Ui.Label("MatName", P, head, 10, 4, pw - 20, 20, 13, TextAnchor.MiddleLeft, FontStyle.Bold);
            string fam = L != null ? FamilyText(locked ? ShaderFamily.PoiyomiLocked : L.family) : "";
            Text sh = Ui.Label("ShaderName", P, (m.shader != null ? m.shader.name : "(no shader)") + (fam.Length > 0 ? "   - " + fam : ""), 10, 24, pw - 20, 18, 11);
            sh.color = Ui.TextDim;
            float y = 44f;

            if (locked)
            {
                Text lt = Ui.Label("LockNote", P, "Locked & optimised. Values marked * are baked in: Unlock to change them. Unmarked ones (kept live) still change.", 10, y, pw - 210, 32, 10, TextAnchor.UpperLeft);
                lt.color = Ui.Warn;
                Button ub = Ui.Button("Button_UnlockP", P, "Unlock for editing", pw - 194, y + 2, 184, 24, UnlockPoiyomi);
                tips.Chip((RectTransform)ub.transform, "UnlockP", "Switch the selected materials back to the full Poiyomi shader they were locked from, so every value edits live. " +
                    "When the material came locked with the avatar, the full shader must be in your shader bundle (Packages tab). Lock again when you are done.", false);
                y += 36f;
            }
            else if (IsUnlockedPoiyomi(m))
            {
                Text lt = Ui.Label("LockNote", P, "Unlocked (editing mode, slower to render). Lock & optimise when you are happy. Tick L on values you want to keep changing after.", 10, y, pw - 330, 32, 10, TextAnchor.UpperLeft);
                lt.color = Ui.TextDim;
                Button lb = Ui.Button("Button_LockP", P, "Lock & optimise", pw - 314, y + 2, 150, 24, delegate { StartLock(false); });
                tips.Chip((RectTransform)lb.transform, "LockP", "Runs Poiyomi's own optimizer on the selected materials, the same as its Lock In button in Unity: everything not marked L is baked in, " +
                    "so the shader gets much faster. It runs in the background in the private Unity project (Packages tab) and takes a minute or two; VNyan stays usable. Undo works.", false);
                Button la = Ui.Button("Button_LockAll", P, "Lock all Poiyomi", pw - 160, y + 2, 150, 24, delegate { StartLock(true); });
                tips.Chip((RectTransform)la.transform, "LockAll", "Lock & optimise every unlocked Poiyomi material on this avatar in one go.", false);
                y += 36f;
            }
            if (variantWarning.Length > 0)
            {
                Text vw = Ui.Label("VariantNote", P, variantWarning, 10, y, pw - 20, 30, 10, TextAnchor.UpperLeft);
                vw.color = Ui.Warn;
                y += 32f;
            }

            Ui.Label("SearchL", P, "Find", 10, y, 36, 22, 12);
            InputField sf = Ui.Input("Input_PropSearch", P, propSearch, 46, y, 200, 22, InputField.ContentType.Standard, null);
            sf.onValueChanged.AddListener(delegate (string s) { propSearch = s; BuildPropRows(P, propTop); });
            tips.Chip((RectTransform)sf.transform, "PropSearch", "Type part of a name (2+ letters) to list matching properties from every section.", false);
            Ui.Button("Button_Expand", P, "Open all", 256, y, 70, 22, delegate { SetAllOpen(L, true); BuildPropRows(P, propTop); }, 11);
            Ui.Button("Button_Collapse", P, "Close all", 330, y, 70, 22, delegate { SetAllOpen(L, false); BuildPropRows(P, propTop); }, 11);
            Ui.Label("RQL", P, "Queue", 410, y, 44, 22, 11, TextAnchor.MiddleRight);
            InputField rq = Ui.Input("Input_Queue", P, m.renderQueue.ToString(), 458, y, 60, 22, InputField.ContentType.IntegerNumber, delegate (string s)
            {
                int q; if (int.TryParse(s, out q)) { EditRenderQueue(Mathf.Clamp(q, 0, 5000)); RefreshRows(); }
            });
            tips.Chip((RectTransform)rq.transform, "Queue", "Render queue: draw order. 2000 opaque, 2450 cutout, 3000 transparent. Higher draws later (on top). Press Enter to apply.", false);
            rowRefreshers.Add(delegate { if (rq != null && !rq.isFocused && Primary != null && Primary.mat != null) rq.text = Primary.mat.renderQueue.ToString(); });
            y += 28f;
            propTop = y;
            BuildPropRows(P, y);
        }

        float propTop;

        static string FamilyText(ShaderFamily f)
        {
            switch (f)
            {
                case ShaderFamily.Poiyomi: return "Poiyomi";
                case ShaderFamily.PoiyomiLocked: return "Poiyomi (locked)";
                case ShaderFamily.LilToon: return "lilToon";
                case ShaderFamily.MToon: return "MToon (VRM 0.x)";
                case ShaderFamily.MToon10: return "MToon 1.0";
                case ShaderFamily.Standard: return "Unity Standard";
                case ShaderFamily.UnityBasic: return "Unity basic";
                case ShaderFamily.Thry: return "Thry-based";
            }
            return "";
        }

        void SetAllOpen(ShaderLayout L, bool open)
        {
            if (L == null) return;
            var stack = new List<PropGroup>(L.roots);
            while (stack.Count > 0)
            {
                PropGroup g = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                string k = FoldKey(L, g);
                if (open) openGroups.Add(k); else openGroups.Remove(k);
                stack.AddRange(g.children);
            }
        }

        static string FoldKey(ShaderLayout L, PropGroup g) { return (L.shader != null ? L.shader.name : "") + "#" + g.key; }

        void BuildPropRows(RectTransform P, float top)
        {
            Transform old = P.Find("PropList");
            float keep = 1f;
            if (old != null)
            {
                ScrollRect osr = old.GetComponent<ScrollRect>();
                if (osr != null) keep = osr.verticalNormalizedPosition;
                old.gameObject.SetActive(false);
                Destroy(old.gameObject);
            }
            rowRefreshers.RemoveAll(delegate (Action a) { return a == null; });
            MatEntry e = Primary;
            if (e == null || e.mat == null) return;
            Material m = e.mat;
            ShaderLayout L = ShaderLayout.For(m.shader);
            propContent = Ui.Scroll("PropList", P, 6, top, RightW - 12, P.rect.height - top - 6, out propScroll);
            if (L == null) return;
            float y = 2f;
            string q = (propSearch ?? "").Trim().ToLowerInvariant();
            if (q.Length >= 2)
            {
                foreach (PropInfo p in L.all)
                {
                    if (!Visible(L, p)) continue;
                    if (p.label.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0 && p.name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                    y = BuildRow(p, m, L, y, 0f);
                }
                if (y <= 2f) Ui.Label("None", propContent, "Nothing matches.", 8, 4, 300, 20, 11);
            }
            else
            {
                foreach (PropGroup g in L.roots) y = BuildGroup(g, m, L, y);
            }
            Ui.SetContentHeight(propContent, y + 6f);
            propScroll.verticalNormalizedPosition = keep;
        }

        bool Visible(ShaderLayout L, PropInfo p)
        {
            if (!p.hiddenInInspector) return true;
            return cfg.showHidden || L.thry;
        }

        float BuildGroup(PropGroup g, Material m, ShaderLayout L, float y)
        {
            int count = 0;
            foreach (PropInfo p in g.props) if (Visible(L, p)) count++;
            if (count == 0 && g.children.Count == 0 && g.toggle == null) return y;
            string key = FoldKey(L, g);
            bool open = openGroups.Contains(key);
            float indent = g.depth * 14f;
            Image hdr = Ui.Box("G_" + g.key, propContent, 2 + indent, y, RightW - 30 - indent, 24, g.depth == 0 ? new Color(0.24f, 0.26f, 0.33f, 1f) : new Color(0.20f, 0.21f, 0.26f, 1f));
            Text ht = Ui.Label("T", hdr.transform, (open ? "[-] " : "[+] ") + g.title + "  (" + g.CountDeep() + ")", 6, 0, RightW - 205 - indent, 24, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            // per-section copy / paste, like Poiyomi's right-click menu in Unity
            PropGroup sg = g;
            Button cpy = Ui.Button("SecCopy", hdr.transform, "Copy", RightW - 196 - indent, 3, 42, 18, delegate { CopySection(sg); }, 10);
            tips.Chip((RectTransform)cpy.transform, "SecCopy_" + g.key, "Copy every value in this section (and its sub-sections), from the first selected material.", false);
            Button pst = Ui.Button("SecPaste", hdr.transform, "Paste", RightW - 150 - indent, 3, 46, 18, delegate { PasteSection(sg); }, 10);
            pst.GetComponent<Image>().color = sectionClip != null ? new Color(0.24f, 0.40f, 0.30f, 1f) : Ui.Btn;
            tips.Chip((RectTransform)pst.transform, "SecPaste_" + g.key, "Paste the copied section onto every selected material. Works between matching sections too " +
                "(Matcap 0 to Matcap 1, Decal 0 to Decal 2, Emission 0 to 1...): values are matched by name, ignoring the section numbers. Undo works.", false);
            var cc = hdr.gameObject.AddComponent<ClickCatcher>();
            PropGroup cg = g;
            cc.onClick = delegate
            {
                if (openGroups.Contains(key)) openGroups.Remove(key); else openGroups.Add(key);
                BuildPropRows(tabPanels[0], propTop);
            };
            if (g.toggle != null && m.HasProperty(g.toggle.name))
            {
                PropInfo tp = g.toggle;
                bool on = m.GetFloat(tp.name) >= 0.5f;
                bool baked = RecipeEngine.IsLockedPoiyomi(m) && !IsAnimated(m, tp.name);
                Toggle t = Ui.Check("SecToggle", hdr.transform, baked ? "baked" : "on", RightW - 100 - indent, 3, 70, 18, on, delegate (bool v)
                {
                    LogToggle(tp, v);
                    EditNumber(tp.name, v ? 1f : 0f);
                    RefreshRows();
                });
                if (baked)
                {
                    t.interactable = false;
                    Text bl = t.GetComponentInChildren<Text>();
                    if (bl != null) bl.color = Ui.TextDim;
                }
                tips.Chip((RectTransform)t.transform, "Sec_" + tp.name, baked
                    ? "This material is locked, so this section is compiled in as it was (" + (on ? "on" : "off") + "). Press Unlock for editing to switch it, then lock again."
                    : "Turns this whole section on or off (" + tp.name + "). A feature this shader copy was not compiled with is compiled in the background.", false);
            }
            y += 26f;
            if (!open) return y;
            foreach (PropInfo p in g.props)
            {
                if (!Visible(L, p) || p == g.toggle) continue;
                y = BuildRow(p, m, L, y, indent + 8f);
            }
            foreach (PropGroup c in g.children) y = BuildGroup(c, m, L, y);
            return y;
        }

        /// <summary>One log line per section switch: which materials, locked or not, keywords before.</summary>
        void LogToggle(PropInfo tp, bool on)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Material m in Targets(tp.name))
                sb.Append(m.name).Append(RecipeEngine.IsLockedPoiyomi(m) ? " (locked)" : (catalog.IsBundleShader(m.shader) ? " (compiled copy)" : " (avatar shader)")).Append("; ");
            Debug.Log(LOG + "section " + tp.name + (tp.keyword != null ? " [" + tp.keyword + "]" : "") + " -> " + (on ? "on" : "off") + " on " + sb);
        }

        // ------------------------------------------------------------------ section copy / paste

        class SectionClip
        {
            public string groupKey, title, from;
            public readonly List<string> order = new List<string>();   // source names in section order
            public readonly Dictionary<string, float> floats = new Dictionary<string, float>();
            public readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
            public readonly Dictionary<string, Vector4> vectors = new Dictionary<string, Vector4>();
            public readonly Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
            public readonly Dictionary<string, Vector4> st = new Dictionary<string, Vector4>();
            public readonly Dictionary<string, string> anim = new Dictionary<string, string>();
            public int Count { get { return floats.Count + colors.Count + vectors.Count + textures.Count; } }
        }

        SectionClip sectionClip;

        static void SectionProps(PropGroup g, List<PropInfo> into)
        {
            if (g.toggle != null) into.Add(g.toggle);
            into.AddRange(g.props);
            foreach (PropGroup c in g.children) SectionProps(c, into);
        }

        /// <summary>
        /// Source name -> target name. Exact names first (same section); then names with every
        /// digit removed, in section order, because Poiyomi numbers its copies inconsistently
        /// (_MatcapColor / _Matcap2Color, _DecalEnabled / _DecalEnabled1, _Decal0Depth / _Decal1Depth).
        /// The type must match too.
        /// </summary>
        static int ClipKind(SectionClip c, string n)
        {
            if (c.floats.ContainsKey(n)) return 0;
            if (c.colors.ContainsKey(n)) return 1;
            if (c.vectors.ContainsKey(n)) return 2;
            if (c.textures.ContainsKey(n)) return 3;
            return -1;
        }

        static int PropKind(PropInfo p)
        {
            switch (p.type)
            {
                case PropType.Color: return 1;
                case PropType.Vector: return 2;
                case PropType.Texture: return 3;
                default: return 0;
            }
        }

        static Dictionary<string, string> MapSection(List<string> source, List<PropInfo> target, Func<string, int> kindOf)
        {
            var map = new Dictionary<string, string>();
            var byName = new Dictionary<string, PropInfo>();
            foreach (PropInfo p in target) byName[p.name] = p;
            var used = new HashSet<string>();
            foreach (string n in source)
                if (byName.ContainsKey(n) && PropKind(byName[n]) == kindOf(n)) { map[n] = n; used.Add(n); }
            var byKey = new Dictionary<string, List<PropInfo>>();
            foreach (PropInfo p in target)
            {
                if (used.Contains(p.name)) continue;
                string k = NoDigits(p.name);
                List<PropInfo> l;
                if (!byKey.TryGetValue(k, out l)) { l = new List<PropInfo>(); byKey[k] = l; }
                l.Add(p);
            }
            foreach (string n in source)
            {
                if (map.ContainsKey(n)) continue;
                List<PropInfo> l;
                if (!byKey.TryGetValue(NoDigits(n), out l)) continue;
                int at = l.FindIndex(delegate (PropInfo p) { return PropKind(p) == kindOf(n); });
                if (at < 0) continue;
                map[n] = l[at].name;
                l.RemoveAt(at);
            }
            return map;
        }

        static string NoDigits(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s) if (ch < '0' || ch > '9') sb.Append(ch);
            return sb.ToString();
        }

        void CopySection(PropGroup g)
        {
            Material m = Primary != null ? Primary.mat : null;
            if (m == null) return;
            var props = new List<PropInfo>();
            SectionProps(g, props);
            var c = new SectionClip { groupKey = g.key, title = g.title, from = m.name };
            foreach (PropInfo p in props)
            {
                if (!m.HasProperty(p.name)) continue;
                if (!c.order.Contains(p.name)) c.order.Add(p.name);
                switch (p.type)
                {
                    case PropType.Float: c.floats[p.name] = m.GetFloat(p.name); break;
                    case PropType.Int: c.floats[p.name] = RecipeEngine.GetIntProp(m, p.name); break;
                    case PropType.Color: c.colors[p.name] = m.GetColor(p.name); break;
                    case PropType.Vector: c.vectors[p.name] = m.GetVector(p.name); break;
                    case PropType.Texture:
                        c.textures[p.name] = m.GetTexture(p.name);
                        Vector2 sc = m.GetTextureScale(p.name), of = m.GetTextureOffset(p.name);
                        c.st[p.name] = new Vector4(sc.x, sc.y, of.x, of.y);
                        break;
                }
                string a = m.GetTag(p.name + "Animated", false, "");
                if (a.Length > 0) c.anim[p.name] = a;
            }
            sectionClip = c;
            SetStatus("Copied '" + g.title + "' (" + c.Count + " values) from " + m.name + ". Paste it on any material's '" + g.title + "' section.");
            RefreshRows();
            BuildPropRows(tabPanels[0], propTop);
        }

        void PasteSection(PropGroup g)
        {
            SectionClip c = sectionClip;
            if (c == null) { SetStatus("Copy a section first (its Copy button)."); return; }
            var props = new List<PropInfo>();
            SectionProps(g, props);
            Dictionary<string, string> map = MapSection(c.order, props, delegate (string n) { return ClipKind(c, n); });
            var names = new HashSet<string>(map.Values);
            List<Material> mats = Targets(null);
            if (mats.Count == 0) return;
            BeginEdit("paste " + g.title, mats);
            int set = 0;
            foreach (Material m in mats)
            {
                foreach (var mv in map)
                {
                    string src = mv.Key, dst = mv.Value;
                    if (!m.HasProperty(dst)) continue;
                    float f; Color col; Vector4 vec; Texture tex;
                    if (c.floats.TryGetValue(src, out f)) { RecipeEngine.SetNumber(m, dst, f); set++; }
                    else if (c.colors.TryGetValue(src, out col)) { m.SetColor(dst, col); set++; }
                    else if (c.vectors.TryGetValue(src, out vec)) { m.SetVector(dst, vec); set++; }
                    else if (c.textures.TryGetValue(src, out tex))
                    {
                        m.SetTexture(dst, tex);
                        Vector4 v;
                        if (c.st.TryGetValue(src, out v)) { m.SetTextureScale(dst, new Vector2(v.x, v.y)); m.SetTextureOffset(dst, new Vector2(v.z, v.w)); }
                        set++;
                    }
                    string a;
                    if (!c.anim.TryGetValue(src, out a)) a = "";
                    if (m.GetTag(dst + "Animated", false, "") != a) m.SetOverrideTag(dst + "Animated", a);
                }
                // section switches flip shader features: recompute them from the pasted values
                KeywordRules.RebuildKeywords(m, ShaderLayout.For(m.shader));
            }
            MarkDirty();
            CheckVariants();
            SetStatus(set == 0
                ? "Nothing matched: '" + c.title + "' and '" + g.title + "' have no values in common."
                : "Pasted '" + c.title + "' from " + c.from + " onto '" + g.title + "' of " + mats.Count + " material(s)" +
                  (map.Count < c.order.Count ? " (" + (c.order.Count - map.Count) + " value(s) had no match there)." : "."));
            RefreshRows();
            BuildPropRows(tabPanels[0], propTop);
        }

        // ------------------------------------------------------------------ rows

        float BuildRow(PropInfo p, Material m, ShaderLayout L, float y, float indent)
        {
            if (!m.HasProperty(p.name)) return y;
            bool baked = RecipeEngine.IsLockedPoiyomi(m) && !IsAnimated(m, p.name);
            float h = RowH;
            if (p.ui == PropUi.Texture && !p.noScaleOffset) h = RowH * 2f + 2f;
            RectTransform row = Ui.Rect("P_" + p.name, propContent);
            Ui.Place(row, 0, y, RightW - 30, h);
            Text lab = Ui.Label("Label", row, p.label + (baked ? " *" : ""), 4 + indent, 0, LabelW - indent, RowH, 11, TextAnchor.MiddleRight);
            lab.horizontalOverflow = HorizontalWrapMode.Wrap;
            if (baked) lab.color = Ui.TextDim;
            string help = p.Help + (baked ? "\n\n* Baked by the Poiyomi lock: changing it shows after export or after Unlock." : "");
            string nm = p.name;

            switch (p.ui)
            {
                case PropUi.Slider: RowSlider(row, p, m, help); break;
                case PropUi.Number:
                case PropUi.IntNumber: RowNumber(row, p, m, help); break;
                case PropUi.Toggle: RowToggle(row, p, m, help); break;
                case PropUi.Enum: RowEnum(row, p, m, help); break;
                case PropUi.Color: RowColor(row, p, m, help); break;
                case PropUi.Vector: RowVector(row, p, m, help); break;
                case PropUi.Texture: RowTexture(row, p, m, help); break;
            }
            Button rb = Ui.Button("Reset", row, "R", RightW - 58, 3, 22, 22, delegate { ResetProp(nm); }, 10);
            rb.GetComponent<Image>().color = new Color(0.30f, 0.22f, 0.22f, 1f);
            if (UsesThryLock(m))
            {
                // Poiyomi's right-click "Animated": keep this value live (changeable) after locking
                Button lk = Ui.Button("Live", row, "L", RightW - 84, 3, 22, 22, delegate { ToggleLiveWhenLocked(nm); }, 10);
                Image li = lk.GetComponent<Image>();
                Action paint = delegate
                {
                    Material pm = Primary != null ? Primary.mat : null;
                    li.color = pm != null && pm.HasProperty(nm) && IsAnimated(pm, nm) ? new Color(0.20f, 0.50f, 0.30f, 1f) : new Color(0.22f, 0.24f, 0.30f, 1f);
                };
                paint();
                rowRefreshers.Add(paint);
            }
            return y + h + (RowPitch - RowH);
        }

        static bool IsAnimated(Material m, string prop)
        {
            string t = m.GetTag(prop + "Animated", false, "");
            return t == "1" || t == "2";
        }

        float Num(Material m, PropInfo p)
        {
            return p.type == PropType.Int ? RecipeEngine.GetIntProp(m, p.name) : m.GetFloat(p.name);
        }

        void RowSlider(RectTransform row, PropInfo p, Material m, string help)
        {
            float v = Num(m, p);
            bool whole = p.type == PropType.Int || HasAttr(p, "IntRange");
            InputField box = null;
            Slider s = Ui.Slider("Slider", row, CtlX, 4, 210, 20, p.min, p.max, v, whole, null);
            box = Ui.Input("Value", row, Ui.F(v), CtlX + 218, 3, 76, 22, InputField.ContentType.DecimalNumber, delegate (string str)
            {
                float f; if (!Ui.TryF(str, out f)) return;
                EditNumber(p.name, f);   // typed values may go past the slider range on purpose
                s.SetValueWithoutNotify(f);
                RefreshRows();
            });
            s.onValueChanged.AddListener(delegate (float f)
            {
                EditNumber(p.name, f);
                if (box != null && !box.isFocused) box.text = Ui.F(f);
            });
            tips.Chip((RectTransform)s.transform, "S_" + p.name, help, true);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                float cur = Num(Primary.mat, p);
                s.SetValueWithoutNotify(cur);
                if (!box.isFocused) box.text = Ui.F(cur);
            });
        }

        void RowNumber(RectTransform row, PropInfo p, Material m, string help)
        {
            InputField box = Ui.Input("Value", row, Ui.F(Num(m, p)), CtlX, 3, 110, 22,
                p.ui == PropUi.IntNumber ? InputField.ContentType.IntegerNumber : InputField.ContentType.DecimalNumber,
                delegate (string str) { float f; if (Ui.TryF(str, out f)) { EditNumber(p.name, f); RefreshRows(); } });
            tips.Chip((RectTransform)box.transform, "N_" + p.name, help + "\nPress Enter to apply.", false);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name) || box.isFocused) return;
                box.text = Ui.F(Num(Primary.mat, p));
            });
        }

        void RowToggle(RectTransform row, PropInfo p, Material m, string help)
        {
            Toggle t = Ui.Check("Toggle", row, p.keyword != null ? p.keyword : "", CtlX, 4, 300, 20, Num(m, p) >= 0.5f, null);
            t.onValueChanged.AddListener(delegate (bool on) { EditNumber(p.name, on ? 1f : 0f); RefreshRows(); });
            Text kl = t.GetComponentInChildren<Text>();
            if (kl != null) { kl.color = Ui.TextDim; kl.fontSize = 10; }
            tips.Chip((RectTransform)t.transform, "T_" + p.name, help, false);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                t.SetIsOnWithoutNotify(Num(Primary.mat, p) >= 0.5f);
            });
        }

        void RowEnum(RectTransform row, PropInfo p, Material m, string help)
        {
            Button b = Ui.Button("Enum", row, EnumText(p, Num(m, p)), CtlX, 3, 240, 22, null, 11);
            b.onClick.AddListener(delegate
            {
                var items = new List<string>();
                for (int i = 0; i < p.enumNames.Length; i++) items.Add(p.enumNames[i]);
                OpenListPopup(p.label, items, delegate (int idx)
                {
                    EditNumber(p.name, p.enumValues[idx]);
                    RefreshRows();
                });
            });
            tips.Chip((RectTransform)b.transform, "E_" + p.name, help, false);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                Ui.SetButtonText(b, EnumText(p, Num(Primary.mat, p)));
            });
        }

        static string EnumText(PropInfo p, float v)
        {
            for (int i = 0; i < p.enumValues.Length; i++)
                if (Mathf.Abs(p.enumValues[i] - v) < 0.001f) return p.enumNames[i];
            return Ui.F(v);
        }

        void RowColor(RectTransform row, PropInfo p, Material m, string help)
        {
            Color c = m.GetColor(p.name);
            Image sw = Ui.Box("Swatch", row, CtlX, 4, 36, 20, Swatch(c));
            var cc = sw.gameObject.AddComponent<ClickCatcher>();
            var boxes = new InputField[4];
            string[] ch = { "R", "G", "B", "A" };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                boxes[i] = Ui.Input("C" + ch[i], row, Ui.F(c[i]), CtlX + 42 + i * 58, 3, 54, 22, InputField.ContentType.DecimalNumber, delegate (string str)
                {
                    float f; if (!Ui.TryF(str, out f) || Primary == null || Primary.mat == null) return;
                    Color cur = Primary.mat.GetColor(p.name);
                    cur[k] = f;
                    EditColor(p.name, cur);
                    RefreshRows();
                });
            }
            cc.onClick = delegate { OpenColorPopup(p); };
            tips.Chip((RectTransform)sw.transform, "C_" + p.name, help + (p.hdr ? "\nHDR: values above 1 glow. Click the swatch for a colour picker." : "\nClick the swatch for a colour picker."), false);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                Color cur = Primary.mat.GetColor(p.name);
                sw.color = Swatch(cur);
                for (int i = 0; i < 4; i++) if (!boxes[i].isFocused) boxes[i].text = Ui.F(cur[i]);
            });
        }

        static Color Swatch(Color c)
        {
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (mx > 1f) { c.r /= mx; c.g /= mx; c.b /= mx; }
            c.a = 1f;
            return c;
        }

        void RowVector(RectTransform row, PropInfo p, Material m, string help)
        {
            Vector4 v = m.GetVector(p.name);
            var boxes = new InputField[4];
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                boxes[i] = Ui.Input("V" + i, row, Ui.F(v[i]), CtlX + i * 74, 3, 70, 22, InputField.ContentType.DecimalNumber, delegate (string str)
                {
                    float f; if (!Ui.TryF(str, out f) || Primary == null || Primary.mat == null) return;
                    Vector4 cur = Primary.mat.GetVector(p.name);
                    cur[k] = f;
                    EditVector(p.name, cur);
                    RefreshRows();
                });
            }
            tips.Chip((RectTransform)boxes[0].transform, "V_" + p.name, help + "\nX Y Z W. Press Enter to apply.", false);
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                Vector4 cur = Primary.mat.GetVector(p.name);
                for (int i = 0; i < 4; i++) if (!boxes[i].isFocused) boxes[i].text = Ui.F(cur[i]);
            });
        }

        void RowTexture(RectTransform row, PropInfo p, Material m, string help)
        {
            Texture t = m.GetTexture(p.name);
            Text name = Ui.Label("TexName", row, TexText(t), CtlX, 3, 110, 22, 11);
            name.color = t != null ? Ui.Ink : Ui.TextDim;
            Button bf = Ui.Button("File", row, "File...", CtlX + 114, 3, 48, 22, delegate
            {
                string start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string f = NativeFileDialog.OpenFile("Pick an image for " + p.label, start, "Images (*.png;*.jpg)", "*.png;*.jpg;*.jpeg");
                if (string.IsNullOrEmpty(f)) return;
                AssignTextureFile(p.name, f, p.linearData);
                RefreshRows();
            }, 11);
            tips.Chip((RectTransform)bf.transform, "TF_" + p.name, help + "\nLoad a PNG/JPG. It is copied next to your saved edits and goes into the export.", false);
            Ui.Button("Pick", row, "Avatar...", CtlX + 166, 3, 58, 22, delegate { OpenTexturePicker(p); }, 11);
            Ui.Button("Clear", row, "None", CtlX + 228, 3, 40, 22, delegate { EditTexture(p.name, null); RefreshRows(); }, 11);
            Button show = Ui.Button("Show", row, "Show", CtlX + 272, 3, 46, 22, delegate { ShowTextureFile(p); }, 11);
            tips.Chip((RectTransform)show.transform, "TS_" + p.name, "Open Explorer with this texture's image selected. A texture you loaded from a file opens where it is kept; " +
                "one packed inside the avatar is saved as a PNG copy first (edit it, then load it back with File...).", false);
            InputField[] st = null;
            if (!p.noScaleOffset)
            {
                st = new InputField[4];
                string[] lbl = { "Tile X", "Tile Y", "Ofs X", "Ofs Y" };
                Text tl = Ui.Label("STL", row, "tiling / offset", 4, RowH + 2, LabelW, RowH, 10, TextAnchor.MiddleRight);
                tl.color = Ui.TextDim;
                Vector2 sc = m.GetTextureScale(p.name), of = m.GetTextureOffset(p.name);
                float[] vals = { sc.x, sc.y, of.x, of.y };
                for (int i = 0; i < 4; i++)
                {
                    int k = i;
                    st[i] = Ui.Input("ST" + i, row, Ui.F(vals[i]), CtlX + i * 74, RowH + 5, 70, 22, InputField.ContentType.DecimalNumber, delegate (string str)
                    {
                        float f; if (!Ui.TryF(str, out f) || Primary == null || Primary.mat == null) return;
                        Vector2 s2 = Primary.mat.GetTextureScale(p.name), o2 = Primary.mat.GetTextureOffset(p.name);
                        if (k == 0) s2.x = f; else if (k == 1) s2.y = f; else if (k == 2) o2.x = f; else o2.y = f;
                        EditTextureST(p.name, s2, o2);
                        RefreshRows();
                    });
                    ((Text)st[i].placeholder).text = lbl[i];
                }
            }
            rowRefreshers.Add(delegate
            {
                if (Primary == null || Primary.mat == null || !Primary.mat.HasProperty(p.name)) return;
                Texture cur = Primary.mat.GetTexture(p.name);
                name.text = TexText(cur);
                name.color = cur != null ? Ui.Ink : Ui.TextDim;
                if (st == null) return;
                Vector2 sc = Primary.mat.GetTextureScale(p.name), of = Primary.mat.GetTextureOffset(p.name);
                float[] vals = { sc.x, sc.y, of.x, of.y };
                for (int i = 0; i < 4; i++) if (!st[i].isFocused) st[i].text = Ui.F(vals[i]);
            });
        }

        /// <summary>Selects the texture's image in Explorer, saving a PNG copy first when it only lives in the avatar.</summary>
        void ShowTextureFile(PropInfo p)
        {
            Material m = Primary != null ? Primary.mat : null;
            Texture t = m != null && m.HasProperty(p.name) ? m.GetTexture(p.name) : null;
            if (t == null) { SetStatus("No texture in " + p.label + "."); return; }
            string path = null, src;
            if (texSources.TryGetValue(t, out src) && src.StartsWith("file:", StringComparison.Ordinal))
                path = Path.Combine(RecipeTexDir, src.Substring(5).Replace('/', Path.DirectorySeparatorChar));
            if (path == null || !File.Exists(path)) path = ExportTexturePng(t, p);
            if (path == null) return;
            // Unity's persistentDataPath uses forward slashes; Explorer's /select only understands
            // backslashes and silently opens its default folder otherwise.
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + WinPath(path) + "\""); }
            catch (Exception e) { SetStatus("Could not open Explorer: " + e.Message); }
        }

        /// <summary>
        /// Avatar textures are GPU-only (compressed, not readable): draw them into a RenderTexture
        /// in their own colour space, read back and save a PNG. Normal maps are decoded to plain
        /// RGB (x = r*a covers both DXT5nm and BC5 layouts) so the picture looks like a normal map.
        /// </summary>
        string ExportTexturePng(Texture t, PropInfo p)
        {
            if (t.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) { SetStatus(t.name + " is a " + t.dimension + " texture; only flat images can be saved."); return null; }
            bool srgb = false;
            try { srgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat); } catch { }
            int w = t.width, h = t.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture prev = RenderTexture.active;
            Texture2D flat = null;
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                flat = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
                flat.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                flat.Apply();
                if (p.isNormal)
                {
                    Color32[] px = flat.GetPixels32();
                    for (int i = 0; i < px.Length; i++)
                    {
                        float x = (px[i].r / 255f) * (px[i].a / 255f) * 2f - 1f, y = px[i].g / 255f * 2f - 1f;
                        float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                        px[i] = new Color32((byte)Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 255);
                    }
                    flat.SetPixels32(px);
                }
                string dir = Path.Combine(ShaderCatalog.Dir, Path.Combine("TextureExports", string.IsNullOrEmpty(avatarKey) ? "avatar" : avatarKey));
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, RecipePaths.SafeFileName(t.name) + ".png");
                File.WriteAllBytes(file, ImageConversion.EncodeToPNG(flat));
                SetStatus("Saved a PNG copy of " + t.name + " (" + w + "x" + h + ") to open in Explorer.");
                return file;
            }
            catch (Exception e) { SetStatus("Could not save " + t.name + ": " + e.Message); return null; }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (flat != null) Destroy(flat);
            }
        }

        static string WinPath(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.Replace('/', '\\');
        }

        static string TexText(Texture t) { return t == null ? "(none)" : t.name + "  " + t.width + "x" + t.height; }

        static bool HasAttr(PropInfo p, string name)
        {
            foreach (string a in p.attrs) if (ShaderLayout.AttrName(a) == name) return true;
            return false;
        }

        void RefreshRows()
        {
            foreach (Action a in rowRefreshers) { try { a(); } catch { } }
            RebuildList();
        }

        void ResetProp(string prop)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit("reset " + prop, mats);
            foreach (Material m in mats)
            {
                MatEntry e = reg.EntryFor(m);
                if (e == null || e.orig == null) continue;
                float f; Color c; Vector4 v; Texture t; Vector4 st;
                if (e.orig.floats.TryGetValue(prop, out f))
                {
                    RecipeEngine.SetNumber(m, prop, f);
                    PropInfo p = PropOn(m, prop);
                    if (p != null) KeywordRules.AfterNumberChanged(m, ShaderLayout.For(m.shader), p, f);
                }
                if (e.orig.colors.TryGetValue(prop, out c)) m.SetColor(prop, c);
                if (e.orig.vectors.TryGetValue(prop, out v)) m.SetVector(prop, v);
                if (e.orig.textures.TryGetValue(prop, out t)) m.SetTexture(prop, t);
                if (e.orig.texST.TryGetValue(prop, out st)) { m.SetTextureScale(prop, new Vector2(st.x, st.y)); m.SetTextureOffset(prop, new Vector2(st.z, st.w)); }
            }
            MarkDirty();
            RefreshRows();
        }

        // ------------------------------------------------------------------ popups

        void ClosePopup()
        {
            if (popup != null) { popup.SetActive(false); Destroy(popup); popup = null; }
        }

        RectTransform PopupFrame(string title, float w, float h)
        {
            ClosePopup();
            Image bg = Ui.Box("Popup", root, 0, 0, w, h, new Color(0.08f, 0.09f, 0.12f, 0.99f));
            RectTransform rt = (RectTransform)bg.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(RightX * 0.5f, 0);
            popup = bg.gameObject;
            Ui.Label("Title", rt, title, 10, 6, w - 50, 22, 13, TextAnchor.MiddleLeft, FontStyle.Bold);
            Ui.Button("Close", rt, "X", w - 32, 6, 24, 22, ClosePopup);
            popup.transform.SetAsLastSibling();
            return rt;
        }

        void OpenListPopup(string title, List<string> items, Action<int> pick)
        {
            RectTransform rt = PopupFrame(title, 320, 360);
            ScrollRect sr;
            RectTransform content = Ui.Scroll("List", rt, 8, 34, 304, 318, out sr);
            float y = 2f;
            for (int i = 0; i < items.Count; i++)
            {
                int idx = i;
                Button b = Ui.Button("I", content, items[i], 4, y, 290, 22, delegate { ClosePopup(); pick(idx); }, 11);
                b.GetComponentInChildren<Text>().alignment = TextAnchor.MiddleLeft;
                y += 24f;
            }
            Ui.SetContentHeight(content, y + 2f);
        }

        void OpenTexturePicker(PropInfo p)
        {
            Dictionary<string, Texture> tex = reg.AvatarTextures();
            foreach (var kv in fileTexCache) if (kv.Value != null && !tex.ContainsKey(kv.Value.name)) tex[kv.Value.name] = kv.Value;
            var names = new List<string>(tex.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            var labels = new List<string>();
            foreach (string n in names) labels.Add(n + "   " + tex[n].width + "x" + tex[n].height);
            OpenListPopup("Use a texture the avatar has", labels, delegate (int idx)
            {
                EditTexture(p.name, tex[names[idx]]);
                RefreshRows();
            });
        }

        void OpenColorPopup(PropInfo p)
        {
            if (Primary == null || Primary.mat == null) return;
            RectTransform rt = PopupFrame(p.label, 330, p.hdr ? 250 : 222);
            Color start = Primary.mat.GetColor(p.name);
            float intensity = 0f;
            Color baseC = start;
            float mx = Mathf.Max(start.r, Mathf.Max(start.g, start.b));
            if (p.hdr && mx > 1f) { intensity = Mathf.Log(mx, 2f); baseC = new Color(start.r / mx, start.g / mx, start.b / mx, start.a); }
            float h, s, v;
            Color.RGBToHSV(baseC, out h, out s, out v);
            Image prev = Ui.Box("Preview", rt, 10, 34, 310, 26, Swatch(start));
            var sl = new Slider[5];
            InputField hex = null;
            Action push = delegate
            {
                Color c = Color.HSVToRGB(sl[0].value, sl[1].value, sl[2].value);
                c.a = sl[3].value;
                if (p.hdr && sl[4] != null) { float k = Mathf.Pow(2f, sl[4].value); c.r *= k; c.g *= k; c.b *= k; }
                prev.color = Swatch(c);
                if (hex != null && !hex.isFocused) hex.text = ColorUtility.ToHtmlStringRGBA(Swatch(c) * new Color(1, 1, 1, 0) + new Color(0, 0, 0, c.a));
                EditColor(p.name, c);
            };
            string[] names = { "Hue", "Saturation", "Brightness", "Alpha", "Intensity" };
            float[] vals = { h, s, v, start.a, intensity };
            int count = p.hdr ? 5 : 4;
            for (int i = 0; i < count; i++)
            {
                Ui.Label("L" + i, rt, names[i], 10, 66 + i * 30, 80, 24, 11, TextAnchor.MiddleRight);
                float min = i == 4 ? -10f : 0f, max = i == 4 ? 10f : 1f;
                sl[i] = Ui.Slider("S" + i, rt, 96, 68 + i * 30, 220, 20, min, max, vals[i], false, delegate { push(); });
            }
            float hy = 66 + count * 30;
            Ui.Label("HexL", rt, "Hex", 10, hy, 80, 24, 11, TextAnchor.MiddleRight);
            hex = Ui.Input("Hex", rt, ColorUtility.ToHtmlStringRGBA(baseC), 96, hy, 120, 22, InputField.ContentType.Alphanumeric, delegate (string str)
            {
                Color c;
                if (!ColorUtility.TryParseHtmlString("#" + str.TrimStart('#'), out c)) return;
                float hh, ss, vv;
                Color.RGBToHSV(c, out hh, out ss, out vv);
                sl[0].SetValueWithoutNotify(hh); sl[1].SetValueWithoutNotify(ss); sl[2].SetValueWithoutNotify(vv); sl[3].SetValueWithoutNotify(c.a);
                push();
            });
            Ui.Button("Done", rt, "Done", 240, hy, 80, 22, delegate { ClosePopup(); RefreshRows(); });
        }
    }
}
