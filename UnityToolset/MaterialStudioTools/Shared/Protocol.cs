using System;
using System.Collections.Generic;

namespace MaterialStudio
{
    // ============================================================================
    //  Files exchanged between the VNyan plugin and the Unity editor toolset.
    //  JsonUtility types only; this file is compiled on both sides. Keep identical.
    // ============================================================================

    [Serializable]
    public class KeywordSet
    {
        public List<string> k = new List<string>();
        public KeywordSet() { }
        public KeywordSet(IEnumerable<string> kws) { if (kws != null) k.AddRange(kws); k.Sort(StringComparer.Ordinal); }
        public string Key() { return string.Join(" ", k.ToArray()); }
    }

    [Serializable]
    public class FloatSet
    {
        public List<NamedFloat> f = new List<NamedFloat>();
    }

    [Serializable]
    public class ShaderRequest
    {
        public string name;                                  // shader name to include
        public List<KeywordSet> variants = new List<KeywordSet>();
        // Number properties of materials that will use this shader. The editor rebuilds
        // each one and lets the shader's own property drawers (ThryToggle, Toggle,
        // KeywordEnum...) pick the keywords, so a locked Poiyomi material gets exactly the
        // variant its unlocked twin needs.
        public List<FloatSet> materials = new List<FloatSet>();
    }

    /// <summary>One job for the editor: kind = "catalog", "shaders", "export" or "setup" (words can be combined).</summary>
    [Serializable]
    public class MSJob
    {
        public string kind = "";
        public string resultPath = "";        // where the editor writes MSResult JSON

        // catalog
        public string catalogPath = "";

        // shaders
        public List<ShaderRequest> shaders = new List<ShaderRequest>();
        public string bundleName = "";        // unique per build, avoids CAB clashes at load
        public string outDir = "";

        // export
        public string sourceKind = "";        // "vsfavatar", "prefab" or "warudo"
        public string sourcePath = "";        // .vsfavatar or .warudo file, or "Assets/....prefab" in the project
        // warudo: the plugin unpacks the mod, so the editor only opens its AssetBundle
        public string warudoBundlePath = "";  // the mod's sharedassets.bin, unpacked
        public string modName = "";
        public string modAuthor = "";
        public string modVersion = "";
        public string modUnityVersion = "";
        public string modCoverPath = "";      // the mod's cover JPEG (VRM thumbnail), optional
        public bool convertPhysics = true;    // bone physics to DynamicBone, as the Warudo Importer does
        public string recipePath = "";
        public string recipeDir = "";         // where "file:" textures live
        public string liveVbxPath = "";       // live mesh snapshot (weights painted in VNyan), optional
        public string outPath = "";           // final .vsfavatar
        public bool relockPoiyomi = true;
        public bool applyLiveWeights = true;
        public bool includeUnusedAssets = true;   // also pack what the source file had but nothing uses now

        // lock
        public List<LockRequest> locks = new List<LockRequest>();
    }

    /// <summary>
    /// One live material to lock with Poiyomi's own optimizer (Thry ShaderOptimizer). The
    /// editor rebuilds it on the unlocked shader from these values; textures only need to say
    /// whether a slot is filled (the lock compiles unused features out) plus tiling/offset.
    /// </summary>
    [Serializable]
    public class LockRequest
    {
        public string id = "";
        public string shader = "";            // unlocked Poiyomi shader name
        public int renderQueue = -1;
        public List<string> keywords = new List<string>();
        public List<NamedFloat> floats = new List<NamedFloat>();
        public List<NamedColor> colors = new List<NamedColor>();
        public List<NamedVector> vectors = new List<NamedVector>();
        public List<TexEdit> textures = new List<TexEdit>();   // src "has" or ""
        public List<NamedString> tags = new List<NamedString>();
    }

    [Serializable]
    public class LockedEntry
    {
        public string id = "";
        public string material = "";          // material name inside the lock bundle
        public string shader = "";            // generated Hidden/Locked/... shader
    }

    [Serializable]
    public class LockManifest
    {
        public string bundleFile = "";
        public string builtAt = "";
        public List<LockedEntry> entries = new List<LockedEntry>();
    }

    [Serializable]
    public class MSResult
    {
        public bool ok;
        public string message = "";
        public List<string> notes = new List<string>();
        public string output = "";
    }

    [Serializable]
    public class CatalogShader
    {
        public string name;
        public string assetPath;
        public string source;                 // top folder under Assets (e.g. _PoiyomiShaders)
        public bool hasErrors;
    }

    [Serializable]
    public class WorkspaceCatalog
    {
        public string unityVersion = "";
        public string builtAt = "";
        public List<CatalogShader> shaders = new List<CatalogShader>();
    }

    [Serializable]
    public class BundledShader
    {
        public string name;
        public string source;
        public List<KeywordSet> variants = new List<KeywordSet>();
    }

    [Serializable]
    public class ShaderBundleManifest
    {
        public string bundleFile = "";
        public string unityVersion = "";
        public string builtAt = "";
        public List<BundledShader> shaders = new List<BundledShader>();
    }
}
