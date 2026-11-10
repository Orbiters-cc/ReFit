using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>A transfer's frames turn their normals from welded normals kept once: the same deltas as computed per frame.</summary>
    public sealed class ReFitFrameNormalsTests
    {
        [Test]
        public void KeptWeldedNormalsGiveTheSameDeltas()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            const int cells = 12;
            for (int z = 0; z <= cells; z++)
                for (int x = 0; x <= cells; x++)
                    vertices.Add(new Vector3(x * .01f, Mathf.Sin(x * .5f) * .005f, z * .01f));
            for (int z = 0; z < cells; z++)
                for (int x = 0; x < cells; x++)
                {
                    int i = z * (cells + 1) + x;
                    triangles.AddRange(new[] { i, i + cells + 1, i + 1, i + 1, i + cells + 1, i + cells + 2 });
                }
            var asset = MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), true);
            var random = new System.Random(2);
            Vector3[] Field(float size)
            {
                var field = new Vector3[vertices.Count];
                for (int i = 0; i < field.Length; i++)
                    field[i] = new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f) * size;
                return field;
            }
            var fit = Field(.004f);
            var cache = new ReFitEngine.WeldedNormalsCache(asset, fit);
            for (int frame = 0; frame < 3; frame++)
            {
                var deltas = Field(.003f);
                foreach (var baseDeltas in new[] { null, fit, Field(.002f) })
                {
                    var expected = ReFitEngine.SurfaceDeltas(asset, deltas, baseDeltas);
                    var kept = ReFitEngine.SurfaceDeltas(asset, deltas, baseDeltas, cache);
                    Assert.That(kept.normals, Is.EqualTo(expected.normals), "Frame " + frame);
                    Assert.That(kept.tangents, Is.EqualTo(expected.tangents), "Frame " + frame);
                }
            }
        }
    }
}
