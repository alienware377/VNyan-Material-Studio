using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Builds MaterialStudio.vnobj headlessly:
//   Unity.exe -batchmode -quit -nographics -projectPath <proj> -executeMethod MaterialStudioBuild.Build
//
// The window is built at runtime (a Poiyomi material has ~1500 properties, so the
// inspector is generated per shader); the bundle only carries:
//   * VNyanTemp.prefab: the autostart object with the plugin component (addressable "vnyanitem")
//   * MaterialStudioWindow.prefab: an empty panel VNyan parents into its UI canvas
//   * carrier materials that pull Unity's built-in shaders (with the common keyword
//     combinations) into the bundle, so "switch to Standard" works on any avatar.
public static class MaterialStudioBuild
{
    const string Starter = "Assets/VNyanTemp.prefab";   // file name must be VNyanTemp (root name)
    const string WindowPath = "Assets/MaterialStudioWindow.prefab";
    const string CarrierDir = "Assets/Carriers";

    public static void Build()
    {
        GameObject window = BuildWindow();
        Material[] carriers = BuildCarriers();

        var go = new GameObject("VNyanTemp");
        var plugin = go.AddComponent<MaterialStudio.MaterialStudioPlugin>();
        plugin.windowPrefab = window;
        plugin.builtinCarriers = carriers;
        plugin.pickOutline = BuildOutlineMaterial();
        GameObject starter = PrefabUtility.SaveAsPrefabAsset(go, Starter);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        if (starter == null) { Debug.LogError("[MaterialStudioBuild] starter prefab failed"); EditorApplication.Exit(2); return; }

        var abb = new AssetBundleBuild
        {
            assetBundleName = "materialstudio",
            assetNames = new[] { Starter },
            addressableNames = new[] { "vnyanitem" }
        };
        Directory.CreateDirectory("AssetBundles");
        AssetBundleManifest m = BuildPipeline.BuildAssetBundles("AssetBundles", new[] { abb },
            BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
        if (m == null) { Debug.LogError("[MaterialStudioBuild] BuildAssetBundles returned null"); EditorApplication.Exit(2); return; }
        string final = Path.Combine("AssetBundles", "MaterialStudio.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(Path.Combine("AssetBundles", "materialstudio"), final);
        Debug.Log("[MaterialStudioBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    static GameObject BuildWindow()
    {
        var root = new GameObject("MaterialStudioWindow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rt = root.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(900, 680);
        root.GetComponent<Image>().color = new Color(0.13f, 0.13f, 0.15f, 0.97f);
        root.AddComponent<MaterialStudio.WindowDrag>();
        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, WindowPath);
        Object.DestroyImmediate(root);
        return asset;
    }

    static Material[] BuildCarriers()
    {
        if (AssetDatabase.IsValidFolder(CarrierDir)) AssetDatabase.DeleteAsset(CarrierDir);
        AssetDatabase.CreateFolder("Assets", "Carriers");
        var list = new List<Material>();
        // Standard / Standard (Specular): render mode x normal map x emission x gloss map
        string[][] modeKw = { new string[0], new[] { "_ALPHATEST_ON" }, new[] { "_ALPHABLEND_ON" }, new[] { "_ALPHAPREMULTIPLY_ON" } };
        foreach (string shName in new[] { "Standard", "Standard (Specular setup)" })
        {
            Shader sh = Shader.Find(shName);
            if (sh == null) { Debug.LogWarning("[MaterialStudioBuild] missing " + shName); continue; }
            string gloss = shName == "Standard" ? "_METALLICGLOSSMAP" : "_SPECGLOSSMAP";
            for (int mode = 0; mode < 4; mode++)
                for (int bits = 0; bits < 8; bits++)
                {
                    var kws = new List<string>(modeKw[mode]);
                    if ((bits & 1) != 0) kws.Add("_NORMALMAP");
                    if ((bits & 2) != 0) kws.Add("_EMISSION");
                    if ((bits & 4) != 0) kws.Add(gloss);
                    var m = new Material(sh) { shaderKeywords = kws.ToArray() };
                    m.SetFloat("_Mode", mode);
                    list.Add(Save(m, shName.Replace(" ", "").Replace("(", "").Replace(")", "") + "_" + mode + "_" + bits));
                }
        }
        foreach (string shName in new[]
        {
            "Unlit/Texture", "Unlit/Color", "Unlit/Transparent", "Unlit/Transparent Cutout",
            "Legacy Shaders/Diffuse", "Legacy Shaders/Transparent/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse",
            "Mobile/Diffuse", "Autodesk Interactive", "Particles/Standard Unlit"
        })
        {
            Shader sh = Shader.Find(shName);
            if (sh == null) { Debug.LogWarning("[MaterialStudioBuild] missing " + shName); continue; }
            list.Add(Save(new Material(sh), shName.Replace("/", "_").Replace(" ", "")));
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[MaterialStudioBuild] " + list.Count + " carrier materials");
        return list.ToArray();
    }

    // The pick-mode highlight: the shader rides into the bundle through this material.
    static Material BuildOutlineMaterial()
    {
        Shader sh = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/MSPickOutline.shader");
        if (sh == null) { Debug.LogError("[MaterialStudioBuild] MSPickOutline.shader missing"); return null; }
        return Save(new Material(sh), "PickOutline");
    }

    static Material Save(Material m, string name)
    {
        string p = CarrierDir + "/" + name + ".mat";
        AssetDatabase.CreateAsset(m, p);
        return AssetDatabase.LoadAssetAtPath<Material>(p);
    }
}
