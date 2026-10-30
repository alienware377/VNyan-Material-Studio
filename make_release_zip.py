"""Packs the release zip for Material Studio.

Layout inside the zip (forward-slash entry names):
  MaterialStudio/MaterialStudio.dll
  MaterialStudio/MaterialStudio.vnobj
  MaterialStudio/UnityToolset/...      (Unity editor scripts the plugin copies into Unity projects)
  install.bat, install.ps1, README.md, LICENSE

The payload comes from dist/MaterialStudio/ (staged by build.py). The version is read from
VERSION in Scripts/MaterialStudioPlugin.cs.

  python make_release_zip.py                 writes C:/PluginUpdateBuild/release/VNyan-Material-Studio-<version>.zip
  python make_release_zip.py --out <folder>  writes it somewhere else
"""
import hashlib
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_NAME = "VNyan-Material-Studio"
PLUGIN = "MaterialStudio"
PAYLOAD = os.path.join(HERE, "dist", PLUGIN)
TOP_FILES = ["install.bat", "install.ps1", "README.md", "LICENSE"]
DEFAULT_OUT = r"C:\PluginUpdateBuild\release"


def md5(p):
    h = hashlib.md5()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def version():
    src = open(os.path.join(HERE, "Scripts", "MaterialStudioPlugin.cs"), encoding="utf-8-sig").read()
    m = re.search(r'const\s+string\s+VERSION\s*=\s*"([^"]+)"', src)
    if not m:
        sys.exit("VERSION not found in Scripts/MaterialStudioPlugin.cs")
    v = m.group(1)
    return v if v.startswith("v") else "v" + v


def main():
    out_dir = DEFAULT_OUT
    if "--out" in sys.argv:
        out_dir = sys.argv[sys.argv.index("--out") + 1]
    for f in (PLUGIN + ".dll", PLUGIN + ".vnobj"):
        if not os.path.isfile(os.path.join(PAYLOAD, f)):
            sys.exit("missing payload file: dist/%s/%s (run build.py first)" % (PLUGIN, f))
    for f in TOP_FILES:
        if not os.path.isfile(os.path.join(HERE, f)):
            sys.exit("missing " + f)

    entries = []   # (zip name, source path)
    for dirpath, dirnames, filenames in os.walk(PAYLOAD):
        dirnames.sort()
        for name in sorted(filenames):
            src = os.path.join(dirpath, name)
            rel = os.path.relpath(src, PAYLOAD).replace(os.sep, "/")
            entries.append((PLUGIN + "/" + rel, src))
    for f in TOP_FILES:
        entries.append((f, os.path.join(HERE, f)))

    os.makedirs(out_dir, exist_ok=True)
    zip_path = os.path.join(out_dir, "%s-%s.zip" % (REPO_NAME, version()))
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, src in entries:
            assert "\\" not in name, name
            z.write(src, name)

    with zipfile.ZipFile(zip_path) as z:
        bad = z.testzip()
        if bad:
            sys.exit("corrupt entry in zip: " + bad)
        names = z.namelist()
    print(zip_path)
    for name, src in entries:
        print("  %-70s %9d  %s" % (name, os.path.getsize(src), md5(src)))
    print("%d entries, %d bytes" % (len(names), os.path.getsize(zip_path)))


if __name__ == "__main__":
    main()
