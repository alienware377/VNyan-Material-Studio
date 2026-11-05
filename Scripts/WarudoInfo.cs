using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MaterialStudio
{
    /// <summary>
    /// The few facts about a .warudo file the Export tab needs before anything is unpacked: the
    /// Unity version that built it (which decides the editor) and the mod's name. Only the ZIP's
    /// directory and the small modinfo.dat are read, so this is quick even for a 900 MB mod.
    /// </summary>
    public class WarudoInfo
    {
        public string unityVersion = "";
        public string modName = "";
        public string author = "";
        public string error = "";

        static WarudoInfo last;
        static string lastKey = "";

        /// <summary>Reads (and remembers) the info of one file; never throws.</summary>
        public static WarudoInfo Read(string path)
        {
            var w = new WarudoInfo();
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) { w.error = "file not found"; return w; }
                string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                if (key == lastKey && last != null) return last;
                using (FileStream fs = File.OpenRead(path))
                {
                    var head = new byte[4];
                    if (fs.Read(head, 0, 4) != 4 || head[0] != 'U' || head[1] != 'M' || head[2] != 'O' || head[3] != 'D')
                    { w.error = "not a Warudo mod file"; return w; }
                    var zip = new ZipArchive(new Skip(fs, 12), ZipArchiveMode.Read, true);
                    ZipArchiveEntry info = null;
                    foreach (ZipArchiveEntry e in zip.Entries)
                        if (string.Equals(e.Name, "modinfo.dat", StringComparison.OrdinalIgnoreCase)) { info = e; break; }
                    if (info == null) { w.error = "no modinfo.dat inside"; return w; }
                    using (Stream s = info.Open())
                    using (var br = new BinaryReader(s, Encoding.UTF8))
                    {
                        br.ReadBytes(4);      // "UMOD"
                        br.ReadInt64();       // build time
                        br.ReadInt32(); br.ReadInt32(); br.ReadInt32();
                        br.ReadString();      // uMod version
                        w.unityVersion = br.ReadString();
                        br.ReadString();      // SDK version
                        w.modName = br.ReadString();
                        br.ReadString();      // mod version
                        br.ReadString();      // description
                        w.author = br.ReadString();
                    }
                }
                last = w;
                lastKey = key;
            }
            catch (Exception e) { w.error = e.Message; }
            return w;
        }

        /// <summary>The ZIP starts after uMod's 12-byte header; ZipArchive wants it at position 0.</summary>
        sealed class Skip : Stream
        {
            readonly Stream inner;
            readonly long origin;
            public Skip(Stream inner, long origin) { this.inner = inner; this.origin = origin; inner.Position = origin; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length - origin; } }
            public override long Position { get { return inner.Position - origin; } set { inner.Position = value + origin; } }
            public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }
            public override long Seek(long offset, SeekOrigin loc)
            {
                if (loc == SeekOrigin.Begin) return inner.Seek(origin + offset, SeekOrigin.Begin) - origin;
                return inner.Seek(offset, loc) - origin;
            }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { }
        }

        // ------------------------------------------------------------------ Unity versions

        /// <summary>Compares Unity versions like "2021.3.45f2"; negative when a is older than b.</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] pa = Parts(a), pb = Parts(b);
            for (int i = 0; i < pa.Length; i++)
                if (pa[i] != pb[i]) return pa[i] < pb[i] ? -1 : 1;
            return 0;
        }

        /// <summary>Same year and minor (2022.3.x and 2022.3.y).</summary>
        public static bool SameLine(string a, string b)
        {
            int[] pa = Parts(a), pb = Parts(b);
            return pa[0] == pb[0] && pa[1] == pb[1];
        }

        static int[] Parts(string v)
        {
            // 2021.3.45f2 -> 2021, 3, 45, rank of 'f', 2
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
