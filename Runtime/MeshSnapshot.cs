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
        /// <summary>Mesh-local authored tangents, or null when the mesh has none.</summary>
        public Vector4[] baseTangents;
        /// <summary>Per-vertex world skinning matrix M(v): world = M(v) * local.</summary>
        public Matrix4x4[] skinMatrices;
        private Matrix4x4[] inverseSkinMatrices;
        private readonly object inverseLock = new object();

        /// <summary>
        /// The inverses of <see cref="skinMatrices"/>, computed once per snapshot (each transferred shape frame un-skins
        /// through them). Thread-safe.
        /// </summary>
        public Matrix4x4[] InverseSkinMatrices()
        {
            lock (inverseLock)
            {
                if (inverseSkinMatrices != null && inverseSkinMatrices.Length == skinMatrices.Length) return inverseSkinMatrices;
                var matrices = skinMatrices;
                var inverse = new Matrix4x4[matrices.Length];
                Parallel.For(0, matrices.Length, i => inverse[i] = matrices[i].inverse);
                return inverseSkinMatrices = inverse;
            }
        }
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
        /// <see cref="MeshBvh.Build"/> leaves them out, so no query finds them.
        /// </summary>
        public bool[] hiddenTriangles;

        // The body content's shared derived data (ReFitBodyCache.Attach), or null.
        internal ReFitBodyCache.Entry shared;
        // A surface moved from another one (a shape frame on the body, see MovedFrom): that one and the triangles that moved.
        private MeshSnapshot movedFrom;
        private int[] movedTriangles;
        private MeshBvh index;
        private Vector3[] indexVertices;
        private bool[] indexHidden;
        private readonly object indexLock = new object();

        /// <summary>
        /// The hierarchy of this surface, its <see cref="hiddenTriangles"/> left out, built once and kept while the vertex array
        /// and the hidden triangles stay the ones it was built on (shared with equal bodies once attached to the body cache).
        /// Stages reading the same body read the same hierarchy. Thread safe. Code moving the vertices in place builds its own.
        /// </summary>
        public MeshBvh Index()
        {
            lock (indexLock)
            {
                if (index != null && ReferenceEquals(indexVertices, worldVertices) && ReferenceEquals(indexHidden, hiddenTriangles)) return index;
                if (MovesFrom()) index = Moved(movedFrom.Index());
                else index = shared != null ? shared.Index(this) : MeshSnapshot.BuildIndex(this);
                indexVertices = worldVertices;
                indexHidden = hiddenTriangles;
                return index;
            }
        }

        /// <summary>
        /// Captures a snapshot of <paramref name="smr"/> in its current pose.
        /// Blendshape weights default to the renderer's current weights; <paramref name="shapeWeightOverrides01"/>
        /// (shape index -> weight in 0..1) overrides individual shapes (e.g. force the transferred shape to 0 or 1).
        /// </summary>
        public static MeshSnapshot Capture(SkinnedMeshRenderer smr, bool buildTopology,
            IDictionary<int, float> shapeWeightOverrides01, ReFitReport report) =>
            Capture(smr, buildTopology, shapeWeightOverrides01, report, false);

        /// <summary>
        /// The skinned world positions of <paramref name="smr"/> in its current pose (see <see cref="Capture(SkinnedMeshRenderer,bool,IDictionary{int,float},ReFitReport)"/>),
        /// bit for bit those a full capture gives, without its normals, tangents, triangles and face normals: for a renderer
        /// whose bounds or sampled points are all that is read.
        /// </summary>
        internal static Vector3[] SkinnedPositions(SkinnedMeshRenderer smr, IDictionary<int, float> shapeWeightOverrides01) =>
            Capture(smr, false, shapeWeightOverrides01, null, true).worldVertices;

        private static MeshSnapshot Capture(SkinnedMeshRenderer smr, bool buildTopology,
            IDictionary<int, float> shapeWeightOverrides01, ReFitReport report, bool positionsOnly)
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
            var baseNormals = positionsOnly ? null : mesh.normals;
            bool authoredNormals = baseNormals != null && baseNormals.Length == vertexCount;
            // The normals the shown shapes give (their normal deltas added, as Unity skins them); the authored ones
            // stay in baseNormals, the rest pose transferred shapes are measured against.
            Vector3[] shownNormals = null;
            BlendShapeEvaluation.Scratch scratch = null;
            bool clamp = BlendShapeEvaluation.ClampWeights;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float w = smr.GetBlendShapeWeight(s);
                if (shapeWeightOverrides01 != null && shapeWeightOverrides01.TryGetValue(s, out var o)) w = o * 100f;
                if (Mathf.Abs(w) < 1e-2f) continue;
                scratch ??= new BlendShapeEvaluation.Scratch(vertexCount);
                if (authoredNormals) shownNormals ??= (Vector3[])baseNormals.Clone();
                BlendShapeEvaluation.Add(mesh, s, w, clamp, snap.localVertices, shownNormals, scratch);
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
            snap.worldNormals = positionsOnly ? null : new Vector3[vertexCount];
            var rigidMatrix = snap.rendererLocalToWorld;
            var localVerts = snap.localVertices;
            var weights = snap.boneWeights;

            // Without authored normals both come from the shown positions.
            if (!authoredNormals && !positionsOnly)
                baseNormals = ComputeVertexNormals(localVerts, GetAllTriangles(mesh));
            snap.baseNormals = baseNormals;
            var skinnedNormals = shownNormals ?? baseNormals;
            var tangents = positionsOnly ? null : mesh.tangents;
            snap.baseTangents = tangents != null && tangents.Length == vertexCount ? tangents : null;

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
                if (positionsOnly) return;
                var nm = m.inverse.transpose;
                var n = nm.MultiplyVector(skinnedNormals[i]);
                snap.worldNormals[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            });

            if (positionsOnly) return snap;
            snap.triangles = GetAllTriangles(mesh);
            snap.CacheFaceNormals();

            if (buildTopology) snap.BuildTopology();
            return snap;
        }

        /// <summary>The most bone influences any vertex of <paramref name="mesh"/> has (0 without skin weights).</summary>
        internal static int MaxBonesPerVertex(Mesh mesh)
        {
            var counts = mesh.GetBonesPerVertex();
            int max = 0;
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > max) max = counts[i];
            return max;
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
        public static Vector3 Unit(Vector3 vector)
        {
            float length = Mathf.Sqrt(vector.x * vector.x + vector.y * vector.y + vector.z * vector.z);
            return length > 1e-12f ? vector / length : Vector3.zero;
        }

        /// <summary>A snapshot of world-space geometry without a renderer (procedural surfaces and tests): rigid, at rest.</summary>
        public static MeshSnapshot FromWorld(Vector3[] worldVertices, int[] triangles, bool buildTopology)
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
            snap.CacheFaceNormals();
            if (buildTopology) snap.BuildTopology();
            return snap;
        }

        /// <summary>
        /// What <see cref="FromWorld"/> derives from a surface, kept to derive a moved copy of it again where it moved
        /// (<see cref="MovedSurface"/>): its geometric vertex normals, face normals and the triangles around each vertex.
        /// </summary>
        internal sealed class WorldSurface
        {
            public MeshSnapshot rest;
            // Per vertex its triangles, ascending (the order ComputeVertexNormals adds them in).
            public int[] triangleStart, trianglesOf;

            public static WorldSurface Of(Vector3[] worldVertices, int[] triangles)
            {
                var rest = FromWorld(worldVertices, triangles, false);
                int n = worldVertices.Length;
                var start = new int[n + 1];
                for (int i = 0; i < triangles.Length; i++) start[triangles[i] + 1]++;
                for (int v = 0; v < n; v++) start[v + 1] += start[v];
                var fill = (int[])start.Clone();
                var around = new int[triangles.Length];
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int v = triangles[t + k];
                        // A triangle using a vertex twice adds its normal there twice, as ComputeVertexNormals does.
                        around[fill[v]++] = t / 3;
                    }
                return new WorldSurface { rest = rest, triangleStart = start, trianglesOf = around };
            }
        }

        /// <summary>
        /// <see cref="FromWorld"/> of <paramref name="vertices"/> (the surface of <paramref name="surface"/> moved), bit for bit.
        /// A vertex moved when any bit of its position differs from the rest's; a triangle moved when a corner did. Only the
        /// corners of moved triangles get their vertex normals again (from the triangles around each, in the ascending order a
        /// full computation adds them in), and only triangles touching such a corner their face normals (a degenerate one takes
        /// its corners' normals); everything else is the rest's, computed from the same bits. The vertex array is taken, not copied.
        /// </summary>
        internal static MeshSnapshot MovedSurface(WorldSurface surface, Vector3[] vertices)
        {
            var rest = surface.rest;
            var triangles = rest.triangles;
            var restVertices = rest.worldVertices;
            if (vertices.Length != restVertices.Length) throw new System.ArgumentException("One position per vertex.", nameof(vertices));
            var normals = (Vector3[])rest.worldNormals.Clone();
            var corners = new List<int>();
            var marked = new bool[vertices.Length];
            for (int moved = 0; moved < vertices.Length; moved++)
            {
                if (SameBits(vertices[moved], restVertices[moved])) continue;
                for (int e = surface.triangleStart[moved]; e < surface.triangleStart[moved + 1]; e++)
                {
                    int t = surface.trianglesOf[e] * 3;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = triangles[t + k];
                        if (!marked[v]) { marked[v] = true; corners.Add(v); }
                    }
                }
            }
            foreach (int v in corners)
            {
                var sum = Vector3.zero;
                for (int e = surface.triangleStart[v]; e < surface.triangleStart[v + 1]; e++)
                {
                    int t = surface.trianglesOf[e] * 3;
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    sum += Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                }
                var unit = Unit(sum);
                normals[v] = unit != Vector3.zero ? unit : Vector3.up;
            }
            var snap = new MeshSnapshot
            {
                localVertices = vertices,
                worldVertices = vertices,
                triangles = triangles,
                rigid = true,
                rendererLocalToWorld = Matrix4x4.identity,
                rendererWorldToLocal = Matrix4x4.identity,
                worldNormals = normals,
                baseNormals = normals,
            };
            var faceNormals = (Vector3[])rest.FaceNormals.Clone();
            var done = new bool[faceNormals.Length];
            foreach (int v in corners)
                for (int e = surface.triangleStart[v]; e < surface.triangleStart[v + 1]; e++)
                {
                    int t = surface.trianglesOf[e];
                    if (done[t]) continue;
                    done[t] = true;
                    faceNormals[t] = snap.ComputeFaceNormal(t);
                }
            snap.FaceNormals = faceNormals;
            return snap;
        }

        private static bool SameBits(Vector3 a, Vector3 b) =>
            System.BitConverter.SingleToInt32Bits(a.x) == System.BitConverter.SingleToInt32Bits(b.x) &&
            System.BitConverter.SingleToInt32Bits(a.y) == System.BitConverter.SingleToInt32Bits(b.y) &&
            System.BitConverter.SingleToInt32Bits(a.z) == System.BitConverter.SingleToInt32Bits(b.z);

        /// <summary>
        /// Unit face normals by triangle (see <see cref="FaceNormal"/>), as of the last <see cref="CacheFaceNormals"/>, or null.
        /// Captured and <see cref="FromWorld"/> snapshots have them; code moving a snapshot's vertices afterwards caches them again.
        /// </summary>
        public Vector3[] FaceNormals { get; private set; }

        private readonly object faceNormalsLock = new object();

        /// <summary>
        /// <see cref="FaceNormals"/>, computed once when none are cached yet (a shaped body made from positions): what the
        /// hierarchy's facing rules read. Thread safe; never recomputes cached normals (code moving the vertices calls
        /// <see cref="CacheFaceNormals"/> itself, before any query reads them).
        /// </summary>
        public Vector3[] KnownFaceNormals()
        {
            var normals = FaceNormals;
            if (normals != null) return normals;
            lock (faceNormalsLock) return FaceNormals ?? CacheFaceNormals();
        }

        /// <summary>
        /// Computes <see cref="FaceNormals"/> for the current vertices (in place when the triangle count is unchanged): queries
        /// filtering by facing read them instead of a cross product and a square root per triangle tested. Not thread safe.
        /// </summary>
        public Vector3[] CacheFaceNormals()
        {
            int count = triangles.Length / 3;
            var normals = FaceNormals != null && FaceNormals.Length == count ? FaceNormals : new Vector3[count];
            FaceNormals = null;
            if (count >= 4096) Parallel.For(0, count, t => normals[t] = ComputeFaceNormal(t));
            else for (int t = 0; t < count; t++) normals[t] = ComputeFaceNormal(t);
            FaceNormals = normals;
            return normals;
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
            // Sorted: every smoothing pass sums neighbours in this order, which must not be a hash set's.
            for (int g = 0; g < adjacency.Length; g++)
            {
                groupAdjacency[g] = new List<int>(adjacency[g]);
                groupAdjacency[g].Sort();
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        // Enclosed skin lies at least this deep under the part around it: closer, a second surface is a decal or a seam.
        private const float EnclosedDepth = .01f;
        // How far out of the skin the part around it is looked for.
        private const float EnclosedReach = .3f;

        /// <summary>
        /// The hierarchy of the whole surface, <see cref="hiddenTriangles"/> included: the way out of a part of the body crosses
        /// enclosed skin too. Shared like <see cref="Index"/> once attached to the body cache.
        /// </summary>
        public MeshBvh WholeIndex()
        {
            if (hiddenTriangles == null) return Index();
            if (MovesFrom()) return Moved(movedFrom.WholeIndex());
            if (shared != null) return shared.Index(this, null);
            return MeshSnapshot.BuildIndex(worldVertices, triangles);
        }

        /// <summary>
        /// The surface of <paramref name="rest"/> moved to <paramref name="vertices"/> (rest positions plus
        /// <paramref name="deltas"/>, a shape frame): the same triangles and hidden triangles, its hierarchy the rest's refitted
        /// along the triangles that moved, which answers exactly as one built anew. Normals are the caller's to set.
        /// </summary>
        internal static MeshSnapshot MovedFrom(MeshSnapshot rest, Vector3[] vertices, Vector3[] deltas) => new MeshSnapshot
        {
            worldVertices = vertices,
            triangles = rest.triangles,
            hiddenTriangles = rest.hiddenTriangles,
            movedFrom = rest,
            movedTriangles = MovedTriangles(rest.triangles, deltas),
        };

        /// <summary>The triangles with a corner whose delta is not exactly zero.</summary>
        internal static int[] MovedTriangles(int[] triangles, Vector3[] deltas)
        {
            var moved = new List<int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
                if (Moves(deltas[triangles[t]]) || Moves(deltas[triangles[t + 1]]) || Moves(deltas[triangles[t + 2]])) moved.Add(t / 3);
            return moved.ToArray();
        }

        private static bool Moves(Vector3 delta) => delta.x != 0f || delta.y != 0f || delta.z != 0f;

        // Still the rest's triangles and hidden triangles (nothing replaced them since MovedFrom).
        private bool MovesFrom() => movedFrom != null && ReferenceEquals(triangles, movedFrom.triangles) &&
                                    ReferenceEquals(hiddenTriangles, movedFrom.hiddenTriangles);

        private MeshBvh Moved(MeshBvh rest)
        {
            var copy = rest.Clone(worldVertices);
            copy.RefitTriangles(movedTriangles);
            return copy;
        }

        /// <summary>
        /// The hierarchy of <paramref name="snapshot"/>'s world triangles but its hidden ones and, with
        /// <paramref name="include"/>, those it leaves out (queries never visit them: far faster than filtering them out when
        /// few are left). Hits give the snapshot's own triangle indices.
        /// </summary>
        public static MeshBvh BuildIndex(MeshSnapshot snapshot, bool[] include = null) =>
            BuildIndex(snapshot.worldVertices, snapshot.triangles, snapshot.hiddenTriangles, include);

        /// <summary>The hierarchy of world <paramref name="vertices"/> and <paramref name="triangles"/> (see <see cref="BuildIndex(MeshSnapshot,bool[])"/>).</summary>
        public static MeshBvh BuildIndex(Vector3[] vertices, int[] triangles, bool[] hidden = null, bool[] include = null)
        {
            bool[] kept = null;
            if (hidden != null || include != null)
            {
                kept = new bool[triangles.Length / 3];
                for (int t = 0; t < kept.Length; t++) kept[t] = (hidden == null || !hidden[t]) && (include == null || include[t]);
            }
            return MeshBvh.Build(vertices, triangles, kept);
        }

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
            var known = shared;
            if (known != null)
                lock (known)
                    if (known.enclosedKnown)
                    {
                        hiddenTriangles = known.enclosed;
                        return Count(hiddenTriangles);
                    }
            var index = Index();
            int count = triangles.Length / 3;
            var hidden = new bool[count];
            Parallel.For(0, count, t =>
            {
                int i = t * 3;
                var a = worldVertices[triangles[i]];
                var normal = Unit(Vector3.Cross(worldVertices[triangles[i + 1]] - a, worldVertices[triangles[i + 2]] - a));
                if (normal == Vector3.zero) return;
                var center = (a + worldVertices[triangles[i + 1]] + worldVertices[triangles[i + 2]]) / 3f;
                var hit = index.Raycast(center + normal * .0005f, normal, EnclosedReach, true, TriangleRule.All.Except(t));
                if (!hit.Found || hit.Distance < EnclosedDepth || Vector3.Dot(FaceNormal(hit.TriangleOrNone), normal) <= 0) return;
                hidden[t] = Inside(index, center + normal * (hit.Distance * .5f), normal);
            });
            int hiddenCount = Count(hidden);
            hiddenTriangles = hiddenCount > 0 ? hidden : null;
            if (known != null)
                lock (known)
                {
                    known.enclosed = hiddenTriangles;
                    known.enclosedKnown = true;
                }
            return hiddenCount;
        }

        private static int Count(bool[] flags)
        {
            int count = 0;
            if (flags != null) foreach (bool flag in flags) if (flag) count++;
            return count;
        }

        // Inside a closed part at point: along the skin (across normal), each way leaves through a surface facing that way.
        private bool Inside(MeshBvh index, Vector3 point, Vector3 normal)
        {
            var side = Unit(Vector3.Cross(normal, Mathf.Abs(normal.y) < .9f ? Vector3.up : Vector3.right));
            var other = Vector3.Cross(normal, side);
            return Exits(index, point, side) && Exits(index, point, -side) && Exits(index, point, other) && Exits(index, point, -other);
        }

        private bool Exits(MeshBvh index, Vector3 point, Vector3 direction)
        {
            var exit = index.Raycast(point, direction, EnclosedReach);
            return exit.Found && Vector3.Dot(FaceNormal(exit.TriangleOrNone), direction) > 0;
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
        /// cards, horns or hair. Read only: a body in the body cache shares it.
        /// </summary>
        public bool[] MainSurface()
        {
            var known = shared;
            if (known == null) return ComputeMainSurface();
            lock (known) return known.main ??= ComputeMainSurface();
        }

        private bool[] ComputeMainSurface()
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
        public Vector3 FaceNormal(int triangle) => FaceNormals != null ? FaceNormals[triangle] : ComputeFaceNormal(triangle);

        private Vector3 ComputeFaceNormal(int triangle)
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
