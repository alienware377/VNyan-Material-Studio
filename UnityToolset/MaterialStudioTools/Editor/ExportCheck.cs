using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// What an avatar file holds: every asset object by type and exact name, every GameObject
    /// by its path with the components on it, and the scripts those components run. Taken of
    /// the source before the export and of the new file after it, so the export can prove that
    /// nothing was lost or renamed on the way.
    /// </summary>
    internal class ExportInventory
    {
        /// <summary>Every asset object (not GameObjects, components or scripts).</summary>
        public readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();
        /// <summary>Type and name of each of <see cref="objects"/>, read straight away: Unity may
        /// unload bundle objects later in the export, and a destroyed object has no name.</summary>
        public readonly List<string> keys = new List<string>();
        /// <summary>GameObject path under the root -> component type -> count (dead scripts left out).</summary>
        public readonly Dictionary<string, Dictionary<string, int>> components = new Dictionary<string, Dictionary<string, int>>();
        /// <summary>Names of every script the components and ScriptableObjects run.</summary>
        public readonly HashSet<string> scripts = new HashSet<string>();
        /// <summary>Those of <see cref="scripts"/> whose class is not in this Unity project.</summary>
        public readonly HashSet<string> deadScripts = new HashSet<string>();
        /// <summary>Scripts the avatar itself runs (its components and the data they use).</summary>
        public readonly HashSet<string> usedScripts = new HashSet<string>();
        /// <summary>The script of each of <see cref="objects"/> that is a data asset, else null.</summary>
        public readonly List<string> objectScripts = new List<string>();

        public static ExportInventory Of(GameObject prefab, AssetBundle bundle)
        {
            var inv = new ExportInventory();
            var seen = new HashSet<UnityEngine.Object>();
            foreach (UnityEngine.Object o in EditorUtility.CollectDependencies(new UnityEngine.Object[] { prefab })) inv.Add(o, seen, true);
            if (bundle != null)
            {
                // also what nothing references (a bundle may hold assets the prefab does not use)
                UnityEngine.Object[] all = null;
                try { all = bundle.LoadAllAssets(); } catch { }
                if (all != null) foreach (UnityEngine.Object o in all) inv.Add(o, seen, false);
            }
            foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
            {
                string path = RecipePaths.PathOf(t, prefab.transform);
                Dictionary<string, int> d;
                if (!inv.components.TryGetValue(path, out d)) { d = new Dictionary<string, int>(); inv.components[path] = d; }
                foreach (Component c in t.GetComponents<Component>())
                {
                    // a component whose script class is not in the project counts too
                    string k = c == null ? "(missing script)" : c.GetType().FullName;
                    int n;
                    d.TryGetValue(k, out n);
                    d[k] = n + 1;
                }
            }
            return inv;
        }

        /// <param name="used">reached from the avatar itself, not only listed in the file</param>
        void Add(UnityEngine.Object o, HashSet<UnityEngine.Object> seen, bool used)
        {
            if (o == null || o is GameObject || o is Component || o is AssetBundle || EditorOnly(o) || MSBatch.IsNameHolder(o) || !seen.Add(o)) return;
            var ms = o as MonoScript;
            if (ms != null)
            {
                Type c = null;
                try { c = ms.GetClass(); } catch { }
                scripts.Add(ms.name);
                if (used) usedScripts.Add(ms.name);
                if (c == null) deadScripts.Add(ms.name);
                return;
            }
            objects.Add(o);
            keys.Add(Key(o));
            // a data asset runs its script too, even one nothing on the avatar uses (VRM
            // AvatarDescription); that script is expected wherever the asset is
            string own = null;
            var so = o as ScriptableObject;
            if (so != null)
            {
                MonoScript s = null;
                try { s = MonoScript.FromScriptableObject(so); } catch { }
                if (s != null)
                {
                    own = s.name;
                    scripts.Add(own);
                    if (used) usedScripts.Add(own);
                    Type c = null;
                    try { c = s.GetClass(); } catch { }
                    if (c == null) deadScripts.Add(own);
                }
            }
            objectScripts.Add(own);
        }

        /// <summary>The states, transitions and blend trees of an animator controller exist only in
        /// the editor; a bundle holds the compiled controller alone.</summary>
        static bool EditorOnly(UnityEngine.Object o)
        {
            return o is UnityEditor.Animations.AnimatorState || o is UnityEditor.Animations.AnimatorStateMachine ||
                   o is UnityEditor.Animations.AnimatorTransitionBase || o is UnityEditor.Animations.BlendTree;
        }

        public static string Key(UnityEngine.Object o) { return o.GetType().Name + " '" + o.name + "'"; }

        public Dictionary<string, int> Counts()
        {
            var d = new Dictionary<string, int>();
            foreach (string k in keys)
            {
                int n;
                d.TryGetValue(k, out n);
                d[k] = n + 1;
            }
            return d;
        }
    }

    /// <summary>A material the export locked: its name, keywords and the locked shader made for it.</summary>
    internal class LockedExpect
    {
        public string name;
        public string[] keywords;
        public string shader;
    }

    /// <summary>The source side of the output check: what the new file must contain.</summary>
    internal class ExportExpectation
    {
        public Dictionary<string, int> objects = new Dictionary<string, int>();
        public Dictionary<string, Dictionary<string, int>> components = new Dictionary<string, Dictionary<string, int>>();
        public HashSet<string> scripts = new HashSet<string>();
        public List<LockedExpect> locked = new List<LockedExpect>();
        public int skinnedMeshes;

        /// <summary>
        /// What cannot be copied out of a .vsfavatar at all: animator controllers (their states
        /// are compiled away), audio, fonts and textures other than 2D and cube. Anything else
        /// whose copy failed is an error.
        /// </summary>
        public static bool CannotBeCopied(UnityEngine.Object o)
        {
            return o is RuntimeAnimatorController || o is AudioClip || o is Font || (o is Texture && !(o is Texture2D) && !(o is Cubemap));
        }

        /// <summary>
        /// Source objects the new file must hold under the same type and name: every one with
        /// <paramref name="all"/> (except unused ones that are not project assets under Assets/,
        /// like a built-in shader, which cannot be packed by themselves), otherwise only those
        /// copied into the bundle. Locked shaders are
        /// generated again and not compared. Failed copies go to <paramref name="leftOut"/> when
        /// they cannot be copied at all (<see cref="CannotBeCopied"/>), otherwise to
        /// <paramref name="failedCopies"/>.
        /// </summary>
        public static ExportExpectation From(ExportInventory src, Func<UnityEngine.Object, UnityEngine.Object> copyOf,
                                             Func<UnityEngine.Object, bool> triedToCopy, HashSet<UnityEngine.Object> inBundle,
                                             bool all, List<string> leftOut, List<string> failedCopies)
        {
            var x = new ExportExpectation();
            x.scripts.UnionWith(src.usedScripts);
            for (int i = 0; i < src.objects.Count; i++)
            {
                UnityEngine.Object o = src.objects[i];
                string key = src.keys[i];
                if (key.StartsWith("Shader 'Hidden/", StringComparison.Ordinal)) continue;
                UnityEngine.Object c = copyOf(o);
                bool expected;
                if (c == null)
                {
                    expected = false;
                    if (triedToCopy(o))
                    {
                        if (CannotBeCopied(o)) leftOut.Add(key);
                        else { failedCopies.Add(key); expected = true; }   // reported missing below
                    }
                    else if (all && !AssetPersist.IsAsset(o)) expected = true;   // never copied: reported missing below
                }
                else expected = inBundle.Contains(c) || (all && AssetPersist.Packable(c));
                if (!expected) continue;
                Count(x.objects, key);
                if (src.objectScripts[i] != null) x.scripts.Add(src.objectScripts[i]);
            }
            foreach (var kv in src.components) x.components[kv.Key] = new Dictionary<string, int>(kv.Value);
            return x;
        }

        static void Count(Dictionary<string, int> d, string k)
        {
            int n;
            d.TryGetValue(k, out n);
            d[k] = n + 1;
        }

        /// <summary>Everything the new file lacks, or an empty list.</summary>
        public List<string> Problems(ExportInventory got)
        {
            var bad = new List<string>();
            Dictionary<string, int> have = got.Counts();
            foreach (var kv in objects)
            {
                int n;
                have.TryGetValue(kv.Key, out n);
                if (n < kv.Value) bad.Add(kv.Key + (kv.Value > 1 ? " (" + n + " of " + kv.Value + ")" : "") + " is missing or renamed");
            }
            foreach (var kv in components)
            {
                Dictionary<string, int> h;
                if (!got.components.TryGetValue(kv.Key, out h)) { bad.Add("GameObject '" + (kv.Key.Length == 0 ? "(root)" : kv.Key) + "' is missing or renamed"); continue; }
                foreach (var c in kv.Value)
                {
                    int n;
                    h.TryGetValue(c.Key, out n);
                    if (n < c.Value) bad.Add("component " + c.Key + " on '" + (kv.Key.Length == 0 ? "(root)" : kv.Key) + "' is missing");
                }
            }
            foreach (string s in scripts)
            {
                if (!got.scripts.Contains(s)) bad.Add("script " + s + " is missing");
                else if (got.deadScripts.Contains(s)) bad.Add("script " + s + " has no class in this Unity project");
            }
            bad.AddRange(LockedProblems(got));
            return bad;
        }

        /// <summary>
        /// Each locked material must be in the file on the shader compiled for it (matched one to
        /// one, so a same-named twin cannot stand in), keep its OriginalKeywords tag, and have
        /// every keyword its values switch on. Materials set up differently must not share one
        /// locked shader.
        /// </summary>
        List<string> LockedProblems(ExportInventory got)
        {
            var bad = new List<string>();
            var taken = new HashSet<Material>();
            var setupOfShader = new Dictionary<string, string>();
            foreach (LockedExpect le in locked)
            {
                Material hit = null;
                string seen = "";
                foreach (UnityEngine.Object o in got.objects)
                {
                    var m = o as Material;
                    if (m == null || m.name != le.name || taken.Contains(m)) continue;
                    seen = m.shader != null ? m.shader.name : "no shader";
                    if (m.shader != null && m.shader.name == le.shader) { hit = m; break; }
                }
                if (hit == null)
                {
                    bad.Add("locked material '" + le.name + "' is not on the shader it was locked with (" + (seen.Length == 0 ? "not found" : seen) + ")");
                    continue;
                }
                taken.Add(hit);
                string[] tag = RecipeEngine.SplitKeywords(hit.GetTag("OriginalKeywords", false, ""));
                if (!new HashSet<string>(le.keywords).SetEquals(tag)) bad.Add("locked material '" + le.name + "' lost its OriginalKeywords tag");
                Shader full = ShaderBundleBuilder.FindShader(hit.GetTag("OriginalShader", false, ""));
                List<string> miss = PoiyomiKeywords.MissingFromValues(hit, full, le.keywords);
                if (miss == null) bad.Add("locked material '" + le.name + "' could not be checked against its values (full shader " + (full == null ? "not found" : "drawers failed") + ")");
                else if (miss.Count > 0) bad.Add("locked material '" + le.name + "' has " + string.Join(", ", miss.ToArray()) + " switched on by its values but not in its locked shader");
                var sorted = new List<string>(le.keywords);
                sorted.Sort(StringComparer.Ordinal);
                string setup = string.Join(" ", sorted.ToArray());
                string had;
                if (setupOfShader.TryGetValue(le.shader, out had) && had != setup)
                    bad.Add("locked material '" + le.name + "' shares its locked shader with a material that has other features");
                else setupOfShader[le.shader] = setup;
            }
            return bad;
        }
    }
}
