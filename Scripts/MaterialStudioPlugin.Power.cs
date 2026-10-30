using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    /// <summary>
    /// The On/Off switch in the title bar. Off = the avatar shows its materials exactly as
    /// the model shipped, nothing is saved (so the edits file is never overwritten with the
    /// shipped state), and background shader compiles, pick mode and editing pause.
    /// On = the saved edits come back. Exports still read the saved edits either way.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        Toggle powerToggle;

        bool IsOn { get { return cfg.enabled; } }
        float nextPowerPoll;

        /// <summary>Test hook: a file "MaterialStudio_power.txt" containing on/off flips the switch (then is deleted).</summary>
        void PowerFileTick()
        {
            if (Time.unscaledTime < nextPowerPoll) return;
            nextPowerPoll = Time.unscaledTime + 2f;
            string p = System.IO.Path.Combine(Application.persistentDataPath, "MaterialStudio_power.txt");
            if (!System.IO.File.Exists(p)) return;
            string v = "";
            try { v = System.IO.File.ReadAllText(p).Trim().ToLowerInvariant(); System.IO.File.Delete(p); } catch { }
            if (v == "on" || v == "off") SetEnabled(v == "on");
            Debug.Log(LOG + "power file '" + v + "': now " + (cfg.enabled ? "on" : "off") + ", " + DescribeLive());
        }

        /// <summary>One-line state of the edited materials, for the log.</summary>
        string DescribeLive()
        {
            int locked = 0, bundle = 0, n = 0;
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null || e.mat.shader == null) continue;
                n++;
                if (e.mat.shader.name.StartsWith("Hidden/Locked/", System.StringComparison.Ordinal)) locked++;
                if (catalog.IsBundleShader(e.mat.shader)) bundle++;
            }
            return n + " materials, " + locked + " locked, " + bundle + " on compiled copies, dirty=" + dirty;
        }

        /// <summary>For edit actions: false (and a hint) while Material Studio is off.</summary>
        bool CanEdit()
        {
            if (cfg.enabled) return true;
            SetStatus("Material Studio is off. Tick \"On\" at the top to edit again.");
            return false;
        }

        void SetEnabled(bool on)
        {
            if (on == cfg.enabled) { UpdatePowerUi(); return; }
            if (!on)
            {
                // keep whatever was edited: turning off must never lose work
                if (dirty) SaveRecipe(true);
                if (pickMode) TogglePickMode();
                cfg.enabled = false;
                SaveConfig();
                int n = RestoreShipped();
                SetStatus("Material Studio is off: the model shows its original materials. Your saved edits are kept.");
                Debug.Log(LOG + "turned off; " + n + " material(s) back to how the model shipped.");
            }
            else
            {
                cfg.enabled = true;
                SaveConfig();
                int applied = 0;
                if (boundAvatar != null)
                {
                    recipe = LoadRecipe();
                    if (recipe != null && cfg.autoApply) applied = ApplyRecipe(recipe);
                    IndexShippedVariants();
                    CheckVariants();
                    RebuildSelection(SelectedIds());
                    dirty = false;
                    saveAt = -1f;
                }
                SetStatus(recipe != null && boundAvatar != null
                    ? (cfg.autoApply ? "Material Studio is on: " + applied + " saved edit(s) applied." : "Material Studio is on. Saved edits are not applied automatically (Settings).")
                    : "Material Studio is on.");
                Debug.Log(LOG + "turned on; " + applied + " saved edit(s) applied.");
            }
            UpdatePowerUi();
            RefreshAll();
        }

        List<string> SelectedIds()
        {
            var ids = new List<string>();
            foreach (MatEntry e in selection) if (e != null) ids.Add(e.id);
            return ids;
        }

        /// <summary>Every material back to how the model shipped; split copies merged back. Returns how many.</summary>
        int RestoreShipped()
        {
            variantBuildAt = -1f;
            missingVariant.Clear();
            variantWarning = "";
            if (boundAvatar == null) return 0;
            List<string> ids = SelectedIds();
            int n = 0;
            bool merged = false;
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null || e.orig == null) continue;
                e.orig.Restore(e.mat);
                e.unlockedPoiyomi = false;
                if (e.unique) merged |= reg.Merge(e.mat);
                n++;
            }
            reg.Bind(boundAvatar);
            pendingKw.Clear();
            lastGood.Clear();
            compileFailed.Clear();
            requestedKey.Clear();
            IndexShippedVariants();
            undo.Clear();
            dirty = false;
            saveAt = -1f;
            RebuildSelection(ids);
            return n;
        }

        void BuildPowerToggle(RectTransform parent, float x, float y)
        {
            powerToggle = Ui.Check("Toggle_Power", parent, "On", x, y, 54, 22, cfg.enabled, delegate (bool v) { SetEnabled(v); });
            tips.Chip((RectTransform)powerToggle.transform, "Power",
                "Turns Material Studio on or off. Off: the model shows its original materials, nothing is saved, and editing, pick mode " +
                "and background compiles pause. Your edits stay in their file and come back when you turn it on (any unsaved edits are saved first). " +
                "Exports still include your saved edits.", false);
        }

        void UpdatePowerUi()
        {
            if (powerToggle != null) powerToggle.SetIsOnWithoutNotify(cfg.enabled);
        }

        /// <summary>The Properties / Shader tabs while off. Returns true when it drew the page.</summary>
        bool BuildOffPage(RectTransform P)
        {
            if (cfg.enabled) return false;
            Ui.Clear(P);
            float pw = RightW;
            Text t = Ui.Label("OffInfo", P,
                "Material Studio is off.\n\nThe model shows its original materials. Your edits are safe in their file and come back when you turn it on. " +
                "Packages, Export and Settings still work.", 14, 14, pw - 28, 110, 13, TextAnchor.UpperLeft);
            t.color = Ui.TextDim;
            Ui.Button("Button_TurnOn", P, "Turn Material Studio on", 14, 132, 220, 30, delegate { SetEnabled(true); }, 12);
            return true;
        }
    }
}
