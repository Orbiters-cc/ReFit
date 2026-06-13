using System;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Bounding volume hierarchy over a triangle soup for fast closest-point-on-surface queries,
    /// optionally restricted by a per-triangle predicate (used for normal / body-region filtering).
    /// Thread-safe for queries once built.
    /// </summary>
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
        private Vector3[] centroids;

        /// <summary>Builds a BVH over the given snapshot's world-space triangles.</summary>
        public static SurfaceBvh Build(MeshSnapshot snapshot)
        {
            var bvh = new SurfaceBvh();
            int triCount = snapshot.triangles.Length / 3;
            bvh.a = new Vector3[triCount];
            bvh.b = new Vector3[triCount];
            bvh.c = new Vector3[triCount];
            bvh.centroids = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                bvh.a[t] = snapshot.worldVertices[snapshot.triangles[t * 3]];
                bvh.b[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 1]];
                bvh.c[t] = snapshot.worldVertices[snapshot.triangles[t * 3 + 2]];
                bvh.centroids[t] = (bvh.a[t] + bvh.b[t] + bvh.c[t]) / 3f;
            }
            bvh.triOrder = new int[triCount];
            for (int t = 0; t < triCount; t++) bvh.triOrder[t] = t;
            bvh.nodes = new Node[Mathf.Max(1, triCount * 2)];
            bvh.nodeCount = 0;
            if (triCount > 0) bvh.BuildNode(0, triCount);
            return bvh;
        }

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
                SortRange(start, count, axis);
                int half = count / 2;
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

        private void SortRange(int start, int count, int axis)
        {
            var cents = centroids;
            var seg = new int[count];
            Array.Copy(triOrder, start, seg, 0, count);
            Comparison<int> cmp;
            if (axis == 0) cmp = (x, y) => cents[x].x.CompareTo(cents[y].x);
            else if (axis == 1) cmp = (x, y) => cents[x].y.CompareTo(cents[y].y);
            else cmp = (x, y) => cents[x].z.CompareTo(cents[y].z);
            Array.Sort(seg, cmp);
            Array.Copy(seg, 0, triOrder, start, count);
        }

        /// <summary>
        /// Closest point on the surface from <paramref name="point"/>, considering only triangles within
        /// <paramref name="maxDistance"/> and (optionally) passing <paramref name="filter"/>.
        /// </summary>
        public Hit ClosestPoint(Vector3 point, float maxDistance, Func<int, bool> filter = null)
        {
            var hit = new Hit { found = false, distance = maxDistance, triangle = -1 };
            if (nodeCount == 0) return hit;

            float bestSqr = maxDistance * maxDistance;
            var stack = new int[64];
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
                        if (filter != null && !filter(t)) continue;
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
                    if (sp + 2 >= stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    if (dl <= dr) { stack[sp++] = node.right; stack[sp++] = node.left; }
                    else { stack[sp++] = node.left; stack[sp++] = node.right; }
                }
            }
            if (hit.found) hit.distance = Mathf.Sqrt(bestSqr);
            return hit;
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
    }
}
