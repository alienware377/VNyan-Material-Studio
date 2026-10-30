using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    /// <summary>
    /// A slim progress banner docked under the window (above it when the window sits near the
    /// bottom of the screen) for anything that runs in the background: compiling switched
    /// features, locking, building, exporting. It never covers the controls, says what is
    /// happening and for which materials, counts the seconds, and reports the result.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        const float BannerH = 60f;

        RectTransform bannerRt;
        Image bannerBg, bannerEdge, bannerBar;
        RectTransform bannerBarRt;
        Text bannerTitle, bannerDetail;
        Button bannerClose;
        string jobLabel = "", jobDetail = "", jobDoneText = "";
        float jobStarted = -1f, bannerHideAt = -1f;
        bool wasBusy, bannerShowingResult, bannerAbove;
        readonly Dictionary<Material, string> lastToggleDesc = new Dictionary<Material, string>();

        static readonly Color BannerWork = new Color(0.30f, 0.62f, 0.95f, 1f);
        static readonly Color BannerOk = new Color(0.40f, 0.82f, 0.45f, 1f);
        static readonly Color BannerBad = new Color(0.95f, 0.40f, 0.35f, 1f);

        void BuildBanner()
        {
            Image bg = Ui.Box("JobBanner", root, 0, 0, W, BannerH, new Color(0.10f, 0.11f, 0.14f, 0.97f));
            bannerRt = (RectTransform)bg.transform;
            bannerBg = bg;
            bannerEdge = Ui.Box("Edge", bannerRt, 0, 0, 5, BannerH, BannerWork);
            bannerTitle = Ui.Label("Title", bannerRt, "", 16, 6, W - 70, 22, 14, TextAnchor.MiddleLeft, FontStyle.Bold);
            bannerDetail = Ui.Label("Detail", bannerRt, "", 16, 27, W - 70, 18, 11, TextAnchor.MiddleLeft);
            bannerDetail.color = new Color(0.80f, 0.83f, 0.88f, 1f);
            Image track = Ui.Box("Track", bannerRt, 16, 49, W - 32, 5, new Color(0.18f, 0.19f, 0.23f, 1f));
            bannerBar = Ui.Box("Bar", track.transform, 0, 0, 140, 5, BannerWork);
            bannerBarRt = (RectTransform)bannerBar.transform;
            bannerClose = Ui.Button("Close", bannerRt, "x", W - 34, 6, 24, 22, delegate { HideBanner(); }, 12);
            PlaceBanner(false);
            bannerRt.gameObject.SetActive(false);
        }

        void PlaceBanner(bool above)
        {
            bannerAbove = above;
            bannerRt.anchorMin = bannerRt.anchorMax = new Vector2(0.5f, above ? 1f : 0f);
            bannerRt.pivot = new Vector2(0.5f, above ? 0f : 1f);
            bannerRt.anchoredPosition = new Vector2(0f, above ? 6f : -6f);
        }

        void HideBanner()
        {
            if (bannerRt != null) bannerRt.gameObject.SetActive(false);
            bannerShowingResult = false;
            bannerHideAt = -1f;
        }

        /// <summary>Called by anything that queues background work, so the banner can say what it is.</summary>
        void SetJob(string label, string detail, string doneText)
        {
            jobLabel = label ?? "";
            jobDetail = detail ?? "";
            jobDoneText = doneText ?? "";
        }

        /// <summary>Remembers the last switch flipped per material ("Matcap 1 on") for the banner text.</summary>
        void NoteToggleForBanner(Material m, PropInfo p)
        {
            if (m == null || p == null || !m.HasProperty(p.name)) return;
            bool on = (p.type == PropType.Int ? RecipeEngine.GetIntProp(m, p.name) : m.GetFloat(p.name)) >= 0.5f;
            string label = p.label;
            ShaderLayout L = ShaderLayout.For(m.shader);
            if (L != null)
                foreach (PropGroup g in AllGroups(L)) if (g.toggle == p) { label = g.title; break; }
            lastToggleDesc[m] = label + (p.enumKeywords != null ? " changed" : (on ? " on" : " off"));
        }

        static IEnumerable<PropGroup> AllGroups(ShaderLayout L)
        {
            var stack = new List<PropGroup>(L.roots);
            while (stack.Count > 0)
            {
                PropGroup g = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                yield return g;
                stack.AddRange(g.children);
            }
        }

        /// <summary>What the pending feature compile is for, in words.</summary>
        string VariantJobDetail()
        {
            var parts = new List<string>();
            foreach (MatEntry e in missingVariant)
            {
                if (e.mat == null) continue;
                string d;
                parts.Add(lastToggleDesc.TryGetValue(e.mat, out d) ? e.mat.name + ": " + d : e.mat.name);
                if (parts.Count >= 3) break;
            }
            if (missingVariant.Count > parts.Count) parts.Add("+" + (missingVariant.Count - parts.Count) + " more");
            return string.Join("   |   ", parts.ToArray()) + "   -  keeps the current look until it is ready";
        }

        void TickBanner()
        {
            if (bannerRt == null) return;
            bool busy = runner.Busy;
            bool waiting = !busy && variantBuildAt > 0f;
            float now = Time.unscaledTime;

            if (busy && jobStarted < 0f) jobStarted = now;
            if (wasBusy && !busy)
            {
                // the job just ended
                bool failed = runner.status.StartsWith("failed", StringComparison.Ordinal);
                bool cancelled = runner.status.StartsWith("cancelled", StringComparison.Ordinal);
                bannerShowingResult = true;
                int secs = jobStarted > 0f ? (int)(now - jobStarted) : 0;
                if (failed || cancelled || variantWarning.StartsWith("Unity could not", StringComparison.Ordinal))
                {
                    bannerTitle.text = cancelled ? "Cancelled." : "That did not work (" + secs + " s)";
                    bannerDetail.text = cancelled ? jobLabel : (variantWarning.StartsWith("Unity could not", StringComparison.Ordinal) ? variantWarning : "See the log on the Packages tab: " + runner.status);
                    SetBannerColor(BannerBad);
                    bannerHideAt = cancelled ? now + 4f : -1f;   // failures stay until closed
                }
                else
                {
                    bannerTitle.text = "Done (" + secs + " s)" + (jobDoneText.Length > 0 ? ": " + jobDoneText : "");
                    bannerDetail.text = jobLabel;
                    SetBannerColor(BannerOk);
                    bannerHideAt = now + 5f;
                }
                bannerBarRt.sizeDelta = new Vector2(bannerBarRt.parent is RectTransform ? ((RectTransform)bannerBarRt.parent).rect.width : W - 32, 5);
                bannerBarRt.anchoredPosition = Vector2.zero;
                bannerClose.gameObject.SetActive(true);
                jobStarted = -1f;
                // a feature compile may already be queued again (edits made while it ran)
                if (variantBuildAt > 0f) bannerHideAt = now + 1.5f;
            }
            wasBusy = busy;

            if (busy || waiting)
            {
                bannerShowingResult = false;
                bannerHideAt = -1f;
                SetBannerColor(BannerWork);
                bannerClose.gameObject.SetActive(false);
                if (busy)
                {
                    string step = runner.current != null ? runner.current.title : "Working";
                    bannerTitle.text = (jobLabel.Length > 0 ? jobLabel : step) + "...   " + (int)(now - jobStarted) + " s";
                    bannerDetail.text = (jobDetail.Length > 0 ? jobDetail + "   " : "") + "(" + step + ")";
                }
                else
                {
                    bannerTitle.text = "Getting ready to compile the switched feature...";
                    bannerDetail.text = VariantJobDetail();
                }
                // indeterminate bar sliding back and forth
                float track = ((RectTransform)bannerBarRt.parent).rect.width;
                bannerBarRt.sizeDelta = new Vector2(140f, 5f);
                float x = (Mathf.Sin(now * 2.2f) * 0.5f + 0.5f) * Mathf.Max(0f, track - 140f);
                bannerBarRt.anchoredPosition = new Vector2(x, 0f);
                if (!bannerRt.gameObject.activeSelf) bannerRt.gameObject.SetActive(true);
            }
            else if (bannerShowingResult)
            {
                if (bannerHideAt > 0f && now >= bannerHideAt) HideBanner();
            }
            else if (bannerRt.gameObject.activeSelf) HideBanner();

            // keep it on screen: below the window, or above when there is no room below
            if (bannerRt.gameObject.activeSelf)
            {
                var corners = new Vector3[4];
                root.GetWorldCorners(corners);
                Canvas c = root.GetComponentInParent<Canvas>();
                Camera cam = c != null && c.renderMode != RenderMode.ScreenSpaceOverlay ? c.worldCamera : null;
                float bottom = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;
                float scale = c != null ? c.scaleFactor : 1f;
                bool needAbove = bottom - (BannerH + 10f) * scale < 0f;
                if (needAbove != bannerAbove) PlaceBanner(needAbove);
            }
        }

        void SetBannerColor(Color c)
        {
            bannerEdge.color = c;
            bannerBar.color = c;
            bannerTitle.color = c == BannerWork ? Color.white : c;
        }
    }
}
