using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The spatial index's queries against brute force (median build, allocation-free filters, copies refitted onto moved
    /// triangles), and the rigid fit small pieces move by.
    /// </summary>
    public sealed class ReFitQueryAndRigidTests
    {
        private static MeshSnapshot Soup(int triangles, int seed, float scale = 1f)
        {
            var random = new System.Random(seed);
            float R() => (float)random.NextDouble() * scale;
            var vertices = new Vector3[triangles * 3];
            var indices = new int[triangles * 3];
            for (int t = 0; t < triangles; t++)
            {
                var center = new Vector3(R(), R(), R());
                for (int k = 0; k < 3; k++)
                {
                    vertices[t * 3 + k] = center + new Vector3(R(), R(), R()) * .05f;
                    indices[t * 3 + k] = t * 3 + k;
                }
            }
            return MeshSnapshot.FromWorld(vertices, indices, false);
        }

        private static float BruteClosest(MeshSnapshot surface, Vector3 p, System.Func<int, bool> accept, out int triangle)
        {
            float best = float.MaxValue;
            triangle = -1;
            for (int t = 0; t < surface.triangles.Length / 3; t++)
            {
                if (accept != null && !accept(t)) continue;
                var q = SurfaceBvh.ClosestPointOnTriangle(p, surface.worldVertices[surface.triangles[t * 3]], surface.worldVertices[surface.triangles[t * 3 + 1]],
                    surface.worldVertices[surface.triangles[t * 3 + 2]], out _);
                float d = (q - p).magnitude;
                if (d < best) { best = d; triangle = t; }
            }
            return best;
        }

        [Test]
        public void ClosestPointMatchesBruteForceWithAndWithoutFilters()
        {
            var surface = Soup(2000, 7);
            var index = SurfaceBvh.Build(surface);
            var random = new System.Random(3);
            var up = Vector3.up;
            var faceNormals = surface.FaceNormals;
            Assert.That(faceNormals, Is.Not.Null, "FromWorld caches the face normals.");
            for (int i = 0; i < 300; i++)
            {
                var p = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * 1.2f - Vector3.one * .1f;
                float expected = BruteClosest(surface, p, null, out _);
                var hit = index.ClosestPoint(p, 10f);
                Assert.That(hit.found, Is.True);
                Assert.That(hit.distance, Is.EqualTo(expected).Within(1e-5f), "Unfiltered closest point " + i);

                float facing = BruteClosest(surface, p, t => Vector3.Dot(surface.FaceNormal(t), up) >= .3f, out _);
                var filtered = index.ClosestPoint(p, 10f, new FacingFilter(faceNormals, up, .3f));
                var delegated = index.ClosestPoint(p, 10f, t => Vector3.Dot(surface.FaceNormal(t), up) >= .3f);
                Assert.That(filtered.distance, Is.EqualTo(facing).Within(1e-5f), "Struct filter " + i);
                Assert.That(delegated.distance, Is.EqualTo(facing).Within(1e-5f), "Delegate filter " + i);
                Assert.That(filtered.triangle, Is.EqualTo(delegated.triangle));
            }
        }

        [Test]
        public void RaycastMatchesBruteForce()
        {
            var surface = Soup(1500, 11);
            var index = SurfaceBvh.Build(surface);
            var random = new System.Random(5);
            int hits = 0;
            for (int i = 0; i < 300; i++)
            {
                var origin = new Vector3((float)random.NextDouble(), -.2f, (float)random.NextDouble());
                var direction = new Vector3((float)random.NextDouble() * .2f - .1f, 1f, (float)random.NextDouble() * .2f - .1f).normalized;
                var hit = index.Raycast(origin, direction, 3f);
                var filtered = index.Raycast(origin, direction, 3f, new FacingFilter(surface.FaceNormals, Vector3.down, 0f));
                float best = float.MaxValue, bestFacing = float.MaxValue;
                for (int t = 0; t < surface.triangles.Length / 3; t++)
                {
                    if (!Ray(origin, direction, surface.worldVertices[surface.triangles[t * 3]], surface.worldVertices[surface.triangles[t * 3 + 1]],
                            surface.worldVertices[surface.triangles[t * 3 + 2]], out float distance) || distance > 3f) continue;
                    best = Mathf.Min(best, distance);
                    if (Vector3.Dot(surface.FaceNormal(t), Vector3.down) >= 0f) bestFacing = Mathf.Min(bestFacing, distance);
                }
                Assert.That(hit.found, Is.EqualTo(best < float.MaxValue));
                if (hit.found) { hits++; Assert.That(hit.distance, Is.EqualTo(best).Within(1e-5f)); }
                Assert.That(filtered.found, Is.EqualTo(bestFacing < float.MaxValue));
                if (filtered.found) Assert.That(filtered.distance, Is.EqualTo(bestFacing).Within(1e-5f));
            }
            Assert.That(hits, Is.GreaterThan(50), "The rays meet the soup.");
        }

        // Moller-Trumbore, both faces.
        private static bool Ray(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            var s = origin - a;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(e2, q) / det;
            return distance >= 0f;
        }

        [Test]
        public void ASubsetHierarchyFindsWhatFilteringTheWholeOneFinds()
        {
            var surface = Soup(1200, 21);
            var random = new System.Random(9);
            var include = Enumerable.Range(0, surface.triangles.Length / 3).Select(_ => random.NextDouble() < .1).ToArray();
            var subset = SurfaceBvh.Build(surface, include);
            var whole = SurfaceBvh.Build(surface);
            for (int i = 0; i < 200; i++)
            {
                var p = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                var fast = subset.ClosestPoint(p, 10f);
                var filtered = whole.ClosestPoint(p, 10f, t => include[t]);
                Assert.That(fast.found, Is.EqualTo(filtered.found));
                Assert.That(fast.distance, Is.EqualTo(filtered.distance).Within(1e-6f), "Point " + i);
                Assert.That(include[fast.triangle], Is.True, "Hits give the snapshot's own triangle indices");
                Assert.That(fast.distance, Is.EqualTo(BruteClosest(surface, p, t => include[t], out _)).Within(1e-5f));
            }
        }

        [Test]
        public void RigidFitRecoversATurnAndAMove()
        {
            var piece = Enumerable.Range(0, 12).ToList();
            var random = new System.Random(4);
            var basis = piece.Select(_ => new Vector3((float)random.NextDouble(), (float)random.NextDouble() * .5f, (float)random.NextDouble() * .3f) * .02f).ToArray();
            var turn = Quaternion.Euler(12f, -18f, 6f);
            var shift = new Vector3(.01f, -.02f, .005f);
            var center = basis.Aggregate(Vector3.zero, (s, v) => s + v) / basis.Length;
            var move = basis.Select(p => center + shift + turn * (p - center) - p).ToArray();
            // Noise a rigid motion would not explain.
            var noisy = move.Select((m, i) => m + new Vector3(Mathf.Sin(i * 3.1f), Mathf.Cos(i * 1.7f), Mathf.Sin(i * .9f)) * .0002f).ToArray();
            RigidFit.Apply(piece, basis, noisy);
            for (int i = 0; i < basis.Length; i++)
                Assert.That((noisy[i] - move[i]).magnitude, Is.LessThan(.0004f), "Point " + i + " follows the turn");
            // Distances between the points are kept exactly.
            for (int i = 1; i < basis.Length; i++)
                Assert.That(((basis[i] + noisy[i]) - (basis[0] + noisy[0])).magnitude, Is.EqualTo((basis[i] - basis[0]).magnitude).Within(1e-6f));
        }

        [Test]
        public void RigidFitTurnsAtMostMaxTurn()
        {
            var piece = Enumerable.Range(0, 8).ToList();
            var basis = piece.Select(i => new Vector3(Mathf.Cos(i * .8f), Mathf.Sin(i * .8f), (i % 3) * .3f) * .01f).ToArray();
            var center = basis.Aggregate(Vector3.zero, (s, v) => s + v) / basis.Length;
            var turn = Quaternion.AngleAxis(120f, new Vector3(.3f, 1f, .2f).normalized);
            var move = basis.Select(p => center + turn * (p - center) - p).ToArray();
            RigidFit.Apply(piece, basis, move);
            // The piece turned by the cap, about the asked axis, around its centre.
            var a = basis[1] - basis[0];
            var b = (basis[1] + move[1]) - (basis[0] + move[0]);
            Assert.That(Vector3.Angle(a, b), Is.LessThanOrEqualTo(RigidFit.MaxTurn + .01f));
            var moved = basis.Select((p, i) => p + move[i]).Aggregate(Vector3.zero, (s, v) => s + v) / basis.Length;
            Assert.That((moved - center).magnitude, Is.LessThan(1e-6f), "The centre stays where it was asked to go");
            var capped = Quaternion.Slerp(Quaternion.identity, turn, RigidFit.MaxTurn / 120f);
            for (int i = 0; i < basis.Length; i++)
                Assert.That((basis[i] + move[i] - (center + capped * (basis[i] - center))).magnitude, Is.LessThan(1e-5f), "Point " + i);
        }

        [Test]
        public void RigidFitOfALineTakesTheMeanMove()
        {
            var piece = new List<int> { 0, 1, 2, 3 };
            var basis = new[] { Vector3.zero, Vector3.right * .01f, Vector3.right * .02f, Vector3.right * .03f };
            var move = new[] { Vector3.up * .001f, Vector3.up * .002f, Vector3.up * .003f, Vector3.up * .004f };
            RigidFit.Apply(piece, basis, move);
            foreach (var m in move) Assert.That((m - Vector3.up * .0025f).magnitude, Is.LessThan(1e-6f));
        }
    }
}
