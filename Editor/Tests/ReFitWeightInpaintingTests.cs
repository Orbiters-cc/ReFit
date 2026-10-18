using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>The weight inpainting: bounded, smooth and monotone where it should be, fast, and zero where nothing is known.</summary>
    public sealed class ReFitWeightInpaintingTests
    {
        // A tube (a sleeve) of rings × around nodes, its first rows known at 1 and its last rows known at 0.
        private static (Vector3[] positions, int[] triangles, bool[] known, double[] values) Tube(int rings, int around, int knownRows)
        {
            var positions = new Vector3[rings * around];
            for (int r = 0; r < rings; r++)
                for (int a = 0; a < around; a++)
                {
                    float angle = a * Mathf.PI * 2f / around;
                    // A little bulge in the middle, so the cotangents differ along it.
                    float radius = .05f * (1f + .3f * Mathf.Sin(r * Mathf.PI / (rings - 1)));
                    positions[r * around + a] = new Vector3(Mathf.Cos(angle) * radius, r * .004f, Mathf.Sin(angle) * radius);
                }
            var triangles = new List<int>();
            for (int r = 0; r + 1 < rings; r++)
                for (int a = 0; a < around; a++)
                {
                    int i = r * around + a, j = r * around + (a + 1) % around, k = (r + 1) * around + a, l = (r + 1) * around + (a + 1) % around;
                    triangles.AddRange(new[] { i, k, j, j, k, l });
                }
            var known = new bool[positions.Length];
            var values = new double[positions.Length];
            for (int r = 0; r < rings; r++)
                for (int a = 0; a < around; a++)
                {
                    if (r < knownRows) { known[r * around + a] = true; values[r * around + a] = 1; }
                    else if (r >= rings - knownRows) known[r * around + a] = true;
                }
            return (positions, triangles.ToArray(), known, values);
        }

        [Test]
        public void FillIsBoundedSmoothAndMonotone()
        {
            const int rings = 150, around = 60;
            var (positions, triangles, known, values) = Tube(rings, around, 10);
            var inpainting = new WeightInpainting(positions, triangles, known);
            var harmonic = (double[])values.Clone();
            Assert.That(inpainting.Solve(harmonic, 0), Is.True, "The harmonic fill converges");
            var watch = Stopwatch.StartNew();
            Assert.That(inpainting.Solve(values), Is.True);
            watch.Stop();
            double low = double.MaxValue, high = double.MinValue, bend = 0, change = 0, spread = 0;
            for (int i = 0; i < values.Length; i++) { low = System.Math.Min(low, values[i]); high = System.Math.Max(high, values[i]); change = System.Math.Max(change, System.Math.Abs(values[i] - harmonic[i])); }
            for (int r = 1; r + 1 < rings; r++)
            {
                double before = values[(r - 1) * around], here = values[r * around], after = values[(r + 1) * around];
                bend = System.Math.Max(bend, System.Math.Abs(before - 2 * here + after));
                // The same around each ring (the tube is round).
                for (int a = 1; a < around; a++) spread = System.Math.Max(spread, System.Math.Abs(values[r * around + a] - here));
                Assert.That(here, Is.LessThanOrEqualTo(before + 1e-6), "Row " + r + " is not above the one before");
            }
            Debug.Log($"[ReFit Tests] Inpainting {inpainting.UnknownCount} unknowns in {watch.ElapsedMilliseconds} ms: range {low:F4}..{high:F4}, " +
                      $"bend {bend:E2}, spread around {spread:E2}, smoothing moved it {change:F4}");
            Assert.That(low, Is.GreaterThan(-.02));
            Assert.That(high, Is.LessThan(1.02));
            Assert.That(spread, Is.LessThan(1e-3));
            // The known rows end at once; the fill's step per row (1/130) may bend by little more than a step.
            Assert.That(bend, Is.LessThan(.02));
        }

        [Test]
        public void PartsWithNoKnownValueStayZero()
        {
            // Two tubes: the first with known ends, the second (separate) known only as zero at one end.
            var (p1, t1, k1, v1) = Tube(40, 16, 4);
            var (p2, t2, _, _) = Tube(40, 16, 0);
            var positions = new Vector3[p1.Length + p2.Length];
            p1.CopyTo(positions, 0);
            for (int i = 0; i < p2.Length; i++) positions[p1.Length + i] = p2[i] + Vector3.right;
            var triangles = new int[t1.Length + t2.Length];
            t1.CopyTo(triangles, 0);
            for (int i = 0; i < t2.Length; i++) triangles[t1.Length + i] = t2[i] + p1.Length;
            var known = new bool[positions.Length];
            k1.CopyTo(known, 0);
            for (int i = 0; i < 16; i++) known[p1.Length + i] = true;
            var values = new double[positions.Length];
            v1.CopyTo(values, 0);
            for (int i = p1.Length; i < values.Length; i++) values[i] = 7;
            for (int i = p1.Length; i < p1.Length + 16; i++) values[i] = 0;
            var inpainting = new WeightInpainting(positions, triangles, known);
            Assert.That(inpainting.PartCount, Is.EqualTo(2));
            Assert.That(inpainting.Solve(values), Is.True);
            for (int i = p1.Length; i < values.Length; i++) Assert.That(values[i], Is.EqualTo(0), "Node " + i);
            Assert.That(values[20 * 16], Is.GreaterThan(.05).And.LessThan(.95), "The first tube is filled in");
        }
    }
}
