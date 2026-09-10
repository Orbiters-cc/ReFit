using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// A low-frequency periodic displacement field for closed, approximately planar tubes.
    /// Fitting the centerline rather than independently moving tube faces preserves the cross-section.
    /// All data is captured math/topology, so this can run on the geometry worker.
    /// </summary>
    public sealed class ReFitTubeField
    {
        private const int Harmonics = 4;
        private const int Terms = 1 + 2 * Harmonics;
        private readonly List<Ring> rings = new List<Ring>();
        public readonly bool[] groups;
        public int Count => rings.Count;

        private sealed class Ring
        {
            public int[] groups;
            public double[][] basis;
            public double[][] derivative;
            public double[,] inverse;
            public Vector3[] center;
            public Vector3[] tangent;
            public Vector3[] offset;
            public Vector3[] original;
            public int[] triangles;
        }

        private ReFitTubeField(int count) { groups = new bool[count]; }

        public static ReFitTubeField Build(MeshSnapshot asset, bool enabled)
        {
            var result = new ReFitTubeField(asset.GroupCount);
            if (!enabled || asset.groupAdjacency == null) return result;
            var visited = new bool[asset.GroupCount];
            var edges = new Dictionary<long, int>();
            var faces = new int[asset.GroupCount];
            for (int t = 0; t < asset.triangles.Length; t += 3)
            {
                faces[asset.groupOfVertex[asset.triangles[t]]]++;
                for (int j = 0; j < 3; j++)
                {
                    int a = asset.groupOfVertex[asset.triangles[t + j]];
                    int b = asset.groupOfVertex[asset.triangles[t + (j + 1) % 3]];
                    long key = Edge(a, b);
                    edges.TryGetValue(key, out int count);
                    edges[key] = count + 1;
                }
            }
            for (int seed = 0; seed < visited.Length; seed++)
            {
                if (visited[seed]) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(seed); visited[seed] = true;
                bool closed = true; int edgeCount = 0, faceCount = 0;
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue(); component.Add(g); faceCount += faces[g];
                    foreach (int n in asset.groupAdjacency[g])
                    {
                        if (g < n) edgeCount++;
                        if (!edges.TryGetValue(Edge(g, n), out int count) || count != 2) closed = false;
                        if (!visited[n]) { visited[n] = true; queue.Enqueue(n); }
                    }
                }
                // Require a closed genus-one component, not a sleeve, cloth shell, or small lace island.
                if (!closed || component.Count < 32 || component.Count - edgeCount + faceCount != 0) continue;
                var ring = TryBuildRing(asset, component);
                if (ring == null) continue;
                result.rings.Add(ring);
                foreach (int g in component) result.groups[g] = true;
            }
            return result;
        }

        public void Apply(MeshSnapshot asset, MeshSnapshot body, SurfaceBvh bodyIndex, Vector3[] bodyDeltas,
            Vector3[] primary, Vector3[] deltas, ReFitSettings settings, ReFitReport report, string label)
        {
            if (Count == 0) return;
            MeshSnapshot shaped = body;
            if (bodyDeltas != null && settings.enableClearanceCorrection)
            {
                var vertices = new Vector3[body.worldVertices.Length];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = body.worldVertices[i] + bodyDeltas[i];
                shaped = new MeshSnapshot { worldVertices = vertices, triangles = body.triangles };
            }
            float worstGap = 0;
            foreach (var ring in rings)
            {
                var wanted = new Vector3[ring.groups.Length];
                for (int i = 0; i < wanted.Length; i++)
                {
                    int g = ring.groups[i]; wanted[i] = deltas[g] + (primary != null ? primary[g] : Vector3.zero);
                }
                var coefficients = Fit(ring, wanted);
                var contacts = new Vector3[wanted.Length];
                var normals = new Vector3[wanted.Length];
                var gaps = new float[wanted.Length];
                if (bodyDeltas != null && settings.enableClearanceCorrection)
                    for (int i = 0; i < wanted.Length; i++)
                    {
                        int g = ring.groups[i];
                        var baseline = asset.worldVertices[asset.groupRep[g]] + (primary != null ? primary[g] : Vector3.zero);
                        var hit = bodyIndex.ClosestPoint(baseline, settings.maxProjectionDistance, null);
                        if (!hit.found) continue;
                        int t = hit.triangle * 3;
                        var motion = bodyDeltas[body.triangles[t]] * hit.bary.x + bodyDeltas[body.triangles[t + 1]] * hit.bary.y + bodyDeltas[body.triangles[t + 2]] * hit.bary.z;
                        contacts[i] = hit.position + motion;
                        normals[i] = shaped.FaceNormal(hit.triangle);
                        gaps[i] = Mathf.Min(Vector3.Dot(baseline - hit.position, body.FaceNormal(hit.triangle)), settings.clearanceMinimumSafetyDistance);
                    }
                // Project contact inequalities onto the shared centerline coefficients. Cross-sectional
                // vertices never receive independent clearance pushes or inward shrinkwrap corrections.
                if (bodyDeltas != null && settings.enableClearanceCorrection)
                    for (int pass = 0; pass < 12; pass++)
                    {
                        float worst = 0;
                        for (int i = 0; i < wanted.Length; i++)
                        {
                            if (normals[i].sqrMagnitude < 0.5f) continue;
                            Vector3 point = Position(ring, coefficients, i);
                            var normal = normals[i];
                            float deficit = gaps[i] - Vector3.Dot(point - contacts[i], normal);
                            worst = Mathf.Max(worst, deficit);
                            if (deficit <= 0.0001f) continue;
                            float step = Mathf.Min(deficit, 0.005f) * 0.8f;
                            var phi = ring.basis[i]; double norm = 0;
                            for (int k = 0; k < Terms; k++) norm += phi[k] * phi[k];
                            for (int k = 0; k < Terms; k++) coefficients[k] += normal * (float)(step * phi[k] / norm);
                        }
                        if (worst < 0.0002f) break;
                    }
                // Measure the final field, not the pre-update residual from the last sweep.
                for (int i = 0; i < wanted.Length; i++)
                    if (normals[i].sqrMagnitude > 0.5f)
                        worstGap = Mathf.Max(worstGap, gaps[i] - Vector3.Dot(Position(ring, coefficients, i) - contacts[i], normals[i]));
                ValidateField(ring, coefficients, report, label);
                for (int i = 0; i < wanted.Length; i++)
                {
                    int g = ring.groups[i];
                    deltas[g] = Position(ring, coefficients, i) - asset.worldVertices[asset.groupRep[g]] -
                        (primary != null ? primary[g] : Vector3.zero);
                }
            }
            report?.Info("tube-field", $"{label}: preserved cross-sections of {Count} closed tube(s); sampled contact residual {worstGap * 1000f:F3}mm (not a full collision test).");
            if (worstGap > 0.001f)
                report?.Warn("tube-contact-unresolved", $"{label}: tube contacts remain {worstGap * 1000f:F2}mm short. Inspect the result; automatic contact correction did not converge within 1mm.");
        }

        private static Vector3 Position(Ring ring, Vector3[] coefficients, int i)
        {
            Vector3 movement = Evaluate(coefficients, ring.basis[i]);
            return ring.center[i] + movement + Rotation(ring, coefficients, i) * ring.offset[i];
        }

        private static Quaternion Rotation(Ring ring, Vector3[] coefficients, int i)
        {
            Vector3 tangent = ring.tangent[i] + Evaluate(coefficients, ring.derivative[i]);
            return tangent.sqrMagnitude > 1e-12f
                ? Quaternion.FromToRotation(ring.tangent[i], tangent) : Quaternion.identity;
        }

        private static void ValidateField(Ring ring, Vector3[] coefficients, ReFitReport report, string label)
        {
            var points = new Vector3[ring.groups.Length];
            for (int i = 0; i < points.Length; i++) points[i] = Position(ring, coefficients, i);
            for (int t = 0; t < ring.triangles.Length; t += 3)
            {
                int a = ring.triangles[t], b = ring.triangles[t + 1], c = ring.triangles[t + 2];
                var oldNormal = Vector3.Cross(ring.original[b] - ring.original[a], ring.original[c] - ring.original[a]);
                var newNormal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                bool invalid = float.IsNaN(newNormal.sqrMagnitude) || float.IsInfinity(newNormal.sqrMagnitude) ||
                    newNormal.sqrMagnitude < oldNormal.sqrMagnitude * 1e-8f ||
                    Vector3.Dot(Rotation(ring, coefficients, a) * oldNormal, newNormal) < 0;
                for (int e = 0; e < 3; e++)
                {
                    int u = ring.triangles[t + e], v = ring.triangles[t + (e + 1) % 3];
                    invalid |= (points[u] - points[v]).sqrMagnitude > (ring.original[u] - ring.original[v]).sqrMagnitude * 64;
                }
                if (!invalid) continue;
                // A report error makes the engine result unsuccessful, so the service never applies/saves it.
                report?.Error("tube-geometry-invalid", $"{label}: closed tube starting at group {ring.groups[0]} folds, collapses or exceeds 8x edge stretch at local triangle {t / 3}. The result cannot be applied; inspect the source fit or use manual fitting.");
                return;
            }
        }

        private static Ring TryBuildRing(MeshSnapshot asset, List<int> component)
        {
            int count = component.Count;
            var points = new Vector3[count]; Vector3 center = Vector3.zero;
            for (int i = 0; i < count; i++) { points[i] = asset.worldVertices[asset.groupRep[component[i]]]; center += points[i]; }
            center /= count;
            var covariance = new double[3, 3];
            foreach (var point in points)
            {
                var d = point - center;
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) covariance[r, c] += (double)d[r] * d[c];
            }
            var axes = Matrix4x4.identity;
            // Symmetric Jacobi eigensolve; only three dimensions, independent of vertex count.
            for (int it = 0; it < 24; it++)
            {
                int p = 0, q = 1;
                for (int r = 0; r < 3; r++) for (int c = r + 1; c < 3; c++)
                    if (Math.Abs(covariance[r, c]) > Math.Abs(covariance[p, q])) { p = r; q = c; }
                if (Math.Abs(covariance[p, q]) < 1e-14) break;
                double angle = 0.5 * Math.Atan2(2 * covariance[p, q], covariance[q, q] - covariance[p, p]);
                double cs = Math.Cos(angle), sn = Math.Sin(angle);
                var old = (double[,])covariance.Clone();
                for (int r = 0; r < 3; r++)
                {
                    covariance[r, p] = cs * old[r, p] - sn * old[r, q];
                    covariance[r, q] = sn * old[r, p] + cs * old[r, q];
                }
                old = (double[,])covariance.Clone();
                for (int c = 0; c < 3; c++)
                {
                    covariance[p, c] = cs * old[p, c] - sn * old[q, c];
                    covariance[q, c] = sn * old[p, c] + cs * old[q, c];
                }
                var ap = axes.GetColumn(p); var aq = axes.GetColumn(q);
                axes.SetColumn(p, (float)cs * ap - (float)sn * aq);
                axes.SetColumn(q, (float)sn * ap + (float)cs * aq);
            }
            int small = 0;
            for (int i = 1; i < 3; i++) if (covariance[i, i] < covariance[small, small]) small = i;
            int x = (small + 1) % 3, y = (small + 2) % 3;
            double planar = Math.Min(covariance[x, x], covariance[y, y]);
            if (planar <= 1e-12 || covariance[small, small] > planar * 0.08) return null;
            Vector3 ax = axes.GetColumn(x), ay = axes.GetColumn(y);
            var ring = new Ring { groups = component.ToArray(), basis = new double[count][], derivative = new double[count][],
                center = new Vector3[count], tangent = new Vector3[count], offset = new Vector3[count], original = points };
            float minRadius = float.MaxValue, maxRadius = 0;
            var occupied = new bool[16];
            for (int i = 0; i < count; i++)
            {
                var d = points[i] - center;
                double angle = Math.Atan2(Vector3.Dot(d, ay), Vector3.Dot(d, ax));
                float radius = new Vector2(Vector3.Dot(d, ax), Vector3.Dot(d, ay)).magnitude;
                minRadius = Mathf.Min(minRadius, radius); maxRadius = Mathf.Max(maxRadius, radius);
                occupied[Math.Min(15, (int)((angle + Math.PI) / (2 * Math.PI) * 16))] = true;
                ring.basis[i] = Basis(angle, false); ring.derivative[i] = Basis(angle, true);
            }
            if (minRadius < maxRadius * 0.3f) return null;
            foreach (bool sector in occupied) if (!sector) return null;
            var gram = new double[Terms, Terms];
            for (int i = 0; i < count; i++) for (int r = 0; r < Terms; r++) for (int c = 0; c < Terms; c++)
                gram[r, c] += ring.basis[i][r] * ring.basis[i][c];
            ring.inverse = Invert(gram);
            if (ring.inverse == null) return null;
            var coefficients = Fit(ring, points);
            float maxOffset = 0;
            for (int i = 0; i < count; i++)
            {
                ring.center[i] = Evaluate(coefficients, ring.basis[i]);
                ring.tangent[i] = Evaluate(coefficients, ring.derivative[i]);
                ring.offset[i] = points[i] - ring.center[i];
                maxOffset = Mathf.Max(maxOffset, ring.offset[i].magnitude);
            }
            if (maxOffset > minRadius * 0.25f) return null;
            var local = new Dictionary<int, int>(count);
            for (int i = 0; i < count; i++) local[component[i]] = i;
            var triangles = new List<int>();
            for (int t = 0; t < asset.triangles.Length; t += 3)
            {
                if (!local.TryGetValue(asset.groupOfVertex[asset.triangles[t]], out int a)) continue;
                triangles.Add(a);
                triangles.Add(local[asset.groupOfVertex[asset.triangles[t + 1]]]);
                triangles.Add(local[asset.groupOfVertex[asset.triangles[t + 2]]]);
            }
            ring.triangles = triangles.ToArray();
            return ring;
        }

        private static double[] Basis(double angle, bool derivative)
        {
            var result = new double[Terms]; result[0] = derivative ? 0 : 1;
            for (int k = 1; k <= Harmonics; k++)
            {
                result[2 * k - 1] = derivative ? -k * Math.Sin(k * angle) : Math.Cos(k * angle);
                result[2 * k] = derivative ? k * Math.Cos(k * angle) : Math.Sin(k * angle);
            }
            return result;
        }

        private static Vector3 Evaluate(Vector3[] coefficients, double[] basis)
        {
            Vector3 result = Vector3.zero;
            for (int k = 0; k < Terms; k++) result += coefficients[k] * (float)basis[k];
            return result;
        }

        private static Vector3[] Fit(Ring ring, Vector3[] samples)
        {
            var rhs = new Vector3[Terms]; var result = new Vector3[Terms];
            for (int i = 0; i < samples.Length; i++) for (int k = 0; k < Terms; k++) rhs[k] += samples[i] * (float)ring.basis[i][k];
            for (int r = 0; r < Terms; r++) for (int c = 0; c < Terms; c++) result[r] += rhs[c] * (float)ring.inverse[r, c];
            return result;
        }

        private static double[,] Invert(double[,] input)
        {
            var a = (double[,])input.Clone(); var inverse = new double[Terms, Terms];
            for (int i = 0; i < Terms; i++) inverse[i, i] = 1;
            for (int p = 0; p < Terms; p++)
            {
                int pivot = p;
                for (int r = p + 1; r < Terms; r++) if (Math.Abs(a[r, p]) > Math.Abs(a[pivot, p])) pivot = r;
                if (Math.Abs(a[pivot, p]) < 1e-10) return null;
                for (int c = 0; c < Terms; c++)
                {
                    double v = a[p, c]; a[p, c] = a[pivot, c]; a[pivot, c] = v;
                    v = inverse[p, c]; inverse[p, c] = inverse[pivot, c]; inverse[pivot, c] = v;
                }
                double scale = a[p, p];
                for (int c = 0; c < Terms; c++) { a[p, c] /= scale; inverse[p, c] /= scale; }
                for (int r = 0; r < Terms; r++) if (r != p)
                {
                    double f = a[r, p];
                    for (int c = 0; c < Terms; c++) { a[r, c] -= f * a[p, c]; inverse[r, c] -= f * inverse[p, c]; }
                }
            }
            return inverse;
        }

        private static long Edge(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
    }
}
