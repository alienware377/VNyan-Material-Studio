"""Headless end-to-end test of the editor toolset, without VNyan.

Mirrors what the plugin does: seed a private workspace from the VSF SDK project,
install the toolset, compile a shader bundle, then export a .vsfavatar with a small
recipe (one colour change) and read the result back.

  python build/headless_test.py seed      copy SDK folders into the test workspace
  python build/headless_test.py shaders   catalog + compile Poiyomi into a bundle
  python build/headless_test.py export    export the test avatar with a test recipe
  python build/headless_test.py verify    load the exported bundle back and check the colour
  python build/headless_test.py lock      lock one Poiyomi material into a bundle

Machine-specific inputs come from environment variables (nothing personal is hard-coded):
  MS_SEED_PROJECT   your VSF SDK Unity project (seed copies its ProjectSettings, Packages and SDK folders)
  MS_TEST_AVATAR    a .vsfavatar to test with (seed copies it into the test folder)
  MS_REAL_JOB       export_real only: an export job folder the plugin wrote (job.json + live.vbx)
  MS_UNITY          optional: Unity.exe of 2019.4.31f1 (default: the Unity Hub install path)
"""
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
UNITY = os.environ.get("MS_UNITY", r"C:\Program Files\Unity\Hub\Editor\2019.4.31f1\Editor\Unity.exe")
WS = r"C:\MaterialStudioBuild\ws_test"
T = r"C:\MaterialStudioBuild\test"
AVATAR = os.path.join(T, "test_avatar.vsfavatar")
FOLDERS = ["VRM", "VRMShaders", "UniGLTF", "VSF SDK", "DynamicBone", "_PoiyomiShaders"]


def env_path(name, what):
    v = os.environ.get(name, "")
    if not v:
        sys.exit("Set %s to %s." % (name, what))
    return v


def copytree(a, b):
    shutil.copytree(a, b, dirs_exist_ok=True)


def seed():
    SEED = env_path("MS_SEED_PROJECT", "your VSF SDK Unity project folder")
    os.makedirs(WS, exist_ok=True)
    copytree(os.path.join(SEED, "ProjectSettings"), os.path.join(WS, "ProjectSettings"))
    os.makedirs(os.path.join(WS, "Packages"), exist_ok=True)
    for f in ("manifest.json", "packages-lock.json"):
        p = os.path.join(SEED, "Packages", f)
        if os.path.exists(p):
            shutil.copyfile(p, os.path.join(WS, "Packages", f))
    for f in FOLDERS:
        src = os.path.join(SEED, "Assets", f)
        if os.path.isdir(src):
            copytree(src, os.path.join(WS, "Assets", f))
            if os.path.exists(src + ".meta"):
                shutil.copyfile(src + ".meta", os.path.join(WS, "Assets", f + ".meta"))
    os.makedirs(T, exist_ok=True)
    if not os.path.exists(AVATAR):
        shutil.copyfile(env_path("MS_TEST_AVATAR", "a .vsfavatar file to test with"), AVATAR)
    print("seeded", WS)


def install_toolset():
    src = os.path.join(ROOT, "dist", "MaterialStudio", "UnityToolset", "MaterialStudioTools")
    copytree(src, os.path.join(WS, "Assets", "MaterialStudioTools"))


def run_job(job, name):
    install_toolset()
    os.makedirs(T, exist_ok=True)
    jp = os.path.join(T, name + "_job.json")
    job["resultPath"] = os.path.join(T, name + "_result.json")
    if os.path.exists(job["resultPath"]):
        os.remove(job["resultPath"])
    with open(jp, "w") as f:
        json.dump(job, f, indent=1)
    log = os.path.join(T, name + "_unity.log")
    t0 = time.time()
    r = subprocess.run([UNITY, "-batchmode", "-quit", "-projectPath", WS, "-executeMethod",
                        "MaterialStudio.EditorTools.MSBatch.Run", "-msjob", jp, "-logFile", log])
    print(name, "exit", r.returncode, "in %.0f s" % (time.time() - t0))
    if os.path.exists(job["resultPath"]):
        print(open(job["resultPath"]).read())
    else:
        print("no result; see", log)


def shaders():
    job = {
        "kind": "catalog shaders describe",
        "sourcePath": AVATAR,
        "catalogPath": os.path.join(T, "catalog.json"),
        "bundleName": "ms_shaders_test",
        "outDir": os.path.join(T, "bundles"),
        "shaders": [{"name": ".poiyomi/Poiyomi 8.1/Poiyomi Toon", "variants": [],
                     "materials": [{"f": [{"n": "_MainColorAdjustToggle", "v": 1.0}]}]}],
    }
    run_job(job, "shaders")
    res = json.load(open(os.path.join(T, "shaders_result.json")))
    rows = [n.split("|") for n in res["notes"] if n.count("|") >= 5]
    # prefer a locked Poiyomi material: exercises unlock + recipe + relock
    pick = next((r for r in rows if r[5]), None) or next((r for r in rows if r[6] != "-"), None)
    if pick:
        json.dump({"path": pick[0], "slot": int(pick[1])}, open(os.path.join(T, "slot.json"), "w"))
        print("test slot:", pick[:4])


def export():
    recipe_dir = os.path.join(T, "recipe")
    os.makedirs(recipe_dir, exist_ok=True)
    recipe_path = os.path.join(T, "test_recipe.json")
    with open(os.path.join(T, "slot.json")) as f:
        slot = json.load(f)
    recipe = {"version": 1, "avatarKey": "test", "materials": [{
        "id": slot["path"] + "|" + str(slot["slot"]), "slots": [slot],
        "colors": [{"n": "_Color", "v": {"r": 1.0, "g": 0.2, "b": 0.2, "a": 1.0}}],
    }]}
    with open(recipe_path, "w") as f:
        json.dump(recipe, f)
    job = {
        "kind": "export", "sourceKind": "vsfavatar", "sourcePath": AVATAR,
        "recipePath": recipe_path, "recipeDir": recipe_dir, "liveVbxPath": "",
        "outPath": os.path.join(T, "out", "test_avatar_edited.vsfavatar"),
        "relockPoiyomi": True, "applyLiveWeights": False,
    }
    run_job(job, "export")


def export_real():
    """Replays an export job the plugin wrote (its recipe + captured live weights); read-only on those files."""
    real_job = env_path("MS_REAL_JOB", "an export job folder from VNyan's MaterialStudio Jobs folder")
    src = json.load(open(os.path.join(real_job, "job.json")))
    live = os.path.join(T, "real_live.vbx")
    shutil.copyfile(os.path.join(real_job, "live.vbx"), live)
    job = {
        "kind": "export", "sourceKind": "vsfavatar", "sourcePath": AVATAR,
        "recipePath": src["recipePath"], "recipeDir": src["recipeDir"], "liveVbxPath": live,
        "outPath": os.path.join(T, "out", "test_avatar_real.vsfavatar"),
        "relockPoiyomi": True, "applyLiveWeights": "--weights" in sys.argv,
    }
    run_job(job, "export_real")


def verify():
    out = os.path.join(T, "out", "test_avatar_edited.vsfavatar")
    run_job({"kind": "describe", "sourcePath": out}, "verify")


def lock():
    req = {"id": "j Body|0", "shader": ".poiyomi/Poiyomi 8.0/Poiyomi Toon", "renderQueue": 2000,
           "keywords": [], "floats": [{"n": "_Cutoff", "v": 0.5}],
           "colors": [{"n": "_Color", "v": {"r": 1.0, "g": 0.4, "b": 0.4, "a": 1.0}}], "vectors": [],
           "textures": [{"n": "_MainTex", "src": "has", "stSet": True, "scale": {"x": 1, "y": 1}, "offset": {"x": 0, "y": 0}}],
           "tags": [{"n": "_ColorAnimated", "v": "1"}]}
    run_job({"kind": "lock", "bundleName": "ms_locked_test", "outDir": os.path.join(T, "locks"), "locks": [req]}, "lock")
    m = os.path.join(T, "locks", "ms_locked_test.json")
    print(open(m).read() if os.path.exists(m) else "no manifest")


if __name__ == "__main__":
    {"seed": seed, "shaders": shaders, "export": export, "export_real": export_real, "verify": verify, "lock": lock}[sys.argv[1]]()
