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
        internal const float Anchor = 1e-4f;
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
            // Laid by an earlier session (Library/ReFit), or laid now and kept there for the next.
            var stored = Load(key, source.worldVertices.Length);
            ReFitBodyRegistration built;
            if (stored != null)
            {
                progress?.Invoke(1f);
                built = new ReFitBodyRegistration(source, stored.registered, stored.confidence, stored.medianGap, stored.p95Gap)
                {
                    timing = "read the bodies laid onto each other by an earlier session (" + stored.timing + ")",
                };
                Captured?.Invoke(built, target);
            }
            else built = Compute(source, sourceTriangleMasks, target, targetIndex, targetTriangleMasks, cancellation, progress, start);
            var kept = stored ?? new CacheEntry { key = key, registered = built.registered, confidence = built.confidence, medianGap = built.medianGap,
                p95Gap = built.p95Gap, timing = built.timing };
            lock (Cache)
            {
                Cache.Insert(0, kept);
                if (Cache.Count > CacheSize) Cache.RemoveAt(Cache.Count - 1);
            }
            if (stored == null) Save(kept);
            return built;
        }

        // Saved registrations: a file per key, the newest kept. The version changes with the algorithm's revision and the
        // registration's own steps, so a file of an earlier one is never read.
        private const int FileVersion = ReFitCacheIdentity.Revision * 100 + 2;
        private const int FilesKept = 12;
        private const uint FileMagic = 0x52544652;

        private static string FilePath(ulong key) =>
            string.IsNullOrEmpty(ReFitCaches.LibraryDirectory) ? null
                : System.IO.Path.Combine(ReFitCaches.LibraryDirectory, "registration-" + key.ToString("x16") + ".bin");

        private static CacheEntry Load(ulong key, int vertexCount)
        {
            string path = FilePath(key);
            if (path == null || !System.IO.File.Exists(path)) return null;
            try
            {
                using (var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() != FileMagic || reader.ReadInt32() != FileVersion || reader.ReadUInt64() != key) return null;
                    int count = reader.ReadInt32();
                    if (count != vertexCount) return null;
                    var entry = new CacheEntry { key = key, registered = new Vector3[count], confidence = new float[count] };
                    for (int i = 0; i < count; i++) entry.registered[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    for (int i = 0; i < count; i++) entry.confidence[i] = reader.ReadSingle();
                    entry.medianGap = reader.ReadSingle();
                    entry.p95Gap = reader.ReadSingle();
                    entry.timing = reader.ReadString();
                    // Anything after the record is another layout or a damaged file.
                    if (reader.BaseStream.Position != reader.BaseStream.Length) return null;
                    return entry;
                }
            }
            catch (Exception)
            {
                // A file cut short (the editor closed while writing) or of another layout: laid again, and written anew.
                return null;
            }
        }

        private static void Save(CacheEntry entry)
        {
            string path = FilePath(entry.key);
            if (path == null) return;
            try
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                System.IO.Directory.CreateDirectory(directory);
                string partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(partial)))
                {
                    writer.Write(FileMagic);
                    writer.Write(FileVersion);
                    writer.Write(entry.key);
                    writer.Write(entry.registered.Length);
                    foreach (var v in entry.registered) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                    foreach (float c in entry.confidence) writer.Write(c);
                    writer.Write(entry.medianGap);
                    writer.Write(entry.p95Gap);
                    writer.Write(entry.timing ?? "");
                }
                // Swapped in whole: a reader sees the old file or the new one, never a part.
                if (System.IO.File.Exists(path)) System.IO.File.Replace(partial, path, null);
                else System.IO.File.Move(partial, path);
                var files = new System.IO.DirectoryInfo(directory).GetFiles("registration-*.bin");
                if (files.Length <= FilesKept) return;
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = FilesKept; i < files.Length; i++) files[i].Delete();
            }
            catch (Exception)
            {
                // Only a later session's time depends on the file: the result is in memory.
            }
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

        /// <summary>
        /// Forgets the registrations kept for reuse, in memory and, with <paramref name="files"/>, in Library/ReFit (tests timing
        /// a full run).
        /// </summary>
        internal static void ClearCache(bool files = true)
        {
            lock (Cache) Cache.Clear();
            if (!files) return;
            try
            {
                string directory = ReFitCaches.LibraryDirectory;
                if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
                    foreach (var file in System.IO.Directory.GetFiles(directory, "registration-*.bin")) System.IO.File.Delete(file);
            }
            catch (Exception)
            {
                // A file in use stays; a later key never matches it wrongly (the key covers every input).
            }
        }

        // FNV-1a over the bits of every input the registration reads: the source's positions, triangles, hidden skin and
        // regions (its normals come from its positions), where the skeletons start it, and the target's positions, triangles,
        // hidden skin, regions and face normals as the passes read them (a collapsed triangle takes its corners' normals).
        // Settings are not read: the passes' constants are the file version's.
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
            Vectors(target.FaceNormals ?? target.CacheFaceNormals());
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
            var coarse = Coarse.Build(nodes, graph, CoarseCell);

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
            // Each node's nearest target triangle in the pass before: a node moves little from one pass to the next, so the
            // search starts bounded by it (the answer is the unseeded one, see SurfaceBvh).
            var seed = new int[nodeCount];
            for (int n = 0; n < nodeCount; n++) seed[n] = -1;
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
                    var nearest = targetIndex.ClosestPoint(p, reach, facing, seed[n]);
                    if (nearest.found) seed[n] = nearest.triangle;
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
                iterations += Solve(move, weights, targets, graph, coarse, Stiffness[pass], cancellation);
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
                timing = $"{nodeCount} skin points ({coarse.Count} coarse), {Stiffness.Length} passes: queries {queryMs:F0} ms, solves {solveMs:F0} ms ({iterations} iterations)",
            };
            Captured?.Invoke(result, target);
            return result;
        }

        /// <summary>The welded skin's edges as compressed rows: the neighbours of node i are next[start[i] .. start[i + 1]).</summary>
        internal sealed class Graph
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
        // Each solve is this many cycles: the coarse correction, then the conjugate gradients from there (restarted).
        private const int Cycles = 2;
        // Coarse nodes gather the welded skin in cubes this large; the coarse system is solved this far.
        internal const float CoarseCell = .03f;
        private const int CoarseIterations = 64;

        /// <summary>
        /// (W + stiffness L + Anchor I) x = W targets + Anchor x0, the three axes at once (each its own conjugate gradients, from the
        /// current move), node ranges in parallel. The conjugate gradients with a diagonal preconditioner smooth what changes
        /// from node to node but barely move what changes over the whole body, which the cap on iterations then leaves where it
        /// was: each cycle first moves the skin by the correction of a coarse copy of the system (the skin gathered in
        /// <see cref="CoarseCell"/> cubes, the same energy restricted to moves constant over each), which lowers the energy and
        /// never raises it, then iterates from there. Returns the iterations taken by the slowest axis.
        /// </summary>
        internal static unsafe int Solve(Vector3[] move, float[] weights, Vector3[] targets, Graph graph, Coarse coarse, float stiffness, CancellationToken cancellation)
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
            double s = stiffness;
            for (int i = 0; i < n; i++)
            {
                // The matrix's diagonal as ReFitRegistrationKernels.Multiply forms it, in double precision.
                diagonal[i] = (double)weights[i] + Anchor + s * graph.Degree(i);
                for (int axis = 0; axis < 3; axis++)
                {
                    x[3 * i + axis] = move[i][axis];
                    b[3 * i + axis] = weights[i] * targets[i][axis] + Anchor * move[i][axis];
                }
            }
            // The inner loops (ReFitRegistrationKernels) over fixed ranges of nodes; the arrays are pinned per call.
            void Multiply(double[] input, double[] output, bool dot)
            {
                Parallel.For(0, ranges, range =>
                {
                    fixed (double* pin = input, pout = output, ppartial = partial)
                    fixed (int* pstart = graph.start, pnext = graph.next)
                    fixed (float* pweights = weights)
                        ReFitRegistrationLoops.Multiply(pin, pout, pstart, pnext, pweights, Anchor, s, range * span, Math.Min(n, (range + 1) * span),
                            dot ? 1 : 0, ppartial + range * 9);
                });
            }

            double[] Sum(int slot)
            {
                var total = new double[3];
                for (int range = 0; range < ranges; range++)
                    for (int axis = 0; axis < 3; axis++) total[axis] += partial[range * 9 + slot * 3 + axis];
                return total;
            }

            // Conjugate gradients from x for at most budget iterations; converged when every axis met its tolerance.
            int Iterate(int budget, out bool converged)
            {
                // r = b - A x, z = r / diag, p = z; rz and |b|² per axis.
                Multiply(x, q, false);
                Parallel.For(0, ranges, range =>
                {
                    fixed (double* pb = b, pq = q, pr = r, pz = z, pp = p, pd = diagonal, ppartial = partial)
                        ReFitRegistrationLoops.Start(pb, pq, pr, pz, pp, pd, range * span, Math.Min(n, (range + 1) * span), ppartial + range * 9);
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
                for (; iteration < budget && (active[0] || active[1] || active[2]); iteration++)
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
                        fixed (double* px = x, pr = r, pz = z, pp = p, pq2 = q, pd = diagonal, ppartial = partial)
                            ReFitRegistrationLoops.Step(px, pr, pz, pp, pq2, pd, alpha[0], alpha[1], alpha[2], range * span, Math.Min(n, (range + 1) * span),
                                ppartial + range * 9);
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
                    int mask = (active[0] ? 1 : 0) | (active[1] ? 2 : 0) | (active[2] ? 4 : 0);
                    Parallel.For(0, ranges, range =>
                    {
                        fixed (double* pp = p, pz = z)
                            ReFitRegistrationLoops.Direction(pp, pz, beta[0], beta[1], beta[2], mask, range * span, Math.Min(n, (range + 1) * span));
                    });
                }
                converged = !(active[0] || active[1] || active[2]);
                return iteration;
            }

            int taken = 0;
            // Without a coarse copy, one uninterrupted run of the whole budget (the solve before the coarse correction).
            int cycles = coarse != null ? Cycles : 1;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                if (coarse != null)
                {
                    Multiply(x, q, false);
                    coarse.Correct(x, b, q, weights, stiffness);
                }
                taken += Iterate(SolverIterations / cycles, out bool converged);
                if (converged || cancellation.IsCancellationRequested) break;
            }
            cancellation.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++) move[i] = new Vector3((float)x[3 * i], (float)x[3 * i + 1], (float)x[3 * i + 2]);
            return taken;
        }

        /// <summary>
        /// The welded skin gathered in cubes (aggregates): the coarse copy of a solve's system, (P^T A P) e = P^T (b - A x) with P
        /// the piecewise constant map of each node to its cube. Its matrix has the cubes' summed data weights and anchor on the
        /// diagonal and the stiffness times the number of skin edges between two cubes off it (a graph Laplacian of the cubes),
        /// built in node order and solved serially: the same bits on every machine.
        /// </summary>
        internal sealed class Coarse
        {
            private int[] of;           // per node, its cube
            private int count;
            public int Count => count;

            /// <summary>The coarse node of a skin node (tests).</summary>
            internal int Of(int node) => of[node];
            private int[] start, next;  // per cube, the cubes it shares edges with (sorted) ...
            private double[] edges;     // ... and how many
            private double[] cut;       // per cube, its edges to other cubes
            private double[] size;      // per cube, its nodes

            public static Coarse Build(Vector3[] nodes, Graph graph, float cell)
            {
                int n = nodes.Length;
                var coarse = new Coarse { of = new int[n] };
                // A coarse node is the skin of one cube connected within it: two surfaces passing through a cube (the inner
                // thighs, an arm against the torso, eyes in their sockets) are never tied together.
                var cubeOf = new Vector3Int[n];
                for (int i = 0; i < n; i++) cubeOf[i] = Vector3Int.FloorToInt(nodes[i] / cell);
                var root = new int[n];
                for (int i = 0; i < n; i++) root[i] = i;
                int Find(int a) { while (root[a] != a) { root[a] = root[root[a]]; a = root[a]; } return a; }
                for (int i = 0; i < n; i++)
                    for (int e = graph.start[i]; e < graph.start[i + 1]; e++)
                    {
                        int j = graph.next[e];
                        if (cubeOf[j] != cubeOf[i]) continue;
                        int a = Find(i), b = Find(j);
                        if (a != b) root[Math.Max(a, b)] = Math.Min(a, b);
                    }
                // Numbered by their lowest node: the same on every machine.
                var numberOf = new Dictionary<int, int>();
                for (int i = 0; i < n; i++)
                {
                    int r = Find(i);
                    if (!numberOf.TryGetValue(r, out int c)) numberOf[r] = c = numberOf.Count;
                    coarse.of[i] = c;
                }
                int cubes = numberOf.Count;
                int count = coarse.count = cubes;
                coarse.size = new double[count];
                coarse.cut = new double[count];
                for (int i = 0; i < n; i++) coarse.size[coarse.of[i]]++;
                // Edges between cubes, from each node's side (both directions), sorted and counted.
                var pairs = new List<long>();
                for (int i = 0; i < n; i++)
                    for (int e = graph.start[i]; e < graph.start[i + 1]; e++)
                    {
                        int a = coarse.of[i], b = coarse.of[graph.next[e]];
                        if (a == b) continue;
                        pairs.Add((long)a * count + b);
                        coarse.cut[a]++;
                    }
                pairs.Sort();
                coarse.start = new int[count + 1];
                var nextList = new List<int>();
                var edgeList = new List<double>();
                int row = 0;
                for (int k = 0; k < pairs.Count; k++)
                {
                    int a = (int)(pairs[k] / count), b = (int)(pairs[k] % count);
                    while (row < a) coarse.start[++row] = nextList.Count;
                    if (nextList.Count > coarse.start[a] && nextList[nextList.Count - 1] == b) { edgeList[edgeList.Count - 1]++; continue; }
                    nextList.Add(b);
                    edgeList.Add(1);
                }
                while (row < count) coarse.start[++row] = nextList.Count;
                coarse.next = nextList.ToArray();
                coarse.edges = edgeList.ToArray();
                return coarse;
            }

            /// <summary>x += P e, e the coarse solve of P^T (b - Ax) (three interleaved axes; <paramref name="ax"/> holds A x).</summary>
            public void Correct(double[] x, double[] b, double[] ax, float[] weights, float stiffness)
            {
                int n = of.Length;
                var diagonal = new double[count];
                var rc = new double[3 * count];
                for (int i = 0; i < n; i++)
                {
                    int c = of[i];
                    diagonal[c] += weights[i];
                    for (int axis = 0; axis < 3; axis++) rc[3 * c + axis] += b[3 * i + axis] - ax[3 * i + axis];
                }
                for (int c = 0; c < count; c++) diagonal[c] += Anchor * size[c] + stiffness * cut[c];

                void Multiply(double[] input, double[] output)
                {
                    for (int c = 0; c < count; c++)
                    {
                        double s0 = 0, s1 = 0, s2 = 0;
                        for (int e = start[c]; e < start[c + 1]; e++)
                        {
                            int j = 3 * next[e];
                            double weight = edges[e];
                            s0 += weight * input[j]; s1 += weight * input[j + 1]; s2 += weight * input[j + 2];
                        }
                        int k = 3 * c;
                        output[k] = diagonal[c] * input[k] - stiffness * s0;
                        output[k + 1] = diagonal[c] * input[k + 1] - stiffness * s1;
                        output[k + 2] = diagonal[c] * input[k + 2] - stiffness * s2;
                    }
                }

                // Jacobi-preconditioned conjugate gradients per axis, from zero.
                var e3 = new double[3 * count];
                var r = rc;
                var z = new double[3 * count];
                var p = new double[3 * count];
                var q = new double[3 * count];
                var rz = new double[3];
                for (int c = 0; c < count; c++)
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int k = 3 * c + axis;
                        z[k] = r[k] / diagonal[c];
                        p[k] = z[k];
                        rz[axis] += r[k] * z[k];
                    }
                var active = new bool[3];
                for (int axis = 0; axis < 3; axis++) active[axis] = rz[axis] > 1e-30;
                for (int iteration = 0; iteration < CoarseIterations && (active[0] || active[1] || active[2]); iteration++)
                {
                    Multiply(p, q);
                    var pq = new double[3];
                    for (int k = 0; k < 3 * count; k++) pq[k % 3] += p[k] * q[k];
                    var alpha = new double[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (active[axis] && pq[axis] <= 0) active[axis] = false;
                        alpha[axis] = active[axis] ? rz[axis] / pq[axis] : 0;
                    }
                    var next3 = new double[3];
                    for (int c = 0; c < count; c++)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int k = 3 * c + axis;
                            e3[k] += alpha[axis] * p[k];
                            r[k] -= alpha[axis] * q[k];
                            z[k] = r[k] / diagonal[c];
                            next3[axis] += r[k] * z[k];
                        }
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (!active[axis]) continue;
                        double beta = next3[axis] / rz[axis];
                        rz[axis] = next3[axis];
                        if (rz[axis] <= 1e-30) { active[axis] = false; continue; }
                        for (int c = 0; c < count; c++) p[3 * c + axis] = z[3 * c + axis] + beta * p[3 * c + axis];
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    int k = 3 * of[i];
                    x[3 * i] += e3[k]; x[3 * i + 1] += e3[k + 1]; x[3 * i + 2] += e3[k + 2];
                }
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
