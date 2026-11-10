using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The registration's loops compiled by Burst and run by Mono give the same bits: each kernel on the same inputs (the
    /// compiled one through its function pointer, the managed one called directly), then a whole solve each way. Ignored
    /// where Burst compilation is off.
    /// </summary>
    public sealed unsafe class ReFitRegistrationKernelTests
    {
        private const int Nodes = 3000;

        private static void RequireBurst()
        {
            if (!BurstCompiler.IsEnabled) Assert.Ignore("Burst compilation is off: nothing compiled to compare with.");
        }

        private static double[] Random(System.Random random, int count)
        {
            var values = new double[count];
            for (int i = 0; i < count; i++) values[i] = random.NextDouble() * 2 - 1;
            return values;
        }

        private static void AssertSameBits(double[] expected, double[] actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
                Assert.That(BitConverter.DoubleToInt64Bits(actual[i]), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected[i])), what + " at " + i);
        }

        // A ring graph with a few chords: each node 2 to 4 neighbours, sorted.
        private static (int[] start, int[] next) Graph()
        {
            var lists = new List<int>[Nodes];
            for (int i = 0; i < Nodes; i++) lists[i] = new List<int>();
            void Link(int a, int b) { if (a != b && !lists[a].Contains(b)) { lists[a].Add(b); lists[b].Add(a); } }
            for (int i = 0; i < Nodes; i++) { Link(i, (i + 1) % Nodes); if (i % 7 == 0) Link(i, (i * 31 + 5) % Nodes); }
            var start = new int[Nodes + 1];
            var next = new List<int>();
            for (int i = 0; i < Nodes; i++) { lists[i].Sort(); next.AddRange(lists[i]); start[i + 1] = next.Count; }
            return (start, next.ToArray());
        }

        [Test]
        public void EachKernelCompiledGivesTheManagedBits()
        {
            RequireBurst();
            var multiply = BurstCompiler.CompileFunctionPointer<ReFitRegistrationKernels.MultiplyFn>(ReFitRegistrationKernels.Multiply);
            var begin = BurstCompiler.CompileFunctionPointer<ReFitRegistrationKernels.StartFn>(ReFitRegistrationKernels.Start);
            var step = BurstCompiler.CompileFunctionPointer<ReFitRegistrationKernels.StepFn>(ReFitRegistrationKernels.Step);
            var direction = BurstCompiler.CompileFunctionPointer<ReFitRegistrationKernels.DirectionFn>(ReFitRegistrationKernels.Direction);
            var random = new System.Random(12);
            var (start, next) = Graph();
            var weights = new float[Nodes];
            for (int i = 0; i < Nodes; i++) weights[i] = (float)random.NextDouble();
            var input = Random(random, 3 * Nodes);
            var diagonal = new double[Nodes];
            for (int i = 0; i < Nodes; i++) diagonal[i] = 1 + random.NextDouble() * 50;
            var b = Random(random, 3 * Nodes);
            var q = Random(random, 3 * Nodes);

            // Each kernel twice, on copies of the same inputs.
            double[][] Copies(params double[][] arrays) { var c = new double[arrays.Length][]; for (int k = 0; k < arrays.Length; k++) c[k] = (double[])arrays[k].Clone(); return c; }

            var managed = Copies(new double[3 * Nodes], new double[3]);
            var compiled = Copies(new double[3 * Nodes], new double[3]);
            fixed (double* pin = input, m0 = managed[0], m1 = managed[1], c0 = compiled[0], c1 = compiled[1])
            fixed (int* ps = start, pn = next)
            fixed (float* pw = weights)
            {
                ReFitRegistrationKernels.Code.Multiply(pin, m0, ps, pn, pw, 1e-4, 37.5, 0, Nodes, 1, m1);
                multiply.Invoke(pin, c0, ps, pn, pw, 1e-4, 37.5, 0, Nodes, 1, c1);
            }
            AssertSameBits(managed[0], compiled[0], "Multiply output");
            AssertSameBits(managed[1], compiled[1], "Multiply dot");

            managed = Copies(new double[3 * Nodes], new double[3 * Nodes], new double[3 * Nodes], new double[6]);
            compiled = Copies(new double[3 * Nodes], new double[3 * Nodes], new double[3 * Nodes], new double[6]);
            fixed (double* pb = b, pq = q, pd = diagonal, mr = managed[0], mz = managed[1], mp = managed[2], mt = managed[3],
                   cr = compiled[0], cz = compiled[1], cp = compiled[2], ct = compiled[3])
            {
                ReFitRegistrationKernels.Code.Start(pb, pq, mr, mz, mp, pd, 0, Nodes, mt);
                begin.Invoke(pb, pq, cr, cz, cp, pd, 0, Nodes, ct);
            }
            for (int k = 0; k < managed.Length; k++) AssertSameBits(managed[k], compiled[k], "Start " + k);

            var x = Random(random, 3 * Nodes);
            var r = Random(random, 3 * Nodes);
            var p = Random(random, 3 * Nodes);
            managed = Copies(x, r, new double[3 * Nodes], p, new double[6]);
            compiled = Copies(x, r, new double[3 * Nodes], p, new double[6]);
            fixed (double* pq = q, pd = diagonal, mx = managed[0], mr = managed[1], mz = managed[2], mp = managed[3], mt = managed[4],
                   cx = compiled[0], cr = compiled[1], cz = compiled[2], cp = compiled[3], ct = compiled[4])
            {
                ReFitRegistrationKernels.Code.Step(mx, mr, mz, mp, pq, pd, .3, -1.7, 2.9e-3, 0, Nodes, mt);
                step.Invoke(cx, cr, cz, cp, pq, pd, .3, -1.7, 2.9e-3, 0, Nodes, ct);
                ReFitRegistrationKernels.Code.Direction(mp, mz, .71, 1e-5, -3.3, 5, 0, Nodes);
                direction.Invoke(cp, cz, .71, 1e-5, -3.3, 5, 0, Nodes);
            }
            for (int k = 0; k < managed.Length; k++) AssertSameBits(managed[k], compiled[k], "Step and Direction " + k);
        }

        [Test]
        public void ASolveCompiledGivesTheManagedBits()
        {
            RequireBurst();
            // A tube of skin with unmatched patches (as ReFitRegistrationSolveTests), solved through each way to the loops.
            const int nu = 120, nv = 60;
            var nodes = new Vector3[nu * nv];
            var weights = new float[nodes.Length];
            var targets = new Vector3[nodes.Length];
            var random = new System.Random(6);
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    float u = i * 2f * Mathf.PI / nu;
                    int n = j * nu + i;
                    nodes[n] = new Vector3(.2f * Mathf.Cos(u), j * .008f, .2f * Mathf.Sin(u));
                    weights[n] = (i / 20 + j / 15) % 3 == 0 ? 0f : (float)random.NextDouble();
                    targets[n] = new Vector3(Mathf.Sin(j * .3f), Mathf.Cos(i * .2f), 0f) * .01f;
                }
            var triangles = new List<int>();
            for (int j = 0; j + 1 < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    int a = j * nu + i, b2 = j * nu + (i + 1) % nu, c = (j + 1) * nu + i, d = (j + 1) * nu + (i + 1) % nu;
                    triangles.AddRange(new[] { a, c, b2, b2, c, d });
                }
            var graph = ReFitBodyRegistration.Graph.Build(triangles.ToArray(), nodes.Length);
            var coarse = ReFitBodyRegistration.Coarse.Build(nodes, graph, ReFitBodyRegistration.CoarseCell);
            var viaManaged = new Vector3[nodes.Length];
            var viaBurst = new Vector3[nodes.Length];
            try
            {
                ReFitRegistrationLoops.managed = true;
                ReFitBodyRegistration.Solve(viaManaged, weights, targets, graph, coarse, 25f, CancellationToken.None);
            }
            finally { ReFitRegistrationLoops.managed = false; }
            ReFitBodyRegistration.Solve(viaBurst, weights, targets, graph, coarse, 25f, CancellationToken.None);
            for (int n = 0; n < nodes.Length; n++)
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(BitConverter.SingleToInt32Bits(viaBurst[n][axis]), Is.EqualTo(BitConverter.SingleToInt32Bits(viaManaged[n][axis])),
                        "Node " + n + " axis " + axis);
        }
    }
}
