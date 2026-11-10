using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// What ReFit keeps of a body from one refit to the next: equal bodies share their enclosed skin and hierarchies, another
    /// body does not, and the bodies laid onto each other are read back from Library/ReFit as they were laid.
    /// </summary>
    public sealed class ReFitBodyCacheTests
    {
        private string directory;
        private string previousDirectory;

        [SetUp]
        public void SetUp()
        {
            previousDirectory = ReFitCaches.LibraryDirectory;
            directory = Path.Combine(Path.GetTempPath(), "ReFitBodyCacheTests-" + System.Guid.NewGuid().ToString("N"));
            ReFitCaches.LibraryDirectory = directory;
            ReFitCaches.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ReFitCaches.Clear();
            ReFitCaches.LibraryDirectory = previousDirectory;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // A closed sphere with a smaller one inside it, as one surface: the inner one is enclosed skin.
        private static (Vector3[] vertices, int[] triangles, int inner) Nested(float outer, float inside)
        {
            var (ov, ot) = Sphere(Vector3.zero, outer, 16, 32);
            var (iv, it) = Sphere(Vector3.zero, inside, 12, 24);
            var vertices = new List<Vector3>(ov);
            vertices.AddRange(iv);
            var triangles = new List<int>(ot);
            foreach (int i in it) triangles.Add(i + ov.Length);
            return (vertices.ToArray(), triangles.ToArray(), it.Length / 3);
        }

        [Test]
        public void EqualBodiesShareTheirEnclosedSkinAndHierarchies()
        {
            var (vertices, triangles, inner) = Nested(.1f, .05f);
            var first = MeshSnapshot.FromWorld(vertices, triangles, false);
            ReFitBodyCache.Attach(first);
            int hidden = first.HideEnclosedSkin();
            Assert.That(hidden, Is.InRange(inner * 9 / 10, inner), "The inner sphere is enclosed");
            for (int t = 0; t < triangles.Length / 3 - inner; t++) Assert.That(first.hiddenTriangles[t], Is.False, "The outer sphere is not");
            var firstIndex = first.Index();

            var second = MeshSnapshot.FromWorld(vertices, triangles, false);
            ReFitBodyCache.Attach(second);
            Assert.That(second.HideEnclosedSkin(), Is.EqualTo(hidden));
            Assert.That(second.hiddenTriangles, Is.SameAs(first.hiddenTriangles), "Found, not found again");
            Assert.That(second.Index(), Is.SameAs(firstIndex), "The hierarchy without the enclosed skin is shared");
            Assert.That(second.WholeIndex(), Is.Not.SameAs(firstIndex), "The whole surface has its own");
            Assert.That(second.MainSurface(), Is.SameAs(first.MainSurface()));

            // Another body (one vertex moved) derives its own.
            var moved = (Vector3[])vertices.Clone();
            moved[0] += Vector3.up * .001f;
            var other = MeshSnapshot.FromWorld(moved, triangles, false);
            ReFitBodyCache.Attach(other);
            Assert.That(other.HideEnclosedSkin(), Is.InRange(inner * 9 / 10, inner));
            Assert.That(other.hiddenTriangles, Is.Not.SameAs(first.hiddenTriangles));
            Assert.That(other.Index(), Is.Not.SameAs(firstIndex));
        }

        [Test]
        public void ASnapshotsIndexFollowsItsVerticesAndHiddenTriangles()
        {
            var (vertices, triangles, _) = Nested(.1f, .05f);
            var body = MeshSnapshot.FromWorld(vertices, triangles, false);
            var whole = body.Index();
            Assert.That(body.Index(), Is.SameAs(whole), "Kept while nothing changed");
            body.HideEnclosedSkin();
            var visible = body.Index();
            Assert.That(visible, Is.Not.SameAs(whole), "Built again without the hidden triangles");
            // The inner sphere is left out: a point at its surface finds the outer one.
            var hit = visible.ClosestPoint(new Vector3(.05f, 0f, 0f), 1f);
            Assert.That(hit.Distance, Is.EqualTo(.05f).Within(.002f));
            body.worldVertices = (Vector3[])body.worldVertices.Clone();
            Assert.That(body.Index(), Is.Not.SameAs(visible), "A new vertex array is another surface");
        }

        [Test]
        public void LaidBodiesAreReadBackFromTheLibraryAsLaid()
        {
            var (sv, st) = Sphere(Vector3.zero, .1f, 16, 32);
            var (tv, tt) = Sphere(new Vector3(.003f, 0f, 0f), .11f, 20, 40);
            var source = MeshSnapshot.FromWorld(sv, st, false);
            var target = MeshSnapshot.FromWorld(tv, tt, false);
            var laid = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(laid.timing, Does.Not.Contain("earlier"));
            Assert.That(Directory.GetFiles(directory, "registration-*.bin").Length, Is.EqualTo(1), "Saved for later sessions");

            // A new session: nothing in memory, the file read back exactly.
            ReFitBodyRegistration.ClearCache(files: false);
            var read = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(read.timing, Does.Contain("earlier session"));
            Assert.That(read.registered, Is.EqualTo(laid.registered));
            Assert.That(read.confidence, Is.EqualTo(laid.confidence));
            Assert.That(read.medianGap, Is.EqualTo(laid.medianGap));

            // Laid again from nothing: the same bits (fixed ranges and neighbour orders).
            ReFitBodyRegistration.ClearCache();
            var again = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(again.timing, Does.Not.Contain("earlier"));
            Assert.That(again.registered, Is.EqualTo(laid.registered));
            // Laid onto the larger sphere, the source lies on it.
            Assert.That(laid.p95Gap, Is.LessThan(.004f));
        }

        [Test]
        public void ATargetWithOtherFaceNormalsIsLaidAgain()
        {
            var (sv, st) = Sphere(Vector3.zero, .1f, 12, 24);
            var (tv, tt) = Sphere(Vector3.zero, .105f, 12, 24);
            // A collapsed triangle (three corners on a line) by the target: its face normal is its corners' normals.
            var vertices = new List<Vector3>(tv) { new Vector3(.12f, 0f, 0f), new Vector3(.12f, .01f, 0f), new Vector3(.12f, .02f, 0f) };
            var triangles = new List<int>(tt) { tv.Length, tv.Length + 1, tv.Length + 2 };
            var source = MeshSnapshot.FromWorld(sv, st, false);
            var target = MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), false);
            var turned = MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), false);
            for (int k = 0; k < 3; k++) turned.worldNormals[tv.Length + k] = Vector3.left;
            turned.CacheFaceNormals();
            Assert.That(turned.FaceNormals[triangles.Count / 3 - 1], Is.Not.EqualTo(target.FaceNormals[triangles.Count / 3 - 1]));

            ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            var same = MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), false);
            Assert.That(ReFitBodyRegistration.Build(source, null, same, same.Index(), null, CancellationToken.None).timing, Does.Contain("reused"));
            var other = ReFitBodyRegistration.Build(source, null, turned, turned.Index(), null, CancellationToken.None);
            Assert.That(other.timing, Does.Not.Contain("reused").And.Not.Contain("earlier"), "Only the normals differ: laid again");
        }

        [Test]
        public void ADamagedLibraryFileIsLaidAgainAndRewritten()
        {
            var (sv, st) = Sphere(Vector3.zero, .1f, 12, 24);
            var (tv, tt) = Sphere(Vector3.zero, .106f, 12, 24);
            var source = MeshSnapshot.FromWorld(sv, st, false);
            var target = MeshSnapshot.FromWorld(tv, tt, false);
            var laid = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            string file = Directory.GetFiles(directory, "registration-*.bin")[0];
            var bytes = File.ReadAllBytes(file);
            foreach (var damaged in new[] { System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Take(bytes, bytes.Length / 2)), Concat(bytes, new byte[] { 1, 2, 3 }) })
            {
                File.WriteAllBytes(file, damaged);
                ReFitBodyRegistration.ClearCache(files: false);
                var again = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
                Assert.That(again.timing, Does.Not.Contain("earlier"), "A damaged file is not read");
                Assert.That(again.registered, Is.EqualTo(laid.registered));
                // Written anew, whole: read back in the next session.
                ReFitBodyRegistration.ClearCache(files: false);
                var read = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
                Assert.That(read.timing, Does.Contain("earlier session"));
                Assert.That(read.registered, Is.EqualTo(laid.registered));
            }
        }

        // A registration laid with the hierarchy's queries as managed code (as before Burst has compiled them, or with Burst
        // off) and one laid with them compiled are two results: neither is ever served for the other, in memory or from
        // Library/ReFit, and each is read back bit for bit.
        [Test]
        public void ManagedAndCompiledQueriesNeverShareARegistration()
        {
            var (sv, st) = Sphere(Vector3.zero, .1f, 12, 24);
            var (tv, tt) = Sphere(new Vector3(.003f, 0, 0), .106f, 12, 24);
            var source = MeshSnapshot.FromWorld(sv, st, false);
            var target = MeshSnapshot.FromWorld(tv, tt, false);
            ReFitBodyRegistration managed, managedAgain;
            try
            {
                MeshBvh.RunManaged = true;
                Assert.That(MeshBvh.QueriesCompiled, Is.False, "the seam reports the managed path");
                managed = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
                Assert.That(managed.timing, Does.Not.Contain("reused").And.Not.Contain("earlier"));
                ReFitBodyRegistration.ClearCache(files: false);
                managedAgain = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            }
            finally { MeshBvh.RunManaged = false; }
            Assert.That(managedAgain.timing, Does.Contain("earlier session"), "the managed result is read back under its own key");
            Assert.That(managedAgain.registered, Is.EqualTo(managed.registered));
            if (!MeshBvh.QueriesCompiled) Assert.Ignore("Burst compilation is off: the managed path is the only one.");

            var compiled = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(compiled.timing, Does.Not.Contain("reused").And.Not.Contain("earlier"), "the compiled path is laid anew");
            Assert.That(Directory.GetFiles(directory, "registration-*.bin").Length, Is.EqualTo(2), "one file per path");
            var again = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(again.timing, Does.Contain("reused"));
            Assert.That(again.registered, Is.EqualTo(compiled.registered));
            ReFitBodyRegistration.ClearCache(files: false);
            var read = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(read.timing, Does.Contain("earlier session"));
            Assert.That(read.registered, Is.EqualTo(compiled.registered));
            // Laid anew on the compiled path, the same bits as the first time (the guard of the damaged-file test).
            ReFitCaches.Clear();
            var fresh = ReFitBodyRegistration.Build(source, null, target, target.Index(), null, CancellationToken.None);
            Assert.That(fresh.registered, Is.EqualTo(compiled.registered));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var all = new byte[a.Length + b.Length];
            a.CopyTo(all, 0);
            b.CopyTo(all, a.Length);
            return all;
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
            int bottom = vertices.Count - 1;
            var triangles = new List<int>();
            int Ring(int r, int s) => 1 + (r - 1) * segments + (s % segments);
            for (int s = 0; s < segments; s++) triangles.AddRange(new[] { 0, Ring(1, s + 1), Ring(1, s) });
            for (int r = 1; r < rings - 1; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = Ring(r, s), b = Ring(r, s + 1), c = Ring(r + 1, s), d = Ring(r + 1, s + 1);
                    triangles.AddRange(new[] { a, b, d, a, d, c });
                }
            for (int s = 0; s < segments; s++) triangles.AddRange(new[] { bottom, Ring(rings - 1, s), Ring(rings - 1, s + 1) });
            return (vertices.ToArray(), triangles.ToArray());
        }
    }
}
