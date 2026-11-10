using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The registration's smoothing solve with its coarse correction: within the same iteration budget it reaches a lower
    /// energy than the uninterrupted conjugate gradients it replaced, where patches of skin have no match (zero data weight),
    /// with a separate part and two surfaces a cube apart, whose coarse nodes it never ties together.
    /// </summary>
    public sealed class ReFitRegistrationSolveTests
    {
        // A closed-around tube of nu x nv nodes (a limb-sized cylinder), its triangles, and data weights with holes.
        private static (Vector3[] nodes, int[] triangles, float[] weights, Vector3[] targets) Problem(int nu, int nv, int seed)
        {
            var random = new System.Random(seed);
            var nodes = new Vector3[nu * nv];
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    float u = i * 2f * Mathf.PI / nu;
                    nodes[j * nu + i] = new Vector3(.3f * Mathf.Cos(u), j * .006f, .3f * Mathf.Sin(u));
                }
            var triangles = new List<int>();
            for (int j = 0; j + 1 < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    int a = j * nu + i, b = j * nu + (i + 1) % nu, c = (j + 1) * nu + i, d = (j + 1) * nu + (i + 1) % nu;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            var holes = new List<(Vector3, float)>();
            for (int h = 0; h < 12; h++) holes.Add((nodes[random.Next(nodes.Length)], .05f + (float)random.NextDouble() * .1f));
            var weights = new float[nodes.Length];
            var targets = new Vector3[nodes.Length];
            for (int n = 0; n < nodes.Length; n++)
            {
                bool hole = false;
                foreach (var (center, radius) in holes) if ((nodes[n] - center).sqrMagnitude < radius * radius) hole = true;
                weights[n] = hole ? 0f : .2f + .8f * (float)random.NextDouble();
                targets[n] = new Vector3(Mathf.Sin(nodes[n].y * 20f), Mathf.Cos(nodes[n].x * 7f), 0f) * .02f;
            }
            return (nodes, triangles.ToArray(), weights, targets);
        }

        private static double Energy(Vector3[] x, float[] weights, Vector3[] targets, int[] triangles, float stiffness, Vector3[] start)
        {
            // 1/2 sum w |x - t|^2 + 1/2 s sum over edges |x_i - x_j|^2 + 1/2 a |x - x0|^2 (each edge of the graph once).
            double energy = 0;
            for (int n = 0; n < x.Length; n++)
                energy += .5 * weights[n] * (x[n] - targets[n]).sqrMagnitude + .5 * ReFitBodyRegistration.Anchor * (x[n] - start[n]).sqrMagnitude;
            var seen = new HashSet<long>();
            for (int t = 0; t < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                    if (!seen.Add((long)Mathf.Min(a, b) * x.Length + Mathf.Max(a, b))) continue;
                    energy += .5 * stiffness * (x[a] - x[b]).sqrMagnitude;
                }
            return energy;
        }

        // Two surfaces 1.5 cm apart (a thigh against the other, an arm against the torso: one 3 cm cube holds both) and a small
        // separate part with no match (an eye), added to the tube.
        private static (Vector3[] nodes, int[] triangles, float[] weights, Vector3[] targets) WithNeighbours(int seed)
        {
            var (nodes, triangles, weights, targets) = Problem(300, 160, seed);
            var (inner, innerTriangles, innerWeights, innerTargets) = Problem(300, 160, seed + 1);
            var eye = new List<Vector3>();
            var eyeTriangles = new List<int>();
            for (int j = 0; j < 6; j++)
                for (int i = 0; i < 6; i++) eye.Add(new Vector3(i * .004f, .3f + j * .004f, -.2f));
            for (int j = 0; j < 5; j++)
                for (int i = 0; i < 5; i++)
                {
                    int k = j * 6 + i;
                    eyeTriangles.AddRange(new[] { k, k + 6, k + 1, k + 1, k + 6, k + 7 });
                }
            var allNodes = new List<Vector3>(nodes);
            var allTriangles = new List<int>(triangles);
            var allWeights = new List<float>(weights);
            var allTargets = new List<Vector3>(targets);
            int offset = allNodes.Count;
            foreach (var p in inner) allNodes.Add(new Vector3(p.x * .95f, p.y, p.z * .95f));
            foreach (int t in innerTriangles) allTriangles.Add(t + offset);
            allWeights.AddRange(innerWeights);
            allTargets.AddRange(innerTargets);
            offset = allNodes.Count;
            allNodes.AddRange(eye);
            foreach (int t in eyeTriangles) allTriangles.Add(t + offset);
            foreach (var _ in eye) { allWeights.Add(0f); allTargets.Add(Vector3.zero); }
            return (allNodes.ToArray(), allTriangles.ToArray(), allWeights.ToArray(), allTargets.ToArray());
        }

        [Test]
        public void TheCoarseCorrectionReachesALowerEnergyThanTheUninterruptedSolve()
        {
            foreach (var problem in new[] { Problem(300, 160, 4), WithNeighbours(9) })
            {
                var (nodes, triangles, weights, targets) = problem;
                var graph = ReFitBodyRegistration.Graph.Build(triangles, nodes.Length);
                var coarse = ReFitBodyRegistration.Coarse.Build(nodes, graph, ReFitBodyRegistration.CoarseCell);
                foreach (float stiffness in new[] { 60f, 4f })
                {
                    var start = new Vector3[nodes.Length];
                    // Without the coarse copy the solve is the one before it: 160 uninterrupted iterations, the same matrix,
                    // start, tolerance and order of sums.
                    var plain = (Vector3[])start.Clone();
                    var corrected = (Vector3[])start.Clone();
                    int plainIterations = ReFitBodyRegistration.Solve(plain, weights, targets, graph, null, stiffness, CancellationToken.None);
                    ReFitBodyRegistration.Solve(corrected, weights, targets, graph, coarse, stiffness, CancellationToken.None);
                    Assert.That(plainIterations, Is.EqualTo(160), "The uninterrupted solve uses its whole budget here");
                    double plainEnergy = Energy(plain, weights, targets, triangles, stiffness, start);
                    double correctedEnergy = Energy(corrected, weights, targets, triangles, stiffness, start);
                    // The same energy (both from rest, anchored there), lower with the coarse correction.
                    Assert.That(correctedEnergy, Is.LessThan(plainEnergy),
                        $"Stiffness {stiffness}: energy {correctedEnergy:E6} with the coarse correction, {plainEnergy:E6} without");
                    // The same bits every time (fixed ranges, a serial coarse solve).
                    var again = (Vector3[])start.Clone();
                    ReFitBodyRegistration.Solve(again, weights, targets, graph, coarse, stiffness, CancellationToken.None);
                    Assert.That(again, Is.EqualTo(corrected));
                }
            }
        }

        [Test]
        public void ACoarseNodeNeverTiesTwoSurfacesTogether()
        {
            var (nodes, triangles, _, _) = WithNeighbours(3);
            var graph = ReFitBodyRegistration.Graph.Build(triangles, nodes.Length);
            var coarse = ReFitBodyRegistration.Coarse.Build(nodes, graph, ReFitBodyRegistration.CoarseCell);
            // The connected parts of the skin (union of its edges).
            var root = new int[nodes.Length];
            for (int i = 0; i < root.Length; i++) root[i] = i;
            int Find(int a) { while (root[a] != a) a = root[a] = root[root[a]]; return a; }
            for (int t = 0; t < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = Find(triangles[t + k]), b = Find(triangles[t + (k + 1) % 3]);
                    if (a != b) root[Mathf.Max(a, b)] = Mathf.Min(a, b);
                }
            var partOf = new Dictionary<int, int>();
            for (int i = 0; i < nodes.Length; i++)
            {
                int part = Find(i);
                if (partOf.TryGetValue(coarse.Of(i), out int seen)) Assert.That(part, Is.EqualTo(seen), "Node " + i + " shares a coarse node with another part");
                else partOf[coarse.Of(i)] = part;
            }
            Assert.That(new HashSet<int>(partOf.Values).Count, Is.EqualTo(3), "The two tubes and the eye");
        }
    }
}
