using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MaterialStudio
{
    /// <summary>
    /// "Pick on model": point at the avatar to outline the part under the cursor, click to
    /// select its material. Skinned meshes have no colliders, so each candidate renderer
    /// (ray through its bounds) is baked to its current pose and hit-tested per triangle,
    /// per submesh. The highlight is drawn on that baked copy with Graphics.DrawMesh, so the
    /// avatar's own materials are never touched. Costs nothing while the mode is off.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        // Assigned by the bundle build (MaterialStudioBuild.cs).
        public Material pickOutline;

        Button pickButton;
        bool pickMode;
        Material hoverMat, selectMat;

        class Baked
        {
            public Mesh mesh;
            public bool owned;              // our baked copy (destroy it) vs the avatar's own mesh (never)
            public Matrix4x4 toWorld;
            public float at = -1f;
            public float vertsAt = -2f;
            public Vector3[] verts;
            public int[][] tris;
        }

        readonly Dictionary<Renderer, Baked> baked = new Dictionary<Renderer, Baked>();
        Renderer hoverRenderer;
        int hoverSub = -1;
        Vector3 lastMouse = new Vector3(-1, -1, -1);
        float selectAge = 0.1f;
        float nextHover;

        void TogglePickMode()
        {
            if (!pickMode && !CanEdit()) return;
            pickMode = !pickMode;
            if (pickMode && pickOutline == null) { pickMode = false; SetStatus("The outline shader is missing from the plugin bundle; reinstall Material Studio."); return; }
            if (pickMode && boundAvatar == null) { pickMode = false; SetStatus("Load an avatar first."); return; }
            if (pickButton != null)
            {
                pickButton.GetComponent<Image>().color = pickMode ? new Color(0.20f, 0.50f, 0.30f, 1f) : Ui.Btn;
                Ui.SetButtonText(pickButton, pickMode ? "Picking... (Esc)" : "Pick on model");
            }
            if (pickMode)
            {
                if (hoverMat == null) { hoverMat = new Material(pickOutline); hoverMat.SetColor("_Color", new Color(1f, 0.82f, 0.25f, 1f)); hoverMat.SetColor("_Fill", new Color(1f, 0.82f, 0.25f, 0.16f)); }
                if (selectMat == null) { selectMat = new Material(pickOutline); selectMat.SetColor("_Color", new Color(0.35f, 0.85f, 1f, 0.9f)); selectMat.SetColor("_Fill", new Color(0.35f, 0.85f, 1f, 0.07f)); }
                SetStatus("Point at the avatar and click a part. Shift/Ctrl-click adds to the selection. Esc stops.");
            }
            else
            {
                hoverRenderer = null; hoverSub = -1;
                foreach (Baked b in baked.Values) if (b.owned && b.mesh != null) Destroy(b.mesh);
                baked.Clear();
                SetStatus("");
            }
        }

        /// <summary>Called every frame from Update while the window is open.</summary>
        void TickPick()
        {
            if (!pickMode) return;
            if (!cfg.enabled) { TogglePickMode(); return; }
            if (boundAvatar == null || window == null || !window.activeSelf) { TogglePickMode(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { TogglePickMode(); return; }
            Camera cam = PickCamera();
            if (cam == null) return;

            bool overUi = PointerOverUi();
            if (!overUi && (Input.mousePosition != lastMouse || Time.unscaledTime >= nextHover))
            {
                lastMouse = Input.mousePosition;
                nextHover = Time.unscaledTime + 0.12f;
                Hover(cam.ScreenPointToRay(Input.mousePosition));
            }
            else if (overUi) { hoverRenderer = null; hoverSub = -1; }

            if (!overUi && Input.GetMouseButtonDown(0) && hoverRenderer != null) PickHovered();

            float w = 2.2f * 2f / Mathf.Max(1, cam.pixelHeight);   // ~2 px in clip space
            if (hoverMat != null) hoverMat.SetFloat("_Width", w * 1.3f);
            if (selectMat != null) selectMat.SetFloat("_Width", w);
            DrawSelection(cam);
            if (hoverRenderer != null && hoverSub >= 0) DrawPart(hoverRenderer, hoverSub, hoverMat, cam, true);
        }

        static bool PointerOverUi()
        {
            EventSystem es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }

        /// <summary>The camera drawing the avatar: enabled, on screen, sees its layer; frontmost wins.</summary>
        Camera PickCamera()
        {
            int layerBit = 1 << boundAvatar.layer;
            Camera best = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || !c.enabled || c.targetTexture != null || (c.cullingMask & layerBit) == 0) continue;
                if (!c.pixelRect.Contains(Input.mousePosition)) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best != null ? best : Camera.main;
        }

        IEnumerable<Renderer> VisibleRenderers()
        {
            foreach (Renderer r in boundAvatar.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                yield return r;
            }
        }

        void Hover(Ray ray)
        {
            // cheap pass: renderer bounds the ray goes through, nearest first
            var cands = new List<KeyValuePair<float, Renderer>>();
            foreach (Renderer r in VisibleRenderers())
            {
                float d;
                if (r.bounds.IntersectRay(ray, out d)) cands.Add(new KeyValuePair<float, Renderer>(d, r));
            }
            cands.Sort(delegate (KeyValuePair<float, Renderer> a, KeyValuePair<float, Renderer> b) { return a.Key.CompareTo(b.Key); });
            float bestDist = float.MaxValue;
            Renderer bestR = null;
            int bestSub = -1;
            foreach (var kv in cands)
            {
                if (kv.Key > bestDist) break;   // every later box starts beyond the hit we have
                Baked b = Bake(kv.Value, 0.15f);
                if (b == null) continue;
                if (b.vertsAt != b.at && b.mesh != null) { b.verts = b.mesh.vertices; b.vertsAt = b.at; }
                float d; int sub;
                if (HitMesh(b, ray, out d, out sub) && d < bestDist) { bestDist = d; bestR = kv.Value; bestSub = sub; }
            }
            if (bestR != hoverRenderer || bestSub != hoverSub)
            {
                hoverRenderer = bestR;
                hoverSub = bestSub;
                if (bestR != null)
                {
                    Material[] ms = bestR.sharedMaterials;
                    Material m = ms.Length > 0 ? ms[Mathf.Min(bestSub, ms.Length - 1)] : null;
                    SetStatus(bestR.name + "  >  " + (m != null ? m.name : "(no material)") + "   (click to edit)");
                }
            }
        }

        /// <summary>Current-pose copy of a renderer, refreshed at most every <paramref name="maxAge"/> seconds.</summary>
        Baked Bake(Renderer r, float maxAge)
        {
            Baked b;
            if (baked.TryGetValue(r, out b) && Time.unscaledTime - b.at < maxAge) return b;
            if (b == null) { b = new Baked(); baked[r] = b; }
            try
            {
                var smr = r as SkinnedMeshRenderer;
                if (smr != null)
                {
                    if (smr.sharedMesh == null) return null;
                    if (b.mesh == null) { b.mesh = new Mesh { name = "MS pick bake" }; b.owned = true; }
                    smr.BakeMesh(b.mesh, true);   // posed, scale applied: world = TRS(pos, rot, 1)
                    b.toWorld = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                    if (b.tris == null || b.tris.Length != b.mesh.subMeshCount) b.tris = Tris(b.mesh);
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return null;
                    b.mesh = mf.sharedMesh;   // not ours: never destroyed
                    b.owned = false;
                    b.toWorld = r.localToWorldMatrix;
                    if (b.tris == null) b.tris = Tris(b.mesh);
                    if (b.verts == null) b.verts = b.mesh.vertices;
                }
                b.at = Time.unscaledTime;
                return b;
            }
            catch { return null; }
        }

        static int[][] Tris(Mesh m)
        {
            var t = new int[m.subMeshCount][];
            for (int s = 0; s < t.Length; s++) t[s] = m.GetTriangles(s);
            return t;
        }

        /// <summary>Nearest triangle hit along the ray, in world distance; which submesh it was.</summary>
        static bool HitMesh(Baked b, Ray worldRay, out float dist, out int sub)
        {
            dist = float.MaxValue; sub = -1;
            if (b.verts == null || b.tris == null) return false;
            Matrix4x4 inv = b.toWorld.inverse;
            Vector3 o = inv.MultiplyPoint3x4(worldRay.origin);
            Vector3 dir = inv.MultiplyVector(worldRay.direction).normalized;
            Vector3[] v = b.verts;
            float best = float.MaxValue;
            for (int s = 0; s < b.tris.Length; s++)
            {
                int[] t = b.tris[s];
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    float d;
                    if (RayTri(o, dir, v[t[i]], v[t[i + 1]], v[t[i + 2]], out d) && d < best) { best = d; sub = s; }
                }
            }
            if (sub < 0) return false;
            dist = Vector3.Distance(worldRay.origin, b.toWorld.MultiplyPoint3x4(o + dir * best));
            return true;
        }

        // Moller-Trumbore, double-sided
        static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-9f && det < 1e-9f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 1e-6f;
        }

        void PickHovered()
        {
            Material[] ms = hoverRenderer.sharedMaterials;
            if (ms.Length == 0) return;
            // more materials than submeshes: the extras draw over the last submesh
            Material m = ms[Mathf.Min(hoverSub, ms.Length - 1)];
            MatEntry e = reg.EntryFor(m);
            if (e == null) { reg.Bind(boundAvatar); e = reg.EntryFor(m); }
            if (e == null) return;
            bool add = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                    || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (add)
            {
                if (selection.Contains(e)) { if (selection.Count > 1) selection.Remove(e); }
                else selection.Add(e);
            }
            else
            {
                selection.Clear();
                selection.Add(e);
                selectedRendererPath = RecipePaths.PathOf(hoverRenderer.transform, boundAvatar.transform);
            }
            scrollToPrimary = true;
            SetStatus((add ? "Added " : "Editing ") + m.name + " (" + hoverRenderer.name + ")." + (selection.Count > 1 ? " " + selection.Count + " selected." : ""));
            RefreshAll();
        }

        /// <summary>Soft outline on every visible part using a selected material.</summary>
        void DrawSelection(Camera cam)
        {
            if (selectMat == null || selection.Count == 0) return;
            var mats = new HashSet<Material>();
            foreach (MatEntry e in selection) if (e.mat != null) mats.Add(e.mat);
            int parts = 0;
            foreach (Renderer r in VisibleRenderers())
                foreach (Material m in r.sharedMaterials) if (m != null && mats.Contains(m)) parts++;
            selectAge = parts <= 6 ? 0.1f : (parts <= 20 ? 0.4f : 1.0f);
            foreach (Renderer r in VisibleRenderers())
            {
                Material[] ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i] == null || !mats.Contains(ms[i])) continue;
                    if (r == hoverRenderer && i == hoverSub) continue;
                    DrawPart(r, i, selectMat, cam, false);
                }
            }
        }

        void DrawPart(Renderer r, int slot, Material mat, Camera cam, bool fresh)
        {
            Baked b = Bake(r, fresh ? 0f : selectAge);
            if (b == null || b.mesh == null || b.mesh.subMeshCount == 0) return;
            int sub = Mathf.Min(slot, b.mesh.subMeshCount - 1);
            // follow the avatar between bakes
            Matrix4x4 m = r is SkinnedMeshRenderer ? Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one) : r.localToWorldMatrix;
            Graphics.DrawMesh(b.mesh, m, mat, r.gameObject.layer, cam, sub);
        }
    }
}
