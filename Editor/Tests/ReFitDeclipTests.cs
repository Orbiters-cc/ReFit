using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// "Fix clipping" (<see cref="ReFitDeclip"/>) on synthetic bodies: a sphere and a cloth shell sunk into it, all of it or
    /// one side, a strap made to press in, a small piece; the settings it adds change the cache identity.
    /// </summary>
    public sealed class ReFitDeclipTests
    {
        private const float Radius = .1f, Room = .001f;
        // The field is trilinear over 2.5 mm cells: its distances are a fraction of a millimetre off on a 10 cm sphere.
        private const float Slack = .0003f;

        [Test]
        public void AShellSunkIntoTheBodyComesOutWithItsRoomSmoothAndUnstretched()
        {
            var (body, index) = Body();
            var cloth = Shell(Vector3.zero, Radius - .003f, 24, 48);
            var deltas = new Vector3[cloth.GroupCount];
            var result = ReFitDeclip.Apply(cloth, deltas, body, index, Rooms(cloth, Room), ReFitDeclip.DefaultIterations, CancellationToken.None);

            Assert.That(result.insideBefore, Is.EqualTo(cloth.GroupCount), result.ToString());
            Assert.That(result.insideAfter, Is.Zero, result.ToString());
            var radii = Fitted(cloth, deltas).Select(p => p.magnitude).ToArray();
            Assert.That(radii.Min(), Is.GreaterThan(Radius + Room - Slack), "Every point lies out of the body with its room.");
            Assert.That(radii.Max() - radii.Min(), Is.LessThan(.0005f), "The pushed shell stays smooth (one radius).");
            Assert.That(MaxEdgeRatio(cloth, deltas), Is.LessThan(ReFitDeclip.StretchRatio), "No edge stretched past the guard's ratio.");
            Assert.That(result.guarded, Is.Zero);
        }

        [Test]
        public void OnlyTheSunkSideMovesAndFabricWithItsRoomStays()
        {
            var (body, index) = Body();
            // Centred 5 mm aside: its -x side lies 3 mm in the body, its +x side 7 mm out.
            var cloth = Shell(new Vector3(.005f, 0f, 0f), Radius + .002f, 24, 48);
            var deltas = new Vector3[cloth.GroupCount];
            var result = ReFitDeclip.Apply(cloth, deltas, body, index, Rooms(cloth, Room), ReFitDeclip.DefaultIterations, CancellationToken.None);

            Assert.That(result.insideBefore, Is.GreaterThan(0), result.ToString());
            Assert.That(result.insideAfter, Is.Zero, result.ToString());
            var fitted = Fitted(cloth, deltas);
            for (int g = 0; g < cloth.GroupCount; g++)
            {
                var authored = cloth.worldVertices[cloth.groupRep[g]];
                if (authored.magnitude > Radius + Room + .002f)
                    Assert.That(deltas[g], Is.EqualTo(Vector3.zero), $"Fabric {authored.magnitude * 1000f:0.0} mm from the centre had its room and stays.");
                Assert.That(fitted[g].magnitude, Is.GreaterThan(Radius + Room - Slack), "Every point lies out of the body with its room.");
            }
            Assert.That(MaxEdgeRatio(cloth, deltas), Is.LessThan(ReFitDeclip.StretchRatio));
        }

        [Test]
        public void FabricMadeToPressInKeepsWhatItHadAsMade()
        {
            var (body, index) = Body();
            var cloth = Shell(Vector3.zero, Radius - .003f, 12, 24);
            // As made, 4 mm under the body it was made on: a strap pressing in. 3 mm under this one, it keeps that.
            var bindings = new SurfaceBinding[cloth.GroupCount];
            for (int g = 0; g < cloth.GroupCount; g++)
            {
                var p = cloth.worldVertices[cloth.groupRep[g]];
                var hit = index.ClosestPoint(p, .05f);
                Assert.That(hit.found);
                bindings[g] = new SurfaceBinding { valid = true, triangle = hit.triangle, bary = hit.bary, point = hit.position + p.normalized * .001f };
            }
            var rooms = ReFitDeclip.Rooms(cloth, bindings, body, Room, true);
            Assert.That(rooms.Max(), Is.LessThan(-.003f), "The room is no more than the authored gap.");
            Assert.That(ReFitDeclip.Rooms(cloth, bindings, body, Room, false).All(r => r == Room), "Made for another body, it gets the room asked.");
            var deltas = new Vector3[cloth.GroupCount];
            var result = ReFitDeclip.Apply(cloth, deltas, body, index, rooms, ReFitDeclip.DefaultIterations, CancellationToken.None);
            Assert.That(result.moved, Is.Zero, result.ToString());
            Assert.That(deltas.All(d => d == Vector3.zero));
        }

        [Test]
        public void ASmallPieceComesOutWhole()
        {
            var (body, index) = Body();
            // A bead of 1 cm, its centre 3 mm under the skin, on a garment standing 2 cm off: pushed out as one, it keeps its shape.
            var bead = Sphere(new Vector3(Radius - .003f, 0f, 0f), .005f, 6, 12);
            var garment = Sphere(Vector3.zero, Radius + .02f, 12, 24);
            var cloth = MeshSnapshot.FromWorld(garment.vertices.Concat(bead.vertices).ToArray(),
                garment.triangles.Concat(bead.triangles.Select(i => i + garment.vertices.Length)).ToArray(), true);
            var deltas = new Vector3[cloth.GroupCount];
            var result = ReFitDeclip.Apply(cloth, deltas, body, index, Rooms(cloth, Room), ReFitDeclip.DefaultIterations, CancellationToken.None);

            Assert.That(result.pieces, Is.EqualTo(1), result.ToString());
            var beadGroups = Enumerable.Range(0, cloth.GroupCount).Where(g => cloth.groupRep[g] >= garment.vertices.Length).ToList();
            Assert.That(beadGroups.Count, Is.EqualTo(bead.vertices.Length));
            foreach (int g in beadGroups)
                Assert.That((deltas[g] - deltas[beadGroups[0]]).magnitude, Is.LessThan(1e-6f), "One move for the whole piece.");
            var fitted = Fitted(cloth, deltas);
            Assert.That(beadGroups.Min(g => fitted[g].magnitude), Is.GreaterThan(Radius + Room - Slack), result.ToString());
            Assert.That(Enumerable.Range(0, cloth.GroupCount).Except(beadGroups).All(g => deltas[g] == Vector3.zero), "The garment had its room.");
        }

        [Test]
        public void NothingNearTheBodyIsLeftAsItIs()
        {
            var (body, index) = Body();
            var cloth = Shell(Vector3.zero, Radius + .02f, 12, 24);
            var deltas = new Vector3[cloth.GroupCount];
            var result = ReFitDeclip.Apply(cloth, deltas, body, index, Rooms(cloth, Room), ReFitDeclip.DefaultIterations, CancellationToken.None);
            Assert.That(result.lacking, Is.Zero);
            Assert.That(result.field, Is.Null, "No field is built when nothing is near the skin.");
            Assert.That(deltas.All(d => d == Vector3.zero));
        }

        [Test]
        public void TheFixIsPartOfTheCacheIdentity()
        {
            var on = new ReFitSettings();
            Assert.That(on.declipAfterFit, Is.EqualTo(ReFitSettings.DefaultDeclipAfterFit));
            var off = on.Clone();
            off.declipAfterFit = !on.declipAfterFit;
            var roomier = on.Clone();
            roomier.declipRoomMm = on.declipRoomMm + 1f;
            Assert.That(ReFitCacheIdentity.Settings(off), Is.Not.EqualTo(ReFitCacheIdentity.Settings(on)));
            Assert.That(ReFitCacheIdentity.Settings(roomier), Is.Not.EqualTo(ReFitCacheIdentity.Settings(on)));
            var overridden = new ReFitSettings();
            ReFitCaseValidation.Override(overridden, "declipAfterFit=false;declipRoomMm=2");
            Assert.That(overridden.declipAfterFit, Is.False);
            Assert.That(overridden.declipRoomMm, Is.EqualTo(2f));
        }

        private static (MeshSnapshot body, SurfaceBvh index) Body()
        {
            var body = Shell(Vector3.zero, Radius, 48, 96);
            return (body, SurfaceBvh.Build(body));
        }

        private static float[] Rooms(MeshSnapshot cloth, float room) => Enumerable.Repeat(room, cloth.GroupCount).ToArray();

        private static Vector3[] Fitted(MeshSnapshot cloth, Vector3[] deltas) =>
            Enumerable.Range(0, cloth.GroupCount).Select(g => cloth.worldVertices[cloth.groupRep[g]] + deltas[g]).ToArray();

        private static float MaxEdgeRatio(MeshSnapshot cloth, Vector3[] deltas)
        {
            var fitted = Fitted(cloth, deltas);
            float worst = 0f;
            for (int g = 0; g < cloth.GroupCount; g++)
                foreach (int n in cloth.groupAdjacency[g])
                {
                    var a = cloth.worldVertices[cloth.groupRep[g]] - cloth.worldVertices[cloth.groupRep[n]];
                    worst = Mathf.Max(worst, (fitted[g] - fitted[n]).magnitude / a.magnitude);
                }
            return worst;
        }

        private static MeshSnapshot Shell(Vector3 center, float radius, int rings, int segments)
        {
            var (vertices, triangles) = Sphere(center, radius, rings, segments);
            return MeshSnapshot.FromWorld(vertices, triangles, true);
        }

        // A closed UV sphere, its poles and seam shared, its faces turned outwards.
        private static (Vector3[] vertices, int[] triangles) Sphere(Vector3 center, float radius, int rings, int segments)
        {
            var vertices = new List<Vector3> { center + Vector3.up * radius };
            for (int r = 1; r < rings; r++)
            {
                float theta = Mathf.PI * r / rings;
                for (int s = 0; s < segments; s++)
                {
                    float phi = 2f * Mathf.PI * s / segments;
                    vertices.Add(center + radius * new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)));
                }
            }
            vertices.Add(center + Vector3.down * radius);
            int south = vertices.Count - 1;
            int Ring(int r, int s) => 1 + (r - 1) * segments + (s % segments);
            var triangles = new List<int>();
            void Face(int a, int b, int c)
            {
                var middle = (vertices[a] + vertices[b] + vertices[c]) / 3f - center;
                bool outward = Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), middle) > 0f;
                triangles.AddRange(outward ? new[] { a, b, c } : new[] { a, c, b });
            }
            for (int s = 0; s < segments; s++)
            {
                Face(0, Ring(1, s), Ring(1, s + 1));
                Face(south, Ring(rings - 1, s), Ring(rings - 1, s + 1));
                for (int r = 1; r < rings - 1; r++)
                {
                    Face(Ring(r, s), Ring(r + 1, s), Ring(r + 1, s + 1));
                    Face(Ring(r, s), Ring(r + 1, s + 1), Ring(r, s + 1));
                }
            }
            return (vertices.ToArray(), triangles.ToArray());
        }
    }
}
