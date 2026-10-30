using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MaterialStudio.EditorTools
{
    /// <summary>
    /// For exporting by hand (VSF SDK's own exporter, Weight Studio's Unity window): bring
    /// the material edits made in VNyan onto the selected avatar first. Works on copies of
    /// the materials, so the originals stay as they are.
    /// </summary>
    public static class MaterialStudioMenu
    {
        public static string RecipeFolder
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Path.Combine("AppData", Path.Combine("LocalLow", Path.Combine("Suvidriel", Path.Combine("VNyan", Path.Combine("MaterialStudio", "Recipes"))))));
                return p;
            }
        }

        [MenuItem("VSF SDK/Material Studio/Apply VNyan material edits to selected avatar...")]
        public static void ApplyToSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go))
            {
                EditorUtility.DisplayDialog("Material Studio", "Select the avatar in the scene (Hierarchy) first.", "OK");
                return;
            }
            string start = Directory.Exists(RecipeFolder) ? RecipeFolder : "";
            string recipe = EditorUtility.OpenFilePanel("Pick the saved edits for this model", start, "json");
            if (string.IsNullOrEmpty(recipe)) return;
            string dir = "Assets/MaterialStudioApplied/" + MSBatch.Sanitize(go.name);
            var notes = new List<string>();
            Undo.RegisterFullObjectHierarchyUndo(go, "Apply Material Studio edits");
            // Work on copies, unlock copied Poiyomi so baked values can change, then apply.
            RecipeApplier.CopyAllMaterials(go, dir);
            int unlocked = VsfExporter.UnlockPoiyomi(go, notes);
            int n = RecipeApplier.ApplyRecipeFile(go, recipe, dir, true, notes);
            int locked = 0;
            if (EditorUtility.DisplayDialog("Material Studio", "Applied edits to " + n + " material(s) (copies in " + dir + ")" +
                    (unlocked > 0 ? ", " + unlocked + " Poiyomi unlocked for it" : "") + ".\n\nLock Poiyomi materials again now?", "Lock", "Leave unlocked"))
                locked = LockSelected(go, notes, dir);
            foreach (string s in notes) Debug.Log("[Material Studio] " + s);
            Debug.Log("[Material Studio] done: " + n + " material(s), " + locked + " locked.");
        }

        [MenuItem("VSF SDK/Material Studio/Open saved edits folder")]
        public static void OpenFolder()
        {
            if (Directory.Exists(RecipeFolder)) EditorUtility.RevealInFinder(RecipeFolder);
            else EditorUtility.DisplayDialog("Material Studio", "No edits saved yet (" + RecipeFolder + ").", "OK");
        }

        static int LockSelected(GameObject go, List<string> notes, string onlyUnder)
        {
            VsfExporter.LockPoiyomi(go, notes, onlyUnder);
            int n = 0;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials) if (m != null && m.shader != null && m.shader.name.StartsWith("Hidden/Locked", StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
