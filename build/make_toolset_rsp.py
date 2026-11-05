"""Writes build/toolset_check.rsp and build/toolset_check_2022.rsp: compiles the editor toolset
against Unity 2019.4.31f1 (the VSF SDK editor) and against Unity 2022.3.22f1 (the newer editor
.warudo exports run on), so API mistakes show up before Unity ever runs it."""
import glob
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
HUB = r"C:\Program Files\Unity\Hub\Editor"

FLAGS = ["-target:library", "-nologo", "-nostdlib+", "-langversion:7.3", "-nowarn:0649,0414,0618"]
TARGETS = [
    ("toolset_check.rsp", "2019.4.31f1", "ToolsetCheck.dll",
     [r"MonoBleedingEdge\lib\mono\4.7.1-api\mscorlib.dll",
      r"MonoBleedingEdge\lib\mono\4.7.1-api\System.dll",
      r"MonoBleedingEdge\lib\mono\4.7.1-api\System.Core.dll",
      r"MonoBleedingEdge\lib\mono\4.7.1-api\Facades\netstandard.dll"]),
    ("toolset_check_2022.rsp", "2022.3.22f1", "ToolsetCheck2022.dll",
     [r"NetStandard\ref\2.1.0\netstandard.dll"]),
]

tool = os.path.join(ROOT, "UnityToolset", "MaterialStudioTools")
sources = []
for sub in ("Shared", "Editor"):
    sources += sorted(glob.glob(os.path.join(tool, sub, "**", "*.cs"), recursive=True))

for rsp, ver, out, base in TARGETS:
    u = os.path.join(HUB, ver, "Editor", "Data")
    lines = list(FLAGS) + [r"-out:C:\dev\MaterialStudioBuild" + "\\" + out]
    # 2022 splits UnityEditor into modules too; its UnityEditor.dll is only a facade over them
    modular = os.path.exists(os.path.join(u, "Managed", "UnityEngine", "UnityEditor.CoreModule.dll"))
    for r in base + ([] if modular else [r"Managed\UnityEditor.dll", r"Managed\UnityEngine\UnityEngine.dll"]):
        lines.append('-reference:"%s"' % os.path.join(u, r))
    for f in sorted(glob.glob(os.path.join(u, "Managed", "UnityEngine", "*Module.dll"))):
        lines.append('-reference:"%s"' % f)
    lines += ['"%s"' % f for f in sources]
    with open(os.path.join(HERE, rsp), "w") as fh:
        fh.write("\n".join(lines) + "\n")
    print(rsp, len(lines), "lines")
