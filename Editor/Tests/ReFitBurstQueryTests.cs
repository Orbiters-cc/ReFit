using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The spatial index's Burst path against its managed code, query for query and bit for bit (Burst rounds as the editor's
    /// Mono does, see SurfaceBvhBurst): the same tree node for node, and the same hit (triangle, distance, position, barycentric
    /// coordinates) on a soup, a closed tessellated surface (closest points on corners and edges several triangles share, needle
    /// triangles at its poles) and the FBX fixture's body; unfiltered, facing, all-but-one-triangle and not-touching-two-groups
    /// filters; subsets, hidden triangles and refitted trees, collapsed triangles; the managed code kept where Burst is off or
    /// an input is out of range. A refit decides on which triangle a query meets, so "the same up to rounding" is not enough.
    /// </summary>
    public sealed class ReFitBurstQueryTests
    {
        // A distance as near as rounding (a metre-sized body: ulp 6e-8 m), for explaining a failure.
        private const float Rounding = 1e-6f;

        private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8");
        private static string Bits(Vector3 value) => Bits(value.x) + Bits(value.y) + Bits(value.z);

        [SetUp]
        public void RequireBurst()
        {
            SurfaceBvh.BurstEnabled = true;
            if (!SurfaceBvh.BurstActive) Assert.Ignore("Burst is not compiling in this editor (Jobs > Burst > Enable Compilation is off).");
        }

        [TearDown]
        public void Restore() => SurfaceBvh.BurstEnabled = true;

        private static (SurfaceBvh managed, SurfaceBvh burst) Both(MeshSnapshot surface, bool[] include = null)
        {
            SurfaceBvh.BurstEnabled = false;
            var managed = SurfaceBvh.Build(surface, include);
            SurfaceBvh.BurstEnabled = true;
            var burst = SurfaceBvh.Build(surface, include);
            Assert.That(managed.UsesBurst, Is.False);
            Assert.That(burst.UsesBurst, Is.True, "Burst builds the hierarchy when it is compiled");
            return (managed, burst);
        }

        private static MeshSnapshot Soup(int triangles, int seed)
        {
            var random = new System.Random(seed);
            float R() => (float)random.NextDouble();
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

        // A closed lumpy sphere of shared vertices: closest points often fall on an edge or corner several triangles share.
        private static MeshSnapshot Lumpy(int rings, int segments)
        {
            var vertices = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();
            for (int r = 0; r <= rings; r++)
            for (int s = 0; s < segments; s++)
            {
                float theta = Mathf.PI * r / rings, phi = 2 * Mathf.PI * s / segments;
                float radius = .3f + .03f * Mathf.Sin(5 * theta) * Mathf.Cos(3 * phi);
                vertices.Add(new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi)) * radius);
            }
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * segments + s, b = r * segments + (s + 1) % segments, c = a + segments, d = b + segments;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
            // Welded: NotTouchingGroups reads the vertex groups.
            return MeshSnapshot.FromWorld(vertices.ToArray(), indices.ToArray(), true);
        }

        private static float TriangleDistance(MeshSnapshot surface, int t, Vector3 p) =>
            (SurfaceBvh.ClosestPointOnTriangle(p, surface.worldVertices[surface.triangles[t * 3]], surface.worldVertices[surface.triangles[t * 3 + 1]],
                surface.worldVertices[surface.triangles[t * 3 + 2]], out _) - p).magnitude;

        /// <summary>Asserts the Burst hit is the managed one, bit for bit; when not, says whether it is a rounding tie or farther.</summary>
        private static void Same(MeshSnapshot surface, SurfaceBvh.Hit managed, SurfaceBvh.Hit burst, Vector3 p, bool ray, string what,
            Func<int, bool> accept = null, Func<int, string> margin = null)
        {
            if (burst.found == managed.found && burst.triangle == managed.triangle && Bits(burst.distance) == Bits(managed.distance) &&
                Bits(burst.position) == Bits(managed.position) && Bits(burst.bary) == Bits(managed.bary))
                return;
            string found = $"{what}: managed found={managed.found} triangle {managed.triangle} distance {managed.distance:R}, " +
                           $"Burst found={burst.found} triangle {burst.triangle} distance {burst.distance:R}; point ({p.x:R}, {p.y:R}, {p.z:R})";
            if (!managed.found || !burst.found) Assert.Fail(found);
            string kind = Mathf.Abs(burst.distance - managed.distance) <= Rounding * Mathf.Max(1f, managed.distance) ? "as near (rounding)" : "NOT as near";
            if (accept != null && !ray)
            {
                // The nearest accepted triangle by brute force, and how near each filter decision was.
                float best = float.MaxValue; int bestTriangle = -1;
                for (int t = 0; t < surface.triangles.Length / 3; t++)
                {
                    if (!accept(t)) continue;
                    float d = TriangleDistance(surface, t, p);
                    if (d < best) { best = d; bestTriangle = t; }
                }
                found += $"; brute force {best:R} (triangle {bestTriangle}); facing margins {margin?.Invoke(managed.triangle)} / {margin?.Invoke(burst.triangle)}";
            }
            Assert.Fail($"{found}; {kind}");
        }

        /// <returns>The number of queries compared.</returns>
        private static int CompareQueries(MeshSnapshot surface, SurfaceBvh managed, SurfaceBvh burst, int seed, int count, float spread, string stage = "",
            bool[] include = null)
        {
            int compared = 0;
            var hidden = surface.hiddenTriangles;
            var random = new System.Random(seed);
            var bounds = new Bounds(surface.worldVertices[0], Vector3.zero);
            foreach (var v in surface.worldVertices) bounds.Encapsulate(v);
            float R() => (float)random.NextDouble();
            var normals = surface.FaceNormals;
            int triangles = surface.triangles.Length / 3;
            var masks = Enumerable.Range(0, triangles).Select(t => t % 5 == 0 ? 0 : 1 << (t % 3)).ToArray();
            var allowed = Enumerable.Range(0, triangles).Select(t => t % 7 != 0).ToArray();
            for (int i = 0; i < count; i++)
            {
                // Near the surface (a vertex pushed off it) and anywhere around it.
                var p = i % 2 == 0
                    ? surface.worldVertices[random.Next(surface.worldVertices.Length)] + new Vector3(R() - .5f, R() - .5f, R() - .5f) * spread
                    : bounds.min - bounds.size * .1f + Vector3.Scale(bounds.size * 1.2f, new Vector3(R(), R(), R()));
                var direction = new Vector3(R() - .5f, R() - .5f, R() - .5f).normalized;
                float reach = i % 3 == 0 ? float.MaxValue : .02f + R() * .3f;
                float minimum = R() - .5f;
                var facing = new FacingFilter(normals, direction, minimum, i % 4 == 0 ? 1 << (i % 3) : 0, masks, i % 5 == 0 ? allowed : null, i % 2 == 1);
                // How far a triangle's facing is from the filter's threshold, exactly (double): a few 1e-8 is a rounding flip.
                string Margin(int t) => t < 0 ? "-" : ((double)normals[t].x * direction.x + (double)normals[t].y * direction.y +
                                                       (double)normals[t].z * direction.z - minimum).ToString("E3");
                compared += surface.groupOfVertex != null ? 9 : 7;
                Same(surface, managed.ClosestPoint(p, reach), burst.ClosestPoint(p, reach), p, false, "closest " + i);
                Same(surface, managed.ClosestPoint(p, reach, facing), burst.ClosestPoint(p, reach, facing), p, false,
                        stage + " facing closest " + i + $" (reach {reach:R}, direction {direction.x:R} {direction.y:R} {direction.z:R}, minimum {minimum:R})",
                        t => facing.Accept(t) && (hidden == null || !hidden[t]) && (include == null || include[t]), Margin);
                float length = .05f + R();
                Same(surface, managed.Raycast(p, direction, length), burst.Raycast(p, direction, length), p, true, "ray " + i);
                Same(surface, managed.Raycast(p, direction, length, facing), burst.Raycast(p, direction, length, facing), p, true, "facing ray " + i);
                // Other filters keep the managed code, on the Burst-built hierarchy too.
                Func<int, bool> func = t => t % 2 == 0;
                Same(surface, managed.ClosestPoint(p, reach, func), burst.ClosestPoint(p, reach, func), p, false, "delegate closest " + i);
                // Every triangle but one; the triangles touching neither of two vertex groups (a welded surface).
                var except = new ExceptTriangle(i % 3 == 0 ? managed.Raycast(p, direction, length).triangle : random.Next(triangles));
                Same(surface, managed.Raycast(p, direction, length, except), burst.Raycast(p, direction, length, except), p, true, "except ray " + i);
                Same(surface, managed.ClosestPoint(p, reach, except), burst.ClosestPoint(p, reach, except), p, false, "except closest " + i);
                if (surface.groupOfVertex != null)
                {
                    var near = managed.ClosestPoint(p, float.MaxValue);
                    int ga = surface.groupOfVertex[surface.triangles[near.triangle * 3]], gb = random.Next(surface.GroupCount);
                    var touching = new NotTouchingGroups(surface, ga, gb);
                    Same(surface, managed.Raycast(p, direction, length, touching), burst.Raycast(p, direction, length, touching), p, true, "untouched ray " + i);
                    Same(surface, managed.ClosestPoint(p, reach, touching), burst.ClosestPoint(p, reach, touching), p, false, "untouched closest " + i);
                }
            }
            return compared;
        }

        [Test]
        public void BurstQueriesMatchTheManagedCodeOnASoup()
        {
            var surface = Soup(3000, 17);
            var (managed, burst) = Both(surface);
            TestContext.WriteLine($"{CompareQueries(surface, managed, burst, 5, 1500, .05f)} queries, all bit for bit");
        }

        [Test]
        public void BurstQueriesMatchTheManagedCodeOnAClosedSurface()
        {
            var surface = Lumpy(60, 90);
            var (managed, burst) = Both(surface);
            TestContext.WriteLine($"{CompareQueries(surface, managed, burst, 8, 3000, .02f)} queries, all bit for bit");
        }

        [Test]
        public void BurstQueriesMatchTheManagedCodeOnTheFixtureBody()
        {
            var model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Packages/orbiters.refit/ReFit unit test v1.fbx"));
            try
            {
                var body = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body custom edit");
                var surface = MeshSnapshot.Capture(body, false, null, null);
                if (surface.FaceNormals == null) surface.CacheFaceNormals();
                var (managed, burst) = Both(surface);
                TestContext.WriteLine($"{CompareQueries(surface, managed, burst, 3, 2000, .03f)} queries, all bit for bit");
            }
            finally { Object.DestroyImmediate(model); }
        }

        // The hierarchy's own arrays (private fields), read for the structural check.
        private static T Field<T>(SurfaceBvh bvh, string name) =>
            (T)typeof(SurfaceBvh).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(bvh);

        /// <summary>
        /// Every kept triangle in exactly one slot, the packed corners its corners, every node's bounds around its triangles and
        /// its children's, the slots of a node the union of its children's.
        /// </summary>
        private static void AssertValid(SurfaceBvh bvh, MeshSnapshot surface, bool[] include, string what)
        {
            var order = Field<int[]>(bvh, "triOrder");
            var corners = Field<float[]>(bvh, "corners");
            var a = Field<Vector3[]>(bvh, "a"); var b = Field<Vector3[]>(bvh, "b"); var c = Field<Vector3[]>(bvh, "c");
            int nodeCount = Field<int>(bvh, "nodeCount");
            var nodes = (Array)typeof(SurfaceBvh).GetField("nodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(bvh);
            var nodeType = nodes.GetType().GetElementType();
            object F(object node, string name) => nodeType.GetField(name).GetValue(node);
            int triangles = surface.triangles.Length / 3;
            var kept = Enumerable.Range(0, triangles).Where(t => (surface.hiddenTriangles == null || !surface.hiddenTriangles[t]) && (include == null || include[t])).ToList();
            Assert.That(order.OrderBy(t => t), Is.EqualTo(kept), what + ": the kept triangles, each in one slot");
            for (int i = 0; i < order.Length; i++)
            {
                int t = order[i];
                var corner = new[] { a[t], b[t], c[t] };
                for (int k = 0; k < 3; k++)
                {
                    Assert.That(corner[k], Is.EqualTo(surface.worldVertices[surface.triangles[t * 3 + k]]), $"{what}: corner {k} of triangle {t}");
                    if (corners != null)
                        Assert.That(new Vector3(corners[9 * i + 3 * k], corners[9 * i + 3 * k + 1], corners[9 * i + 3 * k + 2]), Is.EqualTo(corner[k]),
                            $"{what}: packed corner {k} of slot {i}");
                }
            }
            for (int n = 0; n < nodeCount; n++)
            {
                var node = nodes.GetValue(n);
                var min = (Vector3)F(node, "boundsMin"); var max = (Vector3)F(node, "boundsMax");
                int left = (int)F(node, "left"), right = (int)F(node, "right"), start = (int)F(node, "start"), count = (int)F(node, "count");
                if (left < 0)
                {
                    Assert.That(count, Is.GreaterThan(0), $"{what}: leaf {n} has triangles");
                    for (int i = start; i < start + count; i++)
                        foreach (var v in new[] { a[order[i]], b[order[i]], c[order[i]] })
                            Assert.That(v.x >= min.x && v.y >= min.y && v.z >= min.z && v.x <= max.x && v.y <= max.y && v.z <= max.z, Is.True,
                                $"{what}: leaf {n} holds slot {i}");
                }
                else
                {
                    Assert.That(left, Is.GreaterThan(n), what + ": children follow their parent");
                    Assert.That(right, Is.GreaterThan(n), what + ": children follow their parent");
                    foreach (var child in new[] { nodes.GetValue(left), nodes.GetValue(right) })
                    {
                        var cmin = (Vector3)F(child, "boundsMin"); var cmax = (Vector3)F(child, "boundsMax");
                        Assert.That(cmin.x >= min.x && cmin.y >= min.y && cmin.z >= min.z && cmax.x <= max.x && cmax.y <= max.y && cmax.z <= max.z, Is.True,
                            $"{what}: node {n} holds its children");
                    }
                }
            }
        }

        // The tree, node for node: bounds bits, children, slots, and the triangle in every slot.
        private static void AssertSameTree(SurfaceBvh managed, SurfaceBvh burst, string what)
        {
            Assert.That(Field<int>(burst, "nodeCount"), Is.EqualTo(Field<int>(managed, "nodeCount")), what + " node count");
            Assert.That(Field<int[]>(burst, "triOrder"), Is.EqualTo(Field<int[]>(managed, "triOrder")), what + " triangle order");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var a = (Array)typeof(SurfaceBvh).GetField("nodes", flags).GetValue(managed);
            var b = (Array)typeof(SurfaceBvh).GetField("nodes", flags).GetValue(burst);
            var type = a.GetType().GetElementType();
            for (int n = 0; n < Field<int>(managed, "nodeCount"); n++)
                foreach (var field in type.GetFields())
                {
                    object x = field.GetValue(a.GetValue(n)), y = field.GetValue(b.GetValue(n));
                    if (x is Vector3 vx) Assert.That(Bits((Vector3)y), Is.EqualTo(Bits(vx)), $"{what} node {n} {field.Name}");
                    else Assert.That(y, Is.EqualTo(x), $"{what} node {n} {field.Name}");
                }
        }

        [Test]
        public void BurstBuildsTheManagedTree()
        {
            var fixture = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Packages/orbiters.refit/ReFit unit test v1.fbx"));
            try
            {
                var body = MeshSnapshot.Capture(fixture.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body custom edit"), false, null, null);
                foreach (var (surface, what) in new[] { (Soup(3000, 17), "soup"), (Lumpy(60, 90), "closed surface"), (body, "fixture body") })
                {
                    var (managed, burst) = Both(surface);
                    AssertSameTree(managed, burst, what);
                    var include = Enumerable.Range(0, surface.triangles.Length / 3).Select(t => t % 3 != 0).ToArray();
                    (managed, burst) = Both(surface, include);
                    AssertSameTree(managed, burst, what + " subset");
                }
            }
            finally { Object.DestroyImmediate(fixture); }
        }

        [Test]
        public void SubsetsHiddenTrianglesAndRefitsMatch()
        {
            var surface = Lumpy(40, 60);
            int triangles = surface.triangles.Length / 3;
            var include = Enumerable.Range(0, triangles).Select(t => t % 3 != 0).ToArray();
            var (managed, burst) = Both(surface, include);
            AssertValid(managed, surface, include, "managed subset");
            AssertValid(burst, surface, include, "Burst subset");
            CompareQueries(surface, managed, burst, 12, 800, .02f, "subset", include);

            surface.hiddenTriangles = Enumerable.Range(0, triangles).Select(t => t % 11 == 0).ToArray();
            (managed, burst) = Both(surface);
            AssertValid(burst, surface, null, "Burst with hidden triangles");
            CompareQueries(surface, managed, burst, 13, 800, .02f, "hidden");

            // Moved (as a body shape moves the skin), refitted in place.
            var moved = MeshSnapshot.FromWorld(surface.worldVertices.Select(v => v + new Vector3(Mathf.Sin(v.y * 9), 0, Mathf.Cos(v.x * 7)) * .02f).ToArray(),
                surface.triangles, false);
            moved.hiddenTriangles = surface.hiddenTriangles;
            managed.Refit(moved);
            burst.Refit(moved);
            Assert.That(burst.UsesBurst, Is.True, "A refit keeps the hierarchy's path");
            AssertValid(managed, moved, null, "managed refitted");
            AssertValid(burst, moved, null, "Burst refitted");
            CompareQueries(moved, managed, burst, 14, 800, .02f, "refitted");
        }

        [Test]
        public void CollapsedTrianglesGiveTheSameFinitePoints()
        {
            // Two corners on one point, three on a line, and a regular triangle.
            var vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(.5f, 1, 0), new Vector3(1, 1, 0),
                new Vector3(0, 2, 0), new Vector3(1, 2, 0), new Vector3(0, 2, 1),
            };
            var surface = MeshSnapshot.FromWorld(vertices, Enumerable.Range(0, 9).ToArray(), false);
            var (managed, burst) = Both(surface);
            var random = new System.Random(2);
            for (int i = 0; i < 400; i++)
            {
                var p = new Vector3((float)random.NextDouble() * 1.4f - .2f, (float)random.NextDouble() * 2.4f - .2f, (float)random.NextDouble() * .6f - .3f);
                var a = managed.ClosestPoint(p, 5f);
                var b = burst.ClosestPoint(p, 5f);
                Assert.That(float.IsNaN(b.distance) || float.IsNaN(b.position.x), Is.False, "Finite " + i);
                Same(surface, a, b, p, false, "collapsed " + i);
            }
        }

        [Test]
        public void TheManagedCodeRunsWhenBurstIsOff()
        {
            var surface = Soup(200, 3);
            SurfaceBvh.BurstEnabled = false;
            Assert.That(SurfaceBvh.BurstActive, Is.False);
            var index = SurfaceBvh.Build(surface);
            Assert.That(index.UsesBurst, Is.False);
            Assert.That(index.ClosestPoint(new Vector3(.5f, .5f, .5f), 10f).found, Is.True);
        }

        [Test]
        public void OutOfRangeInputsThrowAsTheManagedCodeDoes()
        {
            var surface = Soup(50, 4);
            var broken = MeshSnapshot.FromWorld(surface.worldVertices, surface.triangles, false);
            broken.triangles[7] = broken.worldVertices.Length + 3;
            Assert.Throws<IndexOutOfRangeException>(() => SurfaceBvh.Build(broken), "A triangle naming a missing vertex");

            var index = SurfaceBvh.Build(surface);
            Assert.That(index.UsesBurst, Is.True);
            // Face normals for fewer triangles than the hierarchy has: the managed query reads past them, as before.
            var shortNormals = surface.FaceNormals.Take(10).ToArray();
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                for (int i = 0; i < 50; i++) index.ClosestPoint(surface.worldVertices[i * 3], float.MaxValue, new FacingFilter(shortNormals, Vector3.up, -2f));
            });
            // Vertex groups for fewer vertices than the triangles name: Burst stops at the first index past them, the managed
            // query then runs and throws.
            var welded = Lumpy(10, 12);
            var weldedIndex = SurfaceBvh.Build(welded);
            var shortGroups = MeshSnapshot.FromWorld(welded.worldVertices, welded.triangles, true);
            shortGroups.groupOfVertex = shortGroups.groupOfVertex.Take(5).ToArray();
            Assert.Throws<IndexOutOfRangeException>(() =>
            {
                for (int i = 0; i < welded.worldVertices.Length; i += 7)
                    weldedIndex.ClosestPoint(welded.worldVertices[i], float.MaxValue, new NotTouchingGroups(shortGroups, -1, -1));
            });
            // Refitted onto a snapshot whose triangle names a missing vertex: the managed refit throws, the queries go managed.
            Assert.Throws<IndexOutOfRangeException>(() => index.Refit(broken));
            Assert.That(index.UsesBurst, Is.False);
        }
    }
}
