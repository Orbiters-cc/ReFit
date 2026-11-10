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

        // A flat grid of quads (two triangles each), every triangle given twice: points over a vertex are equally near up to
        // twelve triangles, points over a face to two.
        private static MeshSnapshot TiedGrid(int cells, float cell)
        {
            var vertices = new List<Vector3>();
            for (int y = 0; y <= cells; y++)
                for (int x = 0; x <= cells; x++)
                    vertices.Add(new Vector3(x * cell, 0f, y * cell));
            var indices = new List<int>();
            for (int copy = 0; copy < 2; copy++)
                for (int y = 0; y < cells; y++)
                    for (int x = 0; x < cells; x++)
                    {
                        int i = y * (cells + 1) + x;
                        indices.AddRange(new[] { i, i + cells + 1, i + 1, i + 1, i + cells + 1, i + cells + 2 });
                    }
            return MeshSnapshot.FromWorld(vertices.ToArray(), indices.ToArray(), false);
        }

        // The query contract: the least distance, the lowest triangle among equal ones (bit for bit, as the hierarchy computes).
        private static void BruteContract(MeshSnapshot surface, Vector3 p, float range, System.Func<int, bool> accept, out int triangle, out float sqr)
        {
            triangle = -1;
            sqr = range * range;
            for (int t = 0; t < surface.triangles.Length / 3; t++)
            {
                if (accept != null && !accept(t)) continue;
                var q = SurfaceBvh.ClosestPointOnTriangle(p, surface.worldVertices[surface.triangles[t * 3]], surface.worldVertices[surface.triangles[t * 3 + 1]],
                    surface.worldVertices[surface.triangles[t * 3 + 2]], out _);
                float d = (q - p).sqrMagnitude;
                if (d < sqr) { sqr = d; triangle = t; }
            }
        }

        [Test]
        public void EquallyNearTrianglesBreakTiesByIndex()
        {
            var surface = TiedGrid(12, .01f);
            var index = SurfaceBvh.Build(surface);
            var random = new System.Random(17);
            for (int i = 0; i < 400; i++)
            {
                // Over a vertex (ties among its triangles and their copies) or anywhere over the grid (ties with the copy).
                var p = i % 2 == 0
                    ? new Vector3(random.Next(13) * .01f, .002f + (float)random.NextDouble() * .01f, random.Next(13) * .01f)
                    : new Vector3((float)random.NextDouble() * .12f, (float)random.NextDouble() * .01f, (float)random.NextDouble() * .12f);
                BruteContract(surface, p, 1f, null, out int expected, out float sqr);
                var hit = index.ClosestPoint(p, 1f);
                Assert.That(hit.triangle, Is.EqualTo(expected), "Point " + i);
                Assert.That(hit.distance, Is.EqualTo(Mathf.Sqrt(sqr)), "Point " + i);
                Assert.That(hit.triangle, Is.LessThan(surface.triangles.Length / 6), "The first copy wins " + i);
            }
            // A ray straight down through a shared edge or vertex meets several triangles at the same distance.
            for (int i = 0; i < 200; i++)
            {
                var origin = new Vector3(random.Next(1, 12) * .01f, .05f, (i % 3 == 0 ? random.Next(1, 12) : (float)random.NextDouble() * 12f) * .01f);
                var hit = index.Raycast(origin, Vector3.down, 1f);
                Assert.That(hit.found, Is.True);
                float best = float.MaxValue;
                int lowest = -1;
                for (int t = 0; t < surface.triangles.Length / 3; t++)
                {
                    if (!SurfaceBvh.RayTriangle(origin, Vector3.down, surface.worldVertices[surface.triangles[t * 3]], surface.worldVertices[surface.triangles[t * 3 + 1]],
                            surface.worldVertices[surface.triangles[t * 3 + 2]], out float distance, out _)) continue;
                    if (distance < best) { best = distance; lowest = t; }
                }
                Assert.That(hit.triangle, Is.EqualTo(lowest), "Ray " + i);
            }
        }

        [Test]
        public void RefittedSeededAndRebuiltHierarchiesAnswerAlike()
        {
            var surface = Soup(3000, 29);
            var index = SurfaceBvh.Build(surface);
            // The surface moves: half of it a little, a few triangles far.
            var moved = (Vector3[])surface.worldVertices.Clone();
            var random = new System.Random(31);
            var movedTriangles = new List<int>();
            for (int t = 0; t < surface.triangles.Length / 3; t++)
            {
                if (t % 2 == 1) continue;
                movedTriangles.Add(t);
                var shift = t % 50 == 0 ? new Vector3(.4f, -.3f, .2f) : new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * .02f;
                for (int k = 0; k < 3; k++) moved[surface.triangles[t * 3 + k]] += shift;
            }
            var movedSurface = MeshSnapshot.FromWorld(moved, surface.triangles, false);
            var rebuilt = SurfaceBvh.Build(movedSurface);
            var refitted = SurfaceBvh.Build(surface);
            refitted.Refit(movedSurface);
            index.RefitTriangles(moved, surface.triangles, movedTriangles);
            var normals = movedSurface.FaceNormals;
            for (int i = 0; i < 400; i++)
            {
                var p = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * 1.2f - Vector3.one * .1f;
                var facing = new FacingFilter(normals, Vector3.up, .2f);
                var a = rebuilt.ClosestPoint(p, .3f, facing);
                var b = refitted.ClosestPoint(p, .3f, facing);
                var c = index.ClosestPoint(p, .3f, facing);
                var seeded = rebuilt.ClosestPoint(p, .3f, facing, (i * 7919) % (surface.triangles.Length / 3));
                BruteContract(movedSurface, p, .3f, t => Vector3.Dot(normals[t], Vector3.up) >= .2f, out int expected, out _);
                Assert.That(a.triangle, Is.EqualTo(expected), "Rebuilt " + i);
                foreach (var other in new[] { b, c, seeded })
                {
                    Assert.That(other.found, Is.EqualTo(a.found), "Point " + i);
                    Assert.That(other.triangle, Is.EqualTo(a.triangle), "Point " + i);
                    Assert.That(other.distance, Is.EqualTo(a.distance), "Point " + i);
                    Assert.That(other.bary, Is.EqualTo(a.bary), "Point " + i);
                }
                var direction = new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f).normalized;
                var ra = rebuilt.Raycast(p, direction, 2f);
                var rb = refitted.Raycast(p, direction, 2f);
                var rc = index.Raycast(p, direction, 2f);
                Assert.That(rb.triangle, Is.EqualTo(ra.triangle), "Ray " + i);
                Assert.That(rc.triangle, Is.EqualTo(ra.triangle), "Ray " + i);
                Assert.That(rc.distance, Is.EqualTo(ra.distance), "Ray " + i);
            }
        }

        private readonly struct ThreeClasses : ITriangleClassifier
        {
            private readonly Vector3[] normals;
            public ThreeClasses(Vector3[] normals) => this.normals = normals;
            // 0: any; 1: facing up; 2: facing up and of the first half of the triangles.
            public int Classes(int triangle) => 1 | (normals[triangle].y >= .3f ? 2 | (triangle < normals.Length / 2 ? 4 : 0) : 0);
        }

        [Test]
        public void OneTraversalAnswersEveryClassAsItsOwnQuery()
        {
            var surface = Soup(2500, 41);
            var index = SurfaceBvh.Build(surface);
            var normals = surface.FaceNormals;
            int half = normals.Length / 2;
            var random = new System.Random(43);
            System.Span<SurfaceBvh.Hit> hits = stackalloc SurfaceBvh.Hit[3];
            for (int i = 0; i < 300; i++)
            {
                var p = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * 1.2f - Vector3.one * .1f;
                index.ClosestPoints(p, .25f, new ThreeClasses(normals), 3, hits);
                var any = index.ClosestPoint(p, .25f);
                var up = index.ClosestPoint(p, .25f, t => normals[t].y >= .3f);
                var upFirst = index.ClosestPoint(p, .25f, t => normals[t].y >= .3f && t < half);
                int k = 0;
                foreach (var expected in new[] { any, up, upFirst })
                {
                    Assert.That(hits[k].found, Is.EqualTo(expected.found), "Class " + k + " point " + i);
                    Assert.That(hits[k].triangle, Is.EqualTo(expected.triangle), "Class " + k + " point " + i);
                    Assert.That(hits[k].distance, Is.EqualTo(expected.distance), "Class " + k + " point " + i);
                    Assert.That(hits[k].position, Is.EqualTo(expected.position), "Class " + k + " point " + i);
                    k++;
                }
            }
        }

        [Test]
        public void ADegenerateSoupBuildsAndAnswers()
        {
            // Every triangle on one point, then on one line: no split separates their centres.
            var point = MeshSnapshot.FromWorld(Enumerable.Repeat(new Vector3(.1f, .2f, .3f), 300).ToArray(), Enumerable.Range(0, 300).ToArray(), false);
            var hit = SurfaceBvh.Build(point).ClosestPoint(Vector3.zero, 1f);
            Assert.That(hit.triangle, Is.EqualTo(0));
            var line = MeshSnapshot.FromWorld(Enumerable.Range(0, 300).Select(i => new Vector3(i * .001f, 0f, 0f)).ToArray(), Enumerable.Range(0, 300).ToArray(), false);
            BruteContract(line, new Vector3(.05f, .01f, 0f), 1f, null, out int expected, out _);
            Assert.That(SurfaceBvh.Build(line).ClosestPoint(new Vector3(.05f, .01f, 0f), 1f).triangle, Is.EqualTo(expected));
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
