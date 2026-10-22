using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Shapes given to clothing alone: clothing made with a body shape the target shows (or the request says) gets it taken
    /// away by its shape and given back by its rest, and the shapes turn the authored normals and tangents instead of
    /// replacing them with smooth ones. Fixtures in a preview scene.
    /// </summary>
    public sealed class ReFitShapeSurfaceTests
    {
        private const string Flex = "Flex";
        private Scene scene;
        private GameObject root;
        private readonly List<Object> owned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Shape surface avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        // Clothing made over the flexed body the target shows: without the shape it lies on the body without it, and at the
        // body's weight it is as authored, its normals and tangents too.
        [Test]
        public void ClothingMadeWithAShownShapeIsAsAuthoredAtTheBodysWeight()
        {
            var (avatar, body, cloth) = Avatar(clothFlexed: true);
            body.SetBlendShapeWeight(0, 100f);
            var authored = cloth.sharedMesh;

            var comp = Run(avatar, cloth, body, null);

            Assert.That(comp.report.messages.Any(m => m.code == "made-with-shape" && m.text.Contains("made with it")), Is.True,
                "It lies over the flexed body: made with it");
            Assert.That(comp.report.messages.Any(m => m.code == "followed-shapes-shown" && m.text.StartsWith("Made with")), Is.True,
                "The report says the clothing was made with the shape");
            var mesh = comp.mesh;
            var (rest, frame, normals, frameNormals, tangents, frameTangents) = Surface(mesh, "refit_" + Flex);
            var vertices = authored.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That((rest[i] + frame[i] - vertices[i]).magnitude, Is.LessThan(1e-5f), "At the body's weight, vertex " + i + " is as authored");
                Assert.That(Vector3.Angle(normals[i] + frameNormals[i], authored.normals[i]), Is.LessThan(.5f), "Its normal " + i);
                var t = authored.tangents[i];
                Assert.That(Vector3.Angle(tangents[i] + frameTangents[i], new Vector3(t.x, t.y, t.z)), Is.LessThan(.5f), "Its tangent " + i);
            }
            int top = Top(vertices);
            Assert.That(rest[top].z, Is.InRange(.001f, .012f), $"Without the shape, the top of the bump ({vertices[top].z * 1000f:0} mm) lies over the flat body");
            int slope = Enumerable.Range(0, vertices.Length).OrderByDescending(i => Vector3.Angle(authored.normals[i], Vector3.forward)).First();
            Assert.That(Vector3.Angle(normals[slope], Vector3.forward), Is.LessThan(Vector3.Angle(authored.normals[slope], Vector3.forward) * .5f),
                "The rest's normals turned with it, flatter on the bump's slope");
        }

        // Clothing made on the base, the body flexed afterwards (it clips): the shape adds the bump from zero, as before.
        [Test]
        public void ClothingMadeBeforeTheBodyWasShapedFollowsTheShapeFromZero()
        {
            var (avatar, body, cloth) = Avatar(clothFlexed: false);
            body.SetBlendShapeWeight(0, 100f);
            var flat = cloth.sharedMesh.vertices;

            var comp = Run(avatar, cloth, body, null);

            Assert.That(comp.report.messages.Any(m => m.code == "made-with-shape" && m.text.Contains("made before it was shown")), Is.True,
                string.Join("\n", comp.report.messages.Select(m => m.ToString())));
            var (rest, frame, normals, _, _, _) = Surface(comp.mesh, "refit_" + Flex);
            int top = Enumerable.Range(0, flat.Length).OrderByDescending(i => Bump(flat[i])).First();
            Assert.That(rest.Zip(flat, (a, b) => (a - b).magnitude).Max(), Is.LessThan(1e-6f), "Its rest is as authored");
            Assert.That(normals.Zip(cloth.sharedMesh.normals, (a, b) => Vector3.Angle(a, b)).Max(), Is.LessThan(1e-3f), "and so are its normals");
            Assert.That(frame[top].z, Is.GreaterThan(.012f), "Its shape adds the bump");
        }

        // A shape that shrinks the body: clothing made over the dent is in the body without it, clothing made before stands off
        // the dented body.
        [Test]
        public void AShapeThatShrinksTheBodyIsToldApartToo()
        {
            var (avatar, body, cloth) = Avatar(clothFlexed: true, sign: -1f);
            body.SetBlendShapeWeight(0, 100f);
            var authored = cloth.sharedMesh.vertices;
            var made = Run(avatar, cloth, body, null);
            Assert.That(made.report.messages.Any(m => m.code == "made-with-shape" && m.text.Contains("made with it")), Is.True, "Made over the dent");
            var (rest, frame, _, _, _, _) = Surface(made.mesh, "refit_" + Flex);
            Assert.That(rest.Zip(frame, (a, b) => a + b).Zip(authored, (a, b) => (a - b).magnitude).Max(), Is.LessThan(1e-5f), "Dented, it is as authored");

            Object.DestroyImmediate(avatar);
            (avatar, body, cloth) = Avatar(clothFlexed: false, sign: -1f);
            body.SetBlendShapeWeight(0, 100f);
            var before = Run(avatar, cloth, body, null);
            Assert.That(before.report.messages.Any(m => m.code == "made-with-shape" && m.text.Contains("made before it was shown")), Is.True,
                "Made flat, it stands off the dent");
        }

        // What the request says it was made with wins over what the body shows, both ways.
        [Test]
        public void TheShapesItWasMadeWithOverrideWhatTheBodyShows()
        {
            var (avatar, body, cloth) = Avatar(clothFlexed: true);
            var authored = cloth.sharedMesh.vertices;
            var made = Run(avatar, cloth, body, new Dictionary<string, float> { [Flex] = 100f });
            var (rest, frame, _, _, _, _) = Surface(made.mesh, "refit_" + Flex);
            int top = Top(authored);
            Assert.That(rest[top].z, Is.InRange(.001f, .012f), "Made flexed on a body that is not: its rest is the clothing without it");
            Assert.That((rest[top] + frame[top] - authored[top]).magnitude, Is.LessThan(1e-5f), "Flexed, it is as authored");

            Object.DestroyImmediate(avatar);
            (avatar, body, cloth) = Avatar(clothFlexed: false);
            body.SetBlendShapeWeight(0, 100f);
            var flat = cloth.sharedMesh.vertices;
            var notMade = Run(avatar, cloth, body, new Dictionary<string, float> { [Flex] = 0f });
            (rest, frame, _, _, _, _) = Surface(notMade.mesh, "refit_" + Flex);
            top = Enumerable.Range(0, flat.Length).OrderByDescending(i => Bump(flat[i])).First();
            Assert.That(rest.Zip(flat, (a, b) => (a - b).magnitude).Max(), Is.LessThan(1e-6f), "Made on the body without it: its rest is as authored");
            Assert.That(frame[top].z, Is.GreaterThan(.012f), "and its shape adds the bump the body shows");
            Assert.That(notMade.report.messages.Any(m => m.code == "followed-shapes-shown"), Is.False);
        }

        // A hard-edged tent the body shape moves as a whole: its faces keep their own normals at full weight (smooth normals
        // would bend both sides of the ridge half way to each other).
        [Test]
        public void AShapeKeepsTheClothingsHardEdges()
        {
            var bone = Bone(root.transform, "Chest");
            var body = Skinned(root.transform, bone, "Body", Grid(33, 0f, 0f));
            body.sharedMesh.AddBlendShapeFrame(Flex, 100f, Enumerable.Repeat(new Vector3(0f, 0f, .01f), body.sharedMesh.vertexCount).ToArray(), null, null);
            var garment = new GameObject("Tent").transform; garment.SetParent(root.transform, false);
            var cloth = Skinned(garment, Bone(garment, "Chest"), "Tent", Tent());

            var comp = Run(root, cloth, body, null);

            var (rest, frame, normals, frameNormals, _, _) = Surface(comp.mesh, "refit_" + Flex);
            var triangles = comp.mesh.triangles;
            float worst = 0f;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                var p = new[] { triangles[t], triangles[t + 1], triangles[t + 2] }.Select(i => rest[i] + frame[i]).ToArray();
                var face = Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized;
                for (int k = 0; k < 3; k++) worst = Mathf.Max(worst, Vector3.Angle(normals[triangles[t + k]] + frameNormals[triangles[t + k]], face));
            }
            Assert.That(worst, Is.LessThan(3f), "Each face's corners keep its normal at full weight");
            Assert.That(frame.Max(d => d.z), Is.GreaterThan(.005f), "The shape moved the tent");
        }

        private ReFitComputation Run(GameObject avatar, SkinnedMeshRenderer cloth, SkinnedMeshRenderer body, Dictionary<string, float> made)
        {
            var comp = new ReFitEngine().Run(new ReFitRequest
            {
                mode = ReFitMode.Blendshape, assetRenderer = cloth, targetAvatar = avatar, targetBodyRenderer = body,
                targetBlendshapes = new List<string> { Flex }, madeWithShapes = made,
                settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false }
            });
            if (comp.mesh != null) owned.Add(comp.mesh);
            Assert.That(comp.success, Is.True, string.Join("\n", comp.report.messages.Select(m => m.ToString())));
            return comp;
        }

        // A body grid with a broad bump as its shape (a dent when the sign is negative), and a finer grid of clothing 5 mm
        // over it, made over the shape or not.
        private (GameObject avatar, SkinnedMeshRenderer body, SkinnedMeshRenderer cloth) Avatar(bool clothFlexed, float sign = 1f)
        {
            var avatar = new GameObject("Avatar").transform; avatar.SetParent(root.transform, false);
            var body = Skinned(avatar, Bone(avatar, "Chest"), "Body", Grid(33, 0f, 0f));
            body.sharedMesh.AddBlendShapeFrame(Flex, 100f, body.sharedMesh.vertices.Select(p => new Vector3(0f, 0f, Bump(p) * sign)).ToArray(), null, null);
            var garment = new GameObject("Shirt").transform; garment.SetParent(avatar, false);
            var cloth = Skinned(garment, Bone(garment, "Chest"), "Shirt", Grid(17, .005f, clothFlexed ? sign : 0f));
            return (avatar.gameObject, body, cloth);
        }

        private static float Bump(Vector3 p) => .02f * Mathf.Exp(-(p.x * p.x + (p.y - .45f) * (p.y - .45f)) / .004f);

        private static int Top(Vector3[] vertices) => Enumerable.Range(0, vertices.Length).OrderByDescending(i => vertices[i].z).First();

        private static Transform Bone(Transform parent, string name)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent, false);
            return bone;
        }

        private Mesh Grid(int count, float z, float bump)
        {
            var vertices = new Vector3[count * count]; var uv = new Vector2[vertices.Length]; var triangles = new List<int>();
            for (int y = 0; y < count; y++) for (int x = 0; x < count; x++)
            {
                int i = y * count + x;
                uv[i] = new Vector2((float)x / (count - 1), (float)y / (count - 1));
                var p = new Vector3(-.15f + .3f * uv[i].x, .3f + .3f * uv[i].y, z);
                vertices[i] = p + new Vector3(0f, 0f, Bump(p) * bump);
                if (x + 1 < count && y + 1 < count) triangles.AddRange(new[] { i, i + 1, i + count, i + 1, i + count + 1, i + count });
            }
            return Finish(new Mesh { name = "Grid", vertices = vertices, uv = uv, triangles = triangles.ToArray() }, true);
        }

        // Two slopes meeting at a ridge 3 cm over the body, every triangle with its own corners (flat shaded).
        private Mesh Tent()
        {
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            const int across = 6, along = 8;
            Vector3 At(int x, int y) => new Vector3(-.06f + .12f * x / across, .35f + .2f * y / along, .005f + .03f * (1f - Mathf.Abs(x - across / 2f) / (across / 2f)));
            for (int y = 0; y < along; y++) for (int x = 0; x < across; x++)
                foreach (var (cx, cy) in new[] { (x, y), (x + 1, y), (x, y + 1), (x + 1, y), (x + 1, y + 1), (x, y + 1) })
                {
                    triangles.Add(vertices.Count);
                    vertices.Add(At(cx, cy));
                }
            return Finish(new Mesh { name = "Tent", vertices = vertices.ToArray(), triangles = triangles.ToArray() }, false);
        }

        private Mesh Finish(Mesh mesh, bool tangents)
        {
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, mesh.vertexCount).ToArray();
            mesh.RecalculateNormals();
            if (tangents) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            owned.Add(mesh);
            return mesh;
        }

        private static SkinnedMeshRenderer Skinned(Transform parent, Transform bone, string name, Mesh mesh)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh; renderer.bones = new[] { bone }; renderer.rootBone = bone;
            return renderer;
        }

        // The result's rest and one of its shapes: positions, normals and tangents.
        private static (Vector3[] rest, Vector3[] frame, Vector3[] normals, Vector3[] frameNormals, Vector3[] tangents, Vector3[] frameTangents) Surface(Mesh mesh, string shape)
        {
            int index = mesh.GetBlendShapeIndex(shape);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "The result has " + shape);
            var frame = new Vector3[mesh.vertexCount]; var frameNormals = new Vector3[frame.Length]; var frameTangents = new Vector3[frame.Length];
            mesh.GetBlendShapeFrameVertices(index, 0, frame, frameNormals, frameTangents);
            var tangents = mesh.tangents.Select(t => new Vector3(t.x, t.y, t.z)).ToArray();
            return (mesh.vertices, frame, mesh.normals, frameNormals, tangents.Length == frame.Length ? tangents : new Vector3[frame.Length], frameTangents);
        }
    }
}
