using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>Independent final-triangle checks, including UV seams and coplanar overlap.</summary>
    internal static class ReFitMeshIntersectionChecks
    {
        public static HashSet<long> Intersections(Vector3[] reference, Vector3[] points, int[] triangles)
        {
            int count = triangles.Length / 3;
            var min = new Vector3[count]; var max = new Vector3[count]; var order = new int[count];
            for (int t = 0; t < count; t++)
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                min[t] = Vector3.Min(points[a], Vector3.Min(points[b], points[c]));
                max[t] = Vector3.Max(points[a], Vector3.Max(points[b], points[c])); order[t] = t;
            }
            Array.Sort(order, (a, b) => min[a].x.CompareTo(min[b].x));
            var result = new HashSet<long>();
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count && min[order[j]].x <= max[order[i]].x; j++)
                {
                    int a = order[i], b = order[j];
                    if (min[a].y > max[b].y || min[b].y > max[a].y || min[a].z > max[b].z || min[b].z > max[a].z) continue;
                    bool adjacent = false;
                    for (int u = 0; u < 3; u++) for (int v = 0; v < 3; v++)
                        if ((reference[triangles[a * 3 + u]] - reference[triangles[b * 3 + v]]).sqrMagnitude < 1e-12f) adjacent = true;
                    if (adjacent) continue;
                    if (Crosses(points, triangles, a, b) || Crosses(points, triangles, b, a))
                        result.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                }
            return result;
        }

        private static bool Crosses(Vector3[] p, int[] tris, int first, int second)
        {
            Vector3 a = p[tris[second * 3]], b = p[tris[second * 3 + 1]], c = p[tris[second * 3 + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (normal.sqrMagnitude < 0.5f) return false;
            for (int k = 0; k < 3; k++)
            {
                Vector3 u = p[tris[first * 3 + k]], v = p[tris[first * 3 + (k + 1) % 3]];
                float du = Vector3.Dot(u - a, normal), dv = Vector3.Dot(v - a, normal);
                if (Mathf.Abs(du) < 1e-7f && Mathf.Abs(dv) < 1e-7f)
                {
                    if (Inside(u, a, b, c) || Inside((u + v) * .5f, a, b, c)) return true;
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 x = p[tris[second * 3 + e]], y = p[tris[second * 3 + (e + 1) % 3]];
                        float s1 = Vector3.Dot(Vector3.Cross(v - u, x - u), normal);
                        float s2 = Vector3.Dot(Vector3.Cross(v - u, y - u), normal);
                        float t1 = Vector3.Dot(Vector3.Cross(y - x, u - x), normal);
                        float t2 = Vector3.Dot(Vector3.Cross(y - x, v - x), normal);
                        if (s1 * s2 < -1e-20f && t1 * t2 < -1e-20f) return true;
                    }
                }
                else if (du * dv < 0 && Inside(Vector3.LerpUnclamped(u, v, du / (du - dv)), a, b, c)) return true;
            }
            return false;
        }

        private static bool Inside(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 u = b - a, v = c - a, w = p - a;
            double uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v);
            double wu = Vector3.Dot(w, u), wv = Vector3.Dot(w, v), d = uu * vv - uv * uv;
            if (Math.Abs(d) < 1e-24) return false;
            double x = (vv * wu - uv * wv) / d, y = (uu * wv - uv * wu) / d;
            return x > 1e-5 && y > 1e-5 && x + y < 1 - 1e-5;
        }

        public static void SelfTest()
        {
            var points = new[] { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, .2f, -1), new Vector3(0, .2f, 1), new Vector3(.3f, .5f, 0) };
            int[] triangles = { 0, 1, 2, 3, 4, 5 };
            if (Intersections(points, points, triangles).Count != 1) throw new InvalidOperationException("Intersection check missed crossing triangles.");
            points[3] = new Vector3(-.2f, .2f, 0); points[4] = new Vector3(.2f, .2f, 0);
            if (Intersections(points, points, triangles).Count != 1) throw new InvalidOperationException("Intersection check missed coplanar triangles.");
            for (int i = 3; i < 6; i++) points[i] += Vector3.forward;
            if (Intersections(points, points, triangles).Count != 0) throw new InvalidOperationException("Intersection check rejected separated triangles.");
            var tetra = new MeshSnapshot { worldVertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 } };
            if (Math.Abs(Winding(tetra, Vector3.one * .1f)) < .99 || Math.Abs(Winding(tetra, Vector3.one)) > .01)
                throw new InvalidOperationException("Solid-angle inside/outside check failed.");
        }

        public static double Winding(MeshSnapshot mesh, Vector3 point)
        {
            double sum = 0;
            for (int t = 0; t < mesh.triangles.Length; t += 3)
            {
                var a = mesh.worldVertices[mesh.triangles[t]] - point;
                var b = mesh.worldVertices[mesh.triangles[t + 1]] - point;
                var c = mesh.worldVertices[mesh.triangles[t + 2]] - point;
                double x = a.magnitude, y = b.magnitude, z = c.magnitude;
                sum += 2 * Math.Atan2(Vector3.Dot(a, Vector3.Cross(b, c)),
                    x * y * z + Vector3.Dot(a, b) * z + Vector3.Dot(b, c) * x + Vector3.Dot(c, a) * y);
            }
            return sum / (4 * Math.PI);
        }
    }
}
