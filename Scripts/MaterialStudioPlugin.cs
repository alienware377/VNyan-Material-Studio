using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace MaterialStudio
{
    [Serializable]
    public class PackageEntry
    {
        public string path = "";
        public bool isFolder;
        public string importedStamp = "";     // size+mtime when it went into the workspace
    }

    [Serializable]
    public class MSConfig
    {
        public int version = 1;
        public bool autoApply = true;
        public bool autoSave = true;
        public bool showHidden;
        public string unityExe = "";
        public string workspace = "";
        public bool useSeedDirectly;
        public string seedProject = "";
        public List<string> seedFolders = new List<string>();
        public bool workspaceSeeded;
        public List<PackageEntry> packages = new List<PackageEntry>();
        public List<string> wantedShaders = new List<string>();
        public string exportOut = "";
        public string exportName = "";
        public string exportSourceKind = "vsfavatar";
        public string exportVsf = "";
        public string exportProject = "";
        public string exportPrefab = "";
        public bool relockPoiyomi = true;
        public bool applyLiveWeights = true;
        public bool loadAfterExport;
        public List<NamedString> avatarPaths = new List<NamedString>();
        public bool autoBuildVariants = true;   // file name -> full path the user picked
        public bool enabled = true;             // the title-bar On switch (off = shipped materials, nothing saved)
    }

    /// <summary>
    /// Material Studio: live material and shader editing for the avatar loaded in VNyan,
    /// for any shader (Poiyomi, lilToon, MToon, Unity built-ins, and shaders the user adds
    /// from .unitypackages), plus its own .vsfavatar exporter that bakes the edits in.
    /// </summary>
    public partial class MaterialStudioPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        public const string LOG = "[MaterialStudio] ";
        public const string VERSION = "v1.0.0";
        // GitHub repo the update check reads (no releases yet: the check stays quiet until there are)
        public const string REPO = "alienware377/VNyan-Material-Studio";
        const string BUTTON = "Material Studio";

        // Assigned by the bundle build (MaterialStudioBuild.cs).
        public GameObject windowPrefab;
        public Material[] builtinCarriers = new Material[0];   // pulls Unity built-in shaders into the .vnobj

        public static MaterialStudioPlugin Instance { get; private set; }

        MSConfig cfg = new MSConfig();
        readonly MaterialRegistry reg = new MaterialRegistry();
        readonly ShaderCatalog catalog = new ShaderCatalog();
        readonly UnityRunner runner = new UnityRunner();
        readonly List<string> notes = new List<string>();

        GameObject boundAvatar;
        GameObject pendingAvatar;
        float pendingSince = -1f;
        float nextWatch;
        string sourcePath = "";
        string avatarKey = "";
        string signature = "";
        Recipe recipe;
        bool dirty;
        float saveAt = -1f;
        bool applyingRecipe;

        readonly List<MatEntry> selection = new List<MatEntry>();
        MatEntry Primary { get { return selection.Count > 0 ? selection[0] : null; } }

        // textures the user loaded from files, and where they live in the recipe folder
        readonly Dictionary<Texture, string> texSources = new Dictionary<Texture, string>();
        readonly Dictionary<string, Texture2D> fileTexCache = new Dictionary<string, Texture2D>();
        MatSnapshot clipboard;

        public static string RecipeDir { get { return Path.Combine(ShaderCatalog.Dir, "Recipes"); } }
        string RecipePath { get { return Path.Combine(RecipeDir, avatarKey + ".json"); } }
        string RecipeTexDir { get { return Path.Combine(RecipeDir, avatarKey); } }
        static string ConfigPath { get { return Path.Combine(ShaderCatalog.Dir, "config.json"); } }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            Instance = this;
            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON, this); }
            catch (Exception e) { Debug.LogWarning(LOG + "registerPluginButton failed: " + e.Message); }
            Ui.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Ui.OnError = delegate (string msg) { SetStatus(msg); };
            Font.textureRebuilt += OnFontRebuilt;

            try { Directory.CreateDirectory(ShaderCatalog.Dir); Directory.CreateDirectory(RecipeDir); } catch { }
            LoadConfig();

            var carried = new List<Shader>();
            if (builtinCarriers != null)
                foreach (Material m in builtinCarriers) if (m != null && m.shader != null && !carried.Contains(m.shader)) carried.Add(m.shader);
            catalog.carried = carried.ToArray();

            var log = new List<string>();
            catalog.LoadShaderBundle(log);
            catalog.LoadLockBundles(log);
            catalog.LoadWorkspaceCatalog(CatalogPath);
            foreach (string l in log) Note(l);

            runner.onFinishedAll = delegate { if (window != null && window.activeSelf && (currentTab == 2 || currentTab == 3)) RebuildTab(); };
            try { BuildWindow(); }
            catch (Exception e) { Debug.LogError(LOG + "window build failed: " + e); }
            // update badge sits left of Undo; the status line makes room for it
            if (root != null)
                PluginChrome.CheckForUpdate(this, root, REPO, VERSION, LOG, Ui.font, W - (PluginChrome.XInset + PluginChrome.XSize + 6f) - 590f,
                    delegate (RectTransform badge) { if (statusLabel != null) statusLabel.rectTransform.sizeDelta = new Vector2(300f, statusLabel.rectTransform.sizeDelta.y); });
            try
            {
                Debug.Log(LOG + "ready. Unity editors found: " + UnityDriver.FindEditors(cfg.unityExe).Count +
                          ", toolset " + (UnityDriver.ToolsetAvailable ? "present" : "MISSING") + ".");
            }
            catch (Exception e) { Debug.Log(LOG + "ready (editor scan failed: " + e.Message + ")."); }
        }

        public void pluginButtonClicked()
        {
            if (window == null) { Debug.LogWarning(LOG + "no window."); return; }
            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show)
            {
                window.transform.SetAsLastSibling();
                if (boundAvatar == null) TryBindNow();
                RefreshAll();
            }
        }

        void OnDestroy()
        {
            Font.textureRebuilt -= OnFontRebuilt;
            if (dirty && cfg.autoSave) SaveRecipe();
            if (Instance == this) Instance = null;
        }

        void OnFontRebuilt(Font f)
        {
            if (window == null || f != Ui.font) return;
            foreach (Text t in window.GetComponentsInChildren<Text>(true)) { t.FontTextureChanged(); t.SetVerticesDirty(); }
        }

        void Update()
        {
            try
            {
                WatchAvatar();
                PowerFileTick();
                runner.Tick();
                TickVariantBuild();
                TickBanner();
                if (saveAt > 0f && Time.unscaledTime >= saveAt) { saveAt = -1f; if (cfg.autoSave) SaveRecipe(); }
                if (window != null && window.activeSelf)
                {
                    if (tips != null) tips.Tick();
                    HandleHotkeys();
                    TickPick();
                    TickJobsUi();
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "Update: " + e.Message); }
        }

        void HandleHotkeys()
        {
            if (!cfg.enabled) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl || !Input.GetKeyDown(KeyCode.Z)) return;
            // typing in a box: Ctrl+Z belongs to the text, not to the materials
            var es = UnityEngine.EventSystems.EventSystem.current;
            GameObject focused = es != null ? es.currentSelectedGameObject : null;
            if (focused != null && focused.GetComponent<InputField>() != null) return;
            // only while the cursor is over our window, so VNyan's own shortcuts stay theirs
            RectTransform rt = window.transform as RectTransform;
            if (rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null)) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift) Redo(); else Undo();
        }

        // ------------------------------------------------------------------ avatar binding

        void WatchAvatar()
        {
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + 0.5f;
            GameObject cur = null;
            try { cur = VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject() as GameObject; } catch { }
            if (cur == boundAvatar && boundAvatar != null) return;
            // The avatar is changing (or already gone): save NOW, while the edited materials
            // are still around. VNyan's cached template keeps them alive past the GameObject.
            if ((object)boundAvatar != null && dirty && cfg.autoSave) SaveRecipe();
            if (cur == null)
            {
                if (boundAvatar != null || (object)boundAvatar != null) { Unbind(); RefreshAll(); }
                return;
            }
            // VNyan finishes setting a new avatar up over a few frames: wait until it is stable.
            if (cur != pendingAvatar) { pendingAvatar = cur; pendingSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - pendingSince < 1.0f) return;
            if (cur.GetComponentsInChildren<Renderer>(true).Length == 0) return;
            Bind(cur);
        }

        void TryBindNow()
        {
            GameObject cur = null;
            try { cur = VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject() as GameObject; } catch { }
            if (cur != null) Bind(cur);
        }

        void Unbind()
        {
            if (dirty && cfg.autoSave) SaveRecipe();
            boundAvatar = null;
            reg.Bind(null);
            selection.Clear();
            undo.Clear();
            recipe = null;
            avatarKey = "";
        }

        void Bind(GameObject avatar)
        {
            if (dirty && (object)boundAvatar != null && cfg.autoSave) SaveRecipe();
            boundAvatar = avatar;
            pendingAvatar = null;
            selection.Clear();
            undo.Clear();
            reg.Bind(avatar);
            signature = MaterialRegistry.Signature(avatar);
            sourcePath = AvatarIdentity.SourcePath(avatar);
            // VNyan reloads the same model now and then; when the lookup comes up empty, the
            // same set of materials means the same file as last time.
            string matSig = MaterialSetKey(avatar);
            string remembered;
            if (sourcePath.Length == 0 && knownSources.TryGetValue(matSig, out remembered)) sourcePath = remembered;
            if (sourcePath.Length > 0) knownSources[matSig] = sourcePath;
            avatarKey = AvatarIdentity.KeyFor(sourcePath, signature);
            if (sourcePath.Length > 0) AdoptTemporaryRecipe(avatar);
            // the export source follows the avatar on screen (VNyan may only give a file name)
            string full = ResolveAvatarFile(sourcePath);
            if (full.Length > 0) { cfg.exportVsf = full; RememberAvatarPath(full); }

            recipe = LoadRecipe();
            int applied = 0;
            if (recipe != null && cfg.autoApply && cfg.enabled) applied = ApplyRecipe(recipe);
            else if (recipe != null) Debug.Log(LOG + "saved edits not applied: " + (cfg.enabled ? "auto-apply is off" : "Material Studio is switched off"));
            catalog.Refresh(reg.Shaders());
            IndexShippedVariants();
            CheckVariants();
            if (reg.entries.Count > 0) selection.Add(reg.entries[0]);
            dirty = false;
            Debug.Log(LOG + "bound '" + (sourcePath.Length > 0 ? Path.GetFileName(sourcePath) : avatar.name) + "': " +
                      reg.entries.Count + " materials, recipe " + (recipe != null ? "found (" + applied + " applied)" : "none") + ".");
            RefreshAll();
        }

        readonly Dictionary<string, string> knownSources = new Dictionary<string, string>();

        static string MaterialSetKey(GameObject avatar)
        {
            var names = new List<string>();
            foreach (Renderer r in avatar.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials) if (m != null) names.Add(r.name + ":" + m.name);
            names.Sort(StringComparer.Ordinal);
            return AvatarIdentity.StableHash(string.Join("|", names.ToArray())).ToString("x8");
        }

        /// <summary>
        /// Edits made while the model's file could not be identified were saved under a
        /// temporary "avatar_xxxx" name. Once the file is known, move them to the real name,
        /// but only when every saved edit still points at the same material on this avatar.
        /// </summary>
        void AdoptTemporaryRecipe(GameObject avatar)
        {
            try
            {
                if (File.Exists(RecipePath) || !Directory.Exists(RecipeDir)) return;
                foreach (string f in Directory.GetFiles(RecipeDir, "avatar_*.json"))
                {
                    Recipe r = Recipe.FromJson(File.ReadAllText(f));
                    if (r == null || r.materials.Count == 0) continue;
                    bool all = true;
                    foreach (MatEdit e in r.materials)
                    {
                        Material m = e.slots.Count > 0 ? reg.MaterialAt(e.slots[0]) : null;
                        MatEntry en = reg.EntryFor(m);
                        if (m == null || en == null || en.originalName != e.originalName) { all = false; break; }
                    }
                    if (!all) continue;
                    r.avatarKey = avatarKey;
                    r.avatarFile = sourcePath;
                    File.WriteAllText(RecipePath, r.ToJson());
                    string oldTex = Path.Combine(RecipeDir, Path.GetFileNameWithoutExtension(f));
                    if (Directory.Exists(oldTex) && !Directory.Exists(RecipeTexDir)) Directory.Move(oldTex, RecipeTexDir);
                    File.Delete(f);
                    Note("Moved " + r.materials.Count + " saved edit(s) from " + Path.GetFileName(f) + " to this model's file name.");
                    return;
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "adopting saved edits failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ recipe

        Recipe LoadRecipe()
        {
            try
            {
                if (!File.Exists(RecipePath)) return null;
                Recipe r = Recipe.FromJson(File.ReadAllText(RecipePath));
                if (r != null && !string.IsNullOrEmpty(r.signature) && r.signature != signature)
                    Note("Recipe '" + avatarKey + "' was made on a slightly different version of this model; applying what still matches.");
                return r;
            }
            catch (Exception e) { Note("Could not read recipe: " + e.Message); return null; }
        }

        /// <summary>Applies a recipe to the bound avatar. Returns how many materials it touched.</summary>
        int ApplyRecipe(Recipe r)
        {
            if (r == null || boundAvatar == null) { Debug.Log(LOG + "apply skipped: " + (r == null ? "no recipe" : "no avatar")); return 0; }
            Debug.Log(LOG + "applying " + r.materials.Count + " saved material edit(s)");
            applyingRecipe = true;
            int n = 0;
            var applyNotes = new List<string>();
            try
            {
                Dictionary<string, Texture> avatarTex = reg.AvatarTextures();
                // splits first, so their edits land on the copy
                foreach (MatEdit e in r.materials)
                {
                    if (e == null || !e.unique || e.slots.Count == 0) continue;
                    Material src = reg.MaterialAt(e.slots[0]);
                    if (src == null) continue;
                    MatEntry existing = reg.EntryFor(src);
                    if (existing != null && existing.unique && existing.slots.Count == e.slots.Count) continue; // already split
                    reg.SplitOff(src, e.slots);
                }
                reg.Bind(boundAvatar);
                foreach (MatEdit e in r.materials)
                {
                    if (e == null || e.slots.Count == 0) { applyNotes.Add("A saved edit has no material address; skipped."); continue; }
                    Material m = reg.MaterialAt(e.slots[0]);
                    if (m == null) { applyNotes.Add("No material at " + e.slots[0] + " any more."); continue; }
                    MatEntry entry = reg.EntryFor(m);
                    // start from the original so applying twice never stacks
                    if (entry != null && entry.orig != null) entry.orig.Restore(m);
                    RecipeEngine.Apply(m, e, ResolveShader, delegate (TexEdit te) { return ResolveTexture(te, avatarTex); }, applyNotes);
                    if (entry != null) entry.unlockedPoiyomi = e.unlockedPoiyomi;
                    n++;
                }
            }
            catch (Exception ex) { applyNotes.Add("Recipe apply stopped: " + ex.Message); }
            finally { applyingRecipe = false; }
            foreach (string s in applyNotes) Note(s);
            return n;
        }

        Shader ResolveShader(string name) { return catalog.Find(name); }

        Texture ResolveTexture(TexEdit te, Dictionary<string, Texture> avatarTex)
        {
            if (te == null || string.IsNullOrEmpty(te.src)) return null;
            if (te.src.StartsWith("avatar:", StringComparison.Ordinal))
            {
                Texture t;
                return avatarTex.TryGetValue(te.src.Substring(7), out t) ? t : null;
            }
            if (te.src.StartsWith("file:", StringComparison.Ordinal))
                return LoadFileTexture(Path.Combine(RecipeTexDir, te.src.Substring(5)), te.linear, te.src);
            return null;
        }

        Texture2D LoadFileTexture(string full, bool linear, string srcKey)
        {
            Texture2D t;
            string cacheKey = full + (linear ? "|lin" : "|srgb");
            if (fileTexCache.TryGetValue(cacheKey, out t) && t != null) return t;
            if (!File.Exists(full)) return null;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
            if (!ImageConversion.LoadImage(t, File.ReadAllBytes(full), false)) { Destroy(t); return null; }
            t.name = Path.GetFileNameWithoutExtension(full);
            t.wrapMode = TextureWrapMode.Repeat;
            t.anisoLevel = 4;
            fileTexCache[cacheKey] = t;
            texSources[t] = srcKey;
            texLinear[t] = linear;
            return t;
        }

        /// <summary>Throw away unsaved changes: back to the shipped state, then the saved file.</summary>
        void ReloadFromFile()
        {
            if (!CanEdit()) return;
            var ids = new List<string>();
            foreach (MatEntry e in selection) ids.Add(e.id);
            bool merged = false;
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null) continue;
                e.orig.Restore(e.mat);
                e.unlockedPoiyomi = false;
                if (e.unique) merged |= reg.Merge(e.mat);
            }
            if (merged) reg.Bind(boundAvatar);
            undo.Clear();
            recipe = LoadRecipe();
            if (recipe != null) ApplyRecipe(recipe);
            else reg.Bind(boundAvatar);
            dirty = false;
            saveAt = -1f;
            RebuildSelection(ids);
            SetStatus(recipe != null ? "Reloaded the saved edits." : "No saved edits; everything is as the model shipped.");
            RefreshAll();
        }

        /// <summary>Entries are recreated by every re-bind; find the same materials again by id.</summary>
        void RebuildSelection(List<string> ids)
        {
            selection.Clear();
            foreach (string id in ids) { MatEntry e = reg.EntryById(id); if (e != null && !selection.Contains(e)) selection.Add(e); }
            if (selection.Count == 0 && reg.entries.Count > 0) selection.Add(reg.entries[0]);
        }

        /// <summary>Diff every material against its original and write the recipe file.</summary>
        public void SaveRecipe() { SaveRecipe(false); }

        public void SaveRecipe(bool manual)
        {
            saveAt = -1f;
            // switched off = the avatar shows the SHIPPED materials; saving now would wipe the edits file
            if (!cfg.enabled) return;
            // (object) check: the avatar GameObject may already be destroyed while its
            // materials (shared with VNyan's cached template) are still fine to read
            if ((object)boundAvatar == null || string.IsNullOrEmpty(avatarKey)) return;
            try
            {
                Recipe r = BuildRecipe();
                dirty = false;
                Directory.CreateDirectory(RecipeDir);
                if (r.materials.Count == 0 && !File.Exists(RecipePath)) return;
                File.WriteAllText(RecipePath, r.ToJson());
                recipe = r;
                SetStatus("Saved " + r.materials.Count + " edited material(s).", !manual);
            }
            catch (Exception e) { SetStatus("Save failed: " + e.Message); Debug.LogWarning(LOG + "save failed: " + e); }
        }

        Recipe BuildRecipe()
        {
            var r = new Recipe
            {
                avatarKey = avatarKey, avatarFile = sourcePath ?? "", signature = signature,
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            foreach (MatEntry e in reg.entries)
            {
                if (e.mat == null)
                {
                    // material already destroyed: keep what was saved for it before
                    MatEdit keep = recipe != null ? recipe.Find(e.id) : null;
                    if (keep != null) r.materials.Add(keep);
                    continue;
                }
                string[] showing = null, wanted;
                if (pendingKw.TryGetValue(e.mat, out wanted)) { showing = e.mat.shaderKeywords; e.mat.shaderKeywords = wanted; }
                MatEdit me = RecipeEngine.Diff(e.orig, e.mat, TexSourceOf);
                if (showing != null) e.mat.shaderKeywords = showing;
                if (me == null && !e.unique) continue;
                if (me == null) me = new MatEdit();
                me.id = e.id;
                me.originalName = e.originalName;
                me.originalShader = e.originalShader;
                me.slots.AddRange(e.slots);
                me.unique = e.unique;
                me.unlockedPoiyomi = e.unlockedPoiyomi;
                // carry the texture-file colour space so the editor imports it the same way; it
                // belongs to the loaded texture object (one per colour space), not to the file
                foreach (TexEdit te in me.textures)
                {
                    if (!te.src.StartsWith("file:", StringComparison.Ordinal)) continue;
                    Texture tt = e.mat.HasProperty(te.n) ? e.mat.GetTexture(te.n) : null;
                    bool lin;
                    te.linear = tt != null && texLinear.TryGetValue(tt, out lin) && lin;
                }
                r.materials.Add(me);
            }
            return r;
        }

        readonly Dictionary<Texture, bool> texLinear = new Dictionary<Texture, bool>();

        string TexSourceOf(Texture t)
        {
            string s;
            if (t != null && texSources.TryGetValue(t, out s)) return s;
            return t != null ? "avatar:" + t.name : "";
        }

        void MarkDirty()
        {
            if (applyingRecipe) return;
            dirty = true;
            saveAt = Time.unscaledTime + 1.5f;
        }

        // ------------------------------------------------------------------ undo

        class UndoStep
        {
            public string key;
            public float time;
            public readonly List<Material> mats = new List<Material>();
            public readonly List<MatSnapshot> before = new List<MatSnapshot>();
            public List<MatSnapshot> after;
        }

        class UndoStack
        {
            public readonly List<UndoStep> done = new List<UndoStep>();
            public readonly List<UndoStep> undone = new List<UndoStep>();
            public void Clear() { done.Clear(); undone.Clear(); }
        }

        readonly UndoStack undo = new UndoStack();

        /// <summary>Record the state before a change. Repeated changes to the same control coalesce.</summary>
        void BeginEdit(string key, List<Material> mats)
        {
            foreach (Material m in mats)
            {
                string[] want;
                if (m != null && pendingKw.TryGetValue(m, out want)) { m.shaderKeywords = want; pendingKw.Remove(m); }
            }
            UndoStep top = undo.done.Count > 0 ? undo.done[undo.done.Count - 1] : null;
            if (top != null && top.key == key && Time.unscaledTime - top.time < 0.8f && SameSet(top.mats, mats))
            {
                top.time = Time.unscaledTime;
                top.after = null;
                return;
            }
            if (top != null && top.after == null) top.after = CaptureAll(top.mats);
            var s = new UndoStep { key = key, time = Time.unscaledTime };
            s.mats.AddRange(mats);
            s.before.AddRange(CaptureAll(mats));
            undo.done.Add(s);
            if (undo.done.Count > 200) undo.done.RemoveAt(0);
            undo.undone.Clear();
        }

        static bool SameSet(List<Material> a, List<Material> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static List<MatSnapshot> CaptureAll(List<Material> mats)
        {
            var l = new List<MatSnapshot>();
            foreach (Material m in mats) l.Add(MatSnapshot.Capture(m));
            return l;
        }

        void Undo()
        {
            if (!CanEdit()) return;
            if (undo.done.Count == 0) { SetStatus("Nothing to undo."); return; }
            UndoStep s = undo.done[undo.done.Count - 1];
            undo.done.RemoveAt(undo.done.Count - 1);
            if (s.after == null) s.after = CaptureAll(s.mats);
            for (int i = 0; i < s.mats.Count; i++) if (s.mats[i] != null) s.before[i].Restore(s.mats[i]);
            undo.undone.Add(s);
            MarkDirty();
            CheckVariants();
            SetStatus("Undid " + s.key + ".");
            RefreshAll();
        }

        void Redo()
        {
            if (!CanEdit()) return;
            if (undo.undone.Count == 0) { SetStatus("Nothing to redo."); return; }
            UndoStep s = undo.undone[undo.undone.Count - 1];
            undo.undone.RemoveAt(undo.undone.Count - 1);
            for (int i = 0; i < s.mats.Count; i++) if (s.mats[i] != null && s.after != null) s.after[i].Restore(s.mats[i]);
            s.time = -10f;   // never coalesce into a redone step
            undo.done.Add(s);
            MarkDirty();
            CheckVariants();
            SetStatus("Redid " + s.key + ".");
            RefreshAll();
        }

        // ------------------------------------------------------------------ edits

        /// <summary>Selected materials that have the property (edits apply to all of them).</summary>
        List<Material> Targets(string prop)
        {
            var l = new List<Material>();
            foreach (MatEntry e in selection)
                if (e.mat != null && (prop == null || e.mat.HasProperty(prop)) && !l.Contains(e.mat)) l.Add(e.mat);
            return l;
        }

        static PropInfo PropOn(Material m, string name)
        {
            ShaderLayout L = ShaderLayout.For(m.shader);
            PropInfo p;
            return L != null && L.byName.TryGetValue(name, out p) ? p : null;
        }

        void EditNumber(string prop, float v)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit(prop, mats);
            foreach (Material m in mats)
            {
                RecipeEngine.SetNumber(m, prop, v);
                PropInfo p = PropOn(m, prop);
                if (p != null) KeywordRules.AfterNumberChanged(m, ShaderLayout.For(m.shader), p, v);
                if (p != null && (p.keyword != null || p.enumKeywords != null)) { NoteFeatureToggle(m, p); NoteToggleForBanner(m, p); }
            }
            MarkDirty();
            CheckVariants();
        }

        void EditColor(string prop, Color c)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit(prop, mats);
            foreach (Material m in mats) { m.SetColor(prop, c); KeywordRules.AfterOtherChanged(m, ShaderLayout.For(m.shader), prop); }
            MarkDirty();
            CheckVariants();
        }

        void EditVector(string prop, Vector4 v)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit(prop, mats);
            foreach (Material m in mats) m.SetVector(prop, v);
            MarkDirty();
            CheckVariants();
        }

        void EditTexture(string prop, Texture t)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit(prop + " texture", mats);
            foreach (Material m in mats) { m.SetTexture(prop, t); KeywordRules.AfterOtherChanged(m, ShaderLayout.For(m.shader), prop); }
            MarkDirty();
            CheckVariants();
        }

        void EditTextureST(string prop, Vector2 scale, Vector2 offset)
        {
            List<Material> mats = Targets(prop);
            if (mats.Count == 0) return;
            BeginEdit(prop + " tiling", mats);
            foreach (Material m in mats) { m.SetTextureScale(prop, scale); m.SetTextureOffset(prop, offset); }
            MarkDirty();
            CheckVariants();
        }

        void EditRenderQueue(int q)
        {
            List<Material> mats = Targets(null);
            if (mats.Count == 0) return;
            BeginEdit("render queue", mats);
            foreach (Material m in mats) m.renderQueue = q;
            MarkDirty();
            CheckVariants();
        }

        /// <summary>Copies a texture file into the recipe folder and assigns it.</summary>
        void AssignTextureFile(string prop, string file, bool linear)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(RecipeTexDir, "tex"));
                byte[] bytes = File.ReadAllBytes(file);
                string name = Path.GetFileNameWithoutExtension(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();
                string rel = "tex/" + RecipePaths.SafeFileName(name) + "_" + AvatarIdentity.StableHash(Convert.ToBase64String(bytes, 0, Math.Min(bytes.Length, 65536)) + bytes.Length).ToString("x8") + ext;
                string dst = Path.Combine(RecipeTexDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dst)) File.Copy(file, dst, true);
                string key = "file:" + rel;
                Texture2D t = LoadFileTexture(dst, linear, key);
                if (t == null) { SetStatus("Could not read " + Path.GetFileName(file) + " as an image (PNG or JPG)."); return; }
                EditTexture(prop, t);
                SetStatus("Texture " + Path.GetFileName(file) + " assigned to " + prop + ".");
            }
            catch (Exception e) { SetStatus("Texture load failed: " + e.Message); }
        }

        void SwapShader(Shader s)
        {
            List<Material> mats = Targets(null);
            if (s == null || mats.Count == 0) return;
            BeginEdit("shader", mats);
            ShaderLayout L = ShaderLayout.For(s);
            foreach (Material m in mats)
            {
                int q = m.renderQueue;
                bool customQueue = q != m.shader.renderQueue;
                m.shader = s;
                if (customQueue) m.renderQueue = q;
                KeywordRules.RebuildKeywords(m, L);
            }
            MarkDirty();
            catalog.Refresh(reg.Shaders());
            SetStatus("Switched " + mats.Count + " material(s) to " + s.name + ".");
            RefreshAll();
        }

        /// <summary>Swaps locked Poiyomi materials to the full shader they were locked from.</summary>
        void UnlockPoiyomi()
        {
            int done = 0, missing = 0;
            var mats = new List<Material>();
            foreach (MatEntry e in selection) if (e.mat != null && RecipeEngine.IsLockedPoiyomi(e.mat)) mats.Add(e.mat);
            if (mats.Count == 0) return;
            BeginEdit("unlock Poiyomi", mats);
            foreach (MatEntry e in selection)
            {
                if (e.mat == null || !RecipeEngine.IsLockedPoiyomi(e.mat)) continue;
                string orig = RecipeEngine.PoiyomiOriginalShader(e.mat);
                Shader full = catalog.Find(orig);
                if (full == null || full.name.StartsWith("Hidden/", StringComparison.Ordinal)) { missing++; continue; }
                int q = e.mat.renderQueue;
                e.mat.shader = full;
                e.mat.renderQueue = q;
                if (e.mat.HasProperty("_ShaderOptimizerEnabled")) e.mat.SetFloat("_ShaderOptimizerEnabled", 0f);
                KeywordRules.RebuildKeywords(e.mat, ShaderLayout.For(full));
                e.unlockedPoiyomi = true;
                done++;
            }
            MarkDirty();
            catalog.Refresh(reg.Shaders());
            CheckVariants();
            SetStatus(done > 0
                ? "Unlocked " + done + " Poiyomi material(s); every property now edits live. Export re-locks them."
                : "The full Poiyomi shader is not built yet. Add your Poiyomi .unitypackage on Packages, then Build.");
            if (missing > 0 && done > 0) Note(missing + " material(s) need a Poiyomi version that is not built yet.");
            RefreshAll();
        }

        void MakeUnique()
        {
            if (!CanEdit()) return;
            MatEntry e = Primary;
            if (e == null || e.mat == null) return;
            if (e.slots.Count < 2) { SetStatus("That material is used by only one slot already."); return; }
            // only the slots of the renderer picked in the list
            var slots = new List<SlotRef>();
            foreach (SlotRef s in e.slots) if (selectedRendererPath == null || s.path == selectedRendererPath) slots.Add(s);
            if (slots.Count == 0 || slots.Count == e.slots.Count) slots = new List<SlotRef> { e.slots[0] };
            Material copy = reg.SplitOff(e.mat, slots);
            reg.Bind(boundAvatar);
            selection.Clear();
            MatEntry ne = reg.EntryFor(copy);
            if (ne != null) selection.Add(ne);
            undo.Clear();   // the material set changed; old steps no longer line up
            MarkDirty();
            SetStatus("Made a separate copy for " + slots.Count + " slot(s). Undo history was cleared.");
            RefreshAll();
        }

        void ResetSelected()
        {
            if (!CanEdit()) return;
            var mats = new List<Material>();
            foreach (MatEntry e in selection) if (e.mat != null) mats.Add(e.mat);
            if (mats.Count == 0) return;
            BeginEdit("reset", mats);
            bool merged = false;
            foreach (MatEntry e in selection)
            {
                if (e.mat == null) continue;
                e.orig.Restore(e.mat);
                e.unlockedPoiyomi = false;
                if (e.unique) merged |= reg.Merge(e.mat);
            }
            if (merged) { reg.Bind(boundAvatar); selection.Clear(); if (reg.entries.Count > 0) selection.Add(reg.entries[0]); undo.Clear(); }
            MarkDirty();
            SetStatus("Reset " + mats.Count + " material(s) to how the model shipped.");
            RefreshAll();
        }

        void ResetAll()
        {
            if (!CanEdit()) return;
            selection.Clear();
            selection.AddRange(reg.entries);
            ResetSelected();
        }

        void CopyMaterial()
        {
            if (Primary == null || Primary.mat == null) return;
            clipboard = MatSnapshot.Capture(Primary.mat);
            SetStatus("Copied " + Primary.mat.name + ".");
        }

        void PasteMaterial()
        {
            if (!CanEdit()) return;
            if (clipboard == null) { SetStatus("Copy a material first."); return; }
            List<Material> mats = Targets(null);
            if (mats.Count == 0) return;
            BeginEdit("paste", mats);
            foreach (Material m in mats)
            {
                if (m.shader == clipboard.shader) { clipboard.Restore(m); continue; }
                // different shader: carry whatever properties both share
                foreach (var kv in clipboard.floats) RecipeEngine.SetNumber(m, kv.Key, kv.Value);
                foreach (var kv in clipboard.colors) if (m.HasProperty(kv.Key)) m.SetColor(kv.Key, kv.Value);
                foreach (var kv in clipboard.vectors) if (m.HasProperty(kv.Key)) m.SetVector(kv.Key, kv.Value);
                foreach (var kv in clipboard.textures) if (m.HasProperty(kv.Key)) m.SetTexture(kv.Key, kv.Value);
                KeywordRules.RebuildKeywords(m, ShaderLayout.For(m.shader));
            }
            MarkDirty();
            SetStatus("Pasted onto " + mats.Count + " material(s).");
            RefreshAll();
        }

        // ------------------------------------------------------------------ variants

        string variantWarning = "";

        // ------------------------------------------------------------------ config + misc

        void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    MSConfig c = JsonIO.FromJson<MSConfig>(File.ReadAllText(ConfigPath));
                    if (c != null) cfg = c;
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "config unreadable, using defaults: " + e.Message); }
            if (cfg.packages == null) cfg.packages = new List<PackageEntry>();
            if (cfg.seedFolders == null) cfg.seedFolders = new List<string>();
            if (cfg.wantedShaders == null) cfg.wantedShaders = new List<string>();
            if (cfg.avatarPaths == null) cfg.avatarPaths = new List<NamedString>();
            if (string.IsNullOrEmpty(cfg.workspace)) cfg.workspace = UnityDriver.DefaultWorkspace;
        }

        void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ShaderCatalog.Dir);
                File.WriteAllText(ConfigPath, JsonIO.ToJson(cfg));
            }
            catch (Exception e) { Debug.LogWarning(LOG + "config save failed: " + e.Message); }
        }

        void Note(string s)
        {
            notes.Add(s);
            if (notes.Count > 50) notes.RemoveAt(0);
            Debug.Log(LOG + s);
            SetStatus(s);
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// For other plugins (Weight Studio builds a .vbx straight from the .vsfavatar file):
        /// applies this avatar's saved recipe to any copy of it. Call by reflection:
        /// MaterialStudio.MaterialStudioPlugin.ApplyRecipeTo(GameObject root, string sourceFile).
        /// Returns the number of materials changed, or -1 when there is no recipe.
        /// </summary>
        public static int ApplyRecipeTo(GameObject root, string sourceFile)
        {
            if (root == null) return -1;
            try
            {
                if (Instance != null && Instance.dirty) Instance.SaveRecipe();
                string sig = MaterialRegistry.Signature(root);
                string key = AvatarIdentity.KeyFor(sourceFile, sig);
                string path = Path.Combine(RecipeDir, key + ".json");
                if (!File.Exists(path)) return -1;
                Recipe r = Recipe.FromJson(File.ReadAllText(path));
                if (r == null) return -1;
                var reg2 = new MaterialRegistry();
                reg2.Bind(root);
                Dictionary<string, Texture> avatarTex = reg2.AvatarTextures();
                ShaderCatalog cat = Instance != null ? Instance.catalog : null;
                // Freeze every source as it SHIPPED before touching anything: a cached
                // avatar's materials may already carry these edits, and a unique copy must
                // not inherit its source's edits.
                var shipped = new Dictionary<Material, MatSnapshot>();
                foreach (MatEdit e in r.materials)
                {
                    if (e == null || e.slots.Count == 0) continue;
                    Material src = reg2.MaterialAt(e.slots[0]);
                    MatEntry en = reg2.EntryFor(src);
                    if (src != null && en != null && !shipped.ContainsKey(src)) shipped[src] = en.orig;
                }
                int n = 0;
                foreach (MatEdit e in r.materials)
                {
                    if (e == null || e.slots.Count == 0) continue;
                    Material m = reg2.MaterialAt(e.slots[0]);
                    if (m == null) continue;
                    MatSnapshot snap;
                    shipped.TryGetValue(m, out snap);
                    if (e.unique)
                    {
                        m = new Material(m);
                        if (snap != null) snap.Restore(m);
                        foreach (SlotRef s in e.slots)
                        {
                            Transform t = RecipePaths.Resolve(root.transform, s.path);
                            Renderer rr = t != null ? t.GetComponent<Renderer>() : null;
                            if (rr == null) continue;
                            Material[] ms = rr.sharedMaterials;
                            if (s.slot >= 0 && s.slot < ms.Length) { ms[s.slot] = m; rr.sharedMaterials = ms; }
                        }
                    }
                    else if (snap != null) snap.Restore(m);
                    string texDir = Path.Combine(RecipeDir, key);
                    RecipeEngine.Apply(m, e,
                        delegate (string sn) { return cat != null ? cat.Find(sn) : Shader.Find(sn); },
                        delegate (TexEdit te)
                        {
                            if (te.src.StartsWith("avatar:", StringComparison.Ordinal)) { Texture t; return avatarTex.TryGetValue(te.src.Substring(7), out t) ? t : null; }
                            if (te.src.StartsWith("file:", StringComparison.Ordinal) && Instance != null)
                                return Instance.LoadFileTexture(Path.Combine(texDir, te.src.Substring(5)), te.linear, te.src);
                            return null;
                        }, null);
                    n++;
                }
                return n;
            }
            catch (Exception e) { Debug.LogWarning(LOG + "ApplyRecipeTo failed: " + e.Message); return -1; }
        }

        /// <summary>
        /// For other exporters (Warudo Importer): where the saved edits for a model loaded
        /// from <paramref name="sourceFile"/> live, or "" when there are none. Saves pending
        /// edits first so the file is current.
        /// </summary>
        public static string RecipePathFor(string sourceFile)
        {
            if (Instance != null && Instance.dirty) Instance.SaveRecipe();
            string key = AvatarIdentity.KeyFor(sourceFile, "");
            string p = Path.Combine(RecipeDir, key + ".json");
            return File.Exists(p) ? p : "";
        }

        /// <summary>Path of the recipe for the avatar on screen ("" when nothing is bound).</summary>
        public static string CurrentRecipePath()
        {
            if (Instance == null || string.IsNullOrEmpty(Instance.avatarKey)) return "";
            if (Instance.dirty) Instance.SaveRecipe();
            return Instance.RecipePath;
        }
    }
}
