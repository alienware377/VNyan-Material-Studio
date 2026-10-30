using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MaterialStudio
{
    public class ShaderChoice
    {
        public string name;
        public Shader shader;          // null = listed but not built yet
        public string origin;          // "Avatar", "Unity", "VNyan", "Shader bundle", "Workspace"
        public ShaderFamily family;
        public string note = "";
    }

    /// <summary>
    /// Every shader the user can switch a material to: what VNyan and the avatar already
    /// have in memory, Unity's built-ins this plugin ships in its .vnobj, shaders from the
    /// shader bundle built from the user's packages, and (listed but greyed) shaders the
    /// workspace has that are not built yet.
    /// </summary>
    public class ShaderCatalog
    {
        public readonly List<ShaderChoice> choices = new List<ShaderChoice>();
        readonly Dictionary<string, ShaderChoice> byName = new Dictionary<string, ShaderChoice>();

        public AssetBundle shaderBundle;
        public ShaderBundleManifest manifest;
        readonly List<Shader> bundleShaders = new List<Shader>();
        public WorkspaceCatalog workspace;
        public Shader[] carried = new Shader[0];      // built-ins shipped inside the .vnobj

        static readonly string[] BuiltinNames =
        {
            "Standard", "Standard (Specular setup)", "Autodesk Interactive",
            "Unlit/Texture", "Unlit/Color", "Unlit/Transparent", "Unlit/Transparent Cutout",
            "Legacy Shaders/Diffuse", "Legacy Shaders/Transparent/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse",
            "Mobile/Diffuse", "Particles/Standard Unlit", "Particles/Standard Surface", "Sprites/Default",
            "VRM/MToon", "VRM10/MToon10", "VRM/UnlitTexture", "VRM/UnlitCutout", "VRM/UnlitTransparent",
            "VRM/UnlitTransparentZWrite", "UniGLTF/UniUnlit",
        };

        public static string Dir { get { return Path.Combine(Application.persistentDataPath, "MaterialStudio"); } }
        public static string BundleDir { get { return Path.Combine(Dir, "ShaderBundles"); } }
        public static string ActiveFile { get { return Path.Combine(BundleDir, "active.txt"); } }

        /// <summary>Loads the active shader bundle (and cleans up superseded ones).</summary>
        public void LoadShaderBundle(List<string> log)
        {
            try
            {
                Directory.CreateDirectory(BundleDir);
                if (!File.Exists(ActiveFile)) return;
                string active = File.ReadAllText(ActiveFile).Trim();
                if (active.Length == 0) return;
                string path = Path.Combine(BundleDir, active);
                // anything not active is a previous build; a loaded bundle keeps its file
                // locked, so they are removed at startup before anything is loaded
                foreach (string f in Directory.GetFiles(BundleDir))
                {
                    string fn = Path.GetFileName(f);
                    if (fn == "active.txt" || fn == active || fn == active + ".json") continue;
                    try { File.Delete(f); } catch { }
                }
                if (!File.Exists(path)) { log.Add("Shader bundle " + active + " is missing; rebuild it on the Packages tab."); return; }
                LoadBundleFile(path, log);
            }
            catch (Exception e) { log.Add("Shader bundle load failed: " + e.Message); }
        }

        public bool LoadBundleFile(string path, List<string> log)
        {
            AssetBundle b = AssetBundle.LoadFromFile(path);
            if (b == null) { log.Add("Could not open shader bundle " + Path.GetFileName(path) + "."); return false; }
            UnloadShaderBundle();
            shaderBundle = b;
            bundleShaders.Clear();
            // Loading the shaders AND the carrier materials keeps every variant resident.
            try { bundleShaders.AddRange(b.LoadAllAssets<Shader>()); } catch { }
            try { b.LoadAllAssets<Material>(); } catch { }
            manifest = null;
            string mpath = path + ".json";
            if (File.Exists(mpath))
            {
                try { manifest = JsonIO.FromJson<ShaderBundleManifest>(File.ReadAllText(mpath)); } catch { }
            }
            log.Add("Shader bundle: " + bundleShaders.Count + " shader(s) from " + Path.GetFileName(path) + ".");
            return true;
        }

        // ---- Poiyomi lock bundles: one per "Lock & optimize" run, all kept loaded so saved
        // edits that point at a locked shader find it on the next load.
        public static string LockDir { get { return Path.Combine(Dir, "LockBundles"); } }
        readonly List<Shader> lockShaders = new List<Shader>();
        readonly Dictionary<string, Material> lockedById = new Dictionary<string, Material>();
        readonly List<AssetBundle> lockBundles = new List<AssetBundle>();

        public void LoadLockBundles(List<string> log)
        {
            try
            {
                Directory.CreateDirectory(LockDir);
                int n = 0;
                foreach (string json in Directory.GetFiles(LockDir, "*.json"))
                {
                    string bundle = json.Substring(0, json.Length - 5);
                    if (File.Exists(bundle) && LoadLockBundle(bundle, null) != null) n++;
                }
                if (n > 0) log.Add("Loaded " + n + " Poiyomi lock bundle(s).");
            }
            catch (Exception e) { log.Add("Lock bundles: " + e.Message); }
        }

        /// <summary>Loads one lock bundle; returns its manifest (entry id -> locked material via LockedMaterial).</summary>
        public LockManifest LoadLockBundle(string path, List<string> log)
        {
            AssetBundle b = AssetBundle.LoadFromFile(path);
            if (b == null) { if (log != null) log.Add("Could not open " + Path.GetFileName(path) + "."); return null; }
            lockBundles.Add(b);
            LockManifest man = null;
            try { man = JsonIO.FromJson<LockManifest>(File.ReadAllText(path + ".json")); } catch { }
            var byName = new Dictionary<string, Material>();
            foreach (Material m in b.LoadAllAssets<Material>()) if (m != null) byName[m.name] = m;
            try { lockShaders.AddRange(b.LoadAllAssets<Shader>()); } catch { }
            foreach (Material m in byName.Values) if (m.shader != null && !lockShaders.Contains(m.shader)) lockShaders.Add(m.shader);
            if (man != null)
                foreach (LockedEntry e in man.entries)
                {
                    Material m;
                    if (byName.TryGetValue(e.material, out m)) lockedById[Path.GetFileName(path) + "|" + e.id] = m;
                }
            return man;
        }

        public Material LockedMaterial(string bundleFile, string id)
        {
            Material m;
            return lockedById.TryGetValue(bundleFile + "|" + id, out m) ? m : null;
        }

        public void UnloadShaderBundle()
        {
            // Unload(false) frees the container but keeps the loaded shader objects alive, so
            // materials already using them keep rendering until they are re-pointed.
            if (shaderBundle != null) { try { shaderBundle.Unload(false); } catch { } }
            shaderBundle = null;
        }

        public void LoadWorkspaceCatalog(string catalogPath)
        {
            workspace = null;
            if (string.IsNullOrEmpty(catalogPath) || !File.Exists(catalogPath)) return;
            try { workspace = JsonIO.FromJson<WorkspaceCatalog>(File.ReadAllText(catalogPath)); } catch { }
        }

        /// <summary>Rebuilds the list. <paramref name="avatarShaders"/> are what the avatar uses now.</summary>
        public void Refresh(IEnumerable<Shader> avatarShaders)
        {
            choices.Clear();
            byName.Clear();
            var seen = new HashSet<Shader>();

            if (avatarShaders != null)
                foreach (Shader s in avatarShaders) Add(s, "Avatar", seen, true);
            foreach (Shader s in bundleShaders) Add(s, "Shader bundle", seen, false);
            foreach (Shader s in carried) Add(s, "Unity", seen, false);
            foreach (string n in BuiltinNames)
            {
                Shader s = null;
                try { s = Shader.Find(n); } catch { }
                Add(s, n.StartsWith("VRM", StringComparison.Ordinal) || n.StartsWith("UniGLTF", StringComparison.Ordinal) ? "VNyan" : "Unity", seen, false);
            }
            // Whatever else is loaded right now (other avatars, other plugins, VNyan itself).
            Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
            for (int i = 0; i < all.Length; i++)
            {
                Shader s = all[i];
                if (s == null || s.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                if (s.name.StartsWith("Legacy Shaders/Particles", StringComparison.Ordinal)) continue;
                Add(s, "Loaded", seen, false);
            }
            if (workspace != null)
            {
                foreach (CatalogShader c in workspace.shaders)
                {
                    if (c == null || c.hasErrors || string.IsNullOrEmpty(c.name) || byName.ContainsKey(c.name)) continue;
                    var ch = new ShaderChoice { name = c.name, shader = null, origin = "Workspace", family = GuessFamily(c.name), note = "not built yet: tick it on Packages, then Build" };
                    choices.Add(ch);
                    byName[c.name] = ch;
                }
            }
        }

        void Add(Shader s, string origin, HashSet<Shader> seen, bool allowHidden)
        {
            if (s == null || seen.Contains(s)) return;
            if (!allowHidden && s.name.StartsWith("Hidden/", StringComparison.Ordinal)) return;
            seen.Add(s);
            if (!s.isSupported) return;
            var ch = new ShaderChoice { name = s.name, shader = s, origin = origin, family = ShaderLayout.FamilyOf(s, null) };
            if (origin == "Shader bundle" && manifest != null)
            {
                BundledShader b = FindBundled(s.name);
                if (b != null && !string.IsNullOrEmpty(b.source)) ch.note = "from " + b.source;
            }
            choices.Add(ch);
            if (!byName.ContainsKey(s.name)) byName[s.name] = ch;
        }

        static ShaderFamily GuessFamily(string n)
        {
            string l = n.ToLowerInvariant();
            if (l.Contains("poiyomi")) return ShaderFamily.Poiyomi;
            if (l.Contains("liltoon")) return ShaderFamily.LilToon;
            if (l.Contains("mtoon10")) return ShaderFamily.MToon10;
            if (l.Contains("mtoon")) return ShaderFamily.MToon;
            return ShaderFamily.Unknown;
        }

        public BundledShader FindBundled(string name)
        {
            if (manifest == null || manifest.shaders == null) return null;
            foreach (BundledShader b in manifest.shaders) if (b != null && b.name == name) return b;
            return null;
        }

        /// <summary>
        /// Finds a usable shader by name. Bundle shaders win over anything else of the same
        /// name only when the caller asks (Poiyomi unlock wants the full shader, not a locked
        /// copy that happens to share the name).
        /// </summary>
        public Shader Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Shader s in bundleShaders) if (s != null && s.name == name) return s;
            foreach (Shader s in lockShaders) if (s != null && s.name == name) return s;
            // our carried built-ins have every common keyword combination compiled; an
            // avatar's own copy of "Standard" only has the variants that avatar used
            foreach (Shader s in carried) if (s != null && s.name == name) return s;
            ShaderChoice c;
            if (byName.TryGetValue(name, out c) && c.shader != null) return c.shader;
            Shader f = null;
            try { f = Shader.Find(name); } catch { }
            if (f != null) return f;
            Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].name == name) return all[i];
            return null;
        }

        public bool IsBundleShader(Shader s) { return s != null && bundleShaders.Contains(s); }

        /// <summary>True when the bundle manifest lists this exact (already filtered) keyword set for the shader.</summary>
        public bool HasVariantKey(Shader s, string key)
        {
            BundledShader b = FindBundled(s.name);
            if (b == null) return false;
            foreach (KeywordSet ks in b.variants) if (ks != null && new KeywordSet(ks.k).Key() == key) return true;
            return false;
        }

        public Shader FindBundleShader(string name)
        {
            foreach (Shader s in bundleShaders) if (s != null && s.name == name) return s;
            return null;
        }

        /// <summary>
        /// Keeps only keywords the shader really declares. Materials often carry leftovers
        /// from an earlier shader, and those must not count as a variant difference.
        /// </summary>
        public static string[] DeclaredOnly(Shader s, string[] keywords)
        {
            if (s == null || keywords == null) return new string[0];
            HashSet<string> declared = null;
            try
            {
                string[] names = s.keywordSpace.keywordNames;
                if (names != null && names.Length > 0) declared = new HashSet<string>(names);
            }
            catch { }
            var outp = new List<string>();
            foreach (string k in keywords)
                if (!string.IsNullOrEmpty(k) && (declared == null || declared.Contains(k))) outp.Add(k);
            return outp.ToArray();
        }

        /// <summary>
        /// True when this keyword combination was compiled into the shader bundle. Unity
        /// silently falls back to the nearest variant otherwise, so the UI says so instead.
        /// Unknown (not from our bundle) returns true.
        /// </summary>
        public bool HasVariant(Shader s, string[] keywords)
        {
            if (!IsBundleShader(s) || manifest == null) return true;
            BundledShader b = FindBundled(s.name);
            if (b == null) return true;
            var want = new KeywordSet(DeclaredOnly(s, keywords));
            string key = want.Key();
            foreach (KeywordSet ks in b.variants) if (ks != null && new KeywordSet(ks.k).Key() == key) return true;
            return false;
        }
    }
}
