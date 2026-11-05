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
    ///   1. stage the avatar (from a .vsfavatar or .warudo file, or from a prefab in its own project),
    ///   2. copy everything it uses into project assets (never touching the project's own),
    ///   3. rebind locked Poiyomi to its full shader,
    ///   4. apply live bone weights captured in VNyan (only meshes that really differ),
    ///   5. apply the material recipe,
    ///   6. lock Poiyomi again (Thry ShaderOptimizer, by reflection),
    ///   7. check and build exactly like the VSF SDK's own exporter,
    ///   8. open the new file and check it against the original (names, components, scripts,
    ///      locks) before it replaces anything at the output path.
    /// </summary>
    public static partial class VsfExporter
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
            ExportInventory source = null;
            string builtPath = null;
            var matCopies = new Dictionary<Material, Material>();
            try
            {
                // ---- 1. stage
                if (job.sourceKind == "prefab")
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(job.sourcePath);
                    if (prefab == null) throw new Exception("Prefab not found: " + job.sourcePath);
                    source = ExportInventory.Of(prefab, null);
                    inst = UnityEngine.Object.Instantiate(prefab);
                    inst.name = prefab.name;
                    // copies of the materials, so the project's own are never changed
                    foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] ms = r.sharedMaterials;
                        for (int i = 0; i < ms.Length; i++)
                        {
                            if (ms[i] == null) continue;
                            Material c;
                            if (!matCopies.TryGetValue(ms[i], out c))
                            {
                                c = new Material(ms[i]) { name = ms[i].name };
                                MSBatch.CreateNamed(c, workDir + "/Materials", ".mat");
                                matCopies[ms[i]] = c;
                            }
                            ms[i] = c;
                        }
                        r.sharedMaterials = ms;
                    }
                    res.notes.Add("Staged prefab " + job.sourcePath + " (" + matCopies.Count + " material copies).");
                }
                else if (job.sourceKind == "warudo")
                {
                    bundle = StageWarudo(job, res, out source, out inst, out lockedSlots);
                    persist = PersistSource(inst, source, job, workDir, res.notes);
                }
                else
                {
                    AssetBundle.UnloadAllAssetBundles(true);
                    bundle = AssetBundle.LoadFromFile(job.sourcePath);
                    if (bundle == null) throw new Exception("Unity could not open " + Path.GetFileName(job.sourcePath) +
                        ". A .vsfavatar can only be read by the Unity version that built it or newer (this is " + Application.unityVersion + ").");
                    GameObject prefab = bundle.LoadAsset<GameObject>("VSFAvatar") ?? PickAvatar(bundle);
                    if (prefab == null) throw new Exception("No avatar inside " + Path.GetFileName(job.sourcePath) + ".");
                    source = ExportInventory.Of(prefab, bundle);
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
                        throw new Exception(dead + " component(s) use scripts that are not in the Unity project, so they would come out missing (the log says where). Seed the project with the folders those scripts live in.");
                    persist = PersistSource(inst, source, job, workDir, res.notes);
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

                // slots an earlier lock dropped, now that the materials are on their full shader
                if (persist != null)
                {
                    int back = persist.RestoreSlotTextures(inst, source.objects);
                    if (back > 0) res.notes.Add("Put back " + back + " texture slot(s) that an earlier lock had dropped from its materials.");
                }

                // ---- 6. lock Poiyomi again, exactly where it is locked in VNyan (or in the file)
                var lockedKw = new Dictionary<Material, string[]>();
                var flat = new Dictionary<Material, string>();
                if (job.relockPoiyomi && lockedSlots.Count > 0)
                {
                    int bad = LockPoiyomi(inst, res.notes, null, lockedSlots, lockedKw, flat);
                    if (bad > 0)
                    {
                        var names = new List<string>();
                        foreach (var kv in flat) names.Add(kv.Key.name + " (" + kv.Value + ")");
                        throw new Exception("These Poiyomi materials would come out of the lock without all their features (flat): " +
                            string.Join("; ", names.ToArray()) + ". Nothing was exported. To export anyway, turn off \"Keep Poiyomi locked where it is locked now\" " +
                            "on the Export tab: unlocked Poiyomi keeps every feature, it is just less optimised.");
                    }
                    int slots = RecordSlotTextures(inst);
                    if (slots > 0)
                        res.notes.Add(slots + " texture slot(s) are not used by their locked shader and are left off the material. Their texture names are kept in the \"" +
                                      SlotTexturesTag + "\" tag, so the next export that unlocks these materials puts them back" +
                                      (job.includeUnusedAssets ? "." : " (Include unused assets is off, so those textures are not in the file)."));
                }
                else if (job.relockPoiyomi) res.notes.Add("No Poiyomi material is locked, so none were locked.");

                // ---- 7. check + build like the VSF SDK
                string hollow = FindNonAssets(inst);
                if (hollow != null) throw new Exception("Some parts are still not project assets, so the export would come out empty: " + hollow);
                string dangling = FindUnsavedScripts(inst);
                if (dangling != null) throw new Exception("Some components would lose their script in the export: " + dangling);
                // a Warudo mod never had VSF SDK parts and the file is for VNyan, so the SDK's
                // check is only a hint there
                if (job.sourceKind == "warudo")
                {
                    if (FindType("VSeeFace.AvatarCheck") == null) res.notes.Add("The VSF SDK is not in this Unity project, so its avatar check was skipped.");
                    else
                    {
                        string err = CheckAvatar(inst);
                        if (err != null) res.notes.Add("The VSF SDK's avatar check says: " + err + " (VNyan may still load it; VSeeFace cannot open this file anyway).");
                    }
                }
                else
                {
                    string err = CheckAvatar(inst);
                    if (err != null) throw new Exception("VSF SDK avatar check failed: " + err);
                }
                // a part whose copy failed would be missing from the file: stop before building it
                if (persist != null)
                {
                    var failedCopies = new List<string>();
                    for (int i = 0; i < source.objects.Count; i++)
                    {
                        UnityEngine.Object o = source.objects[i];
                        if (source.keys[i].StartsWith("Shader 'Hidden/", StringComparison.Ordinal) || !persist.map.ContainsKey(o) || persist.CopyOf(o) != null) continue;
                        if (!ExportExpectation.CannotBeCopied(o)) failedCopies.Add(source.keys[i]);
                    }
                    if (failedCopies.Count > 0)
                        throw new Exception("These parts of the original could not be copied, so they would be missing from the export: " +
                            string.Join(", ", failedCopies.GetRange(0, Math.Min(8, failedCopies.Count)).ToArray()) +
                            (failedCopies.Count > 8 ? " and " + (failedCopies.Count - 8) + " more" : "") + " (the log says why). Nothing was exported.");
                }
                int expectSmr = 0;
                foreach (SkinnedMeshRenderer s in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (s.sharedMesh != null) expectSmr++;
                Dictionary<string, int> expectComps = ComponentCounts(inst);
                // with "Include unused assets", everything copied out of the source file goes in,
                // used or not (a prefab export holds what the prefab uses)
                List<string> extra = persist != null && job.includeUnusedAssets ? persist.CopiedAssetPaths() : null;
                HashSet<UnityEngine.Object> inBundle = Build(inst, job.outPath, extra, res, out builtPath);

                // what the new file must hold, worked out before the source is closed
                var leftOut = new List<string>();
                var lostCopies = new List<string>();
                AssetPersist p = persist;
                ExportExpectation expect = ExportExpectation.From(source,
                    delegate (UnityEngine.Object o)
                    {
                        if (p == null) { Material mc; return o is Material && matCopies.TryGetValue((Material)o, out mc) ? mc : o; }
                        return p.CopyOf(o);
                    },
                    delegate (UnityEngine.Object o) { return p != null && p.map.ContainsKey(o); },
                    inBundle, p != null && job.includeUnusedAssets, leftOut, lostCopies);
                expect.skinnedMeshes = expectSmr;
                foreach (var kv in lockedKw) expect.locked.Add(new LockedExpect { name = kv.Key.name, keywords = kv.Value, shader = kv.Key.shader.name });
                // checked before the build already; anything here is reported missing by the output check
                foreach (string l in lostCopies) res.notes.Add("Could not copy " + l + ".");
                if (leftOut.Count > 0)
                    res.notes.Add("WARNING: these kinds of objects (animator controllers, audio, fonts, 3D and array textures) cannot be copied out of a " +
                                  (job.sourceKind == "warudo" ? ".warudo" : ".vsfavatar") + " file, so they are not in the export: " +
                                  string.Join(", ", leftOut.ToArray()) + ". Export from the avatar's Unity project to keep them.");

                // close the source first: a same-named output gets the same internal bundle name
                if (bundle != null) { bundle.Unload(false); bundle = null; }
                VerifyOutput(builtPath, expect, expectComps, res);
                // only a file that passed the check replaces the one at the output path; it is
                // copied next to it first, so a failed copy to another drive leaves the old one
                Directory.CreateDirectory(Path.GetDirectoryName(job.outPath));
                string part = job.outPath + ".partial";
                try
                {
                    File.Copy(builtPath, part, true);
                    if (File.Exists(job.outPath)) File.Delete(job.outPath);
                    File.Move(part, job.outPath);
                }
                finally
                {
                    if (File.Exists(part)) try { File.Delete(part); } catch { }
                }
                res.notes.Add("Exported " + job.outPath + " (" + (new FileInfo(job.outPath).Length / (1024 * 1024)) + " MB).");
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
                if (builtPath != null) try { File.Delete(builtPath); } catch { }
                if (res.ok && AssetDatabase.IsValidFolder(workDir)) AssetDatabase.DeleteAsset(workDir);
                else if (!res.ok) res.notes.Add("Intermediate assets kept for inspection in " + workDir + ".");
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>Copies everything a bundle-loaded avatar uses into project assets under <paramref name="workDir"/>.</summary>
        static AssetPersist PersistSource(GameObject inst, ExportInventory source, MSJob job, string workDir, List<string> notes)
        {
            var persist = new AssetPersist(workDir, notes) { avatarRoot = inst, keepAllMaterialTextures = job.includeUnusedAssets };
            persist.PersistAll(inst);
            if (persist.missingShaders.Count > 0)
                throw new Exception("These shaders are not in the Unity project: " + string.Join(", ", persist.missingShaders.ToArray()) +
                    ". Add their .unitypackage on the Packages tab (it is imported on the next export), or the avatar would render pink.");
            if (job.includeUnusedAssets) persist.PersistEverything(source.objects);
            return persist;
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

        /// <summary>
        /// Opens the new .vsfavatar again and checks that every original object is in it under
        /// its type and exact name, every GameObject keeps its components and scripts, and
        /// every locked material kept the features it was locked with.
        /// </summary>
        static void VerifyOutput(string path, ExportExpectation expect, Dictionary<string, int> expectComps, MSResult res)
        {
            int expectSmr = expect.skinnedMeshes;
            AssetBundle b = AssetBundle.LoadFromFile(path);
            if (b == null) throw new Exception("The exported file could not be opened again.");
            try
            {
                GameObject p = b.LoadAsset<GameObject>("VSFAvatar");
                if (p == null) throw new Exception("The exported file has no VSFAvatar inside.");
                List<string> lost = expect.Problems(ExportInventory.Of(p, b));
                if (lost.Count > 0)
                {
                    foreach (string l in lost) res.notes.Add("Output check: " + l + ".");
                    throw new Exception("The exported file does not match the original: " + string.Join("; ", lost.GetRange(0, Math.Min(6, lost.Count)).ToArray()) +
                        (lost.Count > 6 ? "; and " + (lost.Count - 6) + " more (see the log)." : "."));
                }
                int objs = 0;
                foreach (var kv in expect.objects) objs += kv.Value;
                res.notes.Add("Checked the new file against the original: " + objs + " object(s) under their own names, " + expect.components.Count +
                              " GameObject(s) with all their components, " + expect.scripts.Count + " script(s)" +
                              (expect.locked.Count > 0 ? ", " + expect.locked.Count + " locked material(s) on their own locked shader with every feature their values switch on." : "."));
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

        /// <summary>Override tag that lists "slot=texture name" pairs a locked shader does not use.</summary>
        public const string SlotTexturesTag = "MaterialStudioSlotTextures";

        /// <summary>
        /// A locked shader declares only the texture slots its features use, and the bundle keeps
        /// only those slots on the material, so the textures in the other slots would be lost to a
        /// later unlock. Writes their names into <see cref="SlotTexturesTag"/>; returns how many.
        /// </summary>
        static int RecordSlotTextures(GameObject root)
        {
            int n = 0;
            var seen = new HashSet<Material>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null || !seen.Add(m) || !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                    var own = new HashSet<string>();
                    for (int i = 0; i < m.shader.GetPropertyCount(); i++) own.Add(m.shader.GetPropertyName(i));
                    var pairs = new List<string>();
                    foreach (string tn in m.GetTexturePropertyNames())
                    {
                        Texture t = m.GetTexture(tn);
                        if (t != null && !own.Contains(tn)) pairs.Add(tn + "=" + t.name);
                    }
                    if (m.GetTag(SlotTexturesTag, false, "") != string.Join("|", pairs.ToArray()))
                    {
                        m.SetOverrideTag(SlotTexturesTag, string.Join("|", pairs.ToArray()));
                        EditorUtility.SetDirty(m);
                    }
                    n += pairs.Count;
                }
            return n;
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
                    // like Thry's unlock: tags, queue and the keywords the lock was compiled with
                    PoiyomiKeywords.Rebind(m, s, notes);
                    EditorUtility.SetDirty(m);
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
            LockPoiyomi(root, notes, onlyUnder, onlySlots, null, null);
        }

        /// <summary>
        /// As above. Thry only compiles a feature in when its keyword is on at lock time, so each
        /// material is locked with its current keywords and tagged OriginalKeywords with that set.
        /// A material whose keywords miss something its values switch on is not locked, and each
        /// locked shader is checked to be the one compiled for its material.
        /// <paramref name="lockedKeywords"/> receives each locked material that passed with its
        /// keyword set, <paramref name="failed"/> each material that failed with the reason.
        /// Returns how many failed.
        /// </summary>
        internal static int LockPoiyomi(GameObject root, List<string> notes, string onlyUnder, HashSet<string> onlySlots,
                                        Dictionary<Material, string[]> lockedKeywords, Dictionary<Material, string> failed)
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
            if (mats.Count == 0) return 0;
            var expected = new Dictionary<Material, string[]>();
            var fullShader = new Dictionary<Material, Shader>();
            var why = new Dictionary<Material, string>();
            Action<Material, string> fail = delegate (Material fm, string reason)
            {
                if (why.ContainsKey(fm)) return;
                why[fm] = reason;
                notes.Add("WARNING: " + fm.name + " cannot be locked with all its features: " + reason + ".");
            };
            foreach (Material m in mats)
            {
                m.SetFloat("_ShaderOptimizerEnabled", 0f);
                EditorUtility.SetDirty(m);
                string[] kw = PoiyomiKeywords.Clean(m.shaderKeywords);
                expected[m] = kw;
                fullShader[m] = m.shader;
                // The keyword list decides which features the locked shader gets. Check it against
                // what the values switch on, which does not come from that list.
                List<string> miss = PoiyomiKeywords.MissingFromValues(m, m.shader, kw);
                if (miss != null && miss.Count > 0)
                    fail(m, "its values switch on " + string.Join(", ", miss.ToArray()) + " but its keywords do not" + (kw.Length == 0 ? " (it has none)" : ""));
                else if (kw.Length == 0) fail(m, "it has no keywords, so its locked shader would get no features");
                else if (m.HasProperty("_ShadingEnabled") && m.GetFloat("_ShadingEnabled") >= 0.5f && Array.IndexOf(kw, "VIGNETTE_MASKED") < 0)
                    fail(m, "shading is on but its keyword (VIGNETTE_MASKED) is not");
                else if (miss == null) fail(m, "its keywords could not be checked against its values");
            }
            var toLock = new List<Material>();
            foreach (Material m in mats) if (!why.ContainsKey(m)) toLock.Add(m);
            // Poiyomi 9 moved it into Thry.ThryEditor
            Type opt = FindType("Thry.ShaderOptimizer") ?? FindType("Thry.ThryEditor.ShaderOptimizer");
            MethodInfo lockAll = null;
            if (opt != null)
                foreach (MethodInfo mi in opt.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (mi.Name == "SetLockedForAllMaterials") { lockAll = mi; break; }
            if (lockAll == null)
            {
                // nothing gets locked, so nothing can come out flat
                notes.Add("Poiyomi's locking tool (ThryEditor) was not found, so " + mats.Count + " Poiyomi material(s) were exported unlocked (they still look right, just less optimised).");
                return 0;
            }

            // Thry names a locked shader after the GUID of the material's file (one GUID for every
            // object in a name holder, so two of them would get the same shader) and puts it in a
            // folder named after the material (impossible for a name that cannot be a file name).
            // Those lock a file-safe stand-in, and the result is copied back.
            var target = new Dictionary<Material, Material>();
            int standIns = 0;
            foreach (Material m in toLock)
            {
                if (!AssetDatabase.IsSubAsset(m) && MSBatch.FileSafe(m.name)) { target[m] = m; continue; }
                string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(m)).Replace('\\', '/') + "/_LockStandIns/" + standIns++;
                MSBatch.EnsureFolder(folder);
                var st = new Material(m) { name = MSBatch.Sanitize(m.name) };
                PoiyomiKeywords.RestoreTags(st, PoiyomiKeywords.CaptureTags(m));
                // the same suffix for renamed animated properties as the material itself would get
                st.SetOverrideTag("thry_rename_suffix", m.GetTag("thry_rename_suffix", false, m.name));
                st.renderQueue = PoiyomiKeywords.RawQueue(m);
                st.shaderKeywords = expected[m];
                st.SetFloat("_ShaderOptimizerEnabled", 0f);
                AssetDatabase.CreateAsset(st, folder + "/" + st.name + ".mat");
                target[m] = st;
            }

            // Thry shares one compiled shader between materials whose values hash alike, but the
            // hash ignores keywords and filled texture slots. Lock in groups that really match,
            // with its cache cleared before each group.
            FieldInfo cacheField = opt.GetField("s_shaderPropertyCombinations", BindingFlags.NonPublic | BindingFlags.Static);
            if (cacheField == null && toLock.Count > 1)
                notes.Add("Poiyomi's shared-shader cache was not found (other ThryEditor version); a material that ends up on another material's locked shader is reported as failed.");
            var groups = new Dictionary<string, List<Material>>();
            var groupOf = new Dictionary<Material, string>();
            var order = new List<string>();
            foreach (Material m in toLock)
            {
                Material t = target[m];
                string key = LockGroupKey(t, expected[m]);
                List<Material> g;
                if (!groups.TryGetValue(key, out g)) { g = new List<Material>(); groups[key] = g; order.Add(key); }
                g.Add(t);
                groupOf[t] = key;
            }
            bool problem = false;
            foreach (string key in order)
            {
                try
                {
                    if (cacheField != null)
                    {
                        var cache = cacheField.GetValue(null) as System.Collections.IDictionary;
                        if (cache != null) cache.Clear();
                    }
                    ParameterInfo[] ps = lockAll.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = groups[key];
                    args[1] = 1;   // lock
                    for (int i = 2; i < ps.Length; i++)
                        args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                    object ok = lockAll.Invoke(null, args);
                    if (ok is bool && !(bool)ok) problem = true;
                }
                catch (Exception e)
                {
                    problem = true;
                    notes.Add("Locking Poiyomi failed for " + groups[key].Count + " material(s) (" + (e.InnerException != null ? e.InnerException.Message : e.Message) + ").");
                }
            }
            AssetDatabase.SaveAssets();

            // stand-ins: their result goes onto the real material, which keeps its exact name
            foreach (Material m in toLock)
            {
                Material t = target[m];
                if (t == m || t.shader == null || !t.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                bool hadSuffix = m.GetTag("thry_rename_suffix", false, "").Length > 0;
                Dictionary<string, string> tags = PoiyomiKeywords.CaptureTags(t);
                int q = PoiyomiKeywords.RawQueue(t);
                m.shader = t.shader;
                m.CopyPropertiesFromMaterial(t);
                PoiyomiKeywords.RestoreTags(m, tags);
                if (!hadSuffix) m.SetOverrideTag("thry_rename_suffix", "");
                m.renderQueue = q;
                m.shaderKeywords = PoiyomiKeywords.Clean(t.shaderKeywords);
                EditorUtility.SetDirty(m);
            }

            // which material each locked shader was compiled for: Thry ends its name with that
            // material's GUID. A material must have its own group's shader, never another one's.
            var ownersByGuid = new Dictionary<string, List<Material>>();
            foreach (Material t in target.Values)
            {
                string g = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t));
                List<Material> l;
                if (!ownersByGuid.TryGetValue(g, out l)) { l = new List<Material>(); ownersByGuid[g] = l; }
                l.Add(t);
            }
            var groupsOnShader = new Dictionary<Shader, HashSet<string>>();
            foreach (Material m in toLock)
            {
                if (m.shader == null || !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                HashSet<string> gs;
                if (!groupsOnShader.TryGetValue(m.shader, out gs)) { gs = new HashSet<string>(); groupsOnShader[m.shader] = gs; }
                gs.Add(groupOf[target[m]]);
            }

            int locked = 0, unlockedLeft = 0;
            foreach (Material m in toLock)
            {
                if (m.shader == null || !m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal))
                {
                    unlockedLeft++;
                    notes.Add("WARNING: Poiyomi did not lock " + m.name + "; it stays unlocked (it looks the same, just less optimised).");
                    continue;
                }
                locked++;
                Material t = target[m];
                // Thry's own tag can be wrong (written after its shader swap, or empty for a cached
                // shader); this is the set the shader was compiled with
                m.SetOverrideTag("OriginalKeywords", string.Join(" ", expected[m]));
                EditorUtility.SetDirty(m);

                string sn = m.shader.name;
                string tail = sn.Substring(sn.LastIndexOf('/') + 1);
                List<Material> owners;
                if (IsGuid(tail))
                {
                    if (!ownersByGuid.TryGetValue(tail, out owners)) fail(m, "it got a locked shader that was made for a material outside this lock (" + sn + ")");
                    else
                        foreach (Material o in owners)
                            if (groupOf.ContainsKey(o) && groupOf[o] != groupOf[t]) { fail(m, "it got the locked shader of " + o.name + ", which is set up differently"); break; }
                }
                if (groupsOnShader[m.shader].Count > 1) fail(m, "its locked shader is shared with materials that are set up differently");

                // every declared keyword it had must be #defined in its own locked shader, unless the
                // lock already removed that feature's code (switched off by value, like rim style)
                HashSet<string> declared = PoiyomiKeywords.Declared(fullShader[m]);
                if (declared.Count == 0) declared = new HashSet<string>(expected[m]);
                HashSet<string> defines = PoiyomiKeywords.DefinesIn(m.shader);
                if (defines.Count == 0) { fail(m, "its locked shader file could not be read to check it"); continue; }
                HashSet<string> used = PoiyomiKeywords.StillUsed(m.shader, expected[m]);
                var missing = new List<string>();
                foreach (string k in expected[m]) if (declared.Contains(k) && !defines.Contains(k) && used.Contains(k)) missing.Add(k);
                if (missing.Count > 0) fail(m, "its locked shader lacks " + string.Join(", ", missing.ToArray()));
            }
            foreach (Material m in mats)
                if (!why.ContainsKey(m) && lockedKeywords != null && m.shader != null && m.shader.name.StartsWith("Hidden/", StringComparison.Ordinal))
                    lockedKeywords[m] = expected[m];
            if (failed != null) foreach (var kv in why) failed[kv.Key] = kv.Value;
            AssetDatabase.SaveAssets();
            notes.Add("Locked " + locked + " of " + mats.Count + " Poiyomi material(s) in " + order.Count + " group(s)" +
                      (standIns > 0 ? " (" + standIns + " through a file-safe stand-in, as their names cannot be file names)" : "") +
                      (problem ? "; Poiyomi reported a problem, see the Unity log." : ".") +
                      (why.Count > 0 ? " " + why.Count + " failed the feature check." : " Every one has all its features.") +
                      (unlockedLeft > 0 ? " " + unlockedLeft + " stayed unlocked." : ""));
            return why.Count;
        }

        static bool IsGuid(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        /// <summary>
        /// Materials Thry may give one shared locked shader: same shader, same keywords, same
        /// filled texture slots, and (renamed animated properties) the same material.
        /// </summary>
        static string LockGroupKey(Material m, string[] keywords)
        {
            var kw = new List<string>(keywords);
            kw.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder(m.shader != null ? m.shader.name : "");
            sb.Append('|').Append(string.Join(" ", kw.ToArray())).Append('|');
            Shader sh = m.shader;
            bool rename = false;
            if (sh != null)
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string pn = sh.GetPropertyName(i);
                    if (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture && m.GetTexture(pn) != null) sb.Append(pn).Append(',');
                    if (m.GetTag(pn + "Animated", false, "") == "2") rename = true;
                }
            if (rename) sb.Append('|').Append(m.name).Append('|').Append(m.GetInstanceID());
            return sb.ToString();
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
                    target = UnityEngine.Object.Instantiate(mesh);
                    target.name = mesh.name;
                    MSBatch.CreateNamed(target, workDir + "/Meshes/weights", ".asset");
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

        /// <summary>
        /// Same steps and options as the VSF SDK's "Export avatar bundle". <paramref name="extraPaths"/>
        /// are assets that go in although the avatar does not use them. The bundle is written to
        /// <paramref name="builtPath"/> in Unity's temp folder, named like <paramref name="outPath"/>.
        /// Returns every object the bundle holds (the prefab's dependencies plus those).
        /// </summary>
        static HashSet<UnityEngine.Object> Build(GameObject obj, string outPath, List<string> extraPaths, MSResult res, out string builtPath)
        {
            builtPath = null;
            string prefabPath = "Assets/VSFAvatarTemporary.prefab";
            string filename = Path.GetFileName(outPath);
            try
            {
                AssetDatabase.DeleteAsset(prefabPath);
                bool packed;
                PrefabUtility.SaveAsPrefabAsset(obj, prefabPath, out packed);
                if (!packed) throw new Exception("Saving the avatar prefab failed (see the Unity log).");
                var inBundle = new HashSet<UnityEngine.Object>(EditorUtility.CollectDependencies(new[] { AssetDatabase.LoadMainAssetAtPath(prefabPath) }));
                // A material also keeps textures in slots its shader does not have (a locked
                // shader drops many), and the bundle build leaves those out although they count
                // as dependencies. Only textures a shader really samples are in for sure.
                var slotTex = new HashSet<UnityEngine.Object>();
                var sampled = new HashSet<UnityEngine.Object>();
                foreach (UnityEngine.Object o in inBundle)
                {
                    var m = o as Material;
                    if (m == null) continue;
                    foreach (string tn in m.GetTexturePropertyNames()) { Texture t = m.GetTexture(tn); if (t != null) slotTex.Add(t); }
                    Shader sh = m.shader;
                    if (sh == null) continue;
                    for (int i = 0; i < sh.GetPropertyCount(); i++)
                        if (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            Texture t = m.GetTexture(sh.GetPropertyName(i));
                            if (t != null) sampled.Add(t);
                        }
                }
                foreach (UnityEngine.Object t in slotTex) if (!sampled.Contains(t)) inBundle.Remove(t);
                var assetNames = new List<string> { prefabPath };
                var addressable = new List<string> { "VSFAvatar" };
                if (extraPaths != null)
                {
                    // only what the prefab does not already pull in, so the layout stays the SDK's
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string ep in extraPaths)
                    {
                        UnityEngine.Object[] at = AssetDatabase.LoadAllAssetsAtPath(ep);
                        bool allIn = true;
                        foreach (UnityEngine.Object o in at) if (o != null && !MSBatch.IsNameHolder(o) && !inBundle.Contains(o)) allIn = false;
                        if (allIn || !added.Add(ep)) continue;
                        assetNames.Add(ep);
                        addressable.Add(ep);
                        foreach (UnityEngine.Object o in at) if (o != null) inBundle.Add(o);
                    }
                    if (added.Count > 0) res.notes.Add("Packed " + added.Count + " asset file(s) of the original that the avatar no longer uses (Include unused assets).");
                }
                var bb = new AssetBundleBuild
                {
                    assetBundleName = filename,
                    assetNames = assetNames.ToArray(),
                    addressableNames = addressable.ToArray()
                };
                AssetDatabase.RemoveUnusedAssetBundleNames();
                BuildAssetBundleOptions options = BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle | BuildAssetBundleOptions.StrictMode;
                if (obj.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true).Length > 0)
                    options |= BuildAssetBundleOptions.UncompressedAssetBundle;
                string built = Path.Combine(Application.temporaryCachePath, filename);
                if (File.Exists(built)) File.Delete(built);
                BuildPipeline.BuildAssetBundles(Application.temporaryCachePath, new[] { bb }, options, BuildTarget.StandaloneWindows);
                if (!File.Exists(built)) throw new Exception("Unity did not produce the bundle (see the Unity log).");
                builtPath = built;
                return inBundle;
            }
            finally
            {
                try { AssetDatabase.DeleteAsset(prefabPath); } catch { }
            }
        }
    }
}
