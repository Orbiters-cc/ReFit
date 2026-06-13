using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// An immutable world-space snapshot of a skinned mesh in its current staged pose.
    /// Skinning is evaluated manually (linear blend skinning from bones + bindposes) so the exact per-vertex
    /// skinning matrix is available — required to convert world-space deltas back into blendshape space.
    /// Also precomputes welding groups (co-located vertices split by UV/normal seams) and group adjacency.
    /// </summary>
    public class MeshSnapshot
    {
        public SkinnedMeshRenderer renderer;
        public Mesh mesh;
        /// <summary>Mesh-local vertex positions with the requested blendshape weights applied (pre-skinning).</summary>
        public Vector3[] localVertices;
        /// <summary>Skinned world-space vertex positions.</summary>
        public Vector3[] worldVertices;
        /// <summary>Skinned world-space vertex normals (normalized).</summary>
        public Vector3[] worldNormals;
        /// <summary>Per-vertex world skinning matrix M(v): world = M(v) * local.</summary>
        public Matrix4x4[] skinMatrices;
        /// <summary>All submesh triangles concatenated.</summary>
        public int[] triangles;
        public BoneWeight[] boneWeights;
        public Transform[] bones;
        public Matrix4x4 rendererLocalToWorld;
        public Matrix4x4 rendererWorldToLocal;
        /// <summary>True when the mesh had no usable skinning data and was treated as rigid.</summary>
        public bool rigid;

        // Welding groups (built when requested)
        /// <summary>Group id per vertex.</summary>
        public int[] groupOfVertex;
        /// <summary>One representative vertex index per group.</summary>
        public int[] groupRep;
        /// <summary>All vertex indices of each group.</summary>
        public List<int>[] groupMembers;
        /// <summary>Neighbor group ids of each group (via triangle edges).</summary>
        public List<int>[] groupAdjacency;
        public int GroupCount => groupRep != null ? groupRep.Length : 0;

        /// <summary>
        /// Captures a snapshot of <paramref name="smr"/> in its current pose.
        /// Blendshape weights default to the renderer's current weights; <paramref name="shapeWeightOverrides01"/>
        /// (shape index -> weight in 0..1) overrides individual shapes (e.g. force the transferred shape to 0 or 1).
        /// </summary>
        public static MeshSnapshot Capture(SkinnedMeshRenderer smr, bool buildTopology,
            IDictionary<int, float> shapeWeightOverrides01, ReFitReport report)
        {
            var snap = new MeshSnapshot
            {
                renderer = smr,
                mesh = smr.sharedMesh,
                rendererLocalToWorld = smr.transform.localToWorldMatrix,
                rendererWorldToLocal = smr.transform.worldToLocalMatrix
            };
            var mesh = snap.mesh;
            int vertexCount = mesh.vertexCount;

            // --- local positions with blendshapes applied -------------------------------------
            snap.localVertices = mesh.vertices;
            var baseNormals = mesh.normals;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float w = smr.GetBlendShapeWeight(s) / 100f;
                if (shapeWeightOverrides01 != null && shapeWeightOverrides01.TryGetValue(s, out var o)) w = o;
                if (Mathf.Abs(w) < 1e-4f) continue;
                int frame = mesh.GetBlendShapeFrameCount(s) - 1;
                var dv = new Vector3[vertexCount];
                mesh.GetBlendShapeFrameVertices(s, frame, dv, null, null);
                for (int i = 0; i < vertexCount; i++) snap.localVertices[i] += dv[i] * w;
            }

            // --- per-vertex skinning matrices --------------------------------------------------
            snap.bones = smr.bones;
            snap.boneWeights = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            bool hasSkin = snap.bones != null && snap.bones.Length > 0 &&
                           bindposes != null && bindposes.Length == snap.bones.Length &&
                           snap.boneWeights != null && snap.boneWeights.Length == vertexCount;
            snap.rigid = !hasSkin;
            if (!hasSkin && report != null)
                report.Info("rigid-mesh", $"'{smr.name}' has no usable skinning data; treating it as a rigid mesh.");

            var boneMatrices = new Matrix4x4[hasSkin ? snap.bones.Length : 0];
            var boneOk = new bool[boneMatrices.Length];
            for (int k = 0; k < boneMatrices.Length; k++)
            {
                if (snap.bones[k] != null)
                {
                    boneMatrices[k] = snap.bones[k].localToWorldMatrix * bindposes[k];
                    boneOk[k] = true;
                }
            }

            snap.skinMatrices = new Matrix4x4[vertexCount];
            snap.worldVertices = new Vector3[vertexCount];
            snap.worldNormals = new Vector3[vertexCount];
            var rigidMatrix = snap.rendererLocalToWorld;
            var localVerts = snap.localVertices;
            var weights = snap.boneWeights;

            if (baseNormals == null || baseNormals.Length != vertexCount)
                baseNormals = ComputeVertexNormals(localVerts, GetAllTriangles(mesh));

            Parallel.For(0, vertexCount, i =>
            {
                Matrix4x4 m;
                if (!hasSkin)
                {
                    m = rigidMatrix;
                }
                else
                {
                    var bw = weights[i];
                    m = default;
                    float total = 0f;
                    AccumulateBone(ref m, ref total, boneMatrices, boneOk, bw.boneIndex0, bw.weight0);
                    AccumulateBone(ref m, ref total, boneMatrices, boneOk, bw.boneIndex1, bw.weight1);
                    AccumulateBone(ref m, ref total, boneMatrices, boneOk, bw.boneIndex2, bw.weight2);
                    AccumulateBone(ref m, ref total, boneMatrices, boneOk, bw.boneIndex3, bw.weight3);
                    if (total <= 1e-6f) m = rigidMatrix;
                    else if (Mathf.Abs(total - 1f) > 1e-4f) ScaleMatrix(ref m, 1f / total);
                }
                snap.skinMatrices[i] = m;
                snap.worldVertices[i] = m.MultiplyPoint3x4(localVerts[i]);
                var nm = m.inverse.transpose;
                var n = nm.MultiplyVector(baseNormals[i]);
                snap.worldNormals[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            });

            snap.triangles = GetAllTriangles(mesh);

            if (buildTopology) snap.BuildTopology();
            return snap;
        }

        private static void AccumulateBone(ref Matrix4x4 m, ref float total, Matrix4x4[] mats, bool[] ok, int index, float w)
        {
            if (w <= 0f || index < 0 || index >= mats.Length || !ok[index]) return;
            for (int c = 0; c < 16; c++) m[c] += mats[index][c] * w;
            total += w;
        }

        private static void ScaleMatrix(ref Matrix4x4 m, float f)
        {
            for (int c = 0; c < 16; c++) m[c] *= f;
        }

        private static int[] GetAllTriangles(Mesh mesh)
        {
            if (mesh.subMeshCount <= 1) return mesh.triangles;
            var all = new List<int>(mesh.triangles.Length);
            for (int s = 0; s < mesh.subMeshCount; s++) all.AddRange(mesh.GetTriangles(s));
            return all.ToArray();
        }

        private static Vector3[] ComputeVertexNormals(Vector3[] verts, int[] tris)
        {
            var normals = new Vector3[verts.Length];
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]); // area-weighted
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].sqrMagnitude > 1e-12f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        // ------------------------------------------------------------------
        // Topology: welding groups + adjacency
        // ------------------------------------------------------------------

        private void BuildTopology()
        {
            int vertexCount = localVertices.Length;
            groupOfVertex = new int[vertexCount];
            var byPosition = new Dictionary<Vector3, int>(vertexCount);
            var reps = new List<int>();
            var members = new List<List<int>>();
            for (int i = 0; i < vertexCount; i++)
            {
                if (!byPosition.TryGetValue(localVertices[i], out int g))
                {
                    g = reps.Count;
                    byPosition[localVertices[i]] = g;
                    reps.Add(i);
                    members.Add(new List<int>(2));
                }
                groupOfVertex[i] = g;
                members[g].Add(i);
            }
            groupRep = reps.ToArray();
            groupMembers = members.ToArray();

            var adjacency = new HashSet<int>[reps.Count];
            for (int g = 0; g < adjacency.Length; g++) adjacency[g] = new HashSet<int>();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int ga = groupOfVertex[triangles[t]];
                int gb = groupOfVertex[triangles[t + 1]];
                int gc = groupOfVertex[triangles[t + 2]];
                if (ga != gb) { adjacency[ga].Add(gb); adjacency[gb].Add(ga); }
                if (gb != gc) { adjacency[gb].Add(gc); adjacency[gc].Add(gb); }
                if (ga != gc) { adjacency[ga].Add(gc); adjacency[gc].Add(ga); }
            }
            groupAdjacency = new List<int>[adjacency.Length];
            for (int g = 0; g < adjacency.Length; g++) groupAdjacency[g] = new List<int>(adjacency[g]);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>World-space point at barycentric coordinates of a triangle.</summary>
        public Vector3 BaryPoint(int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            return worldVertices[triangles[t]] * bary.x +
                   worldVertices[triangles[t + 1]] * bary.y +
                   worldVertices[triangles[t + 2]] * bary.z;
        }

        /// <summary>World-space (non-normalized safe) face normal of a triangle.</summary>
        public Vector3 FaceNormal(int triangle)
        {
            int t = triangle * 3;
            var a = worldVertices[triangles[t]];
            var n = Vector3.Cross(worldVertices[triangles[t + 1]] - a, worldVertices[triangles[t + 2]] - a);
            return n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }

        /// <summary>Smooth world-space normal at barycentric coordinates of a triangle.</summary>
        public Vector3 BaryNormal(int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            var n = worldNormals[triangles[t]] * bary.x +
                    worldNormals[triangles[t + 1]] * bary.y +
                    worldNormals[triangles[t + 2]] * bary.z;
            return n.sqrMagnitude > 1e-12f ? n.normalized : FaceNormal(triangle);
        }
    }
}
