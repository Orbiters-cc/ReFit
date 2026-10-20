using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The push-out brush on a mesh other pieces wear: the piece gets its own copy at the first push (the shared mesh and the
    /// other piece never change), and a stroke is one undo step that puts the positions back. Fixtures in a preview scene and
    /// a temporary Assets folder; the Undo records are reverted.
    /// </summary>
    public sealed class ReFitPushOutTests
    {
        private Scene scene;
        private GameObject root;
        private string folder;
        private int undoGroup;
        private readonly List<Object> created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Push out avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            folder = "Assets/ReFitPushOutTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            ReFitClipFixTool.End();
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(undoGroup);
            foreach (var record in Resources.FindObjectsOfTypeAll<ReFitPushOutUndo>())
                if (record != null && (record.mesh == null || AssetDatabase.GetAssetPath(record.mesh).StartsWith(folder, StringComparison.Ordinal)))
                    Object.DestroyImmediate(record);
            Object.DestroyImmediate(root);
            foreach (var o in created) if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
            AssetDatabase.DeleteAsset(folder);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test]
        public void TheFirstPushGivesThePieceItsOwnMeshAndAStrokeUndoesInOneStep()
        {
            var body = Renderer("Body", Box("Body", .1f));
            var shared = Box("Shirt", .096f);
            AssetDatabase.CreateAsset(shared, folder + "/Shirt.asset");
            var piece = Renderer("Shirt", shared);
            var twin = Renderer("Shirt on another avatar", shared);
            var authored = shared.vertices;

            Assert.That(ReFitClipFixTool.Begin(piece, body), Is.Null);
            var corner = piece.transform.TransformPoint(authored[7]);
            ReFitClipFixTool.PushAt(corner);

            var own = ReFitClipFixTool.PushedMesh;
            Assert.That(own, Is.Not.SameAs(shared), "A mesh another piece wears is copied before the brush changes it.");
            Assert.That(piece.sharedMesh, Is.SameAs(own));
            Assert.That(twin.sharedMesh, Is.SameAs(shared), "The other piece keeps its mesh.");
            Assert.That(shared.vertices, Is.EqualTo(authored), "The shared mesh is untouched.");
            Assert.That(AssetDatabase.GetAssetPath(own), Does.StartWith(folder + "/"), "The copy is saved next to the mesh.");
            Assert.That(AssetDatabase.GetLabels(own), Does.Contain("ReFitPushOut"));
            Assert.That((own.vertices[7] - authored[7]).magnitude, Is.GreaterThan(1e-4f), "The corner inside the body moved out.");

            // A second push in the same session pushes the piece's own copy in place.
            ReFitClipFixTool.PushAt(corner);
            Assert.That(ReFitClipFixTool.PushedMesh, Is.SameAs(own));

            // Undone (this test's own undo group only): the strokes' positions come back on the copy, as Undo puts them
            // back, and the piece wears the shared mesh again.
            ReFitClipFixTool.End();
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(undoGroup);
            ReFitPushOutUndo.Apply();
            Assert.That(piece.sharedMesh, Is.SameAs(shared));
            Assert.That(own.vertices, Is.EqualTo(authored), "The strokes' undo steps put the positions back.");
        }

        [Test]
        public void AnUndoOfSomethingElseKeepsWhatAnotherToolDidToThePushedMesh()
        {
            var body = Renderer("Body", Box("Body", .1f));
            var shared = Box("Shirt", .096f);
            AssetDatabase.CreateAsset(shared, folder + "/Shirt.asset");
            var piece = Renderer("Shirt", shared);
            var authored = shared.vertices;

            Assert.That(ReFitClipFixTool.Begin(piece, body), Is.Null);
            ReFitClipFixTool.PushAt(piece.transform.TransformPoint(authored[7]));
            var own = ReFitClipFixTool.PushedMesh;
            ReFitClipFixTool.End();

            // Another tool moves a point after the stroke, then an Undo (of anything else: the brush's record is unchanged)
            // runs the record's handler.
            var edited = own.vertices;
            edited[0] += new Vector3(0f, .01f, 0f);
            own.vertices = edited;
            ReFitPushOutUndo.Apply();
            Assert.That(own.vertices, Is.EqualTo(edited), "The other tool's edit stays.");

            // The stroke's own undo step still puts the positions back.
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(undoGroup);
            ReFitPushOutUndo.Apply();
            Assert.That(own.vertices, Is.EqualTo(authored));
        }

        [Test]
        public void HiddenEditorCopiesWearingThePiecesOwnMeshNeverMakeTheBrushCopyItAgain()
        {
            var body = Renderer("Body", Box("Body", .1f));
            var own = Box("Shirt", .096f);
            AssetDatabase.CreateAsset(own, folder + "/Shirt pushed out.asset");
            AssetDatabase.SetLabels(own, new[] { "ReFitPushOut" });
            var piece = Renderer("Shirt", own);
            var corner = piece.transform.TransformPoint(own.vertices[7]);
            var hiddenScene = EditorSceneManager.NewPreviewScene();
            try
            {
                // A hidden avatar copy in a preview scene of its own, and a helper never saved next to the piece, wear it too.
                var hidden = EditorUtility.CreateGameObjectWithHideFlags("Hidden copy", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(hidden, hiddenScene);
                hidden.hideFlags = HideFlags.None;
                hidden.AddComponent<SkinnedMeshRenderer>().sharedMesh = own;
                Renderer("Unsaved helper", own).gameObject.hideFlags = HideFlags.DontSave;

                Assert.That(ReFitClipFixTool.Begin(piece, body), Is.Null);
                ReFitClipFixTool.PushAt(corner);
                Assert.That(ReFitClipFixTool.PushedMesh, Is.SameAs(own), "The piece's own copy is pushed in place.");
                Assert.That(piece.sharedMesh, Is.SameAs(own));

                // A piece of another avatar loaded next to it still gets the mesh copied first.
                var twin = Renderer("Shirt on another avatar", own);
                Assert.That(ReFitClipFixTool.Begin(piece, body), Is.Null);
                ReFitClipFixTool.PushAt(corner);
                Assert.That(ReFitClipFixTool.PushedMesh, Is.Not.SameAs(own));
                Assert.That(twin.sharedMesh, Is.SameAs(own));
            }
            finally
            {
                ReFitClipFixTool.End();
                EditorSceneManager.ClosePreviewScene(hiddenScene);
            }
        }

        [Test]
        public void TheWornShapesDescribeTheSameWhateverTheirOrder()
        {
            var a = new Dictionary<string, float> { ["Shrink"] = 100f, ["Fluff off"] = 50f };
            var b = new Dictionary<string, float> { ["Fluff off"] = 50f, ["Shrink"] = 100f };
            Assert.That(ReFitOutfitToggles.Describe(a), Is.EqualTo(ReFitOutfitToggles.Describe(b)));
            Assert.That(ReFitOutfitToggles.Describe(a), Is.EqualTo("Fluff off 50,Shrink 100"));
            Assert.That(ReFitOutfitToggles.Describe(null), Is.Empty);
        }

        private SkinnedMeshRenderer Renderer(string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { root.transform };
            renderer.rootBone = root.transform;
            return renderer;
        }

        // A closed box of eight shared corners, its faces turned outwards.
        private Mesh Box(string name, float half)
        {
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) != 0 ? half : -half, (i & 2) != 0 ? half : -half, (i & 4) != 0 ? half : -half);
            int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } };
            var triangles = new List<int>();
            foreach (var f in faces)
                foreach (var (a, b, c) in new[] { (f[0], f[1], f[2]), (f[0], f[2], f[3]) })
                {
                    var center = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                    bool outward = Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), center) > 0f;
                    triangles.AddRange(outward ? new[] { a, b, c } : new[] { a, c, b });
                }
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles.ToArray() };
            mesh.RecalculateNormals();
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 8).ToArray();
            mesh.RecalculateBounds();
            created.Add(mesh);
            return mesh;
        }
    }
}
