using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR && REFIT_BURST
using Unity.Collections.LowLevel.Unsafe;
#endif

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
    /// Triangles facing a direction (dot of the unit face normal at least <c>minimum</c>, from a snapshot's cached face
    /// normals), optionally of a region a mask accepts: the filter most queries use.
    /// </summary>
    public readonly struct FacingFilter : ITriangleFilter
    {
        internal readonly Vector3[] faceNormals;
        internal readonly Vector3 direction;
        internal readonly float minimum;
        internal readonly int mask;
        internal readonly int[] triangleMasks;
        internal readonly bool[] allowed;
        internal readonly bool strict;

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

    /// <summary>Every triangle but one: a ray leaving a triangle must not find the triangle itself.</summary>
    public readonly struct ExceptTriangle : ITriangleFilter
    {
        internal readonly int triangle;

        public ExceptTriangle(int triangle) => this.triangle = triangle;

        public bool Accept(int t) => t != triangle;
    }

    /// <summary>
    /// Triangles touching neither of two vertex groups (<see cref="MeshSnapshot.groupOfVertex"/>): an edge's own fabric is not
    /// in its way.
    /// </summary>
    internal readonly struct NotTouchingGroups : ITriangleFilter
    {
        internal readonly int[] triangles, groupOf;
        internal readonly int a, b;

        public NotTouchingGroups(MeshSnapshot asset, int a, int b)
        {
            triangles = asset.triangles;
            groupOf = asset.groupOfVertex;
            this.a = a;
            this.b = b;
        }

        public bool Accept(int triangle)
        {
            int t3 = triangle * 3;
            for (int k = 0; k < 3; k++)
            {
                int g = groupOf[triangles[t3 + k]];
                if (g == a || g == b) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Bounding volume hierarchy over a triangle soup for fast closest-point-on-surface queries,
    /// optionally restricted by a per-triangle predicate (used for normal / body-region filtering).
    /// Thread-safe for queries once built.
    /// </summary>
    /// <remarks>
    /// In the editor with Burst enabled, building, refitting and the queries without a filter or with a
    /// <see cref="FacingFilter"/>, <see cref="ExceptTriangle"/> or <see cref="NotTouchingGroups"/> (nearly all of a refit's)
    /// run Burst-compiled (<see cref="SurfaceBvhBurst"/>): the same tree and the same hits as the managed code below, bit for
    /// bit, in a fraction of the time (a refit 2 to 5 times faster over the regression cases). Other filters (a delegate),
    /// Burst turned off (Jobs > Burst > Enable Compilation) and players keep the managed code. A hierarchy keeps the path it
    /// was built with.
    /// </remarks>
    public class SurfaceBvh
    {
        private struct Node
        {
            public Vector3 boundsMin, boundsMax;
            public int left;   // child node index, -1 for leaf
            public int right;
            public int start;  // first triangle (into triOrder) for leaves
            public int count;  // triangle count for leaves
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

        private const int LeafSize = 4;

        private Node[] nodes;
        private int nodeCount;
        private int[] triOrder;
        private Vector3[] a, b, c;       // triangle corners
#if UNITY_EDITOR && REFIT_BURST
        // Nine floats by slot of triOrder (the triangle's corners, leaf order) when Burst built the hierarchy, else null.
        private float[] corners;

        /// <summary>Hierarchies built from now on use Burst when it is compiled (tests compare it with the managed code).</summary>
        internal static bool BurstEnabled = true;

        /// <summary>Whether a hierarchy built now would use Burst: enabled here, and its code compiled (not run managed).</summary>
        internal static bool BurstActive
        {
            get
            {
                if (!BurstEnabled) return false;
                SurfaceBvhBurst.Probe(out int compiled);
                return compiled != 0;
            }
        }

#endif

        /// <summary>
        /// Whether this hierarchy's queries run Burst-compiled: a caller may then hand them a filter Burst evaluates instead of
        /// a delegate (and keeps the delegate, the managed code as it was, otherwise).
        /// </summary>
#if UNITY_EDITOR && REFIT_BURST
        internal bool UsesBurst => corners != null;
#else
        internal bool UsesBurst => false;
#endif

        // Triangle centroids by axis, while building.
        private float[] cx, cy, cz;

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
        public static SurfaceBvh Build(MeshSnapshot snapshot, bool[] include = null)
        {
#if UNITY_EDITOR && REFIT_BURST
            if (BurstActive)
            {
                var built = BuildBurst(snapshot, include);
                if (built != null) return built;
            }
#endif
            var bvh = new SurfaceBvh();
            int triCount = snapshot.triangles.Length / 3;
            bvh.a = new Vector3[triCount];
            bvh.b = new Vector3[triCount];
            bvh.c = new Vector3[triCount];
            bvh.cx = new float[triCount];
            bvh.cy = new float[triCount];
            bvh.cz = new float[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var pa = bvh.a[t] = snapshot.worldVertices[snapshot.triangles[t * 3]];
                var pb = bvh.b[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 1]];
                var pc = bvh.c[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 2]];
                bvh.cx[t] = pa.x + pb.x + pc.x;
                bvh.cy[t] = pa.y + pb.y + pc.y;
                bvh.cz[t] = pa.z + pb.z + pc.z;
            }
            var hidden = snapshot.hiddenTriangles;
            bool Kept(int t) => (hidden == null || !hidden[t]) && (include == null || include[t]);
            int kept = 0;
            for (int t = 0; t < triCount; t++) if (Kept(t)) kept++;
            bvh.triOrder = new int[kept];
            for (int t = 0, k = 0; t < triCount; t++) if (Kept(t)) bvh.triOrder[k++] = t;
            bvh.nodes = new Node[Mathf.Max(1, kept * 2)];
            bvh.nodeCount = 0;
            if (kept > 0) bvh.BuildNode(0, kept);
            bvh.cx = bvh.cy = bvh.cz = null;
            return bvh;
        }

#if UNITY_EDITOR && REFIT_BURST
        // Build by Burst, or null where the managed build would index past an array (it then builds managed and throws as before).
        private static unsafe SurfaceBvh BuildBurst(MeshSnapshot snapshot, bool[] include)
        {
            var vertices = snapshot.worldVertices;
            var triangles = snapshot.triangles;
            var hidden = snapshot.hiddenTriangles;
            int triCount = triangles.Length / 3;
            if (vertices == null || (hidden != null && hidden.Length < triCount) || (include != null && include.Length < triCount)) return null;
            int kept = 0;
            for (int t = 0; t < triCount; t++) if ((hidden == null || !hidden[t]) && (include == null || include[t])) kept++;
            var bvh = new SurfaceBvh
            {
                a = new Vector3[triCount], b = new Vector3[triCount], c = new Vector3[triCount], triOrder = new int[kept],
                nodes = new Node[Mathf.Max(1, kept * 2)], corners = new float[9 * kept],
            };
            var keys = new float[3 * triCount];
            int nodeCount;
            fixed (Vector3* v = vertices)
            fixed (int* tri = triangles)
            fixed (bool* h = hidden)
            fixed (bool* inc = include)
            fixed (Vector3* pa = bvh.a)
            fixed (Vector3* pb = bvh.b)
            fixed (Vector3* pc = bvh.c)
            fixed (float* k = keys)
            fixed (int* order = bvh.triOrder)
            fixed (Node* n = bvh.nodes)
            fixed (float* packed = bvh.corners)
                nodeCount = SurfaceBvhBurst.Build((float*)v, vertices.Length, tri, triCount, (byte*)h, (byte*)inc, (float*)pa, (float*)pb, (float*)pc, k, order, kept,
                    (SurfaceBvhBurst.Node*)n, bvh.nodes.Length, packed);
            if (nodeCount < 0) return null;
            bvh.nodeCount = nodeCount;
            return bvh;
        }
#endif

        /// <summary>
        /// Moves the hierarchy onto <paramref name="snapshot"/>: the same triangles (the snapshot it was built from, or one with
        /// the same triangles) at new positions. The tree keeps its order and only its bounds follow, in linear time: queries
        /// stay exact, and get slower the farther the surface moves from where it was built.
        /// </summary>
        public void Refit(MeshSnapshot snapshot)
        {
            int triCount = a.Length;
            if (snapshot.triangles.Length / 3 != triCount) throw new ArgumentException("Refit needs the triangles the hierarchy was built on.", nameof(snapshot));
#if UNITY_EDITOR && REFIT_BURST
            if (corners != null)
            {
                if (RefitBurst(snapshot)) return;
                // A triangle names a vertex out of range: the managed refit below throws as it did, and the queries go managed.
                corners = null;
            }
#endif
            for (int t = 0; t < triCount; t++)
            {
                a[t] = snapshot.worldVertices[snapshot.triangles[t * 3]];
                b[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 1]];
                c[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 2]];
            }
            // Children always follow their parent in the node array: bounds settle from the last node back to the root.
            for (int n = nodeCount - 1; n >= 0; n--)
            {
                var node = nodes[n];
                if (node.left < 0)
                {
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int i = node.start; i < node.start + node.count; i++)
                    {
                        int t = triOrder[i];
                        min = Vector3.Min(min, Vector3.Min(a[t], Vector3.Min(b[t], c[t])));
                        max = Vector3.Max(max, Vector3.Max(a[t], Vector3.Max(b[t], c[t])));
                    }
                    node.boundsMin = min;
                    node.boundsMax = max;
                }
                else
                {
                    node.boundsMin = Vector3.Min(nodes[node.left].boundsMin, nodes[node.right].boundsMin);
                    node.boundsMax = Vector3.Max(nodes[node.left].boundsMax, nodes[node.right].boundsMax);
                }
                nodes[n] = node;
            }
        }

#if UNITY_EDITOR && REFIT_BURST
        private unsafe bool RefitBurst(MeshSnapshot snapshot)
        {
            var vertices = snapshot.worldVertices;
            if (vertices == null) return false;
            fixed (Vector3* v = vertices)
            fixed (int* tri = snapshot.triangles)
            fixed (Vector3* pa = a)
            fixed (Vector3* pb = b)
            fixed (Vector3* pc = c)
            fixed (int* order = triOrder)
            fixed (Node* n = nodes)
            fixed (float* packed = corners)
                return SurfaceBvhBurst.Refit((float*)v, vertices.Length, tri, a.Length, (float*)pa, (float*)pb, (float*)pc, order, triOrder.Length,
                    (SurfaceBvhBurst.Node*)n, nodeCount, packed) == 0;
        }

        // The filters Burst evaluates itself, with the arrays they read.
        private struct Described
        {
            public FacingFilter facing;
            public bool faces;
            public int except;
            public NotTouchingGroups touching;
        }

        // The filter as Burst reads it; false for another filter, or arrays the managed query would index past (it then runs and
        // throws as before; NotTouchingGroups' arrays are checked index by index in Burst, which then answers false too).
        private bool Describe<T>(ref T filter, out Described described) where T : struct, ITriangleFilter
        {
            described = default;
            described.except = -1;
            if (typeof(T) == typeof(NoFilter)) return true;
            if (typeof(T) == typeof(FacingFilter))
            {
                ref var facing = ref UnsafeUtility.As<T, FacingFilter>(ref filter);
                int count = a.Length;
                if (facing.faceNormals == null || facing.faceNormals.Length < count || (facing.allowed != null && facing.allowed.Length < count) ||
                    (facing.mask != 0 && facing.triangleMasks != null && facing.triangleMasks.Length < count)) return false;
                described.facing = facing;
                described.faces = true;
                return true;
            }
            if (typeof(T) == typeof(ExceptTriangle))
            {
                described.except = UnsafeUtility.As<T, ExceptTriangle>(ref filter).triangle;
                return true;
            }
            if (typeof(T) == typeof(NotTouchingGroups))
            {
                described.touching = UnsafeUtility.As<T, NotTouchingGroups>(ref filter);
                return described.touching.triangles != null && described.touching.groupOf != null;
            }
            return false;
        }

        private unsafe bool ClosestBurst(Vector3 point, float maxDistance, in Described filter, out Hit result)
        {
            SurfaceBvhBurst.Hit hit;
            var facing = filter.facing;
            var touching = filter.touching;
            fixed (Node* n = nodes)
            fixed (int* order = triOrder)
            fixed (float* packed = corners)
            fixed (Vector3* normals = facing.faceNormals)
            fixed (int* masks = facing.triangleMasks)
            fixed (bool* allowed = facing.allowed)
            fixed (int* touchTriangles = touching.triangles)
            fixed (int* groupOf = touching.groupOf)
            {
                var tree = new SurfaceBvhBurst.Tree { nodes = (SurfaceBvhBurst.Node*)n, nodeCount = nodeCount, order = order, corners = packed };
                var data = Filter(filter, normals, masks, allowed, touchTriangles, groupOf);
                SurfaceBvhBurst.Closest(in tree, point.x, point.y, point.z, maxDistance, in data, out hit);
            }
            result = Managed(hit);
            return hit.error == 0;
        }

        private unsafe bool RaycastBurst(Vector3 origin, Vector3 direction, float maxDistance, in Described filter, out Hit result)
        {
            SurfaceBvhBurst.Hit hit;
            var facing = filter.facing;
            var touching = filter.touching;
            fixed (Node* n = nodes)
            fixed (int* order = triOrder)
            fixed (float* packed = corners)
            fixed (Vector3* normals = facing.faceNormals)
            fixed (int* masks = facing.triangleMasks)
            fixed (bool* allowed = facing.allowed)
            fixed (int* touchTriangles = touching.triangles)
            fixed (int* groupOf = touching.groupOf)
            {
                var tree = new SurfaceBvhBurst.Tree { nodes = (SurfaceBvhBurst.Node*)n, nodeCount = nodeCount, order = order, corners = packed };
                var data = Filter(filter, normals, masks, allowed, touchTriangles, groupOf);
                SurfaceBvhBurst.Raycast(in tree, origin.x, origin.y, origin.z, direction.x, direction.y, direction.z, maxDistance, in data, out hit);
            }
            result = Managed(hit);
            return hit.error == 0;
        }

        private static unsafe SurfaceBvhBurst.Filter Filter(in Described filter, Vector3* normals, int* masks, bool* allowed, int* touchTriangles,
            int* groupOf)
        {
            var data = new SurfaceBvhBurst.Filter { except = filter.except };
            if (filter.faces)
            {
                var facing = filter.facing;
                data.normals = (float*)normals;
                data.x = facing.direction.x; data.y = facing.direction.y; data.z = facing.direction.z;
                data.minimum = facing.minimum;
                data.mask = facing.mask;
                data.strict = facing.strict ? 1 : 0;
                data.masks = masks;
                data.allowed = (byte*)allowed;
            }
            if (touchTriangles != null)
            {
                data.triangles = touchTriangles;
                data.triangleInts = filter.touching.triangles.Length;
                data.groupOf = groupOf;
                data.vertexCount = filter.touching.groupOf.Length;
                data.groupA = filter.touching.a;
                data.groupB = filter.touching.b;
            }
            return data;
        }

        private static Hit Managed(in SurfaceBvhBurst.Hit hit) => new Hit
        {
            found = hit.found != 0, triangle = hit.triangle, position = new Vector3(hit.x, hit.y, hit.z), bary = new Vector3(hit.u, hit.v, hit.w),
            distance = hit.distance,
        };
#endif

        private int BuildNode(int start, int count)
        {
            int nodeIndex = nodeCount++;
            if (nodeCount > nodes.Length) Array.Resize(ref nodes, nodes.Length * 2);

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = start; i < start + count; i++)
            {
                int t = triOrder[i];
                min = Vector3.Min(min, Vector3.Min(a[t], Vector3.Min(b[t], c[t])));
                max = Vector3.Max(max, Vector3.Max(a[t], Vector3.Max(b[t], c[t])));
            }

            var node = new Node { boundsMin = min, boundsMax = max, left = -1, right = -1, start = start, count = count };
            if (count > LeafSize)
            {
                var size = max - min;
                int axis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
                int half = count / 2;
                // The halves split at the median centroid: only the median has to be in place (quickselect), not the whole order.
                SelectMedian(start, count, half, axis == 0 ? cx : axis == 1 ? cy : cz);
                node.count = 0;
                nodes[nodeIndex] = node; // reserve before recursing
                int left = BuildNode(start, half);
                int right = BuildNode(start + half, count - half);
                node.left = left;
                node.right = right;
            }
            nodes[nodeIndex] = node;
            return nodeIndex;
        }

        // Reorders triOrder[start, start + count) so that the triangle of rank k (by key) is at start + k, those before it have
        // keys no larger and those after no smaller (Hoare partitioning around the middle element).
        private void SelectMedian(int start, int count, int k, float[] key)
        {
            var order = triOrder;
            int lo = start, hi = start + count - 1, target = start + k;
            while (lo < hi)
            {
                float pivot = key[order[(lo + hi) >> 1]];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (key[order[i]] < pivot) i++;
                    while (key[order[j]] > pivot) j--;
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

        /// <summary>
        /// Closest point on the surface from <paramref name="point"/>, considering only triangles within
        /// <paramref name="maxDistance"/> and (optionally) passing <paramref name="filter"/>.
        /// </summary>
        public Hit ClosestPoint(Vector3 point, float maxDistance, Func<int, bool> filter = null) =>
            filter == null ? ClosestPoint(point, maxDistance, new NoFilter()) : ClosestPoint(point, maxDistance, new FuncFilter(filter));

        /// <summary>Closest point, considering only triangles <paramref name="filter"/> accepts (no allocation).</summary>
        public Hit ClosestPoint<T>(Vector3 point, float maxDistance, T filter) where T : struct, ITriangleFilter
        {
#if UNITY_EDITOR && REFIT_BURST
            if (corners != null && Describe(ref filter, out var described) && ClosestBurst(point, maxDistance, in described, out var burstHit))
                return burstHit;
#endif
            var hit = new Hit { found = false, distance = maxDistance, triangle = -1 };
            if (nodeCount == 0) return hit;

            float bestSqr = maxDistance * maxDistance;
            // Median splitting limits depth to <32 for any int-sized triangle array.
            // A local stack also keeps concurrent and reentrant filtered queries independent.
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int ni = stack[--sp];
                var node = nodes[ni];
                if (SqrDistanceToBounds(point, node.boundsMin, node.boundsMax) > bestSqr) continue;

                if (node.left < 0)
                {
                    for (int i = node.start; i < node.start + node.count; i++)
                    {
                        int t = triOrder[i];
                        if (!filter.Accept(t)) continue;
                        var p = ClosestPointOnTriangle(point, a[t], b[t], c[t], out var bary);
                        float sqr = (p - point).sqrMagnitude;
                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                            hit.found = true;
                            hit.triangle = t;
                            hit.position = p;
                            hit.bary = bary;
                        }
                    }
                }
                else
                {
                    // Visit the closer child first for better pruning.
                    float dl = SqrDistanceToBounds(point, nodes[node.left].boundsMin, nodes[node.left].boundsMax);
                    float dr = SqrDistanceToBounds(point, nodes[node.right].boundsMin, nodes[node.right].boundsMax);
                    if (dl <= dr) { stack[sp++] = node.right; stack[sp++] = node.left; }
                    else { stack[sp++] = node.left; stack[sp++] = node.right; }
                }
            }
            if (hit.found) hit.distance = Mathf.Sqrt(bestSqr);
            return hit;
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
#if UNITY_EDITOR && REFIT_BURST
            if (corners != null && Describe(ref filter, out var described) && RaycastBurst(origin, direction, maxDistance, in described, out var burstHit))
                return burstHit;
#endif
            var hit = new Hit { found = false, distance = maxDistance, triangle = -1 };
            if (nodeCount == 0) return hit;
            var inverse = new Vector3(Inverse(direction.x), Inverse(direction.y), Inverse(direction.z));
            float best = maxDistance;
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                var node = nodes[stack[--sp]];
                if (!RayEntersBounds(origin, inverse, node.boundsMin, node.boundsMax, best)) continue;
                if (node.left < 0)
                {
                    for (int i = node.start; i < node.start + node.count; i++)
                    {
                        int t = triOrder[i];
                        if (!filter.Accept(t)) continue;
                        if (!RayTriangle(origin, direction, a[t], b[t], c[t], out float distance, out var bary) || distance >= best) continue;
                        best = distance;
                        hit.found = true;
                        hit.triangle = t;
                        hit.bary = bary;
                        hit.position = origin + direction * distance;
                    }
                }
                else
                {
                    stack[sp++] = node.left;
                    stack[sp++] = node.right;
                }
            }
            if (hit.found) hit.distance = best;
            return hit;
        }

        private static float Inverse(float value) => 1f / (Mathf.Abs(value) > 1e-12f ? value : (value < 0f ? -1e-12f : 1e-12f));

        private static bool RayEntersBounds(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float maxDistance)
        {
            float t1 = (min.x - origin.x) * inverse.x, t2 = (max.x - origin.x) * inverse.x;
            float near = Mathf.Min(t1, t2), far = Mathf.Max(t1, t2);
            t1 = (min.y - origin.y) * inverse.y; t2 = (max.y - origin.y) * inverse.y;
            near = Mathf.Max(near, Mathf.Min(t1, t2)); far = Mathf.Min(far, Mathf.Max(t1, t2));
            t1 = (min.z - origin.z) * inverse.z; t2 = (max.z - origin.z) * inverse.z;
            near = Mathf.Max(near, Mathf.Min(t1, t2)); far = Mathf.Min(far, Mathf.Max(t1, t2));
            return far >= Mathf.Max(near, 0f) && near <= maxDistance;
        }

        // Moller-Trumbore, both faces; barycentric coordinates in the order of the triangle's corners.
        private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance, out Vector3 bary)
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

        /// <summary>
        /// Closest point on triangle (abc) from p, with barycentric coordinates of the result.
        /// Christer Ericson, "Real-Time Collision Detection".
        /// </summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            var ab = b - a;
            var ac = c - a;
            // A triangle without area (two corners on one point, or all three on a line) is its longest segments: the
            // region tests below would divide zero by zero there.
            if (!(Vector3.Cross(ab, ac).sqrMagnitude > 0f)) return ClosestPointOnCollapsedTriangle(p, a, b, c, out bary);
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
