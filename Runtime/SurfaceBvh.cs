using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Which triangles a <see cref="SurfaceBvh"/> query considers. A struct implementing it is passed by value and called
    /// directly (no delegate, nothing allocated per query): the hot queries use one, the others a <see cref="Func{T,TResult}"/>.
    /// </summary>
    public interface ITriangleFilter
    {
        bool Accept(int triangle);
    }

    /// <summary>
    /// Which of up to <see cref="SurfaceBvh.MaxClasses"/> nested or overlapping acceptance classes a triangle belongs to (bit c:
    /// class c), for <see cref="SurfaceBvh.ClosestPoints{T}"/>: one traversal answering what one filtered query per class would.
    /// </summary>
    public interface ITriangleClassifier
    {
        int Classes(int triangle);
    }

    /// <summary>
    /// Triangles facing a direction (dot of the unit face normal at least <c>minimum</c>, from a snapshot's cached face
    /// normals), optionally of a region a mask accepts: the filter most queries use.
    /// </summary>
    public readonly struct FacingFilter : ITriangleFilter
    {
        private readonly Vector3[] faceNormals;
        private readonly Vector3 direction;
        private readonly float minimum;
        private readonly int mask;
        private readonly int[] triangleMasks;
        private readonly bool[] allowed;
        private readonly bool strict;

        /// <param name="faceNormals">Unit face normals by triangle (<see cref="MeshSnapshot.FaceNormals"/>).</param>
        /// <param name="minimum">The least dot product of a face normal with <paramref name="direction"/> accepted.</param>
        /// <param name="triangleMasks">Regions each triangle accepts (0: any), tested against <paramref name="mask"/> (0: any), or null.</param>
        /// <param name="allowed">Triangles that may be found at all, or null for every triangle.</param>
        /// <param name="strict">The dot product must be above <paramref name="minimum"/>, not only at least it.</param>
        public FacingFilter(Vector3[] faceNormals, Vector3 direction, float minimum, int mask = 0, int[] triangleMasks = null, bool[] allowed = null,
            bool strict = false)
        {
            this.faceNormals = faceNormals;
            this.direction = direction;
            this.minimum = minimum;
            this.mask = mask;
            this.triangleMasks = triangleMasks;
            this.allowed = allowed;
            this.strict = strict;
        }

        /// <summary>Faces turned against <paramref name="direction"/> (a negative dot product with it).</summary>
        public static FacingFilter Against(Vector3[] faceNormals, Vector3 direction) => new FacingFilter(faceNormals, -direction, 0f, strict: true);

        public bool Accept(int triangle)
        {
            if (allowed != null && !allowed[triangle]) return false;
            var n = faceNormals[triangle];
            float dot = n.x * direction.x + n.y * direction.y + n.z * direction.z;
            if (strict ? dot <= minimum : dot < minimum) return false;
            return mask == 0 || triangleMasks == null || triangleMasks[triangle] == 0 || (triangleMasks[triangle] & mask) != 0;
        }
    }

    /// <summary>Every triangle but one (a ray leaving a triangle must not meet it again).</summary>
    public readonly struct AllBut : ITriangleFilter
    {
        private readonly int excluded;
        public AllBut(int excluded) => this.excluded = excluded;
        public bool Accept(int triangle) => triangle != excluded;
    }

    /// <summary>
    /// Bounding volume hierarchy over a triangle soup for fast closest-point-on-surface and ray queries, optionally restricted
    /// by a per-triangle filter (used for normal / body-region filtering). Thread-safe for queries once built.
    /// <para>
    /// Query semantics (the contract every layout keeps): a closest-point query returns, among the accepted triangles closer
    /// than the range, the one at the least distance, the lowest triangle index among equally distant ones; a ray returns the
    /// least hit distance below the range, the lowest index among equal ones. Distances are computed by one routine per
    /// triangle from its corners in the snapshot's order, and bounds are widened by a rounding slack, so no node holding a
    /// possible answer is ever pruned. The answer therefore does not depend on how the tree was built, refitted, traversed
    /// or seeded, only on the triangles: a new build rule, a refit or a seeded start returns the same triangle.
    /// </para>
    /// </summary>
    public class SurfaceBvh
    {
        // 32 bytes. Interior nodes (count 0) have their two children at first and first + 1, always after the node itself;
        // leaves hold count triangles from slot first.
        private struct Node
        {
            public Vector3 min, max;
            public int first;
            public int count;
        }

        // A triangle in leaf order: its corners side by side (one or two cache lines per triangle instead of three arrays
        // reached through an index), its snapshot index, and whether it has no area (its closest point is then on its edges).
        private struct Slot
        {
            public Vector3 a, b, c;
            public int triangle;
            public bool collapsed;
        }

        /// <summary>Result of a closest point query.</summary>
        public struct Hit
        {
            public bool found;
            public int triangle;
            public Vector3 position;
            public Vector3 bary;
            public float distance;
        }

        /// <summary>The most acceptance classes one <see cref="ClosestPoints{T}"/> traversal tracks.</summary>
        public const int MaxClasses = 4;

        private const int LeafSize = 4;
        // Binned surface area heuristic: a body's dense hands and face and long limbs overlap heavily under median splits.
        private const int Bins = 16;
        // Below this depth splits follow the heuristic, deeper ones the median (balanced): the traversal stack stays bounded.
        private const int HeuristicDepth = 40;
        private const int StackSize = 128;

        private Node[] nodes;
        private int nodeCount;
        private Slot[] slots;
        // Snapshot triangle -> its slot, -1 for triangles left out.
        private int[] slotOfTriangle;
        // Per node, its parent (-1 for the root); per slot, its leaf: partial refits walk up from the moved triangles.
        private int[] parent;
        private int[] leafOfSlot;
        private int triangleCount;
        // Bounds are widened by this much: the closest point and ray distance of a triangle are computed with rounding, the
        // bounds of its corners exactly, so a triangle's computed distance can lie a few ulps below its box's.
        private float slack;

        private readonly struct NoFilter : ITriangleFilter
        {
            public bool Accept(int triangle) => true;
        }

        private readonly struct FuncFilter : ITriangleFilter
        {
            private readonly Func<int, bool> filter;
            public FuncFilter(Func<int, bool> filter) => this.filter = filter;
            public bool Accept(int triangle) => filter(triangle);
        }

        /// <summary>
        /// Builds a BVH over the given snapshot's world-space triangles, except its hidden ones and, with
        /// <paramref name="include"/>, those it leaves out (queries then never visit them: much faster than filtering them out
        /// when few are left). Hits give the snapshot's own triangle indices.
        /// </summary>
        public static SurfaceBvh Build(MeshSnapshot snapshot, bool[] include = null) =>
            Build(snapshot.worldVertices, snapshot.triangles, snapshot.hiddenTriangles, include);

        /// <summary>Builds a BVH over world-space <paramref name="vertices"/> and <paramref name="triangles"/> (see <see cref="Build(MeshSnapshot,bool[])"/>).</summary>
        public static SurfaceBvh Build(Vector3[] vertices, int[] triangles, bool[] hidden = null, bool[] include = null)
        {
            var bvh = new SurfaceBvh();
            int triCount = triangles.Length / 3;
            bvh.triangleCount = triCount;
            int kept = 0;
            for (int t = 0; t < triCount; t++) if (Kept(t)) kept++;
            var order = new int[kept];
            for (int t = 0, k = 0; t < triCount; t++) if (Kept(t)) order[k++] = t;

            // Per kept triangle k: its centroid (three times it: only order matters) at 3k, its bounds (min, max) at 6k.
            var centroid = new float[kept * 3];
            var box = new float[kept * 6];
            float extent = 0f;
            for (int k = 0; k < kept; k++)
            {
                int t = order[k];
                var pa = vertices[triangles[t * 3]];
                var pb = vertices[triangles[t * 3 + 1]];
                var pc = vertices[triangles[t * 3 + 2]];
                centroid[k * 3] = pa.x + pb.x + pc.x;
                centroid[k * 3 + 1] = pa.y + pb.y + pc.y;
                centroid[k * 3 + 2] = pa.z + pb.z + pc.z;
                var min = Vector3.Min(pa, Vector3.Min(pb, pc));
                var max = Vector3.Max(pa, Vector3.Max(pb, pc));
                box[k * 6] = min.x; box[k * 6 + 1] = min.y; box[k * 6 + 2] = min.z;
                box[k * 6 + 3] = max.x; box[k * 6 + 4] = max.y; box[k * 6 + 5] = max.z;
                extent = Mathf.Max(extent, MaxAbs(min), MaxAbs(max));
            }
            bvh.slack = Slack(extent);

            // Indices into the kept arrays while building, in leaf order once done.
            var items = new int[kept];
            for (int k = 0; k < kept; k++) items[k] = k;
            bvh.nodes = new Node[Mathf.Max(1, kept * 2)];
            bvh.parent = new int[bvh.nodes.Length];
            bvh.nodeCount = 0;
            if (kept > 0)
            {
                var builder = new Builder(bvh, items, centroid, box);
                bvh.nodeCount = 1;
                bvh.parent[0] = -1;
                builder.Split(0, 0, kept, 0);
            }

            bvh.slots = new Slot[kept];
            bvh.slotOfTriangle = new int[triCount];
            for (int t = 0; t < triCount; t++) bvh.slotOfTriangle[t] = -1;
            for (int s = 0; s < kept; s++)
            {
                int t = order[items[s]];
                bvh.slotOfTriangle[t] = s;
                bvh.slots[s] = MakeSlot(vertices, triangles, t);
            }
            bvh.leafOfSlot = new int[kept];
            for (int n = 0; n < bvh.nodeCount; n++)
                if (bvh.nodes[n].count > 0)
                    for (int s = bvh.nodes[n].first; s < bvh.nodes[n].first + bvh.nodes[n].count; s++) bvh.leafOfSlot[s] = n;
            return bvh;

            bool Kept(int t) => (hidden == null || !hidden[t]) && (include == null || include[t]);
        }

        private static Slot MakeSlot(Vector3[] vertices, int[] triangles, int t)
        {
            var a = vertices[triangles[t * 3]];
            var b = vertices[triangles[t * 3 + 1]];
            var c = vertices[triangles[t * 3 + 2]];
            return new Slot { a = a, b = b, c = c, triangle = t, collapsed = Collapsed(a, b, c) };
        }

        private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        // A few ulps of the largest coordinate (float epsilon is 1.2e-7), and something above zero for a surface at the origin.
        private static float Slack(float extent) => extent * 1e-6f + 1e-9f;

        // The tree built over items (indices into the kept triangles' arrays), reordered in place so each leaf's are contiguous.
        private readonly struct Builder
        {
            private readonly SurfaceBvh bvh;
            private readonly int[] items;
            private readonly float[] centroid, box;
            // Per axis and bin: the count, and the bounds (min, max) of the triangles whose centroid falls in it.
            private readonly int[] binCount;
            private readonly float[] binBox;

            public Builder(SurfaceBvh bvh, int[] items, float[] centroid, float[] box)
            {
                this.bvh = bvh;
                this.items = items;
                this.centroid = centroid;
                this.box = box;
                binCount = new int[3 * Bins];
                binBox = new float[3 * Bins * 6];
            }

            public void Split(int node, int start, int count, int depth)
            {
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                float cminX = float.MaxValue, cminY = float.MaxValue, cminZ = float.MaxValue;
                float cmaxX = float.MinValue, cmaxY = float.MinValue, cmaxZ = float.MinValue;
                for (int i = start; i < start + count; i++)
                {
                    int k = items[i], b = k * 6, c = k * 3;
                    if (box[b] < minX) minX = box[b];
                    if (box[b + 1] < minY) minY = box[b + 1];
                    if (box[b + 2] < minZ) minZ = box[b + 2];
                    if (box[b + 3] > maxX) maxX = box[b + 3];
                    if (box[b + 4] > maxY) maxY = box[b + 4];
                    if (box[b + 5] > maxZ) maxZ = box[b + 5];
                    if (centroid[c] < cminX) cminX = centroid[c];
                    if (centroid[c + 1] < cminY) cminY = centroid[c + 1];
                    if (centroid[c + 2] < cminZ) cminZ = centroid[c + 2];
                    if (centroid[c] > cmaxX) cmaxX = centroid[c];
                    if (centroid[c + 1] > cmaxY) cmaxY = centroid[c + 1];
                    if (centroid[c + 2] > cmaxZ) cmaxZ = centroid[c + 2];
                }
                float slack = bvh.slack;
                bvh.nodes[node] = new Node
                {
                    min = new Vector3(minX - slack, minY - slack, minZ - slack),
                    max = new Vector3(maxX + slack, maxY + slack, maxZ + slack),
                    first = start,
                    count = count,
                };
                if (count <= LeafSize) return;

                var cmin = new Vector3(cminX, cminY, cminZ);
                var cmax = new Vector3(cmaxX, cmaxY, cmaxZ);
                int half = depth < HeuristicDepth ? HeuristicSplit(start, count, cmin, cmax) : -1;
                if (half <= 0 || half >= count)
                {
                    // The median of the longest centroid axis: always splits, and keeps the tree balanced.
                    var size = cmax - cmin;
                    int axis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
                    half = count / 2;
                    SelectMedian(start, count, half, axis);
                }

                int left = bvh.nodeCount;
                bvh.nodeCount += 2;
                if (bvh.nodeCount > bvh.nodes.Length)
                {
                    Array.Resize(ref bvh.nodes, bvh.nodes.Length * 2);
                    Array.Resize(ref bvh.parent, bvh.nodes.Length);
                }
                bvh.nodes[node].first = left;
                bvh.nodes[node].count = 0;
                bvh.parent[left] = node;
                bvh.parent[left + 1] = node;
                Split(left, start, half, depth + 1);
                Split(left + 1, start + half, count - half, depth + 1);
            }

            // Partitions items[start, start + count) at the cheapest of the bin boundaries along each axis (the surface area
            // heuristic) and returns the size of the first part, or -1 when no boundary separates the centroids.
            private int HeuristicSplit(int start, int count, Vector3 cmin, Vector3 cmax)
            {
                Span<float> lo = stackalloc float[3];
                Span<float> scale = stackalloc float[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    lo[axis] = cmin[axis];
                    float span = cmax[axis] - lo[axis];
                    scale[axis] = span > 0f ? Bins / span : 0f;
                }
                for (int b = 0; b < 3 * Bins; b++)
                {
                    binCount[b] = 0;
                    int o = b * 6;
                    binBox[o] = binBox[o + 1] = binBox[o + 2] = float.MaxValue;
                    binBox[o + 3] = binBox[o + 4] = binBox[o + 5] = float.MinValue;
                }
                // One pass bins every triangle along the three axes.
                for (int i = start; i < start + count; i++)
                {
                    int k = items[i], c = k * 3, kb = k * 6;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (scale[axis] == 0f) continue;
                        int b = axis * Bins + Bin(centroid[c + axis], lo[axis], scale[axis]);
                        binCount[b]++;
                        int o = b * 6;
                        if (box[kb] < binBox[o]) binBox[o] = box[kb];
                        if (box[kb + 1] < binBox[o + 1]) binBox[o + 1] = box[kb + 1];
                        if (box[kb + 2] < binBox[o + 2]) binBox[o + 2] = box[kb + 2];
                        if (box[kb + 3] > binBox[o + 3]) binBox[o + 3] = box[kb + 3];
                        if (box[kb + 4] > binBox[o + 4]) binBox[o + 4] = box[kb + 4];
                        if (box[kb + 5] > binBox[o + 5]) binBox[o + 5] = box[kb + 5];
                    }
                }

                Span<float> rightCost = stackalloc float[Bins];
                float bestCost = float.MaxValue;
                int bestAxis = -1, bestBoundary = -1;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (scale[axis] == 0f) continue;
                    int first = axis * Bins;
                    // Sweep from the right: cost of bins b.. on their own.
                    var rmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var rmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    int rcount = 0;
                    for (int b = Bins - 1; b > 0; b--)
                    {
                        rcount += binCount[first + b];
                        if (binCount[first + b] > 0) Grow(ref rmin, ref rmax, (first + b) * 6);
                        rightCost[b] = rcount > 0 ? Area(rmin, rmax) * rcount : 0f;
                    }
                    var lmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var lmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    int lcount = 0;
                    for (int b = 0; b < Bins - 1; b++)
                    {
                        lcount += binCount[first + b];
                        if (binCount[first + b] > 0) Grow(ref lmin, ref lmax, (first + b) * 6);
                        if (lcount == 0 || lcount == count) continue;
                        float cost = Area(lmin, lmax) * lcount + rightCost[b + 1];
                        if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestBoundary = b + 1; }
                    }
                }
                if (bestAxis < 0) return -1;

                float low = lo[bestAxis], binScale = scale[bestAxis];
                int i0 = start, j0 = start + count - 1;
                while (i0 <= j0)
                {
                    if (Bin(centroid[items[i0] * 3 + bestAxis], low, binScale) < bestBoundary) i0++;
                    else
                    {
                        int swap = items[i0]; items[i0] = items[j0]; items[j0] = swap;
                        j0--;
                    }
                }
                return i0 - start;
            }

            private void Grow(ref Vector3 min, ref Vector3 max, int o)
            {
                min = Vector3.Min(min, new Vector3(binBox[o], binBox[o + 1], binBox[o + 2]));
                max = Vector3.Max(max, new Vector3(binBox[o + 3], binBox[o + 4], binBox[o + 5]));
            }

            private static int Bin(float value, float lo, float scale)
            {
                int b = (int)((value - lo) * scale);
                return b < 0 ? 0 : b >= Bins ? Bins - 1 : b;
            }

            private static float Area(Vector3 min, Vector3 max)
            {
                var d = max - min;
                return d.x * d.y + d.y * d.z + d.z * d.x;
            }

            // Reorders items[start, start + count) so that the item of rank k (by centroid on axis) is at start + k, those
            // before it no larger and those after no smaller (Hoare partitioning around the middle element).
            private void SelectMedian(int start, int count, int k, int axis)
            {
                var order = items;
                var key = centroid;
                int lo = start, hi = start + count - 1, target = start + k;
                while (lo < hi)
                {
                    float pivot = key[order[(lo + hi) >> 1] * 3 + axis];
                    int i = lo, j = hi;
                    while (i <= j)
                    {
                        while (key[order[i] * 3 + axis] < pivot) i++;
                        while (key[order[j] * 3 + axis] > pivot) j--;
                        if (i <= j)
                        {
                            int swap = order[i]; order[i] = order[j]; order[j] = swap;
                            i++; j--;
                        }
                    }
                    if (target <= j) hi = j;
                    else if (target >= i) lo = i;
                    else return;
                }
            }
        }

        /// <summary>
        /// Moves the hierarchy onto <paramref name="snapshot"/>: the same triangles (the snapshot it was built from, or one with
        /// the same triangles) at new positions. The tree keeps its order and only its bounds follow, in linear time: queries
        /// return what a new build would (see the class remarks), and get slower the farther the surface moves from where it
        /// was built.
        /// </summary>
        public void Refit(MeshSnapshot snapshot) => Refit(snapshot.worldVertices, snapshot.triangles);

        /// <summary>See <see cref="Refit(MeshSnapshot)"/>.</summary>
        public void Refit(Vector3[] vertices, int[] triangles)
        {
            if (triangles.Length / 3 != triangleCount) throw new ArgumentException("Refit needs the triangles the hierarchy was built on.", nameof(triangles));
            float extent = 0f;
            for (int s = 0; s < slots.Length; s++)
            {
                slots[s] = MakeSlot(vertices, triangles, slots[s].triangle);
                extent = Mathf.Max(extent, MaxAbs(Vector3.Min(slots[s].a, Vector3.Min(slots[s].b, slots[s].c))),
                    MaxAbs(Vector3.Max(slots[s].a, Vector3.Max(slots[s].b, slots[s].c))));
            }
            slack = Slack(extent);
            // Children always follow their parent in the node array: bounds settle from the last node back to the root.
            for (int n = nodeCount - 1; n >= 0; n--) Rebound(n);
        }

        /// <summary>
        /// Moves the hierarchy onto <paramref name="vertices"/> where only <paramref name="movedTriangles"/> (snapshot triangle
        /// indices) moved: their slots and the bounds above them are updated, the rest kept. Queries return what a full
        /// <see cref="Refit(Vector3[],int[])"/> would.
        /// </summary>
        public void RefitTriangles(Vector3[] vertices, int[] triangles, IReadOnlyList<int> movedTriangles)
        {
            if (triangles.Length / 3 != triangleCount) throw new ArgumentException("Refit needs the triangles the hierarchy was built on.", nameof(triangles));
            if (movedTriangles.Count == 0 || nodeCount == 0) return;
            var dirty = new bool[nodeCount];
            float extent = 0f;
            int lowest = nodeCount;
            for (int i = 0; i < movedTriangles.Count; i++)
            {
                int s = slotOfTriangle[movedTriangles[i]];
                if (s < 0) continue;
                slots[s] = MakeSlot(vertices, triangles, slots[s].triangle);
                extent = Mathf.Max(extent, MaxAbs(Vector3.Min(slots[s].a, Vector3.Min(slots[s].b, slots[s].c))),
                    MaxAbs(Vector3.Max(slots[s].a, Vector3.Max(slots[s].b, slots[s].c))));
                for (int n = leafOfSlot[s]; n >= 0 && !dirty[n]; n = parent[n])
                {
                    dirty[n] = true;
                    if (n < lowest) lowest = n;
                }
            }
            // The slack only grows: what was enough for the unmoved triangles still is.
            slack = Mathf.Max(slack, Slack(extent));
            for (int n = nodeCount - 1; n >= lowest; n--)
                if (dirty[n]) Rebound(n);
        }

        /// <summary>
        /// A copy that can be refitted on its own, for a surface moved from this one (a shape frame on the body):
        /// <see cref="RefitTriangles"/> along what moved, far cheaper than a build, answers exactly as a build would.
        /// </summary>
        public SurfaceBvh Clone() => new SurfaceBvh
        {
            nodes = (Node[])nodes.Clone(),
            nodeCount = nodeCount,
            slots = (Slot[])slots.Clone(),
            // The layout's tables never change after the build: shared.
            slotOfTriangle = slotOfTriangle,
            parent = parent,
            leafOfSlot = leafOfSlot,
            triangleCount = triangleCount,
            slack = slack,
        };

        private void Rebound(int n)
        {
            ref var node = ref nodes[n];
            if (node.count > 0)
            {
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int s = node.first; s < node.first + node.count; s++)
                {
                    ref var slot = ref slots[s];
                    min = Vector3.Min(min, Vector3.Min(slot.a, Vector3.Min(slot.b, slot.c)));
                    max = Vector3.Max(max, Vector3.Max(slot.a, Vector3.Max(slot.b, slot.c)));
                }
                var slackVector = new Vector3(slack, slack, slack);
                node.min = min - slackVector;
                node.max = max + slackVector;
            }
            else
            {
                node.min = Vector3.Min(nodes[node.first].min, nodes[node.first + 1].min);
                node.max = Vector3.Max(nodes[node.first].max, nodes[node.first + 1].max);
            }
        }

        /// <summary>
        /// Closest point on the surface from <paramref name="point"/>, considering only triangles within
        /// <paramref name="maxDistance"/> and (optionally) passing <paramref name="filter"/>.
        /// </summary>
        public Hit ClosestPoint(Vector3 point, float maxDistance, Func<int, bool> filter = null) =>
            filter == null ? ClosestPoint(point, maxDistance, new NoFilter()) : ClosestPoint(point, maxDistance, new FuncFilter(filter));

        /// <summary>Closest point, considering only triangles <paramref name="filter"/> accepts (no allocation).</summary>
        public Hit ClosestPoint<T>(Vector3 point, float maxDistance, T filter) where T : struct, ITriangleFilter =>
            ClosestPoint(point, maxDistance, filter, -1);

        /// <summary>
        /// Closest point, starting from <paramref name="seedTriangle"/> (a snapshot triangle, typically the previous answer for
        /// a point that moved a little, or -1): its distance bounds the search from the start, so nearly everything farther is
        /// pruned. The answer is the one of the unseeded query (see the class remarks).
        /// </summary>
        public Hit ClosestPoint<T>(Vector3 point, float maxDistance, T filter, int seedTriangle) where T : struct, ITriangleFilter
        {
            var hit = new Hit { found = false, distance = maxDistance, triangle = -1 };
            if (nodeCount == 0) return hit;

            float bestSqr = maxDistance * maxDistance;
            if (seedTriangle >= 0 && seedTriangle < triangleCount)
            {
                int s = slotOfTriangle[seedTriangle];
                if (s >= 0 && filter.Accept(seedTriangle)) Consider(ref slots[s], point, ref bestSqr, ref hit);
            }
            // A local stack also keeps concurrent and reentrant filtered queries independent.
            Span<int> stack = stackalloc int[StackSize];
            Span<float> entry = stackalloc float[StackSize];
            int sp = 0;
            stack[sp] = 0;
            entry[sp++] = SqrDistanceToBounds(point, nodes[0].min, nodes[0].max);
            while (sp > 0)
            {
                --sp;
                if (entry[sp] > bestSqr) continue;
                ref var node = ref nodes[stack[sp]];
                if (node.count > 0)
                {
                    for (int s = node.first, end = node.first + node.count; s < end; s++)
                    {
                        ref var slot = ref slots[s];
                        if (filter.Accept(slot.triangle)) Consider(ref slot, point, ref bestSqr, ref hit);
                    }
                    continue;
                }
                // Visit the closer child first for better pruning; each child's distance travels with it on the stack.
                int left = node.first;
                float dl = SqrDistanceToBounds(point, nodes[left].min, nodes[left].max);
                float dr = SqrDistanceToBounds(point, nodes[left + 1].min, nodes[left + 1].max);
                if (dl <= dr)
                {
                    if (dr <= bestSqr) { stack[sp] = left + 1; entry[sp++] = dr; }
                    if (dl <= bestSqr) { stack[sp] = left; entry[sp++] = dl; }
                }
                else
                {
                    if (dl <= bestSqr) { stack[sp] = left; entry[sp++] = dl; }
                    if (dr <= bestSqr) { stack[sp] = left + 1; entry[sp++] = dr; }
                }
            }
            if (hit.found) hit.distance = Mathf.Sqrt(bestSqr);
            return hit;
        }

        // The tie rule: nearer, or as near with a lower triangle index.
        private static void Consider(ref Slot slot, Vector3 point, ref float bestSqr, ref Hit hit)
        {
            var p = ClosestPointOnTriangle(point, slot.a, slot.b, slot.c, slot.collapsed, out var bary);
            float sqr = (p - point).sqrMagnitude;
            if (sqr < bestSqr || (sqr == bestSqr && slot.triangle < hit.triangle))
            {
                bestSqr = sqr;
                hit.found = true;
                hit.triangle = slot.triangle;
                hit.position = p;
                hit.bary = bary;
            }
        }

        /// <summary>
        /// For each of <paramref name="classCount"/> acceptance classes (see <see cref="ITriangleClassifier"/>), the closest
        /// point among the triangles of that class within <paramref name="maxDistance"/>, written to
        /// <paramref name="hits"/>[class]: what one <see cref="ClosestPoint{T}(Vector3,float,T)"/> per class, filtered by that
        /// class, would return, in one traversal (a node is pruned only when it is farther than every class's best so far).
        /// </summary>
        public void ClosestPoints<T>(Vector3 point, float maxDistance, T classifier, int classCount, Span<Hit> hits) where T : struct, ITriangleClassifier
        {
            if (classCount < 1 || classCount > MaxClasses || hits.Length < classCount) throw new ArgumentOutOfRangeException(nameof(classCount));
            Span<float> best = stackalloc float[MaxClasses];
            float limit = maxDistance * maxDistance;
            for (int c = 0; c < classCount; c++)
            {
                hits[c] = new Hit { found = false, distance = maxDistance, triangle = -1 };
                best[c] = limit;
            }
            if (nodeCount == 0) return;
            // The bound: the farthest best of the classes (all start at the range).
            float bound = limit;
            int all = (1 << classCount) - 1;

            Span<int> stack = stackalloc int[StackSize];
            Span<float> entry = stackalloc float[StackSize];
            int sp = 0;
            stack[sp] = 0;
            entry[sp++] = SqrDistanceToBounds(point, nodes[0].min, nodes[0].max);
            while (sp > 0)
            {
                --sp;
                if (entry[sp] > bound) continue;
                ref var node = ref nodes[stack[sp]];
                if (node.count > 0)
                {
                    bool improved = false;
                    for (int s = node.first, end = node.first + node.count; s < end; s++)
                    {
                        ref var slot = ref slots[s];
                        int classes = classifier.Classes(slot.triangle) & all;
                        if (classes == 0) continue;
                        var p = ClosestPointOnTriangle(point, slot.a, slot.b, slot.c, slot.collapsed, out var bary);
                        float sqr = (p - point).sqrMagnitude;
                        for (int c = 0; c < classCount; c++)
                        {
                            if ((classes & (1 << c)) == 0) continue;
                            if (sqr < best[c] || (sqr == best[c] && slot.triangle < hits[c].triangle))
                            {
                                best[c] = sqr;
                                hits[c] = new Hit { found = true, triangle = slot.triangle, position = p, bary = bary, distance = maxDistance };
                                improved = true;
                            }
                        }
                    }
                    if (improved)
                    {
                        bound = best[0];
                        for (int c = 1; c < classCount; c++) bound = Mathf.Max(bound, best[c]);
                    }
                    continue;
                }
                int left = node.first;
                float dl = SqrDistanceToBounds(point, nodes[left].min, nodes[left].max);
                float dr = SqrDistanceToBounds(point, nodes[left + 1].min, nodes[left + 1].max);
                if (dl <= dr)
                {
                    if (dr <= bound) { stack[sp] = left + 1; entry[sp++] = dr; }
                    if (dl <= bound) { stack[sp] = left; entry[sp++] = dl; }
                }
                else
                {
                    if (dl <= bound) { stack[sp] = left; entry[sp++] = dl; }
                    if (dr <= bound) { stack[sp] = left + 1; entry[sp++] = dr; }
                }
            }
            for (int c = 0; c < classCount; c++)
                if (hits[c].found)
                {
                    var h = hits[c];
                    h.distance = Mathf.Sqrt(best[c]);
                    hits[c] = h;
                }
        }

        /// <summary>
        /// First triangle (either side) crossed by the ray from <paramref name="origin"/> along the unit
        /// <paramref name="direction"/> within <paramref name="maxDistance"/>, optionally passing <paramref name="filter"/>.
        /// </summary>
        public Hit Raycast(Vector3 origin, Vector3 direction, float maxDistance, Func<int, bool> filter = null) =>
            filter == null ? Raycast(origin, direction, maxDistance, new NoFilter()) : Raycast(origin, direction, maxDistance, new FuncFilter(filter));

        /// <summary>First triangle crossed, considering only triangles <paramref name="filter"/> accepts (no allocation).</summary>
        public Hit Raycast<T>(Vector3 origin, Vector3 direction, float maxDistance, T filter) where T : struct, ITriangleFilter
        {
            var hit = new Hit { found = false, distance = maxDistance, triangle = -1 };
            if (nodeCount == 0) return hit;
            var inverse = new Vector3(Inverse(direction.x), Inverse(direction.y), Inverse(direction.z));
            float best = maxDistance;
            Span<int> stack = stackalloc int[StackSize];
            Span<float> entry = stackalloc float[StackSize];
            int sp = 0;
            float rootEntry = RayEntry(origin, inverse, nodes[0].min, nodes[0].max);
            if (rootEntry > best) return hit;
            stack[sp] = 0;
            entry[sp++] = rootEntry;
            while (sp > 0)
            {
                --sp;
                if (entry[sp] > best) continue;
                ref var node = ref nodes[stack[sp]];
                if (node.count > 0)
                {
                    for (int s = node.first, end = node.first + node.count; s < end; s++)
                    {
                        ref var slot = ref slots[s];
                        if (!filter.Accept(slot.triangle)) continue;
                        if (!RayTriangle(origin, direction, slot.a, slot.b, slot.c, out float distance, out var bary)) continue;
                        if (!(distance < best || (distance == best && hit.found && slot.triangle < hit.triangle))) continue;
                        best = distance;
                        hit.found = true;
                        hit.triangle = slot.triangle;
                        hit.bary = bary;
                        hit.position = origin + direction * distance;
                    }
                    continue;
                }
                // The child the ray enters first is visited first.
                int left = node.first;
                float el = RayEntry(origin, inverse, nodes[left].min, nodes[left].max);
                float er = RayEntry(origin, inverse, nodes[left + 1].min, nodes[left + 1].max);
                if (el <= er)
                {
                    if (er <= best) { stack[sp] = left + 1; entry[sp++] = er; }
                    if (el <= best) { stack[sp] = left; entry[sp++] = el; }
                }
                else
                {
                    if (el <= best) { stack[sp] = left; entry[sp++] = el; }
                    if (er <= best) { stack[sp] = left + 1; entry[sp++] = er; }
                }
            }
            if (hit.found) hit.distance = best;
            return hit;
        }

        private static float Inverse(float value) => 1f / (Mathf.Abs(value) > 1e-12f ? value : (value < 0f ? -1e-12f : 1e-12f));

        // Where the ray enters the box (0 when it starts inside), or infinity when it misses it.
        private static float RayEntry(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max)
        {
            float t1 = (min.x - origin.x) * inverse.x, t2 = (max.x - origin.x) * inverse.x;
            float near = Mathf.Min(t1, t2), far = Mathf.Max(t1, t2);
            t1 = (min.y - origin.y) * inverse.y; t2 = (max.y - origin.y) * inverse.y;
            near = Mathf.Max(near, Mathf.Min(t1, t2)); far = Mathf.Min(far, Mathf.Max(t1, t2));
            t1 = (min.z - origin.z) * inverse.z; t2 = (max.z - origin.z) * inverse.z;
            near = Mathf.Max(near, Mathf.Min(t1, t2)); far = Mathf.Min(far, Mathf.Max(t1, t2));
            near = Mathf.Max(near, 0f);
            return far >= near ? near : float.PositiveInfinity;
        }

        // Moller-Trumbore, both faces; barycentric coordinates in the order of the triangle's corners.
        internal static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance, out Vector3 bary)
        {
            distance = 0f; bary = default;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = origin - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(direction, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(e2, q) * inv;
            if (distance < 0f) return false;
            bary = new Vector3(1f - u - v, u, v);
            return true;
        }

        private static float SqrDistanceToBounds(Vector3 p, Vector3 min, Vector3 max)
        {
            float dx = Mathf.Max(Mathf.Max(min.x - p.x, 0f), p.x - max.x);
            float dy = Mathf.Max(Mathf.Max(min.y - p.y, 0f), p.y - max.y);
            float dz = Mathf.Max(Mathf.Max(min.z - p.z, 0f), p.z - max.z);
            return dx * dx + dy * dy + dz * dz;
        }

        // A triangle without area (two corners on one point, or all three on a line): its closest point lies on its longest
        // segments, since the region tests would divide zero by zero there.
        private static bool Collapsed(Vector3 a, Vector3 b, Vector3 c) => !(Vector3.Cross(b - a, c - a).sqrMagnitude > 0f);

        /// <summary>
        /// Closest point on triangle (abc) from p, with barycentric coordinates of the result.
        /// Christer Ericson, "Real-Time Collision Detection".
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary) =>
            ClosestPointOnTriangle(p, a, b, c, Collapsed(a, b, c), out bary);

        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, bool collapsed, out Vector3 bary)
        {
            if (collapsed) return ClosestPointOnCollapsedTriangle(p, a, b, c, out bary);
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1f, 0f, 0f); return a; }

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0f, 1f, 0f); return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float t = d1 / (d1 - d3);
                bary = new Vector3(1f - t, t, 0f);
                return a + t * ab;
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0f, 0f, 1f); return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float t = d2 / (d2 - d6);
                bary = new Vector3(1f - t, 0f, t);
                return a + t * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0f, 1f - t, t);
                return b + t * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float v = vb * denom;
            float w = vc * denom;
            bary = new Vector3(1f - v - w, v, w);
            return a + ab * v + ac * w;
        }

        private static Vector3 ClosestPointOnCollapsedTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            float tab = SegmentParameter(p, a, b), tbc = SegmentParameter(p, b, c), tca = SegmentParameter(p, c, a);
            var onAb = a + (b - a) * tab;
            var onBc = b + (c - b) * tbc;
            var onCa = c + (a - c) * tca;
            float dab = (p - onAb).sqrMagnitude, dbc = (p - onBc).sqrMagnitude, dca = (p - onCa).sqrMagnitude;
            if (dab <= dbc && dab <= dca) { bary = new Vector3(1f - tab, tab, 0f); return onAb; }
            if (dbc <= dca) { bary = new Vector3(0f, 1f - tbc, tbc); return onBc; }
            bary = new Vector3(tca, 0f, 1f - tca);
            return onCa;
        }

        // Where the closest point of segment from-to lies (0 at from, 1 at to); 0 for a segment without length.
        private static float SegmentParameter(Vector3 p, Vector3 from, Vector3 to)
        {
            var d = to - from;
            float length = d.sqrMagnitude;
            return length > 0f ? Mathf.Clamp01(Vector3.Dot(p - from, d) / length) : 0f;
        }
    }
}
