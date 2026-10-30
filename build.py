"""Builds Material Studio end to end and stages dist/.

  1. compile MaterialStudio.dll (Unity's Roslyn, against VNyan's own managed DLLs)
  2. compile-check the Unity editor toolset against Unity 2019.4.31f1 (VSF SDK editor)
  3. build MaterialStudio.vnobj with Unity 2022.3.22f1 in batch mode
  4. stage dist/MaterialStudio/: DLL, .vnobj, UnityToolset/ (editor scripts the plugin copies
     into Unity projects). make_release_zip.py packs that folder with the installer.

Everything is built in C:/MaterialStudioBuild, a plain local folder: synced or cloud folders
have served stale files before, and Unity's Library folder does not belong in one.

The tool paths below (CSC, UNITY, MANAGED) and the references in build/build.rsp point at
the build machine's Unity editors and VNyan folder; change them to match yours.

  python build.py            full build
  python build.py --dll      DLL + toolset check only (no Unity bundle step)
"""
import hashlib
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
STAGE = r"C:\MaterialStudioBuild"
UNITY_PROJ = os.path.join(STAGE, "unity")
CSC = r"C:\Program Files\Unity\Hub\Editor\2019.4.16f1\Editor\Data\Tools\Roslyn\csc.exe"
UNITY = r"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"
MANAGED = r"C:\Program Files\VNyan\VNyan_Data\Managed"
DIST = os.path.join(HERE, "dist")


def md5(p):
    h = hashlib.md5()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run(args, cwd=None):
    print(">", " ".join('"%s"' % a if " " in a else a for a in args))
    r = subprocess.run(args, cwd=cwd)
    if r.returncode != 0:
        sys.exit("FAILED (%d)" % r.returncode)


def sync_shared():
    """The toolset compiles the same shared files as the plugin; keep them identical."""
    src = os.path.join(HERE, "Scripts", "Shared")
    dst = os.path.join(HERE, "UnityToolset", "MaterialStudioTools", "Shared")
    os.makedirs(dst, exist_ok=True)
    for f in os.listdir(src):
        if f.endswith(".cs"):
            shutil.copyfile(os.path.join(src, f), os.path.join(dst, f))


def main():
    dll_only = "--dll" in sys.argv
    os.makedirs(STAGE, exist_ok=True)
    sync_shared()

    print("[1] compiling MaterialStudio.dll")
    dll = os.path.join(STAGE, "MaterialStudio.dll")
    if os.path.exists(dll):
        os.remove(dll)
    run([CSC, "-noconfig", "@build\\build.rsp", "-out:" + dll], cwd=HERE)

    print("[2] compile-checking the editor toolset against Unity 2019.4")
    run([sys.executable, os.path.join(HERE, "build", "make_toolset_rsp.py")])
    run([CSC, "-noconfig", "@build\\toolset_check.rsp"], cwd=HERE)

    vnobj = None
    if not dll_only:
        print("[3] building MaterialStudio.vnobj (Unity 2022.3.22f1)")
        for sub in ("ProjectSettings", "Packages"):
            shutil.copytree(os.path.join(HERE, "_unitybuild", sub), os.path.join(UNITY_PROJ, sub), dirs_exist_ok=True)
        ed = os.path.join(UNITY_PROJ, "Assets", "Editor")
        plug = os.path.join(UNITY_PROJ, "Assets", "Plugins")
        os.makedirs(ed, exist_ok=True)
        os.makedirs(plug, exist_ok=True)
        shutil.copyfile(os.path.join(HERE, "_unitybuild", "Assets", "Editor", "MaterialStudioBuild.cs"),
                        os.path.join(ed, "MaterialStudioBuild.cs"))
        shd = os.path.join(UNITY_PROJ, "Assets", "Shaders")
        os.makedirs(shd, exist_ok=True)
        for f in os.listdir(os.path.join(HERE, "_unitybuild", "Assets", "Shaders")):
            if f.endswith(".shader"):
                shutil.copyfile(os.path.join(HERE, "_unitybuild", "Assets", "Shaders", f), os.path.join(shd, f))
        probe = os.path.join(ed, "RecipeProbe.cs")
        if os.path.exists(probe):
            os.remove(probe)
        shutil.copyfile(dll, os.path.join(plug, "MaterialStudio.dll"))
        shutil.copyfile(os.path.join(MANAGED, "VNyanInterface.dll"), os.path.join(plug, "VNyanInterface.dll"))
        assert md5(dll) == md5(os.path.join(plug, "MaterialStudio.dll")), "staged DLL mismatch"
        out = os.path.join(UNITY_PROJ, "AssetBundles", "MaterialStudio.vnobj")
        if os.path.exists(out):
            os.remove(out)
        log = os.path.join(STAGE, "unity_build.log")
        run([UNITY, "-batchmode", "-quit", "-nographics", "-projectPath", UNITY_PROJ,
             "-executeMethod", "MaterialStudioBuild.Build", "-logFile", log])
        if not os.path.exists(out):
            sys.exit("FAILED: no .vnobj, see " + log)
        vnobj = out

    print("[4] staging dist/MaterialStudio/ (make_release_zip.py adds the installer)")
    pay = os.path.join(DIST, "MaterialStudio")
    os.makedirs(pay, exist_ok=True)
    shutil.copyfile(dll, os.path.join(pay, "MaterialStudio.dll"))
    if vnobj:
        shutil.copyfile(vnobj, os.path.join(pay, "MaterialStudio.vnobj"))
    tdst = os.path.join(pay, "UnityToolset", "MaterialStudioTools")
    if os.path.exists(tdst):
        shutil.rmtree(tdst)
    shutil.copytree(os.path.join(HERE, "UnityToolset", "MaterialStudioTools"), tdst)
    # old layouts from earlier builds: flat payload, and installer copies next to it
    for f in ("MaterialStudio.dll", "MaterialStudio.vnobj", "install.bat", "install.ps1", "README.md"):
        if os.path.exists(os.path.join(DIST, f)):
            os.remove(os.path.join(DIST, f))
    if os.path.exists(os.path.join(DIST, "UnityToolset")):
        shutil.rmtree(os.path.join(DIST, "UnityToolset"))
    for f in sorted(os.listdir(pay)):
        p = os.path.join(pay, f)
        if os.path.isfile(p):
            print("   %-28s %10d  %s" % (f, os.path.getsize(p), md5(p)))
    print("done")


if __name__ == "__main__":
    main()
