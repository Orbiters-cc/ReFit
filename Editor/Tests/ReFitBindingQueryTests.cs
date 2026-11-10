using System;
using NUnit.Framework;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The binder answers in one traversal what its probe and filtered fallbacks answered one query each: the same triangle,
    /// point and fallback flag for every point, region and normal.
    /// </summary>
    public sealed class ReFitBindingQueryTests
    {
        private static readonly BodyRegion[] Kinds = { BodyRegion.Unknown, BodyRegion.Torso, BodyRegion.LeftArm, BodyRegion.RightLeg, BodyRegion.Head };

        private static MeshSnapshot Soup(int triangles, int seed, out BodyRegion[] regions)
        {
            var random = new System.Random(seed);
            float R() => (float)random.NextDouble();
            var vertices = new Vector3[triangles * 3];
            var indices = new int[triangles * 3];
            regions = new BodyRegion[triangles];
            for (int t = 0; t < triangles; t++)
            {
                var center = new Vector3(R(), R(), R()) * .5f;
                for (int k = 0; k < 3; k++)
                {
                    vertices[t * 3 + k] = center + new Vector3(R() - .5f, R() - .5f, R() - .5f) * .04f;
                    indices[t * 3 + k] = t * 3 + k;
                }
                regions[t] = Kinds[random.Next(Kinds.Length)];
            }
            return MeshSnapshot.FromWorld(vertices, indices, false);
        }

        // The binder as it was: a 1 cm probe, then the filtered query and its fallbacks, one query each.
        private static SurfaceBinding Reference(Vector3 point, MeshSnapshot body, MeshBvh bvh, float maxDistance, BodyRegion region,
            BodyRegion[] regions, Vector3 normal, float cosMax, bool useNormal, bool clothNormal)
        {
            bool useRegion = regions != null && region != BodyRegion.Unknown;
            var near = bvh.ClosestPoint(point, Mathf.Min(maxDistance, .01f));
            if (near.Found)
                return new SurfaceBinding { valid = true, triangle = near.TriangleOrNone, point = near.Position, distance = near.Distance, usedRelaxedFallback = useNormal || useRegion };
            Func<int, bool> regionOnly = useRegion ? t => HumanoidBoneMapper.RegionsCompatible(region, regions[t]) : (Func<int, bool>)null;
            Func<int, bool> full = useNormal || useRegion
                ? t => (!useRegion || HumanoidBoneMapper.RegionsCompatible(region, regions[t])) && (!useNormal || Vector3.Dot(normal, body.FaceNormal(t)) >= cosMax)
                : (Func<int, bool>)null;
            bool relaxed = false;
            var hit = full == null ? bvh.ClosestPoint(point, maxDistance) : bvh.ClosestPoint(point, maxDistance, ReFitTestRules.Where(bvh, full));
            if (useNormal && clothNormal)
            {
                var facing = regionOnly == null ? bvh.ClosestPoint(point, maxDistance) : bvh.ClosestPoint(point, maxDistance, ReFitTestRules.Where(bvh, regionOnly));
                if (facing.Found && Vector3.Dot(-normal, body.FaceNormal(facing.TriangleOrNone)) >= cosMax &&
                    (!hit.Found || hit.Distance > facing.Distance + Mathf.Max(.02f, facing.Distance)))
                    return Binding(facing, false);
            }
            if (hit.Found || full == null) return Binding(hit, false);
            if (useRegion && useNormal)
            {
                hit = bvh.ClosestPoint(point, maxDistance, ReFitTestRules.Where(bvh, regionOnly));
                if (hit.Found) return Binding(hit, true);
            }
            if (!useRegion || region == BodyRegion.Unknown || region == BodyRegion.Torso)
            {
                hit = bvh.ClosestPoint(point, maxDistance);
                relaxed = hit.Found;
            }
            return Binding(hit, relaxed);

            SurfaceBinding Binding(MeshBvh.Hit h, bool fallback) =>
                new SurfaceBinding { valid = h.Found, triangle = h.TriangleOrNone, point = h.Position, distance = h.Distance, usedRelaxedFallback = fallback };
        }

        [Test]
        public void BindingInOneTraversalAnswersAsTheSeparateQueries()
        {
            var body = Soup(4000, 3, out var regions);
            var bvh = body.Index();
            var random = new System.Random(8);
            float cosMax = Mathf.Cos(60f * Mathf.Deg2Rad);
            int fallbacks = 0, probes = 0;
            for (int i = 0; i < 1500; i++)
            {
                var point = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * .6f - Vector3.one * .05f;
                var normal = new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f).normalized;
                var region = Kinds[random.Next(Kinds.Length)];
                bool useNormal = random.Next(4) != 0, clothNormal = random.Next(2) == 0;
                float range = random.Next(3) == 0 ? .005f : .08f;
                var expected = Reference(point, body, bvh, range, region, regions, normal, cosMax, useNormal, clothNormal);
                var actual = SurfaceBindingSolver.BindPoint(point, body, bvh, range, region, regions, normal, cosMax, useNormal, clothNormal);
                Assert.That(actual.valid, Is.EqualTo(expected.valid), "Point " + i);
                if (!expected.valid) continue;
                Assert.That(actual.triangle, Is.EqualTo(expected.triangle), "Point " + i);
                Assert.That(actual.point, Is.EqualTo(expected.point), "Point " + i);
                Assert.That(actual.distance, Is.EqualTo(expected.distance), "Point " + i);
                Assert.That(actual.usedRelaxedFallback, Is.EqualTo(expected.usedRelaxedFallback), "Point " + i);
                if (expected.usedRelaxedFallback) fallbacks++;
                if (expected.distance < .01f) probes++;
            }
            Assert.That(fallbacks, Is.GreaterThan(20), "The fallbacks are exercised");
            Assert.That(probes, Is.GreaterThan(20), "The probe is exercised");
        }
    }
}
