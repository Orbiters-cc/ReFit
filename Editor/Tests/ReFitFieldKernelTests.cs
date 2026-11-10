using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// RFT-4: the field smoothing kernel compiled by Burst and run by Mono gives the same bits (the kernel through its function
    /// pointer, then whole smoothings each way), whatever the split over threads, and what the smoothing is written to do
    /// (each group towards its neighbours' mean, in double, from the adjacency lists). The comparisons with Burst are ignored
    /// where Burst compilation is off.
    /// </summary>
    public sealed unsafe class ReFitFieldKernelTests
    {
        private const int Groups = 5000;

        private static void RequireBurst()
        {
            if (!BurstCompiler.IsEnabled) Assert.Ignore("Burst compilation is off: nothing compiled to compare with.");
        }

        // A ring with chords and a few isolated groups (no neighbours), sorted neighbours.
        private static List<int>[] Adjacency(int seed)
        {
            var random = new System.Random(seed);
            var lists = new List<int>[Groups];
            for (int i = 0; i < Groups; i++) lists[i] = new List<int>();
            void Link(int a, int b) { if (a != b && !lists[a].Contains(b)) { lists[a].Add(b); lists[b].Add(a); } }
            for (int i = 0; i < Groups; i++)
            {
                if (i % 97 == 0) continue;
                int j = (i + 1) % Groups;
                if (j % 97 != 0) Link(i, j);
                if (i % 5 == 0) { int k = random.Next(Groups); if (k % 97 != 0) Link(i, k); }
            }
            foreach (var list in lists) list.Sort();
            return lists;
        }

        private static Vector3[] Field(int seed)
        {
            var random = new System.Random(seed);
            var field = new Vector3[Groups];
            for (int i = 0; i < Groups; i++)
                field[i] = new Vector3((float)random.NextDouble() - .5f, (float)(random.NextDouble() * 1e-3), (float)random.NextDouble() * 30f);
            return field;
        }

        private static void AssertSameBits(Vector3[] expected, Vector3[] actual, string what)
        {
            for (int i = 0; i < expected.Length; i++)
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(BitConverter.SingleToInt32Bits(actual[i][axis]), Is.EqualTo(BitConverter.SingleToInt32Bits(expected[i][axis])),
                        what + ": group " + i + " axis " + axis);
        }

        [Test]
        public void TheKernelCompiledGivesTheManagedBits()
        {
            RequireBurst();
            var smooth = BurstCompiler.CompileFunctionPointer<ReFitFieldKernels.SmoothFn>(ReFitFieldKernels.Smooth);
            var graph = GroupGraph.Of(Adjacency(3));
            var input = Field(4);
            var managed = new Vector3[Groups];
            var compiled = new Vector3[Groups];
            fixed (Vector3* i = input, m = managed, c = compiled)
            fixed (int* s = graph.start, n = graph.next)
            {
                ReFitFieldKernels.Code.Smooth((float*)i, (float*)m, s, n, .37, 0, Groups);
                smooth.Invoke((float*)i, (float*)c, s, n, .37, 0, Groups);
            }
            AssertSameBits(managed, compiled, "Smooth");
        }

        [Test]
        public void SmoothingsBurstManagedSerialAndParallelAgree()
        {
            var adjacency = Adjacency(5);
            var graph = GroupGraph.Of(adjacency);
            var start = Field(6);
            var viaBurst = (Vector3[])start.Clone();
            var serial = (Vector3[])start.Clone();
            ReFitFieldLoops.Smooth(viaBurst, new Vector3[Groups], graph, 7, .6f);
            ReFitFieldLoops.Smooth(serial, new Vector3[Groups], graph, 7, .6f, parallel: false);
            AssertSameBits(viaBurst, serial, "Parallel and serial");

            var viaManaged = (Vector3[])start.Clone();
            try
            {
                ReFitFieldLoops.managed = true;
                ReFitFieldLoops.Smooth(viaManaged, new Vector3[Groups], graph, 7, .6f);
            }
            finally { ReFitFieldLoops.managed = false; }

            // What a pass is: each group plus (its neighbours' mean - itself) times the strength, in double, rounded once.
            var reference = (Vector3[])start.Clone();
            for (int pass = 0; pass < 7; pass++)
            {
                var next = new Vector3[Groups];
                for (int g = 0; g < Groups; g++)
                {
                    if (adjacency[g].Count == 0) { next[g] = reference[g]; continue; }
                    for (int axis = 0; axis < 3; axis++)
                    {
                        double sum = 0;
                        foreach (int n in adjacency[g]) sum += reference[n][axis];
                        double self = reference[g][axis];
                        next[g][axis] = (float)(self + (sum / adjacency[g].Count - self) * (double).6f);
                    }
                }
                reference = next;
            }
            AssertSameBits(reference, viaManaged, "Managed and the written rule");
            if (BurstCompiler.IsEnabled) AssertSameBits(viaManaged, viaBurst, "Burst and managed");

            // The list entry point is the same smoothing; a strength above one is clamped as Vector3.Lerp clamps it.
            var viaLists = (Vector3[])start.Clone();
            DeltaField.Smooth(viaLists, adjacency, 7, .6f);
            AssertSameBits(viaBurst, viaLists, "Lists and graph");
            var over = (Vector3[])start.Clone();
            var one = (Vector3[])start.Clone();
            DeltaField.Smooth(over, adjacency, 2, 3f);
            DeltaField.Smooth(one, adjacency, 2, 1f);
            AssertSameBits(one, over, "Clamped strength");
            var isolated = (Vector3[])start.Clone();
            DeltaField.Smooth(isolated, adjacency, 3, .5f);
            Assert.That(isolated[97], Is.EqualTo(start[97]), "A group without neighbours keeps its value");
        }
    }
}
