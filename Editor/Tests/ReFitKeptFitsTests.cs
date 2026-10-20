using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
#endif
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
#if REFIT_VRCHAT_AVATARS
        /// <summary>
        /// Kept fits of a mesh as ReFit makes it (the original mesh with the fit in its "refit" blendshape at 100) become the
        /// build's menu shapes: the shape of a kept fit is the difference between it and the fit worn, never zero. The kept
        /// fit here is the same mesh worn without its fit shape, so its menu shape is the fit taken away. The fixture's bodies
        /// have the same surface (no fit at all): the target shows a shape the hoodie does not follow, which moves its body
        /// 8 cm forward, and the hoodie is fitted over the body as it shows.
        /// </summary>
        internal static void KeptFits_FromReFitMeshesAreTheDifferenceOfTheFits()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var hoodie = fixture.sourceSpaceAccessory.renderer;
                var targetMesh = fixture.target.renderer.sharedMesh;
                var forward = new Vector3[targetMesh.vertexCount];
                for (int i = 0; i < forward.Length; i++) forward[i] = new Vector3(0f, 0f, .08f);
                targetMesh.AddBlendShapeFrame("ShownForward", 100f, forward, null, null);
                fixture.target.renderer.SetBlendShapeWeight(targetMesh.GetBlendShapeIndex("ShownForward"), 100f);
                var request = BuildMeshAndBlendshapeRequest(fixture, hoodie, false);
                var comp = new ReFitEngine().Run(request);
                var scene = EditorSceneManager.NewPreviewScene();
                var made = new List<Object>();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                GameObject root = null, clone = null;
                try
                {
                    AssertComputationSucceeded(comp);
                    var fitted = comp.mesh;
                    made.Add(fitted);
                    int fitIndex = fitted.GetBlendShapeIndex(comp.primaryShapeName ?? "");
                    AssertTrue(fitIndex >= 0, "ReFit gave its mesh no fit shape.");
                    var shapes = new List<RefitShape>();
                    int count = Mathf.Min(comp.secondaryShapeNames?.Length ?? 0, comp.secondarySourceShapeNames?.Length ?? 0);
                    for (int i = 0; i < count; i++) shapes.Add(new RefitShape(comp.secondarySourceShapeNames[i], comp.secondaryShapeNames[i]));

                    root = new GameObject("Kept fits avatar");
                    SceneManager.MoveGameObjectToScene(root, scene);
                    var pieceObject = new GameObject("Hoodie");
                    pieceObject.transform.SetParent(root.transform, false);
                    var piece = pieceObject.AddComponent<SkinnedMeshRenderer>();
                    piece.bones = new[] { root.transform };
                    var original = new RefitRendererState { mesh = hoodie.sharedMesh };

                    var unfitted = Object.Instantiate(fitted);
                    made.Add(unfitted);
                    piece.sharedMesh = unfitted;
                    piece.SetBlendShapeWeight(fitIndex, 0f);
                    var record = RefitRecords.Register(piece, original, unfitted, null, null, shapes, OrbitersRefit.FitKind.Fitted, "base:1", "Base", "Test");
                    RefitRecords.KeepFit(record, "Unfitted");
                    piece.sharedMesh = fitted;
                    piece.SetBlendShapeWeight(fitIndex, 100f);
                    record = RefitRecords.Register(piece, original, fitted, null, null, shapes, OrbitersRefit.FitKind.Fitted, "base:1", "Base", "Test");
                    RefitRecords.KeepFit(record, "Fitted");
                    AssertTrue(RefitRecords.FitShape(record) == comp.primaryShapeName, $"The fit shape was taken for '{RefitRecords.FitShape(record)}'.");

                    clone = Object.Instantiate(root);
                    SceneManager.MoveGameObjectToScene(clone, scene);
                    var warnings = new List<string>();
                    AssertTrue(RefitKeptFitsBuild.Apply(clone, warnings) == 1, "The kept fits made no menu: " + string.Join("; ", warnings));
                    var copy = clone.transform.Find("Hoodie").GetComponent<SkinnedMeshRenderer>();
                    made.Add(copy.sharedMesh);
                    int kept = copy.sharedMesh.GetBlendShapeIndex("ReFit fit: Unfitted");
                    AssertTrue(kept >= 0, "The kept fit has no shape on the build copy.");
                    int n = fitted.vertexCount;
                    var deltas = new Vector3[n];
                    var fit = new Vector3[n];
                    copy.sharedMesh.GetBlendShapeFrameVertices(kept, 0, deltas, null, null);
                    fitted.GetBlendShapeFrameVertices(fitIndex, fitted.GetBlendShapeFrameCount(fitIndex) - 1, fit, null, null);
                    float largest = 0f, worst = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        largest = Mathf.Max(largest, fit[i].magnitude);
                        worst = Mathf.Max(worst, (deltas[i] + fit[i]).magnitude);
                    }
                    AssertTrue(largest > .001f, $"The fixture's fit moved the hoodie by {largest * 1000f:0.###} mm only.");
                    AssertTrue(worst < 1e-5f, $"The kept fit's shape is not the fit taken away ({worst * 1000f:0.###} mm off).");
                }
                finally
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(undoGroup);
                    if (clone != null) Object.DestroyImmediate(clone);
                    if (root != null) Object.DestroyImmediate(root);
                    foreach (var o in made) if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);
                    EditorSceneManager.ClosePreviewScene(scene);
                }
            }
        }
#endif
    }

    /// <summary>Test Runner entry points for the kept fits of ReFit's own meshes.</summary>
    public sealed class ReFitKeptFitsTests
    {
#if REFIT_VRCHAT_AVATARS
        [Test]
        public void KeptFitsOfReFitMeshesAreTheDifferenceOfTheFits()
        {
            if (VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
            ReFitDeterministicTestRunner.KeptFits_FromReFitMeshesAreTheDifferenceOfTheFits();
        }
#endif
    }
}
