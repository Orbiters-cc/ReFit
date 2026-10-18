using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Fills a per-bone field over a surface from the points where it is known, as smoothly as the surface allows: the weight
    /// inpainting of "Robust Skin Weights Transfer via Weight Inpainting" (Abdrashitov et al., SIGGRAPH Asia 2023). With L the
    /// cotangent Laplacian (positive semi-definite here) and M the lumped mass, the unknowns minimise xᵀQx for
    /// Q = L + L M⁻¹ L, the known values held: Q_uu x_u = −Q_uk x_k, by Jacobi-preconditioned conjugate gradients.
    /// <para>Fast where it can be: the unknowns split into the parts of the surface they connect (a skirt, a hood, each link of
    /// a chain), and a part is solved only for the fields known to be non-zero around it (the others are zero there). Each part
    /// is first filled harmonically (L_uu x_u = −L_uk x_k: well conditioned, solved to convergence), then smoothed towards the
    /// inpainting by a bounded number of its own iterations: Q is far too ill-conditioned for its residual to tell when it has
    /// converged, and what the smoothing adds (no crease where the known part ends) shows within a few hundred. Unknowns in a
    /// part of the surface with no known point stay at zero. Read-only once built (thread safe).</para>
    /// </summary>
    internal sealed class WeightInpainting
    {
        // Cotangents are clamped (obtuse angles give negative ones, slivers huge ones), and an edge weight never vanishes.
        private const float MaxCotangent = 1000f, MinEdgeWeight = 1e-4f;
        // The harmonic fill stops once its residual is this share of the right-hand side's (squared), or at its limit; the
        // smoothing takes at most MaxIterations.
        public const int MaxIterations = 300, MaxHarmonicIterations = 2000;
        public const double Tolerance = 1e-14, HarmonicTolerance = 1e-12;

        private readonly int count;
        private readonly int[] unknowns;
        // Rows of the unknowns, flattened: Q's (and L's) entries over unknowns (columns: unknown indices) then over known nodes
        // (columns: node indices). Row u: [start[u], split[u]) unknown entries, [split[u], start[u + 1]) known ones.
        private readonly int[] qStart, qSplit, qColumn, lStart, lSplit, lColumn;
        private readonly double[] qValue, lValue, qDiagonal, lDiagonal;
        // The parts: their unknowns (indices into the unknowns) and the known nodes around them.
        private readonly int[][] parts, borders;

        public int UnknownCount => unknowns.Length;
        public int PartCount => parts.Length;

        /// <param name="positions">Node positions.</param>
        /// <param name="triangles">Node triangles (indices into <paramref name="positions"/>).</param>
        /// <param name="known">Nodes whose value is given.</param>
        public WeightInpainting(Vector3[] positions, int[] triangles, bool[] known)
        {
            count = positions.Length;
            var edges = new Dictionary<long, double>();
            var mass = new double[count];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a == b || b == c || a == c) continue;
                Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
                double area = Vector3.Cross(pb - pa, pc - pa).magnitude * .5;
                mass[a] += area / 3; mass[b] += area / 3; mass[c] += area / 3;
                // Each edge weighed by the cotangent of the angle opposite it.
                AddEdge(edges, b, c, Cotangent(pb, pa, pc));
                AddEdge(edges, c, a, Cotangent(pc, pb, pa));
                AddEdge(edges, a, b, Cotangent(pa, pc, pb));
            }
            // L as adjacency (positive semi-definite: the diagonal is the sum of the edge weights, off-diagonal minus them).
            var degree = new int[count + 1];
            foreach (var pair in edges) { degree[(int)(pair.Key >> 32)]++; degree[(int)(pair.Key & 0xffffffff)]++; }
            var adjacencyStart = new int[count + 1];
            for (int i = 0; i < count; i++) adjacencyStart[i + 1] = adjacencyStart[i] + degree[i];
            var adjacencyNode = new int[adjacencyStart[count]];
            var adjacencyWeight = new double[adjacencyStart[count]];
            var fill = (int[])adjacencyStart.Clone();
            var diagonal = new double[count];
            foreach (var pair in edges)
            {
                int i = (int)(pair.Key >> 32), j = (int)(pair.Key & 0xffffffff);
                double w = Math.Max(MinEdgeWeight, pair.Value);
                adjacencyNode[fill[i]] = j; adjacencyWeight[fill[i]++] = w;
                adjacencyNode[fill[j]] = i; adjacencyWeight[fill[j]++] = w;
                diagonal[i] += w;
                diagonal[j] += w;
            }
            double meanMass = 0;
            int massCount = 0;
            for (int i = 0; i < count; i++) if (mass[i] > 0) { meanMass += mass[i]; massCount++; }
            meanMass = massCount > 0 ? meanMass / massCount : 1;
            for (int i = 0; i < count; i++) if (mass[i] <= 0) mass[i] = meanMass;

            var unknownOf = new int[count];
            var list = new List<int>();
            for (int i = 0; i < count; i++) { unknownOf[i] = known[i] ? -1 : list.Count; if (!known[i]) list.Add(i); }
            unknowns = list.ToArray();
            int n = unknowns.Length;

            // Rows of Q = L + L M⁻¹ L for the unknowns: L_i + sum over k of L_ik / M_k * L_k (k = i and its neighbours).
            var row = new Dictionary<int, double>(64);
            var qColumns = new List<int>(n * 24);
            var qValues = new List<double>(n * 24);
            qStart = new int[n + 1];
            qSplit = new int[n];
            qDiagonal = new double[n];
            var unknownEntries = new List<KeyValuePair<int, double>>();
            var knownEntries = new List<KeyValuePair<int, double>>();
            for (int u = 0; u < n; u++)
            {
                int i = unknowns[u];
                row.Clear();
                void Add(int column, double value) => row[column] = (row.TryGetValue(column, out double v) ? v : 0) + value;
                Add(i, diagonal[i]);
                for (int e = adjacencyStart[i]; e < adjacencyStart[i + 1]; e++) Add(adjacencyNode[e], -adjacencyWeight[e]);
                double scale = diagonal[i] / mass[i];
                Add(i, scale * diagonal[i]);
                for (int e = adjacencyStart[i]; e < adjacencyStart[i + 1]; e++) Add(adjacencyNode[e], -scale * adjacencyWeight[e]);
                for (int e = adjacencyStart[i]; e < adjacencyStart[i + 1]; e++)
                {
                    int k = adjacencyNode[e];
                    double s = -adjacencyWeight[e] / mass[k];
                    Add(k, s * diagonal[k]);
                    for (int f = adjacencyStart[k]; f < adjacencyStart[k + 1]; f++) Add(adjacencyNode[f], -s * adjacencyWeight[f]);
                }
                Split(row, unknownOf, unknownEntries, knownEntries);
                qStart[u] = qColumns.Count;
                foreach (var entry in unknownEntries)
                {
                    int column = unknownOf[entry.Key];
                    if (column == u) qDiagonal[u] = entry.Value;
                    qColumns.Add(column); qValues.Add(entry.Value);
                }
                qSplit[u] = qColumns.Count;
                foreach (var entry in knownEntries) { qColumns.Add(entry.Key); qValues.Add(entry.Value); }
            }
            qStart[n] = qColumns.Count;
            qColumn = qColumns.ToArray();
            qValue = qValues.ToArray();

            // Rows of L for the unknowns (the harmonic fill the solves start from).
            lStart = new int[n + 1];
            lSplit = new int[n];
            lDiagonal = new double[n];
            var lColumns = new List<int>(n * 7);
            var lValues = new List<double>(n * 7);
            for (int u = 0; u < n; u++)
            {
                int i = unknowns[u];
                lDiagonal[u] = diagonal[i];
                lStart[u] = lColumns.Count;
                lColumns.Add(u);
                lValues.Add(diagonal[i]);
                for (int e = adjacencyStart[i]; e < adjacencyStart[i + 1]; e++)
                    if (unknownOf[adjacencyNode[e]] >= 0) { lColumns.Add(unknownOf[adjacencyNode[e]]); lValues.Add(-adjacencyWeight[e]); }
                lSplit[u] = lColumns.Count;
                for (int e = adjacencyStart[i]; e < adjacencyStart[i + 1]; e++)
                    if (unknownOf[adjacencyNode[e]] < 0) { lColumns.Add(adjacencyNode[e]); lValues.Add(-adjacencyWeight[e]); }
            }
            lStart[n] = lColumns.Count;
            lColumn = lColumns.ToArray();
            lValue = lValues.ToArray();

            // The parts: unknowns joined by Q (which reaches two rings, so across a known node too).
            var parent = new int[n];
            for (int u = 0; u < n; u++) parent[u] = u;
            int Root(int u) { while (parent[u] != u) { parent[u] = parent[parent[u]]; u = parent[u]; } return u; }
            for (int u = 0; u < n; u++)
                for (int e = qStart[u]; e < qSplit[u]; e++)
                {
                    int a = Root(u), b = Root(qColumn[e]);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            var partOf = new Dictionary<int, List<int>>();
            for (int u = 0; u < n; u++)
            {
                int r = Root(u);
                if (!partOf.TryGetValue(r, out var members)) partOf[r] = members = new List<int>();
                members.Add(u);
            }
            parts = new int[partOf.Count][];
            borders = new int[partOf.Count][];
            int p = 0;
            var border = new HashSet<int>();
            foreach (var members in partOf.Values)
            {
                parts[p] = members.ToArray();
                border.Clear();
                foreach (int u in members)
                    for (int e = qSplit[u]; e < qStart[u + 1]; e++) border.Add(qColumn[e]);
                borders[p] = new int[border.Count];
                border.CopyTo(borders[p]);
                p++;
            }
        }

        private static void Split(Dictionary<int, double> row, int[] unknownOf, List<KeyValuePair<int, double>> unknownEntries, List<KeyValuePair<int, double>> knownEntries)
        {
            unknownEntries.Clear();
            knownEntries.Clear();
            foreach (var pair in row)
            {
                if (pair.Value == 0) continue;
                if (unknownOf[pair.Key] >= 0) unknownEntries.Add(pair); else knownEntries.Add(pair);
            }
        }

        private static void AddEdge(Dictionary<long, double> edges, int i, int j, float cotangent)
        {
            if (i > j) { int s = i; i = j; j = s; }
            long key = ((long)i << 32) | (uint)j;
            edges[key] = (edges.TryGetValue(key, out double v) ? v : 0) + .5 * Mathf.Clamp(cotangent, 0f, MaxCotangent);
        }

        // Cotangent of the angle at p between p→a and p→b.
        private static float Cotangent(Vector3 a, Vector3 p, Vector3 b)
        {
            var u = a - p;
            var v = b - p;
            float cross = Vector3.Cross(u, v).magnitude;
            return cross > 1e-12f ? Vector3.Dot(u, v) / cross : MaxCotangent;
        }

        /// <summary>
        /// The unknown nodes' values of one field, given the known ones (<paramref name="values"/> at known nodes; written at
        /// unknown nodes). Returns false when a harmonic fill stopped at its iteration limit (its values are written all the
        /// same: the best it reached). <paramref name="smoothing"/> is the smoothing's iteration count (tests).
        /// </summary>
        public bool Solve(double[] values, int smoothing = MaxIterations)
        {
            int n = unknowns.Length;
            if (n == 0) return true;
            bool converged = true;
            double[] x = null, b = null, r = null, z = null, d = null, q = null;
            for (int part = 0; part < parts.Length; part++)
            {
                var members = parts[part];
                bool any = false;
                foreach (int k in borders[part]) if (values[k] != 0) { any = true; break; }
                if (!any)
                {
                    foreach (int u in members) values[unknowns[u]] = 0;
                    continue;
                }
                if (x == null) { x = new double[n]; b = new double[n]; r = new double[n]; z = new double[n]; d = new double[n]; q = new double[n]; }
                foreach (int u in members) x[u] = 0;
                converged &= Conjugate(members, values, lStart, lSplit, lColumn, lValue, lDiagonal, x, b, r, z, d, q, MaxHarmonicIterations, HarmonicTolerance);
                if (smoothing > 0) Conjugate(members, values, qStart, qSplit, qColumn, qValue, qDiagonal, x, b, r, z, d, q, smoothing, Tolerance);
                foreach (int u in members) values[unknowns[u]] = x[u];
            }
            return converged;
        }

        // Jacobi-preconditioned conjugate gradients over one part's rows, from x; true when converged.
        private static bool Conjugate(int[] members, double[] values, int[] start, int[] split, int[] column, double[] value, double[] diagonal,
            double[] x, double[] b, double[] r, double[] z, double[] d, double[] q, int maxIterations, double tolerance)
        {
            double norm = 0, rz = 0;
            foreach (int u in members)
            {
                double sum = 0;
                for (int e = split[u]; e < start[u + 1]; e++) sum -= value[e] * values[column[e]];
                b[u] = sum;
                norm += sum * sum;
            }
            if (norm < 1e-30) { foreach (int u in members) x[u] = 0; return true; }
            foreach (int u in members)
            {
                double ax = 0;
                for (int e = start[u]; e < split[u]; e++) ax += value[e] * x[column[e]];
                r[u] = b[u] - ax;
                double diag = diagonal[u] > 0 ? diagonal[u] : 1;
                z[u] = r[u] / diag;
                d[u] = z[u];
                rz += r[u] * z[u];
            }
            double limit = norm * tolerance;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                double dq = 0;
                foreach (int u in members)
                {
                    double sum = 0;
                    for (int e = start[u]; e < split[u]; e++) sum += value[e] * d[column[e]];
                    q[u] = sum;
                    dq += d[u] * sum;
                }
                // Exactly converged (no direction left), or lost to rounding.
                if (dq <= 0) return rz <= 0;
                double alpha = rz / dq, residual = 0, next = 0;
                foreach (int u in members)
                {
                    x[u] += alpha * d[u];
                    r[u] -= alpha * q[u];
                    residual += r[u] * r[u];
                    double diag = diagonal[u] > 0 ? diagonal[u] : 1;
                    z[u] = r[u] / diag;
                    next += r[u] * z[u];
                }
                if (residual < limit) return true;
                double beta = next / rz;
                rz = next;
                foreach (int u in members) d[u] = z[u] + beta * d[u];
            }
            return false;
        }
    }
}
