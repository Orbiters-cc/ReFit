using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Burst;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Group adjacency as compressed rows: the neighbours of group g are <see cref="next"/>[<see cref="start"/>[g] ..
    /// <see cref="start"/>[g + 1]), in the order of the lists it was made from (sorted where the snapshot sorts them). What the
    /// field kernels read; made once per adjacency.
    /// </summary>
    internal sealed class GroupGraph
    {
        public readonly int[] start;
        public readonly int[] next;
        public int Count => start.Length - 1;

        private GroupGraph(int[] start, int[] next)
        {
            this.start = start;
            this.next = next;
        }

        public static GroupGraph Of(List<int>[] adjacency)
        {
            var start = new int[adjacency.Length + 1];
            for (int g = 0; g < adjacency.Length; g++) start[g + 1] = start[g] + adjacency[g].Count;
            var next = new int[start[adjacency.Length]];
            for (int g = 0; g < adjacency.Length; g++) adjacency[g].CopyTo(next, start[g]);
            return new GroupGraph(start, next);
        }
    }

    /// <summary>
    /// The deformation field's smoothing pass (<see cref="DeltaField.Smooth(Vector3[], GroupGraph, int, float)"/>, the cloth fit's
    /// spreading) over a range of groups, three floats per group.
    /// <para>
    /// Written once (<see cref="Code"/>) and reached two ways, as the registration kernels are (<see cref="ReFitRegistrationKernels"/>):
    /// the [BurstCompile] entry, called directly from any thread (the engine runs on a worker, where jobs cannot be scheduled),
    /// and <see cref="Code"/> run by Mono. Both give the same bits: every operation is an IEEE double addition, subtraction,
    /// multiplication or division on exact widenings of the floats, rounded to float once at the store; Strict float mode
    /// (no reassociation, no fused multiply-add); each group's neighbours summed in their stored order; every group's result
    /// its own (no reduction), so how the groups are split over threads changes nothing. ReFitFieldKernelTests compares them.
    /// </para>
    /// </summary>
    [BurstCompile]
    internal static unsafe class ReFitFieldKernels
    {
        public delegate void SmoothFn(float* input, float* output, int* start, int* next, double strength, int begin, int end);

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        public static void Smooth(float* input, float* output, int* start, int* next, double strength, int begin, int end) =>
            Code.Smooth(input, output, start, next, strength, begin, end);

        /// <summary>The loops as written: what Burst compiles behind the entries, and what Mono runs when called directly.</summary>
        internal static class Code
        {
            /// <summary>
            /// output[g] = input[g] + (mean of the neighbours - input[g]) * strength for groups [begin, end) (strength already in
            /// 0..1); a group without neighbours keeps its value.
            /// </summary>
            public static void Smooth(float* input, float* output, int* start, int* next, double strength, int begin, int end)
            {
                for (int g = begin; g < end; g++)
                {
                    int k = 3 * g, first = start[g], last = start[g + 1];
                    if (last == first)
                    {
                        output[k] = input[k]; output[k + 1] = input[k + 1]; output[k + 2] = input[k + 2];
                        continue;
                    }
                    double x = 0, y = 0, z = 0;
                    for (int e = first; e < last; e++)
                    {
                        int j = 3 * next[e];
                        x += input[j]; y += input[j + 1]; z += input[j + 2];
                    }
                    double count = last - first;
                    double sx = input[k], sy = input[k + 1], sz = input[k + 2];
                    output[k] = (float)(sx + (x / count - sx) * strength);
                    output[k + 1] = (float)(sy + (y / count - sy) * strength);
                    output[k + 2] = (float)(sz + (z / count - sz) * strength);
                }
            }
        }
    }

    /// <summary>The field loops' way to the kernels: the Burst entries, or with <see cref="managed"/> (tests comparing the two) Mono.</summary>
    internal static unsafe class ReFitFieldLoops
    {
        internal static bool managed;

        // Groups per Burst call: a fixed split (any split gives the same bits, see ReFitFieldKernels).
        internal const int Range = 1024;

        /// <summary>
        /// <paramref name="passes"/> smoothing passes of <paramref name="field"/> over <paramref name="graph"/> by
        /// <paramref name="strength"/> (clamped to 0..1, as <see cref="Vector3.Lerp"/> clamps it), through
        /// <paramref name="buffer"/> (one per group). <paramref name="parallel"/>: ranges spread over the thread pool.
        /// </summary>
        public static void Smooth(Vector3[] field, Vector3[] buffer, GroupGraph graph, int passes, float strength, bool parallel = true)
        {
            int n = graph.Count;
            if (field.Length < n || buffer.Length < n) throw new ArgumentException("A value per group.");
            double t = Mathf.Clamp01(strength);
            int ranges = (n + Range - 1) / Range;
            for (int pass = 0; pass < passes; pass++)
            {
                if (parallel && ranges > 1) Parallel.For(0, ranges, r => Run(field, buffer, graph, t, r * Range, Math.Min(n, (r + 1) * Range)));
                else Run(field, buffer, graph, t, 0, n);
                Array.Copy(buffer, field, n);
            }
        }

        private static void Run(Vector3[] field, Vector3[] buffer, GroupGraph graph, double strength, int begin, int end)
        {
            fixed (Vector3* input = field)
            fixed (Vector3* output = buffer)
            fixed (int* start = graph.start)
            fixed (int* next = graph.next)
            {
                if (managed) ReFitFieldKernels.Code.Smooth((float*)input, (float*)output, start, next, strength, begin, end);
                else ReFitFieldKernels.Smooth((float*)input, (float*)output, start, next, strength, begin, end);
            }
        }
    }
}
