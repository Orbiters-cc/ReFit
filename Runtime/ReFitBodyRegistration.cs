using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The source body laid onto the target body: every source skin point gets the target point that is the same place on
    /// the body. The source surface is moved as one piece (a non-rigid registration): each point is pulled to the target
    /// skin turned its way, of a compatible body region, while the move stays smooth over the source surface, stiff at first
    /// (proportions), then looser (shape). Points matched independently jump into creases and onto fluff wherever the bodies
    /// differ; laid on as one surface, neighbouring points keep neighbouring counterparts, so clothing over them moves as one.
    /// Runs on captured arrays (worker thread safe).
    /// </summary>
    internal sealed class ReFitBodyRegistration
    {
        // Stiffness of each pass (graph Laplacian weight against a data weight of at most 1): the smoothing reaches about
        // its square root in edges. The skeletons have placed the limbs already (see Retarget), so the passes only shape the
        // skin: a stiff pass would move a whole limb as one piece, carrying the feet along with a thigh much larger than
        // the base's. Each stiffness matches twice, so skin far from its counterpart gets there in steps.
        private static readonly float[] Stiffness = { 60f, 60f, 25f, 25f, 10f, 10f, 4f, 4f, 1.5f, 1.5f };
        // How far a pass looks for the target, and the distance over which a match loses its weight.
        private const float Reach = .3f;
        private static readonly float[] Falloff = { .2f, .16f, .12f, .1f, .08f, .07f, .06f, .05f, .045f, .04f };
        // Target skin turned this far from the source skin's normal (dot) never matches it.
        private const float Facing = .35f;
        // The skin met along the normal is taken over the nearest skin while it is at most this much farther (factor, slack).
        private const float AlongFactor = 1.5f, AlongSlack = .005f;
        // Keeps parts without any match (eyes, teeth) where they are.
        private const float Anchor = 1e-4f;
        private const int SolverIterations = 160;

        public readonly MeshSnapshot source;
        /// <summary>Per source vertex: where it lies on the target.</summary>
        public readonly Vector3[] registered;
        /// <summary>Per source vertex: the weight of its last match (0: it followed its neighbours only).</summary>
        public readonly float[] confidence;
        /// <summary>Distance from the registered visible source skin to the target: median and 95th percentile (metres).</summary>
        public readonly float medianGap, p95Gap;

        /// <summary>Debug: each registration built, with the target it was laid onto (tests only).</summary>
        internal static Action<ReFitBodyRegistration, MeshSnapshot> Captured;

        private ReFitBodyRegistration(MeshSnapshot source, Vector3[] registered, float[] confidence, float medianGap, float p95Gap)
        {
            this.source = source;
            this.registered = registered;
            this.confidence = confidence;
            this.medianGap = medianGap;
            this.p95Gap = p95Gap;
        }

        /// <summary>
        /// Where the source skeleton carried onto the target's puts each source vertex: its skinning blended through the bones'
        /// retargets (see <c>ReFitEngine.SkeletonRetarget</c>).
        /// </summary>
        public static Vector3[] Retarget(MeshSnapshot source, Matrix4x4[] boneRetarget)
        {
            int count = source.worldVertices.Length;
            var result = new Vector3[count];
            var weights = source.boneWeights;
            Parallel.For(0, count, v =>
            {
                var p = source.worldVertices[v];
                if (weights == null || weights.Length != count) { result[v] = p; return; }
                var w = weights[v];
                var sum = Vector3.zero;
                float total = 0f;
                void Add(int bone, float weight)
                {
                    if (weight <= 0f || bone < 0 || bone >= boneRetarget.Length) return;
                    sum += boneRetarget[bone].MultiplyPoint3x4(p) * weight;
                    total += weight;
                }
                Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                result[v] = total > 1e-6f ? sum / total : p;
            });
            return result;
        }

        /// <summary>The target point of a source surface point.</summary>
        public Vector3 Point(int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            return registered[source.triangles[t]] * bary.x + registered[source.triangles[t + 1]] * bary.y +
                   registered[source.triangles[t + 2]] * bary.z;
        }

        /// <summary>How the source surface point moves onto the target.</summary>
        public Vector3 Displacement(int triangle, Vector3 bary) => Point(triangle, bary) - source.BaryPoint(triangle, bary);

        /// <summary>The turn of a source triangle laid onto the target (its normal and first edge).</summary>
        public Quaternion Turn(int triangle)
        {
            int t = triangle * 3;
            var s = source.worldVertices;
            int i0 = source.triangles[t], i1 = source.triangles[t + 1], i2 = source.triangles[t + 2];
            var from = Frame(s[i0], s[i1], s[i2]);
            var to = Frame(registered[i0], registered[i1], registered[i2]);
            return to * Quaternion.Inverse(from);
        }

        private static Quaternion Frame(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = MeshSnapshot.Unit(Vector3.Cross(b - a, c - a));
            var edge = MeshSnapshot.Unit(b - a);
            if (normal == Vector3.zero || edge == Vector3.zero) return Quaternion.identity;
            return Quaternion.LookRotation(normal, edge);
        }

        /// <param name="sourceTriangleMasks">Body regions of each source triangle (see <see cref="SurfaceBindingSolver.RegionMask"/>), or null.</param>
        /// <param name="targetTriangleMasks">Body regions each target triangle accepts, or null.</param>
        /// <param name="start">Per source vertex, where the skeletons put it on the target (see <see cref="Retarget"/>), or null.</param>
        public static ReFitBodyRegistration Build(MeshSnapshot source, int[] sourceTriangleMasks, MeshSnapshot target, SurfaceBvh targetIndex,
            int[] targetTriangleMasks, CancellationToken cancellation, Action<float> progress = null, Vector3[] start = null)
        {
            int vertexCount = source.worldVertices.Length;
            var weld = Weld(source.worldVertices, out int nodeCount, out var nodeVertex);
            var nodes = new Vector3[nodeCount];
            for (int n = 0; n < nodeCount; n++) nodes[n] = source.worldVertices[nodeVertex[n]];
            var triangles = new int[source.triangles.Length];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = weld[source.triangles[i]];
            var neighbours = Neighbours(triangles, nodeCount);

            // A node's regions: every region of the triangles around it. Hidden skin (enclosed in another part) matches nothing.
            var masks = new int[nodeCount];
            var visible = new bool[nodeCount];
            for (int t = 0; t < triangles.Length / 3; t++)
            {
                bool hidden = source.hiddenTriangles != null && source.hiddenTriangles[t];
                for (int k = 0; k < 3; k++)
                {
                    int n = triangles[t * 3 + k];
                    if (sourceTriangleMasks != null) masks[n] |= sourceTriangleMasks[t];
                    if (!hidden) visible[n] = true;
                }
            }

            var move = new Vector3[nodeCount];
            // From where the skeletons carry the source: limbs posed or placed apart (legs wider apart, longer arms) start on
            // the target's limbs, which skin alone could not find.
            if (start != null)
                for (int n = 0; n < nodeCount; n++) move[n] = start[nodeVertex[n]] - nodes[n];
            var weights = new float[nodeCount];
            var targets = new Vector3[nodeCount];
            var position = new Vector3[nodeCount];
            for (int pass = 0; pass < Stiffness.Length; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int n = 0; n < nodeCount; n++) position[n] = nodes[n] + move[n];
                var normals = Normals(position, triangles);
                float reach = Reach, falloff = Falloff[pass];
                Parallel.For(0, nodeCount, n =>
                {
                    weights[n] = 0f;
                    if (!visible[n] || normals[n] == Vector3.zero) return;
                    var p = position[n];
                    var normal = normals[n];
                    int mask = masks[n];
                    Func<int, bool> facing = t => Vector3.Dot(target.FaceNormal(t), normal) >= Facing &&
                        (mask == 0 || targetTriangleMasks == null || targetTriangleMasks[t] == 0 || (targetTriangleMasks[t] & mask) != 0);
                    var nearest = targetIndex.ClosestPoint(p, reach, facing);
                    float limit = nearest.found ? Mathf.Min(reach, nearest.distance * AlongFactor + AlongSlack) : reach;
                    var outward = targetIndex.Raycast(p, normal, limit, facing);
                    var inward = targetIndex.Raycast(p, -normal, outward.found ? outward.distance : limit, facing);
                    var along = inward.found ? inward : outward;
                    SurfaceBvh.Hit hit = along.found ? along : nearest;
                    if (!hit.found) return;
                    float agreement = Vector3.Dot(target.FaceNormal(hit.triangle), normal);
                    float d = hit.distance / falloff;
                    weights[n] = agreement * agreement * Mathf.Exp(-d * d);
                    targets[n] = hit.position - nodes[n];
                });
                Solve(move, weights, targets, neighbours, Stiffness[pass], cancellation);
                progress?.Invoke((pass + 1f) / Stiffness.Length);
            }

            var registered = new Vector3[vertexCount];
            var confidence = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                registered[i] = source.worldVertices[i] + move[weld[i]];
                confidence[i] = weights[weld[i]];
            }

            var gaps = new List<float>(nodeCount);
            for (int n = 0; n < nodeCount; n++)
                if (visible[n])
                {
                    var hit = targetIndex.ClosestPoint(nodes[n] + move[n], .2f);
                    gaps.Add(hit.found ? hit.distance : .2f);
                }
            gaps.Sort();
            float median = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0f;
            float p95 = gaps.Count > 0 ? gaps[Mathf.Min(gaps.Count - 1, (int)(gaps.Count * .95f))] : 0f;
            var result = new ReFitBodyRegistration(source, registered, confidence, median, p95);
            Captured?.Invoke(result, target);
            return result;
        }

        // (W + stiffness L + Anchor I) x = W targets + Anchor x0, per axis, by conjugate gradients from the current move.
        private static void Solve(Vector3[] move, float[] weights, Vector3[] targets, int[][] neighbours, float stiffness,
            CancellationToken cancellation)
        {
            int n = move.Length;
            var diagonal = new float[n];
            for (int i = 0; i < n; i++) diagonal[i] = weights[i] + stiffness * neighbours[i].Length + Anchor;
            var anchor = (Vector3[])move.Clone();
            var solved = new double[3][];
            Parallel.For(0, 3, axis =>
            {
                var x = solved[axis] = new double[n];
                var b = new double[n];
                for (int i = 0; i < n; i++)
                {
                    x[i] = move[i][axis];
                    b[i] = weights[i] * targets[i][axis] + Anchor * anchor[i][axis];
                }
                var r = new double[n];
                var z = new double[n];
                var p = new double[n];
                var q = new double[n];
                Multiply(x, q, weights, neighbours, stiffness);
                double rz = 0, norm = 0;
                for (int i = 0; i < n; i++)
                {
                    r[i] = b[i] - q[i];
                    z[i] = r[i] / diagonal[i];
                    p[i] = z[i];
                    rz += r[i] * z[i];
                    norm += b[i] * b[i];
                }
                double tolerance = Math.Max(norm, 1e-20) * 1e-12;
                for (int iteration = 0; iteration < SolverIterations && rz > 1e-30; iteration++)
                {
                    if ((iteration & 31) == 0 && cancellation.IsCancellationRequested) return;
                    Multiply(p, q, weights, neighbours, stiffness);
                    double pq = 0;
                    for (int i = 0; i < n; i++) pq += p[i] * q[i];
                    if (pq <= 0) break;
                    double alpha = rz / pq, residual = 0, next = 0;
                    for (int i = 0; i < n; i++)
                    {
                        x[i] += alpha * p[i];
                        r[i] -= alpha * q[i];
                        residual += r[i] * r[i];
                        z[i] = r[i] / diagonal[i];
                        next += r[i] * z[i];
                    }
                    if (residual < tolerance) break;
                    double beta = next / rz;
                    rz = next;
                    for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
                }
            });
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++) move[i] = new Vector3((float)solved[0][i], (float)solved[1][i], (float)solved[2][i]);
        }

        private static void Multiply(double[] x, double[] result, float[] weights, int[][] neighbours, float stiffness)
        {
            for (int i = 0; i < x.Length; i++)
            {
                var around = neighbours[i];
                double sum = 0;
                for (int k = 0; k < around.Length; k++) sum += x[i] - x[around[k]];
                result[i] = (weights[i] + Anchor) * x[i] + stiffness * sum;
            }
        }

        // Vertices at the same place (UV and normal seams) are one node.
        private static int[] Weld(Vector3[] vertices, out int count, out int[] first)
        {
            var map = new int[vertices.Length];
            var byPosition = new Dictionary<Vector3, int>(vertices.Length);
            var firsts = new List<int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!byPosition.TryGetValue(vertices[i], out int node))
                {
                    node = firsts.Count;
                    byPosition[vertices[i]] = node;
                    firsts.Add(i);
                }
                map[i] = node;
            }
            count = firsts.Count;
            first = firsts.ToArray();
            return map;
        }

        private static int[][] Neighbours(int[] triangles, int count)
        {
            var sets = new HashSet<int>[count];
            for (int n = 0; n < count; n++) sets[n] = new HashSet<int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                    if (a == b) continue;
                    sets[a].Add(b);
                    sets[b].Add(a);
                }
            var result = new int[count][];
            for (int n = 0; n < count; n++)
            {
                result[n] = new int[sets[n].Count];
                sets[n].CopyTo(result[n]);
            }
            return result;
        }

        private static Vector3[] Normals(Vector3[] positions, int[] triangles)
        {
            var normals = new Vector3[positions.Length];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = MeshSnapshot.Unit(normals[i]);
            return normals;
        }
    }
}
