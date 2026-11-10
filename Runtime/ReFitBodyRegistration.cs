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
        /// <summary>Where the time went: the target queries and the smoothing solves, with their iterations (diagnostics).</summary>
        public string timing;

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

        /// <summary>
        /// The source laid onto a body, carried with it by its shapes: each registered point moves as the skin nearest to it on
        /// <paramref name="laidOnto"/> (<paramref name="motion"/> per vertex). Laid onto the target without the shapes it shows
        /// (the bodies alike), the source follows them onto <paramref name="target"/>. The gaps are those of the laying.
        /// </summary>
        public ReFitBodyRegistration Carried(MeshSnapshot laidOnto, SurfaceBvh laidIndex, Vector3[] motion, MeshSnapshot target)
        {
            var moved = new Vector3[registered.Length];
            Parallel.For(0, moved.Length, v =>
            {
                moved[v] = registered[v];
                var hit = laidIndex.ClosestPoint(registered[v], Reach);
                if (!hit.found) return;
                int t = hit.triangle * 3;
                moved[v] += motion[laidOnto.triangles[t]] * hit.bary.x + motion[laidOnto.triangles[t + 1]] * hit.bary.y +
                            motion[laidOnto.triangles[t + 2]] * hit.bary.z;
            });
            var carried = new ReFitBodyRegistration(source, moved, confidence, medianGap, p95Gap) { timing = timing };
            Captured?.Invoke(carried, target);
            return carried;
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
            // The same bodies laid onto each other again (the next piece of an outfit, a refit run again): the last results are
            // kept by the content of everything the registration reads.
            ulong key = Key(source, sourceTriangleMasks, target, targetTriangleMasks, start);
            lock (Cache)
                foreach (var entry in Cache)
                    if (entry.key == key && entry.registered.Length == source.worldVertices.Length)
                    {
                        Cache.Remove(entry);
                        Cache.Insert(0, entry);
                        progress?.Invoke(1f);
                        var reused = new ReFitBodyRegistration(source, entry.registered, entry.confidence, entry.medianGap, entry.p95Gap)
                        {
                            timing = "reused the bodies laid onto each other by an earlier refit (" + entry.timing + ")",
                        };
                        Captured?.Invoke(reused, target);
                        return reused;
                    }
            var built = Compute(source, sourceTriangleMasks, target, targetIndex, targetTriangleMasks, cancellation, progress, start);
            lock (Cache)
            {
                Cache.Insert(0, new CacheEntry { key = key, registered = built.registered, confidence = built.confidence, medianGap = built.medianGap,
                    p95Gap = built.p95Gap, timing = built.timing });
                if (Cache.Count > CacheSize) Cache.RemoveAt(Cache.Count - 1);
            }
            return built;
        }

        // A few registrations: the pieces of one outfit, a source on a couple of targets.
        private const int CacheSize = 3;
        private sealed class CacheEntry
        {
            public ulong key;
            public Vector3[] registered;
            public float[] confidence;
            public float medianGap, p95Gap;
            public string timing;
        }
        private static readonly List<CacheEntry> Cache = new List<CacheEntry>();

        /// <summary>Forgets the registrations kept for reuse (tests timing a full run).</summary>
        internal static void ClearCache() { lock (Cache) Cache.Clear(); }

        // FNV-1a over the bits of every input the registration reads.
        private static ulong Key(MeshSnapshot source, int[] sourceMasks, MeshSnapshot target, int[] targetMasks, Vector3[] start)
        {
            ulong hash = 14695981039346656037UL;
            void Add(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
            void Vectors(Vector3[] values)
            {
                Add(values?.Length ?? -1);
                if (values == null) return;
                foreach (var v in values)
                {
                    Add(BitConverter.SingleToInt32Bits(v.x)); Add(BitConverter.SingleToInt32Bits(v.y)); Add(BitConverter.SingleToInt32Bits(v.z));
                }
            }
            void Ints(int[] values) { Add(values?.Length ?? -1); if (values != null) foreach (int v in values) Add(v); }
            void Flags(bool[] values) { Add(values?.Length ?? -1); if (values != null) for (int i = 0; i < values.Length; i++) if (values[i]) Add(i); }
            Vectors(source.worldVertices); Ints(source.triangles); Flags(source.hiddenTriangles); Ints(sourceMasks);
            Vectors(target.worldVertices); Ints(target.triangles); Flags(target.hiddenTriangles); Ints(targetMasks);
            Vectors(start);
            return hash;
        }

        private static ReFitBodyRegistration Compute(MeshSnapshot source, int[] sourceTriangleMasks, MeshSnapshot target, SurfaceBvh targetIndex,
            int[] targetTriangleMasks, CancellationToken cancellation, Action<float> progress, Vector3[] start)
        {
            int vertexCount = source.worldVertices.Length;
            var weld = Weld(source.worldVertices, out int nodeCount, out var nodeVertex);
            var nodes = new Vector3[nodeCount];
            for (int n = 0; n < nodeCount; n++) nodes[n] = source.worldVertices[nodeVertex[n]];
            var triangles = new int[source.triangles.Length];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = weld[source.triangles[i]];
            var graph = Graph.Build(triangles, nodeCount);

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
            var faceNormals = target.FaceNormals ?? target.CacheFaceNormals();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double queryMs = 0, solveMs = 0;
            int iterations = 0;
            for (int pass = 0; pass < Stiffness.Length; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                watch.Restart();
                for (int n = 0; n < nodeCount; n++) position[n] = nodes[n] + move[n];
                var normals = Normals(position, triangles);
                float reach = Reach, falloff = Falloff[pass];
                Parallel.For(0, nodeCount, n =>
                {
                    weights[n] = 0f;
                    if (!visible[n] || normals[n] == Vector3.zero) return;
                    var p = position[n];
                    var normal = normals[n];
                    var facing = new FacingFilter(faceNormals, normal, Facing, masks[n], targetTriangleMasks);
                    var nearest = targetIndex.ClosestPoint(p, reach, facing);
                    float limit = nearest.found ? Mathf.Min(reach, nearest.distance * AlongFactor + AlongSlack) : reach;
                    var outward = targetIndex.Raycast(p, normal, limit, facing);
                    var inward = targetIndex.Raycast(p, -normal, outward.found ? outward.distance : limit, facing);
                    var along = inward.found ? inward : outward;
                    SurfaceBvh.Hit hit = along.found ? along : nearest;
                    if (!hit.found) return;
                    float agreement = Vector3.Dot(faceNormals[hit.triangle], normal);
                    float d = hit.distance / falloff;
                    weights[n] = agreement * agreement * Mathf.Exp(-d * d);
                    targets[n] = hit.position - nodes[n];
                });
                queryMs += watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                iterations += Solve(move, weights, targets, graph, Stiffness[pass], cancellation);
                solveMs += watch.Elapsed.TotalMilliseconds;
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
            var result = new ReFitBodyRegistration(source, registered, confidence, median, p95)
            {
                timing = $"{nodeCount} skin points, {Stiffness.Length} passes: queries {queryMs:F0} ms, solves {solveMs:F0} ms ({iterations} iterations)",
            };
            Captured?.Invoke(result, target);
            return result;
        }

        /// <summary>The welded skin's edges as compressed rows: the neighbours of node i are next[start[i] .. start[i + 1]).</summary>
        private sealed class Graph
        {
            public int[] start, next;

            public static Graph Build(int[] triangles, int count)
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
                var graph = new Graph { start = new int[count + 1] };
                for (int n = 0; n < count; n++) graph.start[n + 1] = graph.start[n] + sets[n].Count;
                graph.next = new int[graph.start[count]];
                // Sorted: the solver sums neighbours in this order, which must not be a hash set's.
                for (int n = 0; n < count; n++)
                {
                    sets[n].CopyTo(graph.next, graph.start[n]);
                    Array.Sort(graph.next, graph.start[n], sets[n].Count);
                }
                return graph;
            }

            public int Degree(int n) => start[n + 1] - start[n];
        }

        // Nodes are worked in at most this many ranges, whatever the machine: the dot products are summed per range, then over
        // the ranges in order, so the same count on every machine gives the same bits (a count from the cores would not).
        private const int MaxRanges = 64;

        /// <summary>
        /// (W + stiffness L + Anchor I) x = W targets + Anchor x0, the three axes at once (each its own conjugate gradients, from the
        /// current move), node ranges in parallel. Returns the iterations taken by the slowest axis.
        /// </summary>
        private static int Solve(Vector3[] move, float[] weights, Vector3[] targets, Graph graph, float stiffness, CancellationToken cancellation)
        {
            int n = move.Length;
            int ranges = Math.Max(1, Math.Min(n / 512 + 1, MaxRanges));
            int span = (n + ranges - 1) / ranges;
            // Interleaved by axis: [3i + axis].
            var x = new double[3 * n];
            var b = new double[3 * n];
            var r = new double[3 * n];
            var z = new double[3 * n];
            var p = new double[3 * n];
            var q = new double[3 * n];
            var diagonal = new double[n];
            var partial = new double[ranges * 3 * 3];
            for (int i = 0; i < n; i++)
            {
                diagonal[i] = weights[i] + stiffness * graph.Degree(i) + Anchor;
                for (int axis = 0; axis < 3; axis++)
                {
                    x[3 * i + axis] = move[i][axis];
                    b[3 * i + axis] = weights[i] * targets[i][axis] + Anchor * move[i][axis];
                }
            }

            void Multiply(double[] input, double[] output, bool dot)
            {
                Parallel.For(0, ranges, range =>
                {
                    double d0 = 0, d1 = 0, d2 = 0;
                    int end = Math.Min(n, (range + 1) * span);
                    for (int i = range * span; i < end; i++)
                    {
                        double s0 = 0, s1 = 0, s2 = 0;
                        for (int e = graph.start[i]; e < graph.start[i + 1]; e++)
                        {
                            int j = 3 * graph.next[e];
                            s0 += input[j]; s1 += input[j + 1]; s2 += input[j + 2];
                        }
                        double self = weights[i] + Anchor + stiffness * (graph.start[i + 1] - graph.start[i]);
                        int k = 3 * i;
                        double o0 = output[k] = self * input[k] - stiffness * s0;
                        double o1 = output[k + 1] = self * input[k + 1] - stiffness * s1;
                        double o2 = output[k + 2] = self * input[k + 2] - stiffness * s2;
                        if (dot) { d0 += input[k] * o0; d1 += input[k + 1] * o1; d2 += input[k + 2] * o2; }
                    }
                    int at = range * 9;
                    partial[at] = d0; partial[at + 1] = d1; partial[at + 2] = d2;
                });
            }

            double[] Sum(int slot)
            {
                var total = new double[3];
                for (int range = 0; range < ranges; range++)
                    for (int axis = 0; axis < 3; axis++) total[axis] += partial[range * 9 + slot * 3 + axis];
                return total;
            }

            // r = b - A x, z = r / diag, p = z; rz and |b|² per axis.
            Multiply(x, q, false);
            Parallel.For(0, ranges, range =>
            {
                double rz0 = 0, rz1 = 0, rz2 = 0, b0 = 0, b1 = 0, b2 = 0;
                int end = Math.Min(n, (range + 1) * span);
                for (int i = range * span; i < end; i++)
                {
                    double inverse = 1.0 / diagonal[i];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int k = 3 * i + axis;
                        r[k] = b[k] - q[k];
                        z[k] = r[k] * inverse;
                        p[k] = z[k];
                    }
                    rz0 += r[3 * i] * z[3 * i]; rz1 += r[3 * i + 1] * z[3 * i + 1]; rz2 += r[3 * i + 2] * z[3 * i + 2];
                    b0 += b[3 * i] * b[3 * i]; b1 += b[3 * i + 1] * b[3 * i + 1]; b2 += b[3 * i + 2] * b[3 * i + 2];
                }
                int at = range * 9;
                partial[at] = rz0; partial[at + 1] = rz1; partial[at + 2] = rz2;
                partial[at + 3] = b0; partial[at + 4] = b1; partial[at + 5] = b2;
            });
            var rz = Sum(0);
            var norm = Sum(1);
            var tolerance = new double[3];
            var active = new bool[3];
            for (int axis = 0; axis < 3; axis++)
            {
                tolerance[axis] = Math.Max(norm[axis], 1e-20) * 1e-12;
                active[axis] = rz[axis] > 1e-30;
            }
            var alpha = new double[3];
            var beta = new double[3];
            int iteration = 0;
            for (; iteration < SolverIterations && (active[0] || active[1] || active[2]); iteration++)
            {
                if ((iteration & 31) == 0 && cancellation.IsCancellationRequested) break;
                Multiply(p, q, true);
                var pq = Sum(0);
                for (int axis = 0; axis < 3; axis++)
                {
                    if (active[axis] && pq[axis] <= 0) active[axis] = false;
                    alpha[axis] = active[axis] ? rz[axis] / pq[axis] : 0;
                }
                Parallel.For(0, ranges, range =>
                {
                    double res0 = 0, res1 = 0, res2 = 0, next0 = 0, next1 = 0, next2 = 0;
                    int end = Math.Min(n, (range + 1) * span);
                    for (int i = range * span; i < end; i++)
                    {
                        double inverse = 1.0 / diagonal[i];
                        int k = 3 * i;
                        x[k] += alpha[0] * p[k]; r[k] -= alpha[0] * q[k]; z[k] = r[k] * inverse;
                        x[k + 1] += alpha[1] * p[k + 1]; r[k + 1] -= alpha[1] * q[k + 1]; z[k + 1] = r[k + 1] * inverse;
                        x[k + 2] += alpha[2] * p[k + 2]; r[k + 2] -= alpha[2] * q[k + 2]; z[k + 2] = r[k + 2] * inverse;
                        res0 += r[k] * r[k]; res1 += r[k + 1] * r[k + 1]; res2 += r[k + 2] * r[k + 2];
                        next0 += r[k] * z[k]; next1 += r[k + 1] * z[k + 1]; next2 += r[k + 2] * z[k + 2];
                    }
                    int at = range * 9;
                    partial[at] = res0; partial[at + 1] = res1; partial[at + 2] = res2;
                    partial[at + 3] = next0; partial[at + 4] = next1; partial[at + 5] = next2;
                });
                var residual = Sum(0);
                var next = Sum(1);
                for (int axis = 0; axis < 3; axis++)
                {
                    beta[axis] = 0;
                    if (!active[axis]) continue;
                    if (residual[axis] < tolerance[axis]) { active[axis] = false; continue; }
                    beta[axis] = next[axis] / rz[axis];
                    rz[axis] = next[axis];
                    if (rz[axis] <= 1e-30) active[axis] = false;
                }
                if (!(active[0] || active[1] || active[2])) { iteration++; break; }
                Parallel.For(0, ranges, range =>
                {
                    int end = Math.Min(n, (range + 1) * span);
                    for (int i = range * span; i < end; i++)
                    {
                        int k = 3 * i;
                        if (active[0]) p[k] = z[k] + beta[0] * p[k];
                        if (active[1]) p[k + 1] = z[k + 1] + beta[1] * p[k + 1];
                        if (active[2]) p[k + 2] = z[k + 2] + beta[2] * p[k + 2];
                    }
                });
            }
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++) move[i] = new Vector3((float)x[3 * i], (float)x[3 * i + 1], (float)x[3 * i + 2]);
            return iteration;
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
