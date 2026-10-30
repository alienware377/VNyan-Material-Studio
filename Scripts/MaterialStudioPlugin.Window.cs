using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace MaterialStudio
{
    public partial class MaterialStudioPlugin
    {
        const float W = 900f, H = 680f;
        const float LeftW = 290f;
        const float RightX = 304f;
        const float RightW = W - RightX - 8f;
        const float TopY = 38f;

        GameObject window;
        RectTransform root;
        Tooltips tips;
        Text statusLabel, avatarLabel;
        RectTransform listContent;
        ScrollRect listScroll;
        string selectedRendererPath;
        readonly string[] tabNames = { "Properties", "Shader", "Packages", "Export", "Settings" };
        readonly List<Button> tabButtons = new List<Button>();
        readonly List<RectTransform> tabPanels = new List<RectTransform>();
        int currentTab;

        static readonly Dictionary<string, string> Help = new Dictionary<string, string>
        {
            { "Undo", "Undo the last change (Ctrl+Z while the cursor is over this window). Every slider, toggle, colour, texture and shader switch is recorded." },
            { "Redo", "Redo what you just undid (Ctrl+Shift+Z)." },
            { "Save", "Save the edits for this model now. They also save by themselves a moment after each change, and load again whenever this model is loaded." },
            { "Rebind", "Look at the avatar again (after you load a different model, or if another plugin swapped its materials)." },
            { "All", "Select every material. Edits then go to every selected material that has that property." },
            { "None", "Clear the selection." },
            { "Unique", "Give the picked renderer's slot its own copy of this material, so editing it no longer changes the other meshes that share it." },
            { "ResetSel", "Put the selected materials back exactly how the model shipped (shader, values, textures)." },
            { "Copy", "Copy every setting of the first selected material." },
            { "Paste", "Paste the copied settings onto the selected materials. Same shader: an exact copy. Different shader: every property the two share." },
            { "List", "Tick boxes to edit several materials at once. Click a name to edit just that one." },
        };

        void BuildWindow()
        {
            if (windowPrefab == null) { Debug.LogWarning(LOG + "no window prefab in the bundle."); return; }
            window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);
            if (window == null) return;
            root = (RectTransform)window.transform;
            root.sizeDelta = new Vector2(W, H);
            root.anchoredPosition = Vector2.zero;
            Image bg = window.GetComponent<Image>();
            if (bg != null) bg.color = Ui.Bg;
            if (window.GetComponent<WindowDrag>() == null) window.AddComponent<WindowDrag>();
            tips = new Tooltips(root);

            Ui.Label("Title", root, "Material Studio", 10, 6, 160, 26, 16, TextAnchor.MiddleLeft, FontStyle.Bold);
            statusLabel = Ui.Label("Label_Status", root, "", 172, 6, 420, 26, 11);
            statusLabel.color = Ui.TextDim;
            Button b;
            b = Ui.Button("Button_Undo", root, "Undo", 596, 7, 56, 24, Undo); tips.Chip((RectTransform)b.transform, "Undo", Help["Undo"], false);
            b = Ui.Button("Button_Redo", root, "Redo", 656, 7, 56, 24, Redo); tips.Chip((RectTransform)b.transform, "Redo", Help["Redo"], false);
            b = Ui.Button("Button_Save", root, "Save", 716, 7, 56, 24, delegate { if (CanEdit()) SaveRecipe(true); }); tips.Chip((RectTransform)b.transform, "Save", Help["Save"], false);
            BuildPowerToggle(root, 788, 8);
            PluginChrome.EnsureCloseX(root, delegate { window.SetActive(false); }, Ui.font);

            // ---- left: materials ----
            Image left = Ui.Box("Left", root, 8, TopY, LeftW, H - TopY - 8, Ui.Panel);
            Transform L = left.transform;
            avatarLabel = Ui.Label("Label_Avatar", L, "No avatar loaded.", 6, 4, LeftW - 12, 34, 11, TextAnchor.UpperLeft);
            b = Ui.Button("Button_Rebind", L, "Rebind", 6, 40, 66, 22, delegate { TryBindNow(); }); tips.Chip((RectTransform)b.transform, "Rebind", Help["Rebind"], false);
            b = Ui.Button("Button_All", L, "All", 76, 40, 50, 22, delegate { selection.Clear(); selection.AddRange(reg.entries); RefreshAll(); }); tips.Chip((RectTransform)b.transform, "All", Help["All"], false);
            b = Ui.Button("Button_None", L, "None", 130, 40, 50, 22, delegate { selection.Clear(); RefreshAll(); }); tips.Chip((RectTransform)b.transform, "None", Help["None"], false);
            pickButton = Ui.Button("Button_Pick", L, "Pick on model", 184, 40, 100, 22, TogglePickMode, 11);
            tips.Chip((RectTransform)pickButton.transform, "Pick", "Turn on, then point at the avatar: the part under the cursor gets an outline. " +
                "Click to edit its material; Shift or Ctrl-click to add more. Esc or this button turns it off.", false);
            listContent = Ui.Scroll("MaterialList", L, 6, 66, LeftW - 12, H - TopY - 8 - 66 - 92, out listScroll);
            tips.Chip((RectTransform)listScroll.transform, "List", Help["List"], false);
            float by = H - TopY - 8 - 88;
            b = Ui.Button("Button_Unique", L, "Make unique", 6, by, 136, 24, MakeUnique); tips.Chip((RectTransform)b.transform, "Unique", Help["Unique"], false);
            b = Ui.Button("Button_ResetSel", L, "Reset selected", 148, by, 136, 24, delegate { Confirm("Button_ResetSel", ResetSelected); }); tips.Chip((RectTransform)b.transform, "ResetSel", Help["ResetSel"], false);
            b = Ui.Button("Button_Copy", L, "Copy", 6, by + 28, 136, 24, CopyMaterial); tips.Chip((RectTransform)b.transform, "Copy", Help["Copy"], false);
            b = Ui.Button("Button_Paste", L, "Paste", 148, by + 28, 136, 24, PasteMaterial); tips.Chip((RectTransform)b.transform, "Paste", Help["Paste"], false);
            Text hint = Ui.Label("Label_Hint", L, "Ctrl+Z / Ctrl+Shift+Z over this window.", 6, by + 56, LeftW - 12, 26, 10);
            hint.color = Ui.TextDim;

            // ---- right: tabs ----
            float tw = (RightW - 4f * 4f) / tabNames.Length;
            for (int i = 0; i < tabNames.Length; i++)
            {
                int idx = i;
                Button tb = Ui.Button("Tab_" + tabNames[i], root, tabNames[i], RightX + i * (tw + 4f), TopY, tw, 26, delegate { ShowTab(idx); }, 12);
                tabButtons.Add(tb);
                Image panel = Ui.Box("Panel_" + tabNames[i], root, RightX, TopY + 30, RightW, H - TopY - 30 - 8, Ui.Panel);
                tabPanels.Add((RectTransform)panel.transform);
            }
            ShowTab(0);
            BuildBanner();
            window.SetActive(false);
        }

        void ShowTab(int i)
        {
            currentTab = i;
            for (int k = 0; k < tabPanels.Count; k++)
            {
                tabPanels[k].gameObject.SetActive(k == i);
                tabButtons[k].GetComponent<Image>().color = k == i ? Ui.Selected : Ui.Btn;
            }
            RebuildTab();
        }

        void RefreshAll()
        {
            if (window == null || !window.activeSelf) return;
            RebuildList();
            RebuildTab();
        }

        void RebuildTab()
        {
            if (window == null || currentTab < 0 || currentTab >= tabPanels.Count) return;
            tips.Hide();
            try
            {
                switch (currentTab)
                {
                    case 0: if (!BuildOffPage(tabPanels[0])) BuildPropertiesTab(tabPanels[0]); break;
                    case 1: if (!BuildOffPage(tabPanels[1])) BuildShaderTab(tabPanels[1]); break;
                    case 2: BuildPackagesTab(tabPanels[2]); break;
                    case 3: BuildExportTab(tabPanels[3]); break;
                    case 4: BuildSettingsTab(tabPanels[4]); break;
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "tab build failed: " + e); }
        }

        float statusAt = -100f;

        void SetStatus(string s, bool minor = false)
        {
            if (statusLabel == null) return;
            float now = Time.unscaledTime;
            if (minor && now - statusAt < 6f && statusLabel.text.Length > 0) return;
            statusLabel.text = s ?? "";
            if (!minor) statusAt = now;
        }

        // ------------------------------------------------------------------ material list

        static readonly Color ListGray = new Color(0.70f, 0.72f, 0.76f, 1f);
        static readonly Color ListEdited = new Color(0.86f, 0.78f, 0.58f, 1f);
        float primaryRowY = -1f;
        bool scrollToPrimary;

        void RebuildList()
        {
            if (listContent == null) return;
            float keep = listScroll != null ? listScroll.verticalNormalizedPosition : 1f;
            Ui.Clear(listContent);
            if (avatarLabel != null)
            {
                avatarLabel.text = boundAvatar == null
                    ? "No avatar loaded. Load one in VNyan, then press Rebind."
                    : (sourcePath.Length > 0 ? Path.GetFileName(sourcePath) : "(source file unknown)") + "\n" +
                      reg.entries.Count + " materials" + (dirty ? "  (unsaved)" : "") + (cfg.enabled ? "" : "  (Material Studio is off)");
            }
            if (boundAvatar == null) { Ui.SetContentHeight(listContent, 10); return; }

            float y = 2f, w = LeftW - 12f - 4f;
            primaryRowY = -1f;
            string lastPath = null;
            Renderer[] rs = boundAvatar.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs)
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                string path = RecipePaths.PathOf(r.transform, boundAvatar.transform);
                Material[] mats = r.sharedMaterials;
                if (mats.Length == 0) continue;
                if (path != lastPath)
                {
                    bool shown = r.gameObject.activeInHierarchy && r.enabled;
                    Text h = Ui.Label("R_" + path, listContent, r.name + (shown ? "" : "  <size=11><color=#9aa0aa>(hidden)</color></size>"), 4, y + 2, w - 8, 22, 14, TextAnchor.MiddleLeft, FontStyle.Bold);
                    h.supportRichText = true;
                    h.color = shown ? Color.white : new Color(0.86f, 0.87f, 0.90f, 1f);
                    y += 24f;
                    lastPath = path;
                }
                for (int s = 0; s < mats.Length; s++)
                {
                    Material m = mats[s];
                    MatEntry e = reg.EntryFor(m);
                    bool sel = e != null && selection.Contains(e);
                    bool prim = e != null && Primary == e && (selectedRendererPath == null || selectedRendererPath == path);
                    Image row = Ui.Box("Row", listContent, 2, y, w, 20, prim ? Ui.Selected : (sel ? new Color(0.20f, 0.26f, 0.36f, 1f) : new Color(0, 0, 0, 0.001f)));
                    string pth = path; MatEntry ce = e;
                    Toggle t = Ui.Check("Pick", row.transform, "", 4, 2, 16, 16, sel, delegate (bool on)
                    {
                        if (ce == null) return;
                        if (on && !selection.Contains(ce)) selection.Add(ce);
                        if (!on) selection.Remove(ce);
                        RefreshAll();
                    });
                    string label = s + ": " + (m != null ? m.name : "(empty)");
                    if (e != null)
                    {
                        label += "  [" + MatEntry.ShortShader(m.shader != null ? m.shader.name : "") + "]";
                        if (e.slots.Count > 1) label += "  x" + e.slots.Count;
                        if (e.unique) label += "  (unique)";
                    }
                    Text lt = Ui.Label("Name", row.transform, label, 24, 0, w - 28, 20, 11);
                    lt.color = ListGray;
                    if (e != null && e.mat != null && RecipeEngine.Diff(e.orig, e.mat, null) != null) { lt.color = ListEdited; lt.text = label + "  *"; }
                    if (prim) primaryRowY = y;
                    var cc = row.gameObject.AddComponent<ClickCatcher>();
                    cc.onClick = delegate
                    {
                        if (ce == null) return;
                        selection.Clear();
                        selection.Add(ce);
                        selectedRendererPath = pth;
                        RefreshAll();
                    };
                    y += 21f;
                }
            }
            Ui.SetContentHeight(listContent, y + 4f);
            if (listScroll != null) listScroll.verticalNormalizedPosition = keep;
            // a pick on the model scrolls the list to what was picked
            if (scrollToPrimary && listScroll != null && primaryRowY >= 0f)
            {
                float vh = ((RectTransform)listScroll.transform).rect.height, ch = y + 4f;
                if (ch > vh) listScroll.verticalNormalizedPosition = 1f - Mathf.Clamp01((primaryRowY - vh * 0.4f) / (ch - vh));
            }
            scrollToPrimary = false;
        }

        // ------------------------------------------------------------------ shader tab

        string shaderSearch = "";

        void BuildShaderTab(RectTransform P)
        {
            Ui.Clear(P);
            float pw = RightW;
            if (Primary == null) { Ui.Label("Empty", P, "Pick a material on the left.", 10, 10, pw - 20, 24, 12); return; }
            var cur = new List<string>();
            foreach (MatEntry e in selection) if (e.mat != null && e.mat.shader != null && !cur.Contains(e.mat.shader.name)) cur.Add(e.mat.shader.name);
            Ui.Label("Cur", P, "Now: " + string.Join(", ", cur.ToArray()), 10, 6, pw - 20, 34, 11, TextAnchor.UpperLeft);

            float y = 42f;
            bool anyLocked = false;
            foreach (MatEntry e in selection) if (e.mat != null && RecipeEngine.IsLockedPoiyomi(e.mat)) anyLocked = true;
            if (anyLocked)
            {
                Button ub = Ui.Button("Button_Unlock", P, "Unlock Poiyomi for live editing", 10, y, 240, 24, UnlockPoiyomi);
                tips.Chip((RectTransform)ub.transform, "Unlock", "Locked Poiyomi bakes most values into the shader, so only 'animated' properties change live. " +
                    "This swaps to the full Poiyomi shader the material was locked from (it must be in your shader bundle: add your Poiyomi .unitypackage on Packages, then Build). " +
                    "The export locks it again.", false);
                y += 30f;
            }

            Ui.Label("SearchL", P, "Find", 10, y, 40, 22, 12);
            InputField f = Ui.Input("Input_ShaderSearch", P, shaderSearch, 50, y, 260, 22, InputField.ContentType.Standard, null);
            f.onValueChanged.AddListener(delegate (string s) { shaderSearch = s; BuildShaderList(P, y + 28f); });
            Text n = Ui.Label("Note", P, "Switching keeps every value both shaders share. Undo works.", 320, y, pw - 330, 22, 10);
            n.color = Ui.TextDim;
            BuildShaderList(P, y + 28f);
        }

        RectTransform shaderListContent;

        void BuildShaderList(RectTransform P, float top)
        {
            Transform old = P.Find("ShaderList");
            if (old != null) { old.gameObject.SetActive(false); Destroy(old.gameObject); }
            ScrollRect sr;
            shaderListContent = Ui.Scroll("ShaderList", P, 10, top, RightW - 20, P.rect.height - top - 10, out sr);
            string q = (shaderSearch ?? "").Trim().ToLowerInvariant();
            var origins = new[] { "Avatar", "Shader bundle", "Unity", "VNyan", "Loaded", "Workspace" };
            float y = 2f, w = RightW - 24f;
            foreach (string origin in origins)
            {
                bool header = false;
                foreach (ShaderChoice c in catalog.choices)
                {
                    if (c.origin != origin) continue;
                    if (q.Length > 0 && c.name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                    if (!header)
                    {
                        Text h = Ui.Label("H_" + origin, shaderListContent, OriginTitle(origin), 4, y, w, 20, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
                        h.color = Ui.Accent;
                        y += 22f; header = true;
                    }
                    ShaderChoice ch = c;
                    string label = c.name + (c.note.Length > 0 ? "   (" + c.note + ")" : "");
                    Button b = Ui.Button("S", shaderListContent, "", 8, y, w - 12, 20, delegate
                    {
                        if (ch.shader == null) { SetStatus(ch.name + " is not built yet: tick it on Packages and press Build."); return; }
                        SwapShader(ch.shader);
                    });
                    Text t = b.GetComponentInChildren<Text>();
                    t.alignment = TextAnchor.MiddleLeft;
                    t.text = "  " + label;
                    t.fontSize = 11;
                    if (c.shader == null) t.color = Ui.TextDim;
                    y += 22f;
                }
            }
            Ui.SetContentHeight(shaderListContent, y + 4f);
        }

        static string OriginTitle(string o)
        {
            switch (o)
            {
                case "Avatar": return "This avatar's shaders";
                case "Shader bundle": return "From your shader packages";
                case "Unity": return "Unity built-in";
                case "VNyan": return "VRM (MToon) and glTF";
                case "Loaded": return "Other loaded shaders";
                case "Workspace": return "In your workspace, not built yet";
            }
            return o;
        }

        // ------------------------------------------------------------------ settings tab

        void BuildSettingsTab(RectTransform P)
        {
            Ui.Clear(P);
            float y = 8f, pw = RightW;
            Toggle t;
            t = Ui.Check("Toggle_AutoApply", P, "Apply saved edits whenever this model loads", 10, y, 360, 22, cfg.autoApply, delegate (bool v) { cfg.autoApply = v; SaveConfig(); });
            tips.Chip((RectTransform)t.transform, "AutoApply", "Your edits are saved per model file. With this on they come back every time VNyan loads that model.", false);
            y += 26f;
            t = Ui.Check("Toggle_AutoSave", P, "Save edits automatically", 10, y, 360, 22, cfg.autoSave, delegate (bool v) { cfg.autoSave = v; SaveConfig(); });
            tips.Chip((RectTransform)t.transform, "AutoSave", "Saves a moment after each change. Turn off to save only with the Save button.", false);
            y += 26f;
            t = Ui.Check("Toggle_AutoVariants", P, "Compile missing shader features automatically", 10, y, 360, 22, cfg.autoBuildVariants, delegate (bool v) { cfg.autoBuildVariants = v; SaveConfig(); CheckVariants(); });
            tips.Chip((RectTransform)t.transform, "AutoVariants", "A shader inside your avatar only has the feature combinations the avatar shipped with. Switching a feature on (Poiyomi section ticks, normal maps...) " +
                "needs a combination it may not have, and Unity then draws the nearest one, which can switch other effects off. With this on, Material Studio compiles the missing one in the background and switches over when it is ready.", false);
            y += 26f;
            t = Ui.Check("Toggle_ShowHidden", P, "Show hidden properties", 10, y, 360, 22, cfg.showHidden, delegate (bool v) { cfg.showHidden = v; SaveConfig(); });
            tips.Chip((RectTransform)t.transform, "ShowHidden", "Shaders hide some internal properties (blend modes, helper values). Show them to edit everything. Poiyomi's sub-options are always shown.", false);
            y += 34f;

            Ui.Label("UnityL", P, "Unity editor (for shader packages and export):", 10, y, pw - 20, 20, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            y += 22f;
            string ed = string.IsNullOrEmpty(cfg.unityExe) ? "automatic (Unity Hub installs: " + UnityDriver.FindEditors(null).Count + " found)" : cfg.unityExe;
            Ui.Label("UnityPath", P, ed, 10, y, pw - 200, 34, 11, TextAnchor.UpperLeft);
            Button b = Ui.Button("Button_PickUnity", P, "Pick Unity.exe", pw - 184, y, 104, 22, delegate
            {
                string p = NativeFileDialog.OpenFile("Pick Unity.exe", @"C:\Program Files\Unity\Hub\Editor", "Unity editor (Unity.exe)", "Unity.exe");
                if (!string.IsNullOrEmpty(p)) { cfg.unityExe = p; SaveConfig(); RebuildTab(); }
            });
            tips.Chip((RectTransform)b.transform, "PickUnity", "Only needed if Unity is not installed through Unity Hub. Normally the editor matching the project is picked by itself.", false);
            Ui.Button("Button_AutoUnity", P, "Auto", pw - 76, y, 60, 22, delegate { cfg.unityExe = ""; SaveConfig(); RebuildTab(); });
            y += 42f;

            Ui.Label("RecL", P, "Saved edits for this model:", 10, y, pw - 20, 20, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            y += 22f;
            Ui.Label("RecP", P, string.IsNullOrEmpty(avatarKey) ? "(no avatar)" : RecipePath, 10, y, pw - 20, 34, 11, TextAnchor.UpperLeft);
            y += 36f;
            b = Ui.Button("Button_Reload", P, "Reload from file", 10, y, 130, 24, delegate { Confirm("Button_Reload", ReloadFromFile); });
            tips.Chip((RectTransform)b.transform, "Reload", "Throw away unsaved changes and apply the saved file again.", false);
            b = Ui.Button("Button_OpenFolder", P, "Open folder", 146, y, 110, 24, delegate { OpenFolder(RecipeDir); });
            tips.Chip((RectTransform)b.transform, "OpenFolder", "Open the folder with every model's saved edits. Other exporters (Weight Studio's Unity window, Warudo Importer) read these files.", false);
            b = Ui.Button("Button_ResetAll", P, "Reset ALL materials", 262, y, 150, 24, delegate { Confirm("Button_ResetAll", ResetAll); });
            tips.Chip((RectTransform)b.transform, "ResetAll", "Every material back to how the model shipped. Click twice to confirm. Undo still works.", false);
            y += 36f;

            Ui.Label("NotesL", P, "Messages:", 10, y, pw - 20, 20, 12, TextAnchor.MiddleLeft, FontStyle.Bold);
            y += 22f;
            string all = string.Join("\n", notes.ToArray());
            if (all.Length > 6000) all = all.Substring(all.Length - 6000);
            Text nt = Ui.Label("Notes", P, all, 10, y, pw - 20, P.rect.height - y - 10, 11, TextAnchor.UpperLeft);
            nt.verticalOverflow = VerticalWrapMode.Truncate;
        }

        // ------------------------------------------------------------------ helpers

        readonly Dictionary<string, float> confirmUntil = new Dictionary<string, float>();

        /// <summary>Two-click confirm: the first click turns the button into "Sure?" for 3 s.</summary>
        void Confirm(string buttonName, Action act)
        {
            float until;
            Transform bt = FindDeep(root, buttonName);
            Button b = bt != null ? bt.GetComponent<Button>() : null;
            if (confirmUntil.TryGetValue(buttonName, out until) && Time.unscaledTime < until)
            {
                confirmUntil.Remove(buttonName);
                act();
                return;
            }
            confirmUntil[buttonName] = Time.unscaledTime + 3f;
            if (b != null)
            {
                Text t = b.GetComponentInChildren<Text>();
                string orig = t != null ? t.text : "";
                if (t != null) t.text = "Sure?";
                StartCoroutine(RestoreText(t, orig, 3f));
            }
        }

        System.Collections.IEnumerator RestoreText(Text t, string s, float secs)
        {
            yield return new WaitForSecondsRealtime(secs);
            if (t != null && t.text == "Sure?") t.text = s;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        static void OpenFolder(string dir)
        {
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + WinPath(dir) + "\""); }
            catch (Exception e) { Debug.LogWarning(LOG + "open folder failed: " + e.Message); }
        }
    }
}
