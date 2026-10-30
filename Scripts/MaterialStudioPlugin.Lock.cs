using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MaterialStudio
{
    /// <summary>
    /// Poiyomi "Lock &amp; optimize" from inside VNyan. Locking writes and compiles a brand new
    /// shader (Poiyomi's own optimizer), which only the Unity editor can do, so it runs as a
    /// background job in the private Unity project; the locked shaders come back in a bundle
    /// and the live materials switch onto them. Unlocking needs no Unity: it switches back to
    /// the full shader the material was locked from.
    /// </summary>
    public partial class MaterialStudioPlugin
    {
        /// <summary>A Poiyomi (or other Thry-optimizer) material that is currently unlocked.</summary>
        public static bool IsUnlockedPoiyomi(Material m)
        {
            return m != null && m.shader != null && m.HasProperty("_ShaderOptimizerEnabled")
                && !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal);
        }

        static bool UsesThryLock(Material m)
        {
            return m != null && (m.HasProperty("_ShaderOptimizerEnabled") || RecipeEngine.IsLockedPoiyomi(m));
        }

        LockRequest BuildLockRequest(MatEntry e)
        {
            Material m = e.mat;
            var r = new LockRequest { id = e.id, shader = m.shader.name, renderQueue = m.renderQueue };
            r.keywords.AddRange(m.shaderKeywords ?? new string[0]);
            MatSnapshot s = MatSnapshot.Capture(m);
            foreach (var kv in s.floats) r.floats.Add(new NamedFloat(kv.Key, kv.Value));
            foreach (var kv in s.colors) r.colors.Add(new NamedColor(kv.Key, kv.Value));
            foreach (var kv in s.vectors) r.vectors.Add(new NamedVector(kv.Key, kv.Value));
            foreach (var kv in s.textures)
            {
                Vector4 st;
                s.texST.TryGetValue(kv.Key, out st);
                r.textures.Add(new TexEdit { n = kv.Key, src = kv.Value != null ? "has" : "", stSet = true,
                                             scale = new Vector2(st.x, st.y), offset = new Vector2(st.z, st.w) });
            }
            foreach (var kv in s.animTags) r.tags.Add(new NamedString(kv.Key + "Animated", kv.Value));
            string rt = m.GetTag("RenderType", false, "");
            if (rt.Length > 0) r.tags.Add(new NamedString("RenderType", rt));
            return r;
        }

        /// <summary>Locks the selected unlocked Poiyomi materials (or every one on the avatar).</summary>
        void StartLock(bool all)
        {
            if (runner.Busy) { SetStatus("A job is already running; try again when it finishes."); return; }
            var entries = new List<MatEntry>();
            foreach (MatEntry e in all ? reg.entries : selection)
                if (e.mat != null && IsUnlockedPoiyomi(e.mat) && !entries.Contains(e)) entries.Add(e);
            if (entries.Count == 0) { SetStatus(all ? "No unlocked Poiyomi materials on this avatar." : "None of the selected materials is unlocked Poiyomi."); return; }

            AutoPickSeedProject();
            string project = WorkProject;
            if (string.IsNullOrEmpty(project)) { SetStatus("Locking runs Poiyomi's optimizer in Unity: pick your VSF SDK project on the Packages tab first."); ShowTab(2); return; }
            string exe = PickEditor(project);
            if (exe == null || !CheckNotOpen(project)) { ShowTab(2); return; }
            if (!QueuePrepare(project, exe, false)) { ShowTab(2); return; }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string jobDir = Path.Combine(UnityDriver.JobsDir, "lock_" + stamp);
            Directory.CreateDirectory(jobDir);
            var job = new MSJob
            {
                kind = "lock",
                resultPath = Path.Combine(jobDir, "result.json"),
                bundleName = "ms_locked_" + stamp,
                outDir = ShaderCatalog.LockDir,
            };
            // what each material looked like when it was sent, to notice edits made meanwhile
            var sent = new Dictionary<string, MatSnapshot>();
            foreach (MatEntry e in entries)
            {
                job.locks.Add(BuildLockRequest(e));
                sent[e.id] = MatSnapshot.Capture(e.mat);
            }
            string jobPath = Path.Combine(jobDir, "job.json");
            File.WriteAllText(jobPath, JsonIO.ToJson(job, true));
            var lockNames = new List<string>();
            foreach (MatEntry e in entries) { if (lockNames.Count < 4) lockNames.Add(e.mat.name); }
            SetJob("Locking " + entries.Count + " Poiyomi material(s)",
                   string.Join(", ", lockNames.ToArray()) + (entries.Count > lockNames.Count ? " +" + (entries.Count - lockNames.Count) + " more" : "") + "   -  you can keep editing",
                   "locked and optimised");
            runner.Enqueue(new UnityStep
            {
                title = "Locking " + entries.Count + " Poiyomi material(s)",
                exe = exe, project = project, graphics = true,
                extraArgs = "-executeMethod MaterialStudio.EditorTools.MSBatch.Run -msjob \"" + jobPath + "\"",
                logPath = Path.Combine(jobDir, "unity.log"),
                resultPath = job.resultPath,
                onDone = delegate (UnityStep st, MSResult r)
                {
                    if (!r.ok || string.IsNullOrEmpty(r.output)) { SetStatus("Locking failed; see the log on the Packages tab."); return; }
                    ApplyLocked(Path.Combine(ShaderCatalog.LockDir, r.output), sent);
                }
            });
            SetStatus("Locking " + entries.Count + " Poiyomi material(s) in the background (Unity, a minute or two)...");
            RefreshAll();
        }

        void ApplyLocked(string bundlePath, Dictionary<string, MatSnapshot> sent)
        {
            var log = new List<string>();
            LockManifest man = catalog.LoadLockBundle(bundlePath, log);
            foreach (string l in log) runner.Log(l);
            if (man == null) { SetStatus("The locked shaders could not be loaded."); return; }
            // switched off while Unity was locking: keep the shipped look, nothing is saved
            if (!cfg.enabled) { SetStatus("Locking finished, but Material Studio is off, so nothing was changed. Lock again after turning it on."); return; }
            string file = Path.GetFileName(bundlePath);
            var targets = new List<KeyValuePair<Material, Material>>();
            int changedMeanwhile = 0;
            foreach (LockedEntry le in man.entries)
            {
                MatEntry e = reg.EntryById(le.id);
                Material locked = catalog.LockedMaterial(file, le.id);
                if (e == null || e.mat == null || locked == null) continue;
                MatSnapshot before;
                if (sent.TryGetValue(le.id, out before) && RecipeEngine.Diff(before, e.mat, null) != null) changedMeanwhile++;
                targets.Add(new KeyValuePair<Material, Material>(e.mat, locked));
            }
            if (targets.Count == 0) { SetStatus("Nothing to switch: the avatar changed while locking."); return; }
            var mats = new List<Material>();
            foreach (var kv in targets) mats.Add(kv.Key);
            BeginEdit("lock Poiyomi", mats);
            foreach (var kv in targets)
            {
                Material m = kv.Key, locked = kv.Value;
                m.shader = locked.shader;
                m.renderQueue = locked.renderQueue;
                m.shaderKeywords = locked.shaderKeywords;
                foreach (string tag in new[] { "OriginalShader", "OriginalShaderGUID", "RenderType" })
                {
                    string v = locked.GetTag(tag, false, "");
                    if (v.Length > 0) m.SetOverrideTag(tag, v);
                }
                if (m.HasProperty("_ShaderOptimizerEnabled")) m.SetFloat("_ShaderOptimizerEnabled", 1f);
                MatEntry e = reg.EntryFor(m);
                if (e != null) e.unlockedPoiyomi = false;
            }
            MarkDirty();
            catalog.Refresh(reg.Shaders());
            SetStatus("Locked and optimised " + targets.Count + " Poiyomi material(s)." +
                      (changedMeanwhile > 0 ? " " + changedMeanwhile + " were edited while locking; lock them again to include those edits." : ""));
            RefreshAll();
        }

        /// <summary>Marks a property to stay live (uniform) after locking, like Poiyomi's right-click "Animated".</summary>
        void ToggleLiveWhenLocked(string prop)
        {
            List<Material> mats = Targets(prop);
            mats.RemoveAll(delegate (Material m) { return !UsesThryLock(m); });
            if (mats.Count == 0) return;
            bool on = !IsAnimated(mats[0], prop);
            BeginEdit("live when locked " + prop, mats);
            bool anyLocked = false;
            foreach (Material m in mats)
            {
                m.SetOverrideTag(prop + "Animated", on ? "1" : "");
                if (RecipeEngine.IsLockedPoiyomi(m)) anyLocked = true;
            }
            MarkDirty();
            SetStatus(prop + (on ? " stays live after locking." : " gets baked in when locking.") +
                      (anyLocked ? " Unlock and lock again for it to take effect." : ""));
            RefreshRows();
        }
    }
}
