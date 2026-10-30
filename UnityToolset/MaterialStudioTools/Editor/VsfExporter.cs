using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VNyanWeightStudio;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// Builds a .vsfavatar with the VNyan edits baked in:
    ///   1. stage the avatar (from a .vsfavatar file, or from a prefab in its own project),
    ///   2. copy everything it uses into project assets (never touching the user's own),
    ///   3. rebind locked Poiyomi to its full shader,
    ///   4. apply live bone weights captured in VNyan (only meshes that really differ),
    ///   5. apply the material recipe,
    ///   6. lock Poiyomi again (Thry ShaderOptimizer, by reflection),
    ///   7. check and build exactly like the VSF SDK's own exporter.
    /// </summary>
    public static class VsfExporter
    {
        public static void Export(MSJob job, MSResult res)
        {
            string name = Path.GetFileNameWithoutExtension(job.outPath);
            string workDir = "Assets/MaterialStudioExport/" + MSBatch.Sanitize(name);
            if (AssetDatabase.IsValidFolder(workDir)) AssetDatabase.DeleteAsset(workDir);
            MSBatch.EnsureFolder(workDir);

            GameObject inst = null;
            HashSet<string> lockedSlots = null;
            AssetBundle bundle = null;
            AssetPersist persist = null;
            try
            {
                // ---- 1. stage
                if (job.sourceKind == "prefab")
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(job.sourcePath);
                    if (prefab == null) throw new Exception("Prefab not found: " + job.sourcePath);
                    inst = UnityEngine.Object.Instantiate(prefab);
                    inst.name = prefab.name;
                    // copies of the materials, so the project's own are never changed
                    var matCopies = new Dictionary<Material, Material>();
                    foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] ms = r.sharedMaterials;
                        for (int i = 0; i < ms.Length; i++)
                        {
                            if (ms[i] == null) continue;
                            Material c;
                            if (!matCopies.TryGetValue(ms[i], out c))
                            {
                                MSBatch.EnsureFolder(workDir + "/Materials");
                                c = new Material(ms[i]) { name = ms[i].name };
                                AssetDatabase.CreateAsset(c, MSBatch.NamedPath(workDir + "/Materials", ms[i].name, ".mat"));
                                matCopies[ms[i]] = c;
                            }
                            ms[i] = c;
                        }
                        r.sharedMaterials = ms;
                    }
                    res.notes.Add("Staged prefab " + job.sourcePath + " (" + matCopies.Count + " material copies).");
                }
                else
                {
                    AssetBundle.UnloadAllAssetBundles(true);
                    bundle = AssetBundle.LoadFromFile(job.sourcePath);
                    if (bundle == null) throw new Exception("Unity could not open " + Path.GetFileName(job.sourcePath) +
                        ". A .vsfavatar can only be read by the Unity version that built it or newer (this is " + Application.unityVersion + ").");
                    GameObject prefab = bundle.LoadAsset<GameObject>("VSFAvatar") ?? PickAvatar(bundle);
                    if (prefab == null) throw new Exception("No avatar inside " + Path.GetFileName(job.sourcePath) + ".");
                    inst = UnityEngine.Object.Instantiate(prefab);
                    inst.name = Path.GetFileNameWithoutExtension(job.sourcePath);
                    lockedSlots = LockedSlots(inst);
                    int dead = RemoveDeadScripts(inst, res.notes);
                    int relinked = 0, unlinked = 0;
                    foreach (MonoBehaviour mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null || AssetPersist.ScriptIsAsset(mb)) continue;
                        if (AssetPersist.RelinkScript(mb)) relinked++;
                        else { unlinked++; res.notes.Add("WARNING: no script file for " + mb.GetType().FullName + " on " + mb.gameObject.name + "."); }
                    }
                    res.notes.Add("Re-linked " + relinked + " component script(s) to this project (VRM, VSF and other components).");
                    if (unlinked > 0) throw new Exception(unlinked + " component(s) have no script file in the Unity project, so they would come out missing. Seed the project with the folders those scripts live in.");
                    if (dead > 0)
                        res.notes.Add("Removed " + dead + " component(s) whose scripts are not in this project. Seed the project with the folders those scripts live in to keep them.");
                    persist = new AssetPersist(workDir, res.notes) { avatarRoot = inst };
                    persist.PersistAll(inst);
                    if (persist.missingShaders.Count > 0)
                        throw new Exception("These shaders are not in the Unity project: " + string.Join(", ", persist.missingShaders.ToArray()) +
                            ". Add their .unitypackage on the Packages tab (it is imported on the next export), or the avatar would render pink.");
                }

                // ---- 3. locked Poiyomi (prefab mode: materials are project assets, the lock is Thry's job)
                if (lockedSlots == null) lockedSlots = LockedSlots(inst);
                int unlocked = UnlockPoiyomi(inst, res.notes);
                if (unlocked > 0) res.notes.Add("Unlocked " + unlocked + " Poiyomi material(s) for editing.");

                // ---- 4. live weights
                if (job.applyLiveWeights && !string.IsNullOrEmpty(job.liveVbxPath) && File.Exists(job.liveVbxPath))
                    ApplyLiveWeights(inst, job.liveVbxPath, workDir, res.notes);

                // ---- 5. recipe
                if (!string.IsNullOrEmpty(job.recipePath) && File.Exists(job.recipePath))
                {
                    Recipe r = Recipe.FromJson(File.ReadAllText(job.recipePath));
                    if (r != null)
                    {
                        // Locked / Unlocked in VNyan (Shader tab) shows up as a shader change
                        foreach (MatEdit e in r.materials)
                        {
                            if (e == null || e.slots == null || string.IsNullOrEmpty(e.shader)) continue;
                            bool locked = e.shader.StartsWith("Hidden/Locked/", StringComparison.Ordinal);
                            foreach (SlotRef s in e.slots)
                                if (locked) lockedSlots.Add(s.path + "|" + s.slot); else lockedSlots.Remove(s.path + "|" + s.slot);
                        }
                        RecipeApplier.Apply(inst, r, job.recipeDir, workDir, true,
                            persist != null ? persist.textureByOriginalName : null, res.notes);
                    }
                }
                else res.notes.Add("No saved edits for this model; exporting it as it is.");

                // ---- 6. lock Poiyomi again, exactly where it is locked in VNyan (or in the file)
                if (job.relockPoiyomi && lockedSlots.Count > 0) LockPoiyomi(inst, res.notes, null, lockedSlots);
                else if (job.relockPoiyomi) res.notes.Add("No Poiyomi material is locked in VNyan, so none were locked.");

                // ---- 7. check + build like the VSF SDK
                string hollow = FindNonAssets(inst);
                if (hollow != null) throw new Exception("Some parts are still not project assets, so the export would come out empty: " + hollow);
                string dangling = FindUnsavedScripts(inst);
                if (dangling != null) throw new Exception("Some components would lose their script in the export: " + dangling);
                string err = CheckAvatar(inst);
                if (err != null) throw new Exception("VSF SDK avatar check failed: " + err);
                int expectSmr = 0;
                foreach (SkinnedMeshRenderer s in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (s.sharedMesh != null) expectSmr++;
                Dictionary<string, int> expectComps = ComponentCounts(inst);
                Build(inst, job.outPath, res);
                // close the source first: a same-named output gets the same internal bundle name
                if (bundle != null) { bundle.Unload(false); bundle = null; }
                VerifyOutput(job.outPath, expectSmr, expectComps, res);
                res.output = job.outPath;
            }
            catch (Exception e)
            {
                res.ok = false;
                res.message = e.Message;
                Debug.LogError("[MS] export failed: " + e);
            }
            finally
            {
                if (inst != null) UnityEngine.Object.DestroyImmediate(inst);
                if (bundle != null) bundle.Unload(true);
                if (res.ok && AssetDatabase.IsValidFolder(workDir)) AssetDatabase.DeleteAsset(workDir);
                else if (!res.ok) res.notes.Add("Intermediate assets kept for inspection in " + workDir + ".");
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>Lists meshes/materials that would be left behind by the bundle build, or null.</summary>
        static string FindNonAssets(GameObject root)
        {
            var bad = new List<string>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                var smr = r as SkinnedMeshRenderer;
                Mesh mesh = smr != null ? smr.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
                if (mesh != null && !AssetPersist.IsAsset(mesh)) bad.Add(r.name + " (mesh)");
                foreach (Material m in r.sharedMaterials)
                    if (m != null && !AssetPersist.IsAsset(m)) bad.Add(r.name + " (material " + m.name + ")");
                if (bad.Count > 8) break;
            }
            return bad.Count == 0 ? null : string.Join(", ", bad.ToArray());
        }

        /// <summary>Components and ScriptableObjects whose script is not a project asset, or null.</summary>
        static string FindUnsavedScripts(GameObject root)
        {
            var bad = new List<string>();
            var seen = new HashSet<UnityEngine.Object>();
            var stack = new Stack<UnityEngine.Object>();
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) stack.Push(mb);
            while (stack.Count > 0 && bad.Count < 8)
            {
                UnityEngine.Object o = stack.Pop();
                if (o == null || !seen.Add(o)) continue;
                if (!AssetPersist.ScriptIsAsset(o)) bad.Add(o.GetType().Name + " '" + o.name + "'");
                // follow references into ScriptableObjects (VRM meta, blendshape avatar -> clips)
                SerializedProperty it = new SerializedObject(o).GetIterator();
                while (it.Next(true))
                    if (it.propertyType == SerializedPropertyType.ObjectReference && it.name != "m_Script" && it.objectReferenceValue is ScriptableObject)
                        stack.Push(it.objectReferenceValue);
            }
            return bad.Count == 0 ? null : string.Join(", ", bad.ToArray());
        }

        /// <summary>Component type -> count over the whole avatar ("(missing)" for dead scripts).</summary>
        static Dictionary<string, int> ComponentCounts(GameObject root)
        {
            var d = new Dictionary<string, int>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                foreach (Component c in t.GetComponents<Component>())
                {
                    string k = c == null ? "(missing)" : c.GetType().FullName;
                    int n;
                    d.TryGetValue(k, out n);
                    d[k] = n + 1;
                }
            return d;
        }

        /// <summary>Opens the new .vsfavatar again and checks every mesh and component made it in.</summary>
        static void VerifyOutput(string path, int expectSmr, Dictionary<string, int> expectComps, MSResult res)
        {
            AssetBundle b = AssetBundle.LoadFromFile(path);
            if (b == null) throw new Exception("The exported file could not be opened again.");
            try
            {
                GameObject p = b.LoadAsset<GameObject>("VSFAvatar");
                if (p == null) throw new Exception("The exported file has no VSFAvatar inside.");
                int got = 0, maxInf = 0;
                foreach (SkinnedMeshRenderer s in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (s.sharedMesh == null) continue;
                    got++;
                    foreach (byte c in s.sharedMesh.GetBonesPerVertex()) if (c > maxInf) maxInf = c;
                }
                if (got != expectSmr) throw new Exception("Only " + got + " of " + expectSmr + " meshes are in the exported file.");
                Dictionary<string, int> comps = ComponentCounts(p);
                var diff = new List<string>();
                foreach (var kv in expectComps)
                {
                    int n;
                    comps.TryGetValue(kv.Key, out n);
                    if (n != kv.Value) diff.Add(kv.Key + " " + n + "/" + kv.Value);
                }
                int dead;
                if (comps.TryGetValue("(missing)", out dead) && !expectComps.ContainsKey("(missing)")) diff.Add(dead + " missing script(s)");
                if (diff.Count > 0) throw new Exception("The exported file lost components: " + string.Join(", ", diff.ToArray()) + ".");
                res.notes.Add("Checked the new file: " + got + " meshes, up to " + maxInf + " bone influences per vertex.");
            }
            finally { b.Unload(true); }
        }

        static GameObject PickAvatar(AssetBundle b)
        {
            GameObject best = null; int bestScore = 0;
            foreach (GameObject go in b.LoadAllAssets<GameObject>())
            {
                int s = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                if (go.GetComponent<Animator>() != null) s += 1000;
                if (s > bestScore) { bestScore = s; best = go; }
            }
            return best;
        }

        static int RemoveDeadScripts(GameObject root, List<string> notes)
        {
            int n = 0;
            var where = new List<string>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                int k = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (k > 0) { n += k; if (where.Count < 6) where.Add(RecipePaths.PathOf(t, root.transform)); }
            }
            if (where.Count > 0) notes.Add("Missing scripts were on: " + string.Join(", ", where.ToArray()) + (n > where.Count ? ", ..." : ""));
            return n;
        }

        // ------------------------------------------------------------------ Poiyomi

        internal static int UnlockPoiyomi(GameObject root, List<string> notes)
        {
            int n = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    string orig = RecipeEngine.PoiyomiOriginalShader(m);
                    if (orig.Length == 0) continue;
                    Shader s = ShaderBundleBuilder.FindShader(orig);
                    if (s == null) { notes.Add("Poiyomi '" + orig + "' is not in this project, so " + m.name + " stays as it was."); continue; }
                    int q = m.renderQueue;
                    m.shader = s;
                    m.renderQueue = q;
                    if (m.HasProperty("_ShaderOptimizerEnabled")) m.SetFloat("_ShaderOptimizerEnabled", 0f);
                    n++;
                }
            return n;
        }

        internal static void LockPoiyomi(GameObject root, List<string> notes) { LockPoiyomi(root, notes, null, null); }
        internal static void LockPoiyomi(GameObject root, List<string> notes, string onlyUnder) { LockPoiyomi(root, notes, onlyUnder, null); }

        /// <summary>Renderer slots ("path|slot") whose material is a locked Poiyomi.</summary>
        static HashSet<string> LockedSlots(GameObject root)
        {
            var set = new HashSet<string>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] ms = r.sharedMaterials;
                string p = RecipePaths.PathOf(r.transform, root.transform);
                for (int i = 0; i < ms.Length; i++)
                    if (ms[i] != null && ms[i].shader != null &&
                        (ms[i].shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal) || RecipeEngine.IsLockedPoiyomi(ms[i])))
                        set.Add(p + "|" + i);
            }
            return set;
        }

        /// <summary>
        /// Locks Poiyomi materials; with <paramref name="onlyUnder"/> only our own copies in that
        /// folder, with <paramref name="onlySlots"/> only the materials on those renderer slots.
        /// </summary>
        internal static void LockPoiyomi(GameObject root, List<string> notes, string onlyUnder, HashSet<string> onlySlots)
        {
            var mats = new List<Material>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] ms = r.sharedMaterials;
                string rp = onlySlots != null ? RecipePaths.PathOf(r.transform, root.transform) : null;
                for (int i = 0; i < ms.Length; i++)
                {
                    Material m = ms[i];
                    if (m != null && m.shader != null && m.HasProperty("_ShaderOptimizerEnabled")
                        && !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal) && !mats.Contains(m)
                        && AssetPersist.IsAsset(m)
                        && (onlyUnder == null || AssetDatabase.GetAssetPath(m).StartsWith(onlyUnder + "/", StringComparison.Ordinal))
                        && (onlySlots == null || onlySlots.Contains(rp + "|" + i)))
                        mats.Add(m);
                }
            }
            if (mats.Count == 0) return;
            foreach (Material m in mats) { m.SetFloat("_ShaderOptimizerEnabled", 0f); EditorUtility.SetDirty(m); }
            Type opt = FindType("Thry.ShaderOptimizer");
            MethodInfo lockAll = null;
            if (opt != null)
                foreach (MethodInfo mi in opt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (mi.Name == "SetLockedForAllMaterials") { lockAll = mi; break; }
            if (lockAll == null)
            {
                notes.Add("Poiyomi's locking tool (ThryEditor) was not found, so " + mats.Count + " Poiyomi material(s) were exported unlocked (they still look right, just less optimised).");
                return;
            }
            try
            {
                ParameterInfo[] ps = lockAll.GetParameters();
                var args = new object[ps.Length];
                args[0] = mats;
                args[1] = 1;   // lock
                for (int i = 2; i < ps.Length; i++)
                    args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                object ok = lockAll.Invoke(null, args);
                AssetDatabase.SaveAssets();
                int locked = 0;
                foreach (Material m in mats) if (m.shader != null && m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) locked++;
                notes.Add("Locked " + locked + " of " + mats.Count + " Poiyomi material(s)" + (ok is bool && !(bool)ok ? " (Poiyomi reported a problem; see the Unity log)." : "."));
            }
            catch (Exception e)
            {
                notes.Add("Locking Poiyomi failed (" + (e.InnerException != null ? e.InnerException.Message : e.Message) + "); exported unlocked.");
            }
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

        // ------------------------------------------------------------------ live weights

        /// <summary>
        /// Writes the bone weights VNyan rendered (captured to a .vbx) onto the export's
        /// meshes. Bones are matched by NAME, vertices by index (vertex counts must agree),
        /// and a mesh is only touched when its weights really differ.
        /// </summary>
        static void ApplyLiveWeights(GameObject root, string vbxPath, string workDir, List<string> notes)
        {
            VbxDoc doc;
            using (var fs = File.OpenRead(vbxPath)) doc = VbxIO.Read(fs);
            int changed = 0, same = 0, skipped = 0;
            var changedNames = new List<string>();
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null) continue;
                string path = RecipePaths.PathOf(smr.transform, root.transform);
                VbxMesh vm = null;
                foreach (VbxMesh x in doc.meshes) if (x.name == path) { vm = x; break; }
                if (vm == null) continue;   // only meshes Weight Studio painted are in the snapshot
                if (vm.positions == null || vm.positions.Length != mesh.vertexCount) { notes.Add("Live weights for " + smr.name + " skipped: vertex count differs."); skipped++; continue; }

                // live slot -> target slot by bone name
                Transform[] bones = smr.bones;
                var slotOf = new Dictionary<string, int>();
                for (int i = 0; i < bones.Length; i++) if (bones[i] != null && !slotOf.ContainsKey(bones[i].name)) slotOf[bones[i].name] = i;
                int[] remap = new int[vm.boneMap.Length];
                bool ok = true;
                // Same bone list (the usual case): map slot to slot, so two bones that share a
                // name (outfit armatures) never swap. Otherwise match by transform path, then name.
                bool sameList = vm.boneMap.Length == bones.Length;
                for (int i = 0; i < remap.Length && sameList; i++)
                {
                    int g = vm.boneMap[i];
                    string bn = g >= 0 && g < doc.bones.Count ? doc.bones[g].name : null;
                    if (bones[i] == null || bn != bones[i].name) sameList = false;
                }
                var slotOfPath = new Dictionary<string, int>();
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    string bp = RecipePaths.PathOf(bones[i], root.transform);
                    if (!slotOfPath.ContainsKey(bp)) slotOfPath[bp] = i;
                }
                for (int i = 0; i < remap.Length; i++)
                {
                    if (sameList) { remap[i] = i; continue; }
                    int g = vm.boneMap[i];
                    string bn = g >= 0 && g < doc.bones.Count ? doc.bones[g].name : null;
                    string bpath = g >= 0 && g < doc.bones.Count ? VbxPath(doc, g) : null;
                    int s;
                    if (bpath != null && slotOfPath.TryGetValue(bpath, out s)) remap[i] = s;
                    else remap[i] = bn != null && slotOf.TryGetValue(bn, out s) ? s : -1;
                }
                var bpv = new byte[mesh.vertexCount];
                var wts = new List<BoneWeight1>();
                int k = 0;
                for (int v = 0; v < mesh.vertexCount && ok; v++)
                {
                    int cnt = v < vm.bonesPerVertex.Length ? vm.bonesPerVertex[v] : 0;
                    var list = new List<BoneWeight1>();
                    float sum = 0f;
                    for (int j = 0; j < cnt; j++, k++)
                    {
                        int b = vm.influenceBone[k];
                        int slot = b >= 0 && b < remap.Length ? remap[b] : -1;
                        float w = vm.influenceWeight[k];
                        if (slot < 0) { if (w > 1e-4f) ok = false; continue; }
                        if (w <= 0f) continue;
                        list.Add(new BoneWeight1 { boneIndex = slot, weight = w });
                        sum += w;
                    }
                    if (list.Count == 0) { list.Add(new BoneWeight1 { boneIndex = 0, weight = 1f }); sum = 1f; }
                    list.Sort(delegate (BoneWeight1 a, BoneWeight1 b) { return b.weight.CompareTo(a.weight); });
                    if (list.Count > 255) list.RemoveRange(255, list.Count - 255);
                    bpv[v] = (byte)list.Count;
                    foreach (BoneWeight1 bw in list) wts.Add(new BoneWeight1 { boneIndex = bw.boneIndex, weight = bw.weight / sum });
                }
                if (!ok) { notes.Add("Live weights for " + smr.name + " skipped: they use a bone this avatar does not have."); skipped++; continue; }
                if (SameWeights(mesh, bpv, wts)) { same++; continue; }

                Mesh target = mesh;
                if (!AssetDatabase.GetAssetPath(mesh).StartsWith(workDir, StringComparison.Ordinal))
                {
                    MSBatch.EnsureFolder(workDir + "/Meshes");
                    target = UnityEngine.Object.Instantiate(mesh);
                    target.name = mesh.name;
                    AssetDatabase.CreateAsset(target, MSBatch.NamedPath(workDir + "/Meshes/weights", mesh.name, ".asset"));
                    smr.sharedMesh = target;
                }
                using (var nb = new NativeArray<byte>(bpv, Allocator.Temp))
                using (var nw = new NativeArray<BoneWeight1>(wts.ToArray(), Allocator.Temp))
                    target.SetBoneWeights(nb, nw);
                EditorUtility.SetDirty(target);
                int max = 0; foreach (byte b in bpv) if (b > max) max = b;
                changed++;
                changedNames.Add(smr.name + " (up to " + max + " bones/vertex)");
            }
            if (changed > 0) SetUnlimitedSkinWeights();
            notes.Add("Live weights: " + changed + " mesh(es) changed" + (changedNames.Count > 0 ? " [" + string.Join(", ", changedNames.ToArray()) + "]" : "") +
                      ", " + same + " unchanged" + (skipped > 0 ? ", " + skipped + " skipped" : "") + ".");
        }

        /// <summary>Path of a VBX bone under the avatar root (root name excluded, like RecipePaths).</summary>
        static string VbxPath(VbxDoc doc, int i)
        {
            var parts = new List<string>();
            int guard = 0;
            while (i >= 0 && i < doc.bones.Count && doc.bones[i].parent >= 0 && guard++ < 1000)
            {
                // same "name[#k]" rule as RecipePaths: k = earlier siblings with that name
                VbxBone b = doc.bones[i];
                int k = 0;
                for (int j = 0; j < i; j++)
                    if (doc.bones[j].parent == b.parent && doc.bones[j].name == b.name) k++;
                parts.Add(k == 0 ? b.name : b.name + "[#" + k + "]");
                i = b.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        static bool SameWeights(Mesh mesh, byte[] bpv, List<BoneWeight1> wts)
        {
            NativeArray<byte> ob = mesh.GetBonesPerVertex();
            NativeArray<BoneWeight1> ow = mesh.GetAllBoneWeights();
            if (ob.Length != bpv.Length || ow.Length != wts.Count) return false;
            for (int i = 0; i < bpv.Length; i++) if (ob[i] != bpv[i]) return false;
            for (int i = 0; i < wts.Count; i++)
            {
                if (ow[i].boneIndex != wts[i].boneIndex) return false;
                if (Mathf.Abs(ow[i].weight - wts[i].weight) > 1e-4f) return false;
            }
            return true;
        }

        static void SetUnlimitedSkinWeights()
        {
            // only affects how the editor previews; the .vsfavatar mesh keeps every influence
            string[] names = QualitySettings.names;
            int cur = QualitySettings.GetQualityLevel();
            for (int i = 0; i < names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.skinWeights = SkinWeights.Unlimited;
            }
            QualitySettings.SetQualityLevel(cur, false);
        }

        // ------------------------------------------------------------------ SDK check + build

        static string CheckAvatar(GameObject obj)
        {
            Type t = FindType("VSeeFace.AvatarCheck");
            if (t == null) return "the VSF SDK is not in this Unity project (seed it from your VSF SDK project on the Packages tab)";
            MethodInfo m = t.GetMethod("CheckAvatar", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return null;
            var args = new object[] { obj, null };
            bool ok = (bool)m.Invoke(null, args);
            return ok ? null : (args[1] as string ?? "unknown problem");
        }

        /// <summary>Same steps and options as the VSF SDK's "Export avatar bundle".</summary>
        static void Build(GameObject obj, string outPath, MSResult res)
        {
            string prefabPath = "Assets/VSFAvatarTemporary.prefab";
            string filename = Path.GetFileName(outPath);
            try
            {
                AssetDatabase.DeleteAsset(prefabPath);
                bool packed;
                PrefabUtility.SaveAsPrefabAsset(obj, prefabPath, out packed);
                if (!packed) throw new Exception("Saving the avatar prefab failed (see the Unity log).");
                var bb = new AssetBundleBuild
                {
                    assetBundleName = filename,
                    assetNames = new[] { prefabPath },
                    addressableNames = new[] { "VSFAvatar" }
                };
                AssetDatabase.RemoveUnusedAssetBundleNames();
                BuildAssetBundleOptions options = BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle | BuildAssetBundleOptions.StrictMode;
                if (obj.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true).Length > 0)
                    options |= BuildAssetBundleOptions.UncompressedAssetBundle;
                BuildPipeline.BuildAssetBundles(Application.temporaryCachePath, new[] { bb }, options, BuildTarget.StandaloneWindows);
                string built = Path.Combine(Application.temporaryCachePath, filename);
                if (!File.Exists(built)) throw new Exception("Unity did not produce the bundle (see the Unity log).");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(built, outPath);
                res.notes.Add("Exported " + outPath + " (" + (new FileInfo(outPath).Length / (1024 * 1024)) + " MB).");
            }
            finally
            {
                try { AssetDatabase.DeleteAsset(prefabPath); } catch { }
            }
        }
    }
}
