using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// RFT-26: a shaped body derived again only where the frame moves the skin has the normals and face normals of one derived
    /// whole (<see cref="MeshSnapshot.FromWorld"/>), bit for bit: patches moved, nothing moved, everything moved, signed zeros
    /// and degenerate triangles.
    /// </summary>
    public sealed class ReFitMovedSurfaceTests
    {
        private static (Vector3[] vertices, int[] triangles) Grid(int size)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int y = 0; y <= size; y++)
                for (int x = 0; x <= size; x++)
                    vertices.Add(new Vector3((x - size / 2) * .01f, (y - size / 2) * .01f, Mathf.Sin(x * .4f) * Mathf.Cos(y * .3f) * .02f));
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int a = y * (size + 1) + x;
                    triangles.AddRange(new[] { a, a + size + 1, a + 1, a + 1, a + size + 1, a + size + 2 });
                }
            // A degenerate triangle (two corners at one vertex) and one on a line, touching the grid.
            triangles.AddRange(new[] { 0, 0, 1, 2, 3, 4 });
            var v = vertices.ToArray();
            // Signed zeros on the symmetry line.
            for (int i = 0; i < v.Length; i++) if (v[i].x == 0f) v[i].x = -0f;
            return (v, triangles.ToArray());
        }

        private static void AssertSameBits(Vector3[] expected, Vector3[] actual, string what)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), what);
            for (int i = 0; i < expected.Length; i++)
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(BitConverter.SingleToInt32Bits(actual[i][axis]), Is.EqualTo(BitConverter.SingleToInt32Bits(expected[i][axis])),
                        what + " " + i + " axis " + axis);
        }

        [Test]
        public void AMovedSurfaceIsTheWholeDerivationBitForBit()
        {
            var (rest, triangles) = Grid(60);
            var surface = MeshSnapshot.WorldSurface.Of(rest, triangles);
            var random = new System.Random(4);
            for (int frame = 0; frame < 8; frame++)
            {
                var shaped = new Vector3[rest.Length];
                for (int i = 0; i < rest.Length; i++)
                {
                    var delta = Vector3.zero;
                    if (frame == 1) delta = Vector3.one * .001f;                                  // everything
                    else if (frame > 1 && (rest[i] - new Vector3(.1f * (frame - 4), 0, 0)).magnitude < .08f)
                        delta = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * .01f;
                    if (frame == 7 && i < 3) delta = Vector3.zero - new Vector3(rest[i].x, rest[i].y, rest[i].z); // onto the origin
                    // A delta of zero keeps the rest bits, a signed zero too (frame 0: nothing moved).
                    shaped[i] = delta == Vector3.zero && frame != 7 ? rest[i] : rest[i] + delta;
                }
                var whole = MeshSnapshot.FromWorld(shaped, triangles, false);
                var moved = MeshSnapshot.MovedSurface(surface, (Vector3[])shaped.Clone());
                AssertSameBits(whole.worldNormals, moved.worldNormals, "frame " + frame + " normal");
                AssertSameBits(whole.FaceNormals, moved.FaceNormals, "frame " + frame + " face normal");
                AssertSameBits(whole.worldVertices, moved.worldVertices, "frame " + frame + " position");
                Assert.That(moved.triangles, Is.EqualTo(whole.triangles), "frame " + frame);
            }
        }
    }
}
