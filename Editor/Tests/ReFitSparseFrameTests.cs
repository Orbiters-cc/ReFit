using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Shape frames treated as sparse: a frame moving nothing near the clothing is told apart, and a body moved by a frame
    /// gets its hierarchy by refitting the rest one where the frame moves it, answering exactly as one built anew.
    /// </summary>
    public sealed class ReFitSparseFrameTests
    {
        // A grid of quads over x, z in [0, 1] m.
        private static MeshSnapshot Sheet(int cells)
        {
            var vertices = new List<Vector3>();
            for (int z = 0; z <= cells; z++)
                for (int x = 0; x <= cells; x++)
                    vertices.Add(new Vector3((float)x / cells, 0f, (float)z / cells));
            var triangles = new List<int>();
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int i = z * (cells + 1) + x;
                    triangles.AddRange(new[] { i, i + cells + 1, i + 1, i + 1, i + cells + 1, i + cells + 2 });
                }
            return MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), false);
        }

        // A bump raising the sheet within radius of a point (a muscle), zero elsewhere.
        private static Vector3[] Bump(MeshSnapshot sheet, Vector2 at, float radius, float height)
        {
            var motion = new Vector3[sheet.worldVertices.Length];
            for (int i = 0; i < motion.Length; i++)
            {
                var p = sheet.worldVertices[i];
                float d = new Vector2(p.x - at.x, p.z - at.y).magnitude;
                if (d < radius) motion[i] = Vector3.up * (height * (1f - d / radius));
            }
            return motion;
        }

        [Test]
        public void AMovedBodysHierarchyAnswersAsOneBuiltAnew()
        {
            var rest = Sheet(60);
            rest.hiddenTriangles = new bool[rest.triangles.Length / 3];
            for (int t = 0; t < rest.hiddenTriangles.Length; t += 7) rest.hiddenTriangles[t] = true;
            var motion = Bump(rest, new Vector2(.3f, .6f), .2f, .08f);
            var vertices = new Vector3[motion.Length];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = rest.worldVertices[i] + motion[i];
            var moved = MeshSnapshot.MovedFrom(rest, vertices, motion);
            var built = MeshSnapshot.BuildIndex(vertices, rest.triangles, rest.hiddenTriangles);
            var wholeBuilt = MeshSnapshot.BuildIndex(vertices, rest.triangles);
            var refitted = moved.Index();
            var whole = moved.WholeIndex();
            Assert.That(refitted, Is.Not.SameAs(rest.Index()), "The rest's hierarchy is left as it is");
            var random = new System.Random(5);
            for (int i = 0; i < 400; i++)
            {
                var p = new Vector3((float)random.NextDouble(), (float)random.NextDouble() * .2f - .05f, (float)random.NextDouble());
                foreach (var (a, b) in new[] { (refitted, built), (whole, wholeBuilt) })
                {
                    var x = a.ClosestPoint(p, .3f);
                    var y = b.ClosestPoint(p, .3f);
                    Assert.That(x.TriangleOrNone, Is.EqualTo(y.TriangleOrNone), "Point " + i);
                    Assert.That(x.Distance, Is.EqualTo(y.Distance), "Point " + i);
                    var rx = a.Raycast(p + Vector3.up, Vector3.down, 2f);
                    var ry = b.Raycast(p + Vector3.up, Vector3.down, 2f);
                    Assert.That(rx.TriangleOrNone, Is.EqualTo(ry.TriangleOrNone), "Ray " + i);
                    Assert.That(rx.Distance, Is.EqualTo(ry.Distance), "Ray " + i);
                }
            }
            // The rest's own hierarchy still answers for the rest.
            var restHit = rest.Index().Raycast(new Vector3(.3f, 1f, .6f), Vector3.down, 2f);
            Assert.That(restHit.Distance, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void AFrameMovingNothingNearTheClothingIsToldApart()
        {
            var body = Sheet(40);
            // The clothing lies over one corner of the sheet.
            var garmentMin = new Vector3(0f, 0f, 0f);
            var garmentMax = new Vector3(.2f, .05f, .2f);
            var far = Bump(body, new Vector2(.9f, .9f), .05f, .01f);
            var near = Bump(body, new Vector2(.45f, .45f), .05f, .01f);
            var none = new Vector3[body.worldVertices.Length];
            Assert.That(ReFitEngine.FrameReaches(body, none, null, null, garmentMin, garmentMax, .3f), Is.False, "Nothing moves");
            Assert.That(ReFitEngine.FrameReaches(body, far, null, null, garmentMin, garmentMax, .3f), Is.False, "Moves only far from it");
            Assert.That(ReFitEngine.FrameReaches(body, near, null, null, garmentMin, garmentMax, .3f), Is.True, "Moves skin within reach");
            // The frame takes skin from far into reach.
            var into = new Vector3[body.worldVertices.Length];
            into[body.worldVertices.Length - 1] = new Vector3(-.75f, 0f, -.75f);
            Assert.That(ReFitEngine.FrameReaches(body, into, null, null, garmentMin, garmentMax, .3f), Is.True, "Brought into reach");
            // A garment worn under it moves within reach by its own version of the frame.
            var layer = Sheet(4);
            var layerMotion = new Vector3[layer.worldVertices.Length];
            layerMotion[0] = Vector3.up * .001f;
            Assert.That(ReFitEngine.FrameReaches(body, far, new[] { layer }, new[] { layerMotion }, garmentMin, garmentMax, .3f), Is.True,
                "A layer under it moves");
        }

        [Test]
        public void ATriangleMovedByAFarCornerReachesTheClothingUnderIt()
        {
            // One large triangle; the clothing lies just over its middle. Its first corner, far outside the clothing's reach, rises
            // 10 cm: the point under the clothing (barycentrics .25, .25, .5) rises 2.5 cm.
            var body = MeshSnapshot.FromWorld(new[] { new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(0f, 0f, 1f) },
                new[] { 0, 1, 2 }, false);
            var garmentMin = new Vector3(-.05f, .01f, -.05f);
            var garmentMax = new Vector3(.05f, .01f, .05f);
            var motion = new[] { Vector3.up * .1f, Vector3.zero, Vector3.zero };
            Assert.That(ReFitEngine.FrameReaches(body, motion, null, null, garmentMin, garmentMax, .4f), Is.True);
            // The same for a garment worn under it.
            var none = new Vector3[3];
            Assert.That(ReFitEngine.FrameReaches(body, none, new[] { body }, new[] { motion }, garmentMin, garmentMax, .4f), Is.True);
            Assert.That(ReFitEngine.FrameReaches(body, none, new[] { body }, new[] { none }, garmentMin, garmentMax, .4f), Is.False);
        }
    }
}
