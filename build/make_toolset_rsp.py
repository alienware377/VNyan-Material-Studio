"""Writes build/toolset_check.rsp: compiles the editor toolset against Unity 2019.4.31f1
(the VSF SDK editor) so API mistakes show up before Unity ever runs it."""
import glob
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
U = r"C:\Program Files\Unity\Hub\Editor\2019.4.31f1\Editor\Data"

lines = [
    "-target:library", "-nologo", "-nostdlib+", "-langversion:7.3", "-nowarn:0649,0414,0618",
    r"-out:C:\MaterialStudioBuild\ToolsetCheck.dll",
]
for r in [r"MonoBleedingEdge\lib\mono\4.7.1-api\mscorlib.dll",
          r"MonoBleedingEdge\lib\mono\4.7.1-api\System.dll",
          r"MonoBleedingEdge\lib\mono\4.7.1-api\System.Core.dll",
          r"MonoBleedingEdge\lib\mono\4.7.1-api\Facades\netstandard.dll",
          r"Managed\UnityEditor.dll", r"Managed\UnityEngine\UnityEngine.dll"]:
    lines.append('-reference:"%s"' % os.path.join(U, r))
for f in sorted(glob.glob(os.path.join(U, "Managed", "UnityEngine", "*Module.dll"))):
    lines.append('-reference:"%s"' % f)
tool = os.path.join(ROOT, "UnityToolset", "MaterialStudioTools")
for sub in ("Shared", "Editor"):
    for f in sorted(glob.glob(os.path.join(tool, sub, "*.cs"))):
        lines.append('"%s"' % f)
with open(os.path.join(HERE, "toolset_check.rsp"), "w") as fh:
    fh.write("\n".join(lines) + "\n")
print(len(lines), "lines")
