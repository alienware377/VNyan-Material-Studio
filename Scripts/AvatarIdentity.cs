using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MaterialStudio
{
    /// <summary>
    /// Which file is the avatar on screen? VNyan names every loaded avatar
    /// "VSFAvatarTemporary(Clone)", so the name is useless. AvatarCache keeps a
    /// path -> template dictionary (class and getInstance() unmangled; the field is found by
    /// type); the on-screen avatar is a clone of one entry, matched by mesh signature.
    /// Fallback: VNyan's own "Loading colliders for avatar ..." log line.
    /// </summary>
    public static class AvatarIdentity
    {
        public static string SourcePath(GameObject avatar)
        {
            if (avatar == null) return "";
            try
            {
                string p = FromCache(avatar);
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try { return FromLog(); } catch { return ""; }
        }

        static string FromCache(GameObject avatar)
        {
            Type t = Type.GetType("AvatarCache, Assembly-CSharp");
            if (t == null) return "";
            MethodInfo gi = t.GetMethod("getInstance", BindingFlags.Public | BindingFlags.Static);
            object inst = gi != null ? gi.Invoke(null, null) : null;
            if (inst == null) return "";
            Dictionary<string, GameObject> map = null;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                map = f.GetValue(inst) as Dictionary<string, GameObject>;
                if (map != null) break;
            }
            if (map == null || map.Count == 0) return "";

            HashSet<string> mine = Sig(avatar);
            string best = ""; int bestScore = 0, live = 0;
            foreach (KeyValuePair<string, GameObject> kv in map)
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Key)) continue;
                live++;
                HashSet<string> theirs = Sig(kv.Value);
                int score = 0;
                foreach (string m in mine) if (theirs.Contains(m)) score++;
                if (score > bestScore) { bestScore = score; best = kv.Key; }
                else if (live == 1 && best.Length == 0) best = kv.Key;
            }
            if (bestScore == 0 && live != 1) return "";
            return best;
        }

        static HashSet<string> Sig(GameObject go)
        {
            var set = new HashSet<string>();
            foreach (SkinnedMeshRenderer r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh m = r != null ? r.sharedMesh : null;
                if (m != null) set.Add(m.name + "#" + m.vertexCount);
            }
            // Soft-body plugins swap meshes for per-frame copies; materials and renderer names
            // stay, so they keep the match working.
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                set.Add("r:" + r.name);
                foreach (Material mt in r.sharedMaterials) if (mt != null) set.Add("m:" + mt.name);
            }
            return set;
        }

        static string FromLog()
        {
            string path = Path.Combine(Application.persistentDataPath, "Player.log");
            if (!File.Exists(path)) return "";
            const string tag = "Loading colliders for avatar ";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long window = 1L << 20;
                while (true)
                {
                    long start = Math.Max(0, fs.Length - window);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int got = 0;
                    while (got < buf.Length) { int k = fs.Read(buf, got, buf.Length - got); if (k <= 0) break; got += k; }
                    string text = System.Text.Encoding.UTF8.GetString(buf, 0, got);
                    int at = text.LastIndexOf(tag, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int end = text.IndexOf('\n', at);
                        string line = (end < 0 ? text.Substring(at) : text.Substring(at, end - at)).Trim();
                        return line.Substring(tag.Length).Trim();
                    }
                    if (start == 0 || window >= (64L << 20)) return "";
                    window *= 4;
                }
            }
        }

        /// <summary>Recipe key: the source file name when known, else a hash of the mesh signature.</summary>
        public static string KeyFor(string sourcePath, string signature)
        {
            string file = string.IsNullOrEmpty(sourcePath) ? "" : Path.GetFileNameWithoutExtension(sourcePath);
            if (file.Length > 0 && file.IndexOf("VSFAvatarTemporary", StringComparison.OrdinalIgnoreCase) < 0)
                return RecipePaths.SafeFileName(file);
            return "avatar_" + StableHash(signature ?? "").ToString("x8");
        }

        public static uint StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return h;
            }
        }
    }
}
