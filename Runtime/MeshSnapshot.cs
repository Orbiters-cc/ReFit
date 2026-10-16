using System.Collections.Generic;
using System.Threading.Tasks;
using Orbiters.Toolkit.Meshes;
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
        /// <summary>Mesh-local base normals (authored, or recomputed when absent). Thread-safe to read off the main thread.</summary>
        public Vector3[] baseNormals;
        /// <summary>Per-vertex world skinning matrix M(v): world = M(v) * local.</summary>
        public Matrix4x4[] skinMatrices;
        /// <summary>All submesh triangles concatenated.</summary>
        public int[] triangles;
        public BoneWeight[] boneWeights;
        public Transform[] bones;
        public Matrix4x4[] boneMatrices;
        public bool[] boneMatrixValid;
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
        /// Triangles of skin enclosed in another part of the same body (see <see cref="HideEnclosedSkin"/>), or null.
        /// <see cref="SurfaceBvh.Build"/> leaves them out, so no query finds them.
        /// </summary>
        public bool[] hiddenTriangles;

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
            BlendShapeEvaluation.Scratch scratch = null;
            bool clamp = BlendShapeEvaluation.ClampWeights;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float w = smr.GetBlendShapeWeight(s);
                if (shapeWeightOverrides01 != null && shapeWeightOverrides01.TryGetValue(s, out var o)) w = o * 100f;
                if (Mathf.Abs(w) < 1e-2f) continue;
                scratch ??= new BlendShapeEvaluation.Scratch(vertexCount);
                BlendShapeEvaluation.Add(mesh, s, w, clamp, snap.localVertices, null, scratch);
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
            snap.boneMatrices = boneMatrices;
            snap.boneMatrixValid = boneOk;

            snap.skinMatrices = new Matrix4x4[vertexCount];
            snap.worldVertices = new Vector3[vertexCount];
            snap.worldNormals = new Vector3[vertexCount];
            var rigidMatrix = snap.rendererLocalToWorld;
            var localVerts = snap.localVertices;
            var weights = snap.boneWeights;

            if (baseNormals == null || baseNormals.Length != vertexCount)
                baseNormals = ComputeVertexNormals(localVerts, GetAllTriangles(mesh));
            snap.baseNormals = baseNormals;

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
            {
                var unit = Unit(normals[i]);
                normals[i] = unit != Vector3.zero ? unit : Vector3.up;
            }
            return normals;
        }

        /// <summary>
        /// <paramref name="vector"/> scaled to length 1, or zero when it has no direction. Unlike <see cref="Vector3.normalized"/>
        /// (zero below a length of 1e-5), a cross product of millimetre edges keeps its direction.
        /// </summary>
        internal static Vector3 Unit(Vector3 vector)
        {
            float length = Mathf.Sqrt(vector.x * vector.x + vector.y * vector.y + vector.z * vector.z);
            return length > 1e-12f ? vector / length : Vector3.zero;
        }

        /// <summary>A snapshot of world-space geometry without a renderer (procedural surfaces and tests): rigid, at rest.</summary>
        internal static MeshSnapshot FromWorld(Vector3[] worldVertices, int[] triangles, bool buildTopology)
        {
            var snap = new MeshSnapshot
            {
                localVertices = (Vector3[])worldVertices.Clone(),
                worldVertices = (Vector3[])worldVertices.Clone(),
                triangles = (int[])triangles.Clone(),
                rigid = true,
                rendererLocalToWorld = Matrix4x4.identity,
                rendererWorldToLocal = Matrix4x4.identity
            };
            snap.worldNormals = ComputeVertexNormals(snap.worldVertices, snap.triangles);
            snap.baseNormals = (Vector3[])snap.worldNormals.Clone();
            if (buildTopology) snap.BuildTopology();
            return snap;
        }

        // ------------------------------------------------------------------
        // Topology: welding groups + adjacency
        // ------------------------------------------------------------------

        // Vertices on open edges this close in the world are one point of the surface: a seam the modelling tool split a hair
        // apart opens into a crack once its two sides are moved apart. Closed pieces touching (a chain's links) stay apart.
        private const float WeldDistance = 1e-4f;

        private void BuildTopology()
        {
            int vertexCount = localVertices.Length;
            groupOfVertex = new int[vertexCount];
            var root = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) root[i] = i;
            int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
            void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) root[Mathf.Max(a, b)] = Mathf.Min(a, b); }
            var byPosition = new Dictionary<Vector3, int>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                if (byPosition.TryGetValue(localVertices[i], out int same)) Join(i, same);
                else byPosition[localVertices[i]] = i;
            }
            if (worldVertices != null && worldVertices.Length == vertexCount)
            {
                // The vertices on open edges (an edge of one triangle), once vertices at the same mesh position are one.
                var edgeUse = new Dictionary<(int, int), int>(triangles.Length);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = Find(triangles[t + k]), b = Find(triangles[t + (k + 1) % 3]);
                        if (a == b) continue;
                        var key = a < b ? (a, b) : (b, a);
                        edgeUse[key] = edgeUse.TryGetValue(key, out int used) ? used + 1 : 1;
                    }
                var onOpenEdge = new bool[vertexCount];
                foreach (var edge in edgeUse)
                    if (edge.Value == 1) { onOpenEdge[edge.Key.Item1] = true; onOpenEdge[edge.Key.Item2] = true; }
                for (int i = 0; i < vertexCount; i++) onOpenEdge[i] = onOpenEdge[Find(i)];

                var cells = new Dictionary<Vector3Int, List<int>>();
                for (int i = 0; i < vertexCount; i++)
                {
                    if (!onOpenEdge[i]) continue;
                    var cell = Vector3Int.FloorToInt(worldVertices[i] / WeldDistance);
                    if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<int>(1);
                    list.Add(i);
                }
                for (int i = 0; i < vertexCount; i++)
                {
                    if (!onOpenEdge[i]) continue;
                    var cell = Vector3Int.FloorToInt(worldVertices[i] / WeldDistance);
                    for (int x = -1; x <= 1; x++)
                        for (int y = -1; y <= 1; y++)
                            for (int z = -1; z <= 1; z++)
                                if (cells.TryGetValue(cell + new Vector3Int(x, y, z), out var near))
                                    foreach (int j in near)
                                        if (j > i && (worldVertices[j] - worldVertices[i]).sqrMagnitude <= WeldDistance * WeldDistance) Join(i, j);
                }
            }
            var groupOfRoot = new Dictionary<int, int>();
            var reps = new List<int>();
            var members = new List<List<int>>();
            for (int i = 0; i < vertexCount; i++)
            {
                int r = Find(i);
                if (!groupOfRoot.TryGetValue(r, out int g))
                {
                    g = reps.Count;
                    groupOfRoot[r] = g;
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

        // Enclosed skin lies at least this deep under the part around it: closer, a second surface is a decal or a seam.
        private const float EnclosedDepth = .01f;
        // How far out of the skin the part around it is looked for.
        private const float EnclosedReach = .3f;

        /// <summary>
        /// Marks <see cref="hiddenTriangles"/>: skin inside another closed part of the same body, such as the flat chest left
        /// under added breasts or a thigh pressed into the other. Out of such a triangle the first surface met, at least
        /// <see cref="EnclosedDepth"/> away, faces the same way: the way out of the part enclosing it. That part must be
        /// closed around the skin: from halfway out, every way along the skin leaves it through its surface too. A fluff card
        /// or a mane standing over the skin (a single sheet, open air beneath it) encloses nothing. Nothing can touch enclosed
        /// skin and clothing covers the part around it instead. Returns the number of hidden triangles.
        /// </summary>
        public int HideEnclosedSkin()
        {
            hiddenTriangles = null;
            var index = SurfaceBvh.Build(this);
            int count = triangles.Length / 3;
            var hidden = new bool[count];
            Parallel.For(0, count, t =>
            {
                int i = t * 3;
                var a = worldVertices[triangles[i]];
                var normal = Unit(Vector3.Cross(worldVertices[triangles[i + 1]] - a, worldVertices[triangles[i + 2]] - a));
                if (normal == Vector3.zero) return;
                var center = (a + worldVertices[triangles[i + 1]] + worldVertices[triangles[i + 2]]) / 3f;
                var hit = index.Raycast(center + normal * .0005f, normal, EnclosedReach, other => other != t);
                if (!hit.found || hit.distance < EnclosedDepth || Vector3.Dot(FaceNormal(hit.triangle), normal) <= 0) return;
                hidden[t] = Inside(index, center + normal * (hit.distance * .5f), normal);
            });
            int hiddenCount = 0;
            foreach (bool h in hidden) if (h) hiddenCount++;
            hiddenTriangles = hiddenCount > 0 ? hidden : null;
            return hiddenCount;
        }

        // Inside a closed part at point: along the skin (across normal), each way leaves through a surface facing that way.
        private bool Inside(SurfaceBvh index, Vector3 point, Vector3 normal)
        {
            var side = Unit(Vector3.Cross(normal, Mathf.Abs(normal.y) < .9f ? Vector3.up : Vector3.right));
            var other = Vector3.Cross(normal, side);
            foreach (var direction in new[] { side, -side, other, -other })
            {
                var exit = index.Raycast(point, direction, EnclosedReach);
                if (!exit.found || Vector3.Dot(FaceNormal(exit.triangle), direction) <= 0) return false;
            }
            return true;
        }

        /// <summary>Vertices of hidden triangles only (see <see cref="hiddenTriangles"/>), or null when none is hidden.</summary>
        public bool[] HiddenVertices()
        {
            if (hiddenTriangles == null) return null;
            var visible = new bool[worldVertices.Length];
            for (int t = 0; t < hiddenTriangles.Length; t++)
                if (!hiddenTriangles[t])
                    for (int k = 0; k < 3; k++) visible[triangles[t * 3 + k]] = true;
            var hidden = new bool[visible.Length];
            for (int v = 0; v < hidden.Length; v++) hidden[v] = !visible[v];
            return hidden;
        }

        /// <summary>
        /// The vertices of the largest connected part (vertices at the same place joined): a body's main surface, not its fluff
        /// cards, horns or hair.
        /// </summary>
        public bool[] MainSurface()
        {
            int count = worldVertices.Length;
            var root = new int[count];
            for (int i = 0; i < count; i++) root[i] = i;
            int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
            void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) root[a] = b; }
            var byPosition = new Dictionary<Vector3, int>(count);
            for (int i = 0; i < count; i++)
            {
                if (byPosition.TryGetValue(worldVertices[i], out int same)) Join(i, same);
                else byPosition[worldVertices[i]] = i;
            }
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Join(triangles[t], triangles[t + 1]);
                Join(triangles[t], triangles[t + 2]);
            }
            var sizes = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int r = Find(i);
                sizes[r] = sizes.TryGetValue(r, out int n) ? n + 1 : 1;
            }
            int largest = -1, most = 0;
            foreach (var pair in sizes) if (pair.Value > most) { most = pair.Value; largest = pair.Key; }
            var main = new bool[count];
            for (int i = 0; i < count; i++) main[i] = Find(i) == largest;
            return main;
        }

        /// <summary>World-space point at barycentric coordinates of a triangle.</summary>
        public Vector3 BaryPoint(int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            return worldVertices[triangles[t]] * bary.x +
                   worldVertices[triangles[t + 1]] * bary.y +
                   worldVertices[triangles[t + 2]] * bary.z;
        }

        /// <summary>
        /// World-space unit face normal of a triangle, however small. A degenerate triangle (no area) takes the smooth normal of
        /// its corners, or no direction (zero) without vertex normals: it never claims to face a way it does not.
        /// </summary>
        public Vector3 FaceNormal(int triangle)
        {
            int t = triangle * 3;
            var a = worldVertices[triangles[t]];
            var n = Unit(Vector3.Cross(worldVertices[triangles[t + 1]] - a, worldVertices[triangles[t + 2]] - a));
            if (n != Vector3.zero || worldNormals == null) return n;
            return Unit(worldNormals[triangles[t]] + worldNormals[triangles[t + 1]] + worldNormals[triangles[t + 2]]);
        }

        /// <summary>Smooth world-space normal at barycentric coordinates of a triangle.</summary>
        public Vector3 BaryNormal(int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            var n = worldNormals[triangles[t]] * bary.x +
                    worldNormals[triangles[t + 1]] * bary.y +
                    worldNormals[triangles[t + 2]] * bary.z;
            var unit = Unit(n);
            return unit != Vector3.zero ? unit : FaceNormal(triangle);
        }
    }
}
