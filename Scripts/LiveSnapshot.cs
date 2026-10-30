using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using VNyanWeightStudio;

namespace MaterialStudio
{
    /// <summary>
    /// Writes the avatar's LIVE skinning (as VNyan currently renders it) to a .vbx so the
    /// editor-side exporter can carry weight edits made by other plugins (Weight Studio
    /// paints straight onto the mesh) into the new .vsfavatar. Uses the shared VBX format
    /// so &gt;4 influences per vertex survive. Geometry is included only for vertex matching;
    /// materials are left out (the recipe carries those).
    /// </summary>
    public static class LiveSnapshot
    {
        public static int Write(string path, GameObject avatar, List<string> notes)
        {
            var doc = new VbxDoc();
            doc.avatarName = avatar.name;
            var index = new Dictionary<Transform, int>();
            var order = new List<Transform>();
            Collect(avatar.transform, index, order);
            foreach (Transform t in order)
            {
                doc.bones.Add(new VbxBone
                {
                    name = t.name,
                    parent = (t.parent != null && index.ContainsKey(t.parent)) ? index[t.parent] : -1,
                    localPos = t.localPosition, localRot = t.localRotation, localScale = t.localScale
                });
            }

            int written = 0;
            var redone = new List<string>();
            foreach (SkinnedMeshRenderer smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr != null ? smr.sharedMesh : null;
                if (mesh == null) continue;
                // Only Weight Studio's paint is a real edit. Bone Weight Smoother and JigglyPhys
                // rework weights on every load; baking their result made them run twice on the
                // export (measured: 7 hair meshes re-smoothed). Their copies carry a name suffix.
                string mn = mesh.name ?? "";
                bool painted = mn.Contains("(WeightStudio)");
                bool auto = mn.Contains("(smoothed weights)") || mn.Contains("(JigglyPhys)");
                if (!painted) { if (auto) redone.Add(smr.name); continue; }
                if (auto) notes.Add(smr.name + ": Weight Studio paint sits on top of auto-smoothed weights, so the smoothing goes into the file too.");
                if (!mesh.isReadable) { notes.Add("Mesh " + mesh.name + " is not readable; its live weights were skipped."); continue; }
                var vm = new VbxMesh();
                // Renderer PATH (not just the name) so the editor can match duplicates.
                vm.name = RecipePaths.PathOf(smr.transform, avatar.transform);
                Transform[] bones = smr.bones;
                vm.boneMap = new int[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                {
                    int bi;
                    vm.boneMap[b] = bones[b] != null && index.TryGetValue(bones[b], out bi) ? bi : -1;
                }
                int rb;
                vm.rootBone = smr.rootBone != null && index.TryGetValue(smr.rootBone, out rb) ? rb : -1;
                vm.positions = mesh.vertices;
                vm.normals = new Vector3[0];
                vm.tangents = new Vector4[0];
                vm.uv = new Vector2[0];
                vm.uv2 = new Vector2[0];
                vm.bindposes = mesh.bindposes;

                NativeArray<byte> bpv = mesh.GetBonesPerVertex();
                NativeArray<BoneWeight1> w = mesh.GetAllBoneWeights();
                vm.bonesPerVertex = bpv.ToArray();
                vm.influenceBone = new int[w.Length];
                vm.influenceWeight = new float[w.Length];
                for (int i = 0; i < w.Length; i++) { vm.influenceBone[i] = w[i].boneIndex; vm.influenceWeight[i] = w[i].weight; }
                vm.submeshes = new int[0][];
                doc.meshes.Add(vm);
                written++;
            }

            if (redone.Count > 0)
                notes.Add("Left " + redone.Count + " mesh(es) as in the file, because a weight plugin redoes them on every load: " + string.Join(", ", redone.ToArray()) + ".");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                VbxIO.Write(fs, doc);
            return written;
        }

        static void Collect(Transform t, Dictionary<Transform, int> index, List<Transform> order)
        {
            index[t] = order.Count;
            order.Add(t);
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), index, order);
        }
    }
}
