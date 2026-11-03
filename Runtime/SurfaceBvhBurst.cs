#if UNITY_EDITOR && REFIT_BURST
using System;
using Unity.Burst;

namespace Orbiters.ReFit
{
    /// <summary>
    /// <see cref="SurfaceBvh"/>'s build, refit and queries (unfiltered, or with a <see cref="FacingFilter"/>, an
    /// <see cref="ExceptTriangle"/> or a <see cref="NotTouchingGroups"/>) compiled by Burst, on the hierarchy's own arrays pinned
    /// for the call, with each leaf's triangle corners packed together in leaf order (<see cref="Tree.corners"/>) instead of read
    /// through the triangle order from three arrays. The managed code statement for statement, rounded as the editor's Mono
    /// rounds it: the same tree and the same hits, bit for bit.
    /// </summary>
    /// <remarks>
    /// Why: a refit spends most of its time in these queries and in building hierarchies, and Mono as the Unity editor runs it
    /// (with its debugger agent, in both code optimization modes) is many times slower than native code on them; Burst gives
    /// the native speed with no binaries to ship.
    /// Why bit for bit: a refit takes discrete decisions on which triangle a query meets (its facing, its region, a fallback),
    /// and on a body's surface several triangles are often exactly or nearly as close (around a shared corner or edge). Plain
    /// single precision arithmetic, or a tree whose order differs by one rounding of a centroid, flips some of those choices,
    /// and the fit downstream follows: on the regression cases outputs then moved by up to 60 mm, so "the same up to rounding"
    /// is not a property ReFit's pipeline has. The editor's Mono evaluates float expressions in double and rounds when a value
    /// is stored (ECMA-335 III.1.1.1); written the same way here (see <see cref="Dot"/>), Burst computes the same floats.
    /// <see cref="FloatMode.Strict"/>: no fused or reordered operations.
    /// The methods are called directly from managed code (Burst's direct call) and compile synchronously on first use (about
    /// a second, then cached by Burst), so a refit never runs them half managed. Unity's jobs are not used: they can only be
    /// scheduled from the main thread, and a refit computes on a worker thread so the editor stays responsive; the callers'
    /// Parallel.For loops run the queries on every core instead.
    /// </remarks>
    [BurstCompile]
    internal static unsafe class SurfaceBvhBurst
    {
        /// <summary>SurfaceBvh's node as laid out in memory: bounds (two Vector3), children, then its triangles' slots.</summary>
        internal struct Node
        {
            public float minx, miny, minz, maxx, maxy, maxz;
            public int left, right, start, count;
        }

        /// <summary>The hierarchy a query walks.</summary>
        internal struct Tree
        {
            public Node* nodes;
            public int nodeCount;
            /// <summary>Triangle by slot (leaf order).</summary>
            public int* order;
            /// <summary>Nine floats by slot: the triangle's corners a, b, c.</summary>
            public float* corners;
        }

        /// <summary>The filters as data, each off when its arrays are null (except: -1).</summary>
        internal struct Filter
        {
            /// <summary><see cref="FacingFilter"/>.</summary>
            public float* normals;
            public float x, y, z, minimum;
            public int mask, strict;
            public int* masks;
            public byte* allowed;
            /// <summary><see cref="ExceptTriangle"/>.</summary>
            public int except;
            /// <summary><see cref="NotTouchingGroups"/>: its arrays and their lengths (read index by index, checked).</summary>
            public int* triangles;
            public int triangleInts;
            public int* groupOf;
            public int vertexCount;
            public int groupA, groupB;
        }

        internal struct Hit
        {
            public int found, triangle;
            public float x, y, z, u, v, w, distance;
            /// <summary>1: a filter array was too short for an index (the managed query then runs and throws as before).</summary>
            public int error;
        }

        private const int LeafSize = 4;

        private static float Min(float a, float b) => a < b ? a : b;
        private static float Max(float a, float b) => a > b ? a : b;

        /// <summary>Sets 1 when Burst compiled this (its managed version sets 0): whether the other methods run compiled.</summary>
        [BurstCompile(CompileSynchronously = true)]
        public static void Probe(out int compiled)
        {
            compiled = 1;
            Managed(ref compiled);
        }

        [BurstDiscard]
        private static void Managed(ref int compiled) => compiled = 0;

        /// <summary>
        /// SurfaceBvh.Build: corners by triangle (<paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>), the kept
        /// triangles' order, the nodes (capacity <paramref name="nodeCapacity"/>) and the packed corners. Returns the node
        /// count, or -1 when a triangle names a vertex out of range or the nodes would not fit (the caller builds managed).
        /// </summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Strict, CompileSynchronously = true)]
        public static int Build(float* vertices, int vertexCount, int* triangles, int triangleCount, byte* hidden, byte* include, float* a, float* b,
            float* c, float* keys, int* order, int kept, Node* nodes, int nodeCapacity, float* corners)
        {
            int n = triangleCount;
            for (int i = 0; i < 3 * n; i++)
                if ((uint)triangles[i] >= (uint)vertexCount) return -1;
            for (int t = 0; t < n; t++)
            {
                float* pa = vertices + 3 * triangles[3 * t], pb = vertices + 3 * triangles[3 * t + 1], pc = vertices + 3 * triangles[3 * t + 2];
                a[3 * t] = pa[0]; a[3 * t + 1] = pa[1]; a[3 * t + 2] = pa[2];
                b[3 * t] = pb[0]; b[3 * t + 1] = pb[1]; b[3 * t + 2] = pb[2];
                c[3 * t] = pc[0]; c[3 * t + 1] = pc[1]; c[3 * t + 2] = pc[2];
                // cx[t] = pa.x + pb.x + pc.x: summed in double, rounded once as stored (see Dot).
                keys[t] = (float)((double)pa[0] + pb[0] + pc[0]);
                keys[n + t] = (float)((double)pa[1] + pb[1] + pc[1]);
                keys[2 * n + t] = (float)((double)pa[2] + pb[2] + pc[2]);
            }
            int k = 0;
            for (int t = 0; t < n; t++)
                if ((hidden == null || hidden[t] == 0) && (include == null || include[t] != 0))
                {
                    if (k == kept) return -1;
                    order[k++] = t;
                }
            if (k != kept) return -1;
            int nodeCount = 0;
            if (kept > 0 && BuildNode(a, b, c, keys, n, order, nodes, nodeCapacity, ref nodeCount, 0, kept) < 0) return -1;
            Pack(a, b, c, order, kept, corners);
            return nodeCount;
        }

        // SurfaceBvh.BuildNode: the median split, recursively.
        private static int BuildNode(float* a, float* b, float* c, float* keys, int n, int* order, Node* nodes, int capacity, ref int nodeCount, int start,
            int count)
        {
            int index = nodeCount++;
            if (index >= capacity) return -1;
            Bounds(a, b, c, order, start, count, out var node);
            node.left = -1;
            node.right = -1;
            node.start = start;
            node.count = count;
            if (count > LeafSize)
            {
                float sx = node.maxx - node.minx, sy = node.maxy - node.miny, sz = node.maxz - node.minz;
                int axis = sx >= sy && sx >= sz ? 0 : (sy >= sz ? 1 : 2);
                int half = count / 2;
                SelectMedian(order, keys + axis * n, start, count, half);
                node.count = 0;
                nodes[index] = node;
                int left = BuildNode(a, b, c, keys, n, order, nodes, capacity, ref nodeCount, start, half);
                if (left < 0) return -1;
                int right = BuildNode(a, b, c, keys, n, order, nodes, capacity, ref nodeCount, start + half, count - half);
                if (right < 0) return -1;
                node.left = left;
                node.right = right;
            }
            nodes[index] = node;
            return index;
        }

        // Vector3.Min/Max of every corner of the slots' triangles, per axis in the managed order.
        private static void Bounds(float* a, float* b, float* c, int* order, int start, int count, out Node node)
        {
            node = default;
            float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
            float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
            for (int i = start; i < start + count; i++)
            {
                int t = 3 * order[i];
                minx = Min(minx, Min(a[t], Min(b[t], c[t])));
                miny = Min(miny, Min(a[t + 1], Min(b[t + 1], c[t + 1])));
                minz = Min(minz, Min(a[t + 2], Min(b[t + 2], c[t + 2])));
                maxx = Max(maxx, Max(a[t], Max(b[t], c[t])));
                maxy = Max(maxy, Max(a[t + 1], Max(b[t + 1], c[t + 1])));
                maxz = Max(maxz, Max(a[t + 2], Max(b[t + 2], c[t + 2])));
            }
            node.minx = minx; node.miny = miny; node.minz = minz;
            node.maxx = maxx; node.maxy = maxy; node.maxz = maxz;
        }

        // SurfaceBvh.SelectMedian (Hoare partitioning around the middle element).
        private static void SelectMedian(int* order, float* key, int start, int count, int k)
        {
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

        private static void Pack(float* a, float* b, float* c, int* order, int kept, float* corners)
        {
            for (int i = 0; i < kept; i++)
            {
                int t = 3 * order[i];
                float* o = corners + 9 * i;
                o[0] = a[t]; o[1] = a[t + 1]; o[2] = a[t + 2];
                o[3] = b[t]; o[4] = b[t + 1]; o[5] = b[t + 2];
                o[6] = c[t]; o[7] = c[t + 1]; o[8] = c[t + 2];
            }
        }

        /// <summary>SurfaceBvh.Refit: the corners at the new positions, the bounds from the last node back to the root. -1: a vertex out of range.</summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Strict, CompileSynchronously = true)]
        public static int Refit(float* vertices, int vertexCount, int* triangles, int triangleCount, float* a, float* b, float* c, int* order, int kept,
            Node* nodes, int nodeCount, float* corners)
        {
            for (int i = 0; i < 3 * triangleCount; i++)
                if ((uint)triangles[i] >= (uint)vertexCount) return -1;
            for (int t = 0; t < triangleCount; t++)
            {
                float* pa = vertices + 3 * triangles[3 * t], pb = vertices + 3 * triangles[3 * t + 1], pc = vertices + 3 * triangles[3 * t + 2];
                a[3 * t] = pa[0]; a[3 * t + 1] = pa[1]; a[3 * t + 2] = pa[2];
                b[3 * t] = pb[0]; b[3 * t + 1] = pb[1]; b[3 * t + 2] = pb[2];
                c[3 * t] = pc[0]; c[3 * t + 1] = pc[1]; c[3 * t + 2] = pc[2];
            }
            for (int n = nodeCount - 1; n >= 0; n--)
            {
                Node* node = nodes + n;
                if (node->left < 0)
                {
                    Bounds(a, b, c, order, node->start, node->count, out var bounds);
                    node->minx = bounds.minx; node->miny = bounds.miny; node->minz = bounds.minz;
                    node->maxx = bounds.maxx; node->maxy = bounds.maxy; node->maxz = bounds.maxz;
                }
                else
                {
                    Node* l = nodes + node->left, r = nodes + node->right;
                    node->minx = Min(l->minx, r->minx); node->miny = Min(l->miny, r->miny); node->minz = Min(l->minz, r->minz);
                    node->maxx = Max(l->maxx, r->maxx); node->maxy = Max(l->maxy, r->maxy); node->maxz = Max(l->maxz, r->maxz);
                }
            }
            Pack(a, b, c, order, kept, corners);
            return 0;
        }

        // The editor's Mono evaluates float expressions in double precision and rounds a value to float when it is stored (a local,
        // an argument, a field, an array element, a return value: ECMA-335 III.1.1.1). Each managed statement is written here the
        // same way, so that Burst computes what the managed code computes, bit for bit: a C# expression of several operations in
        // double, cast to float where the managed code stores it. One operation on floats (a - b, a * b, a / b, sqrt) gives the
        // same float either way. Vector3's methods store their results (Dot and sqrMagnitude return floats, the operators and
        // Cross construct a Vector3 from float arguments).
        private static float Dot(float ax, float ay, float az, float bx, float by, float bz) => (float)((double)ax * bx + (double)ay * by + (double)az * bz);

        private static float SqrMagnitude(float x, float y, float z) => (float)((double)x * x + (double)y * y + (double)z * z);

        // Vector3.Cross(a, b)
        private static void Cross(float ax, float ay, float az, float bx, float by, float bz, out float x, out float y, out float z)
        {
            x = (float)((double)ay * bz - (double)az * by);
            y = (float)((double)az * bx - (double)ax * bz);
            z = (float)((double)ax * by - (double)ay * bx);
        }

        private static bool Filtered(in Filter f) => f.normals != null || f.except >= 0 || f.triangles != null;

        // The filters' Accept; error set where an index falls outside an array.
        private static bool Accept(in Filter f, int t, ref int error)
        {
            if (t == f.except) return false;
            if (f.triangles != null)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = 3 * t + k;
                    if ((uint)i >= (uint)f.triangleInts) { error = 1; return false; }
                    int vertex = f.triangles[i];
                    if ((uint)vertex >= (uint)f.vertexCount) { error = 1; return false; }
                    int g = f.groupOf[vertex];
                    if (g == f.groupA || g == f.groupB) return false;
                }
            }
            if (f.normals == null) return true;
            if (f.allowed != null && f.allowed[t] == 0) return false;
            float* n = f.normals + 3 * t;
            // float dot = n.x * direction.x + n.y * direction.y + n.z * direction.z;
            float dot = (float)((double)n[0] * f.x + (double)n[1] * f.y + (double)n[2] * f.z);
            if (f.strict != 0 ? dot <= f.minimum : dot < f.minimum) return false;
            return f.mask == 0 || f.masks == null || f.masks[t] == 0 || (f.masks[t] & f.mask) != 0;
        }

        private static float SqrDistanceToBounds(float px, float py, float pz, Node* n)
        {
            float dx = Max(Max(n->minx - px, 0f), px - n->maxx);
            float dy = Max(Max(n->miny - py, 0f), py - n->maxy);
            float dz = Max(Max(n->minz - pz, 0f), pz - n->maxz);
            return (float)((double)dx * dx + (double)dy * dy + (double)dz * dz);
        }

        /// <summary>SurfaceBvh.ClosestPoint.</summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Strict, CompileSynchronously = true)]
        public static void Closest(in Tree tree, float px, float py, float pz, float maxDistance, in Filter filter, out Hit hit)
        {
            hit = default;
            hit.triangle = -1;
            hit.distance = maxDistance;
            if (tree.nodeCount == 0) return;
            float best = maxDistance * maxDistance;
            bool filtered = Filtered(filter);
            int* stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            Node* nodes = tree.nodes;
            while (sp > 0)
            {
                Node* node = nodes + stack[--sp];
                if (SqrDistanceToBounds(px, py, pz, node) > best) continue;
                if (node->left < 0)
                {
                    for (int i = node->start; i < node->start + node->count; i++)
                    {
                        int t = tree.order[i];
                        if (filtered && !Accept(in filter, t, ref hit.error))
                        {
                            if (hit.error != 0) return;
                            continue;
                        }
                        ClosestOnTriangle(px, py, pz, tree.corners + 9 * i, out float cx, out float cy, out float cz, out float u, out float v, out float w);
                        // (p - point).sqrMagnitude
                        float sqr = SqrMagnitude(cx - px, cy - py, cz - pz);
                        if (sqr < best)
                        {
                            best = sqr;
                            hit.found = 1;
                            hit.triangle = t;
                            hit.x = cx; hit.y = cy; hit.z = cz;
                            hit.u = u; hit.v = v; hit.w = w;
                        }
                    }
                }
                else
                {
                    // The closer child first.
                    float dl = SqrDistanceToBounds(px, py, pz, nodes + node->left);
                    float dr = SqrDistanceToBounds(px, py, pz, nodes + node->right);
                    if (dl <= dr) { stack[sp++] = node->right; stack[sp++] = node->left; }
                    else { stack[sp++] = node->left; stack[sp++] = node->right; }
                }
            }
            if (hit.found != 0) hit.distance = (float)Math.Sqrt(best);
        }

        // SurfaceBvh.ClosestPointOnTriangle (Christer Ericson's region tests), statement for statement: barycentric coordinates
        // in the order of the corners.
        private static void ClosestOnTriangle(float px, float py, float pz, float* t, out float rx, out float ry, out float rz, out float u, out float v,
            out float w)
        {
            float ax = t[0], ay = t[1], az = t[2], bx = t[3], by = t[4], bz = t[5], cx = t[6], cy = t[7], cz = t[8];
            // var ab = b - a; var ac = c - a;
            float abx = bx - ax, aby = by - ay, abz = bz - az;
            float acx = cx - ax, acy = cy - ay, acz = cz - az;
            // if (!(Vector3.Cross(ab, ac).sqrMagnitude > 0f)) collapsed
            Cross(abx, aby, abz, acx, acy, acz, out float nx, out float ny, out float nz);
            if (!(SqrMagnitude(nx, ny, nz) > 0f))
            {
                Collapsed(px, py, pz, ax, ay, az, bx, by, bz, cx, cy, cz, out rx, out ry, out rz, out u, out v, out w);
                return;
            }
            float apx = px - ax, apy = py - ay, apz = pz - az;
            float d1 = Dot(abx, aby, abz, apx, apy, apz);
            float d2 = Dot(acx, acy, acz, apx, apy, apz);
            if (d1 <= 0f && d2 <= 0f) { rx = ax; ry = ay; rz = az; u = 1f; v = 0f; w = 0f; return; }
            float bpx = px - bx, bpy = py - by, bpz = pz - bz;
            float d3 = Dot(abx, aby, abz, bpx, bpy, bpz);
            float d4 = Dot(acx, acy, acz, bpx, bpy, bpz);
            if (d3 >= 0f && d4 <= d3) { rx = bx; ry = by; rz = bz; u = 0f; v = 1f; w = 0f; return; }
            float vc = (float)((double)d1 * d4 - (double)d3 * d2);
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float s = (float)(d1 / ((double)d1 - d3));
                // bary = (1 - t, t, 0); return a + t * ab;
                u = 1f - s; v = s; w = 0f;
                rx = ax + abx * s; ry = ay + aby * s; rz = az + abz * s;
                return;
            }
            float cpx = px - cx, cpy = py - cy, cpz = pz - cz;
            float d5 = Dot(abx, aby, abz, cpx, cpy, cpz);
            float d6 = Dot(acx, acy, acz, cpx, cpy, cpz);
            if (d6 >= 0f && d5 <= d6) { rx = cx; ry = cy; rz = cz; u = 0f; v = 0f; w = 1f; return; }
            float vb = (float)((double)d5 * d2 - (double)d1 * d6);
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float s = (float)(d2 / ((double)d2 - d6));
                // bary = (1 - t, 0, t); return a + t * ac;
                u = 1f - s; v = 0f; w = s;
                rx = ax + acx * s; ry = ay + acy * s; rz = az + acz * s;
                return;
            }
            float va = (float)((double)d3 * d6 - (double)d5 * d4);
            if (va <= 0f && ((double)d4 - d3) >= 0 && ((double)d5 - d6) >= 0)
            {
                float s = (float)(((double)d4 - d3) / (((double)d4 - d3) + ((double)d5 - d6)));
                // bary = (0, 1 - t, t); return b + t * (c - b);
                u = 0f; v = 1f - s; w = s;
                float bcx = cx - bx, bcy = cy - by, bcz = cz - bz;
                rx = bx + bcx * s; ry = by + bcy * s; rz = bz + bcz * s;
                return;
            }
            float denom = (float)(1.0 / ((double)va + vb + vc));
            float vv = vb * denom;
            float ww = vc * denom;
            // bary = (1 - v - w, v, w); return a + ab * v + ac * w (each operator a stored Vector3);
            u = (float)(1.0 - vv - ww); v = vv; w = ww;
            float sx = ax + abx * vv, sy = ay + aby * vv, sz = az + abz * vv;
            rx = sx + acx * ww; ry = sy + acy * ww; rz = sz + acz * ww;
        }

        // SurfaceBvh.SegmentParameter.
        private static float Segment(float px, float py, float pz, float fx, float fy, float fz, float tx, float ty, float tz)
        {
            float dx = tx - fx, dy = ty - fy, dz = tz - fz;
            float length = SqrMagnitude(dx, dy, dz);
            if (!(length > 0f)) return 0f;
            float s = Dot(px - fx, py - fy, pz - fz, dx, dy, dz) / length;
            // Mathf.Clamp01
            return s < 0f ? 0f : (s > 1f ? 1f : s);
        }

        // SurfaceBvh.ClosestPointOnCollapsedTriangle: the closest of its three segments.
        private static void Collapsed(float px, float py, float pz, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz,
            out float rx, out float ry, out float rz, out float u, out float v, out float w)
        {
            float tab = Segment(px, py, pz, ax, ay, az, bx, by, bz), tbc = Segment(px, py, pz, bx, by, bz, cx, cy, cz),
                tca = Segment(px, py, pz, cx, cy, cz, ax, ay, az);
            // var onAb = a + (b - a) * tab; ...
            float abx = ax + (bx - ax) * tab, aby = ay + (by - ay) * tab, abz = az + (bz - az) * tab;
            float bcx = bx + (cx - bx) * tbc, bcy = by + (cy - by) * tbc, bcz = bz + (cz - bz) * tbc;
            float cax = cx + (ax - cx) * tca, cay = cy + (ay - cy) * tca, caz = cz + (az - cz) * tca;
            float dab = SqrMagnitude(px - abx, py - aby, pz - abz), dbc = SqrMagnitude(px - bcx, py - bcy, pz - bcz),
                dca = SqrMagnitude(px - cax, py - cay, pz - caz);
            if (dab <= dbc && dab <= dca) { rx = abx; ry = aby; rz = abz; u = 1f - tab; v = tab; w = 0f; return; }
            if (dbc <= dca) { rx = bcx; ry = bcy; rz = bcz; u = 0f; v = 1f - tbc; w = tbc; return; }
            rx = cax; ry = cay; rz = caz; u = tca; v = 0f; w = 1f - tca;
        }

        // SurfaceBvh.Inverse.
        private static float Inverse(float value) => 1f / ((value < 0f ? -value : value) > 1e-12f ? value : (value < 0f ? -1e-12f : 1e-12f));

        /// <summary>SurfaceBvh.Raycast; the direction is a unit vector.</summary>
        [BurstCompile(FloatPrecision.Standard, FloatMode.Strict, CompileSynchronously = true)]
        public static void Raycast(in Tree tree, float ox, float oy, float oz, float dx, float dy, float dz, float maxDistance, in Filter filter, out Hit hit)
        {
            hit = default;
            hit.triangle = -1;
            hit.distance = maxDistance;
            if (tree.nodeCount == 0) return;
            float ix = Inverse(dx), iy = Inverse(dy), iz = Inverse(dz);
            float best = maxDistance;
            bool filtered = Filtered(filter);
            int* stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            Node* nodes = tree.nodes;
            while (sp > 0)
            {
                Node* node = nodes + stack[--sp];
                if (!RayEntersBounds(ox, oy, oz, ix, iy, iz, node, best)) continue;
                if (node->left < 0)
                {
                    for (int i = node->start; i < node->start + node->count; i++)
                    {
                        int t = tree.order[i];
                        if (filtered && !Accept(in filter, t, ref hit.error))
                        {
                            if (hit.error != 0) return;
                            continue;
                        }
                        if (!RayTriangle(ox, oy, oz, dx, dy, dz, tree.corners + 9 * i, out float distance, out float u, out float v, out float w) || distance >= best)
                            continue;
                        best = distance;
                        hit.found = 1;
                        hit.triangle = t;
                        hit.u = u; hit.v = v; hit.w = w;
                        // origin + direction * distance
                        float mx = dx * distance, my = dy * distance, mz = dz * distance;
                        hit.x = ox + mx; hit.y = oy + my; hit.z = oz + mz;
                    }
                }
                else
                {
                    stack[sp++] = node->left;
                    stack[sp++] = node->right;
                }
            }
            if (hit.found != 0) hit.distance = best;
        }

        // SurfaceBvh.RayEntersBounds (slabs, Mathf.Min/Max as written).
        private static bool RayEntersBounds(float ox, float oy, float oz, float ix, float iy, float iz, Node* n, float maxDistance)
        {
            float t1 = (float)(((double)n->minx - ox) * ix), t2 = (float)(((double)n->maxx - ox) * ix);
            float near = Min(t1, t2), far = Max(t1, t2);
            t1 = (float)(((double)n->miny - oy) * iy); t2 = (float)(((double)n->maxy - oy) * iy);
            near = Max(near, Min(t1, t2)); far = Min(far, Max(t1, t2));
            t1 = (float)(((double)n->minz - oz) * iz); t2 = (float)(((double)n->maxz - oz) * iz);
            near = Max(near, Min(t1, t2)); far = Min(far, Max(t1, t2));
            return far >= Max(near, 0f) && near <= maxDistance;
        }

        // SurfaceBvh.RayTriangle: Moller-Trumbore, both faces, statement for statement.
        private static bool RayTriangle(float ox, float oy, float oz, float dx, float dy, float dz, float* t, out float distance, out float bu, out float bv,
            out float bw)
        {
            distance = 0f; bu = 0f; bv = 0f; bw = 0f;
            float ax = t[0], ay = t[1], az = t[2];
            float e1x = t[3] - ax, e1y = t[4] - ay, e1z = t[5] - az;
            float e2x = t[6] - ax, e2y = t[7] - ay, e2z = t[8] - az;
            Cross(dx, dy, dz, e2x, e2y, e2z, out float px, out float py, out float pz);
            float det = Dot(e1x, e1y, e1z, px, py, pz);
            if ((det < 0f ? -det : det) < 1e-12f) return false;
            float inv = 1f / det;
            float sx = ox - ax, sy = oy - ay, sz = oz - az;
            float u = Dot(sx, sy, sz, px, py, pz) * inv;
            if (u < 0f || u > 1f) return false;
            Cross(sx, sy, sz, e1x, e1y, e1z, out float qx, out float qy, out float qz);
            float v = Dot(dx, dy, dz, qx, qy, qz) * inv;
            // u + v > 1f: the sum is compared as evaluated, in double.
            if (v < 0f || (double)u + v > 1.0) return false;
            distance = Dot(e2x, e2y, e2z, qx, qy, qz) * inv;
            if (distance < 0f) return false;
            bu = (float)(1.0 - u - v); bv = u; bw = v;
            return true;
        }
    }
}
#endif
