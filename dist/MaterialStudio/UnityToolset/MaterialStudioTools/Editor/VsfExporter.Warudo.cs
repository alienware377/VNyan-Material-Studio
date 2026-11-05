using System;
using System.Collections.Generic;
using System.IO;
using MaterialStudio.Warudo;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// The .warudo source. A Warudo mod is an AssetBundle whose components were replaced by uMod
    /// placeholders and which has no VRM setup at all, so before the normal export can run, the
    /// avatar is put together the way the Warudo Importer does it in VNyan: the mod's own
    /// components are rebuilt from uMod's data, the humanoid avatar and the VRM components are
    /// added (keeping the mod's own expression set when it has one), and bone physics becomes
    /// DynamicBone. The plugin has already unpacked the mod's bundle next to the job.
    /// </summary>
    public static partial class VsfExporter
    {
        static AssetBundle StageWarudo(MSJob job, MSResult res, out ExportInventory source, out GameObject inst, out HashSet<string> lockedSlots)
        {
            source = null;
            inst = null;
            lockedSlots = null;
            if (!string.IsNullOrEmpty(job.modUnityVersion) && CompareUnityVersions(Application.unityVersion, job.modUnityVersion) < 0)
                throw new Exception("This mod was made with Unity " + job.modUnityVersion + ", and Unity " + Application.unityVersion +
                                    " cannot open it. Install Unity " + job.modUnityVersion + " or newer.");
            if (string.IsNullOrEmpty(job.warudoBundlePath) || !File.Exists(job.warudoBundlePath))
                throw new Exception("The mod's unpacked AssetBundle is missing (" + job.warudoBundlePath + ").");
            if (!VrmReflect.Available)
                throw new Exception("UniVRM 0.x is not in this Unity project, so the avatar cannot get its VRM components.");

            AssetBundle.UnloadAllAssetBundles(true);
            WarudoBundle.Result wb = WarudoBundle.Load(job.warudoBundlePath);
            if (!wb.Ok)
            {
                if (wb.bundle != null) wb.bundle.Unload(true);
                throw new Exception("Unity could not open " + Path.GetFileName(job.sourcePath) + ": " + wb.error);
            }
            try
            {
                source = ExportInventory.Of(wb.prefab, wb.bundle);
                int dropped = DropModPlaceholders(source);
                inst = UnityEngine.Object.Instantiate(wb.prefab);
                inst.name = string.IsNullOrEmpty(job.modName) ? Path.GetFileNameWithoutExtension(job.sourcePath) : job.modName;
                res.notes.Add("Opened " + Path.GetFileName(job.sourcePath) + " (made with Unity " + job.modUnityVersion + ", asset " + wb.assetName + ").");
                lockedSlots = LockedSlots(inst);

                // the mod's real components, rebuilt from uMod's link data in the bundle
                ModRestore.Result rr = ModRestore.Restore(inst, job.warudoBundlePath);
                foreach (string n in rr.notes) res.notes.Add(n);
                if (!string.IsNullOrEmpty(rr.error)) res.notes.Add("WARNING: " + rr.error);
                int dead = RemoveDeadScripts(inst, res.notes);
                if (dead > 0 || dropped > 0)
                    res.notes.Add("Removed " + dead + " uMod placeholder component(s); the mod's own components were rebuilt from their data where this project has their scripts.");

                // humanoid avatar + VRM components, the way the Warudo Importer sets them up in VNyan
                var opt = new AvatarPrep.Options
                {
                    title = inst.name,
                    author = job.modAuthor,
                    version = job.modVersion,
                    thumbnail = LoadCover(job.modCoverPath),
                    modBlendShapeAvatar = wb.blendShapeAvatar,
                };
                AvatarPrep.Result prep = AvatarPrep.Prepare(inst, opt);
                foreach (string n in prep.notes) res.notes.Add(n);
                if (!prep.Ok) throw new Exception("The avatar could not be set up: " + string.Join("; ", prep.errors.ToArray()));
                res.notes.Add(prep.Summary());
                KeepWhatPrepRemoved(source, inst, res.notes);

                if (job.convertPhysics) ConvertPhysics(inst, rr, res.notes);
                // VRChat's PhysBones only carried their settings here; VNyan cannot run them
                int vrc = 0;
                foreach (MonoBehaviour mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && (mb.GetType().Namespace ?? "").StartsWith("VRC.", StringComparison.Ordinal)) { UnityEngine.Object.DestroyImmediate(mb); vrc++; }
                if (vrc > 0)
                    res.notes.Add("Removed " + vrc + " VRChat PhysBone part(s) VNyan cannot run" +
                                  (job.convertPhysics ? " (their settings went into DynamicBone)." : " (bone physics conversion is off, so those bones will not move)."));

                foreach (MonoBehaviour mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || AssetPersist.ScriptIsAsset(mb)) continue;
                    if (!AssetPersist.RelinkScript(mb))
                        throw new Exception("No script file in the Unity project for " + mb.GetType().FullName + " on " + mb.gameObject.name + ".");
                }
                return wb.bundle;
            }
            catch
            {
                if (inst != null) UnityEngine.Object.DestroyImmediate(inst);
                inst = null;
                wb.bundle.Unload(true);
                throw;
            }
        }

        /// <summary>
        /// uMod placeholders are not part of the avatar: leave them, their scripts and their data
        /// objects out of what the new file is checked against. Returns how many were dropped.
        /// </summary>
        static int DropModPlaceholders(ExportInventory inv)
        {
            int n = 0;
            foreach (Dictionary<string, int> d in inv.components.Values)
            {
                var drop = new List<string>();
                foreach (string k in d.Keys) if (k == "(missing script)" || k.StartsWith("UMod.", StringComparison.Ordinal)) drop.Add(k);
                foreach (string k in drop) { n += d[k]; d.Remove(k); }
            }
            foreach (string s in inv.deadScripts) { inv.scripts.Remove(s); inv.usedScripts.Remove(s); }
            for (int i = 0; i < inv.objectScripts.Count; i++)
                if (inv.objectScripts[i] != null && inv.deadScripts.Contains(inv.objectScripts[i])) inv.objectScripts[i] = null;
            inv.deadScripts.Clear();
            for (int i = inv.objects.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object o = inv.objects[i];
                if (!(o is MonoBehaviour) && !(o is ScriptableObject)) continue;
                Type t = o.GetType();
                if (t != typeof(MonoBehaviour) && t != typeof(ScriptableObject) && !(t.Namespace ?? "").StartsWith("UMod", StringComparison.Ordinal)) continue;
                inv.objects.RemoveAt(i);
                inv.keys.RemoveAt(i);
                inv.objectScripts.RemoveAt(i);
            }
            return n;
        }

        /// <summary>Animators below the root are removed on purpose (VNyan drives the root one only).</summary>
        static void KeepWhatPrepRemoved(ExportInventory inv, GameObject inst, List<string> notes)
        {
            var now = new Dictionary<string, Transform>();
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true)) now[RecipePaths.PathOf(t, inst.transform)] = t;
            int n = 0;
            foreach (var kv in inv.components)
            {
                int had;
                if (kv.Key.Length == 0 || !kv.Value.TryGetValue(typeof(Animator).FullName, out had)) continue;
                Transform t;
                int left = now.TryGetValue(kv.Key, out t) ? t.GetComponents<Animator>().Length : 0;
                if (left >= had) continue;
                if (left == 0) kv.Value.Remove(typeof(Animator).FullName); else kv.Value[typeof(Animator).FullName] = left;
                n += had - left;
            }
            if (n > 0) notes.Add("Left out " + n + " animator(s) below the avatar's root, as the Warudo Importer does (VNyan drives the root one).");
        }

        /// <summary>The mod's bone physics as DynamicBone, skipping bones its own physics already moves.</summary>
        static void ConvertPhysics(GameObject inst, ModRestore.Result rr, List<string> notes)
        {
            if (!DynamicBoneConvert.DynamicBoneAvailable)
            {
                notes.Add("DynamicBone is not in this Unity project, so no bone physics were converted" +
                          (rr.springBones > 0 ? " (the mod's own " + rr.springBones + " VRM spring bone(s) still work)." : "."));
                return;
            }
            var dopt = new DynamicBoneConvert.Options { gen = new GenOptions(), preClaimed = rr.nativelyDriven };
            if (rr.HasNativeCloth) dopt.fromMagicaCloth = false;
            if (rr.HasNativeSpringBones) dopt.fromSpringBone = false;
            Animator anim = inst.GetComponent<Animator>();
            dopt.gen.scale = PhysBonesGen.MeasureScale(anim);
            DynamicBoneConvert.Result dr = DynamicBoneConvert.Convert(inst, anim, dopt);
            foreach (string n in dr.notes) notes.Add(n);
            notes.Add("Bone physics: " + dr.Total + " DynamicBone chain(s) (" + dr.fromPhysBone + " from PhysBones, " + dr.fromSpringBone +
                      " from spring bones, " + dr.fromMagicaCloth + " from Magica Cloth, " + dr.fromHeuristic + " found by bone names), " + dr.colliders + " collider(s).");
        }

        static Texture2D LoadCover(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Thumbnail" };
            if (t.LoadImage(File.ReadAllBytes(path))) return t;
            UnityEngine.Object.DestroyImmediate(t);
            return null;
        }

        /// <summary>Compares Unity versions like "2021.3.45f2"; negative when a is older than b.</summary>
        static int CompareUnityVersions(string a, string b)
        {
            int[] pa = VersionParts(a), pb = VersionParts(b);
            for (int i = 0; i < pa.Length; i++)
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            return 0;
        }

        static int[] VersionParts(string v)
        {
            // 2021.3.45f2 -> 2021, 3, 45, 'f' rank, 2
            var p = new int[5];
            if (string.IsNullOrEmpty(v)) return p;
            int i = 0, k = 0;
            while (i < v.Length && k < 5)
            {
                if (char.IsDigit(v[i]))
                {
                    int n = 0;
                    while (i < v.Length && char.IsDigit(v[i])) n = n * 10 + (v[i++] - '0');
                    p[k++] = n;
                }
                else
                {
                    if (k == 3) p[k++] = "abpf".IndexOf(char.ToLowerInvariant(v[i]));
                    i++;
                }
            }
            return p;
        }
    }
}
