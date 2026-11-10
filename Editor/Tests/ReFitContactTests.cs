using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Review of RFT-5: a body point already on the target stays there. Two layers facing the same way 1 cm apart, the point on
    /// the first: the registration's match and the binder's chaining keep the first, as SurfaceBvh's rays (which met a surface
    /// at distance 0) did.
    /// </summary>
    public sealed class ReFitContactTests
    {
        private static MeshSnapshot TwoLayers()
        {
            var vertices = new[]
            {
                new Vector3(-1, -1, 0), new Vector3(0, 1, 0), new Vector3(1, -1, 0),
                new Vector3(-1, -1, .01f), new Vector3(0, 1, .01f), new Vector3(1, -1, .01f),
            };
            // Both face +z (wound so their normals agree).
            var triangles = new[] { 0, 2, 1, 3, 5, 4 };
            return MeshSnapshot.FromWorld(vertices, triangles, false);
        }

        [Test]
        public void ARegisteredPointOnTheTargetStaysOnIt()
        {
            var target = TwoLayers();
            Assert.That(target.FaceNormals[0].z, Is.GreaterThan(.9f));
            Assert.That(target.FaceNormals[1].z, Is.GreaterThan(.9f));
            var index = MeshSnapshot.BuildIndex(target);
            var facing = SurfaceRules.Facing(target.FaceNormals, Vector3.forward, .35f);
            int seed = -1;
            var hit = ReFitBodyRegistration.Match(index, Vector3.zero, Vector3.forward, .2f, facing, ref seed);
            Assert.That(hit.Found, Is.True);
            Assert.That(hit.Triangle, Is.EqualTo(0), "the layer the point is on, not the one 1 cm out");
            Assert.That(hit.Distance, Is.EqualTo(0f));
            Assert.That(seed, Is.EqualTo(0), "the seed follows the nearest triangle");
            var ray = index.Ray(Vector3.zero, Vector3.forward, 1f, true, facing);
            Assert.That(ray.Found && ray.Triangle == 0 && ray.Distance == 0f, Is.True, "ReFit's rays meet the surface they start on");
            var chained = SurfaceBindingSolver.ChainPoint(Vector3.zero, Vector3.forward, target, index, .2f, BodyRegion.Unknown, null, .35f, true);
            Assert.That(chained.valid && chained.triangle == 0, Is.True, "chaining keeps the surface the point is on");
        }
    }
}
