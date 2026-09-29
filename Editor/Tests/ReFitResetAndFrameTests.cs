using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        /// <summary>Focused audit checks on disposable fixtures; no active avatar or third-party build is used.</summary>
        public static void RunAuditRegressionTestsOrThrow()
        {
            GeneratedMetadata_IsEditorOnlyForVRChat();
            ArmatureReplacement_ResetRestoresRemovedRigAndReferences();
            BlendShapeFrames_SnapshotMatchesUnity();
            TransferredBlendshape_KeepsBodyFrames();
            SavePrefab_NeverSavesTheAvatar();
            Debug.Log("[ReFit Tests] Five focused audit regression checks passed.");
        }

        // The VRChat SDK only accepts unknown components on an avatar when they are IEditorOnly.
        private static void GeneratedMetadata_IsEditorOnlyForVRChat()
        {
            Type editorOnly = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if ((editorOnly = assembly.GetType("VRC.SDKBase.IEditorOnly")) != null) break;
            if (editorOnly == null) throw new SkippedTestException("The VRChat SDK is not installed.");
            AssertTrue(editorOnly.IsAssignableFrom(typeof(ReFitGeneratedAssetMetadata)),
                "ReFit's generated metadata component is not IEditorOnly, so VRChat SDK validation rejects it.");
        }

        // Reset after an armature replacement puts back the removed bones (with their components), the component
        // references ReFit rebound, and removes the rebuilt rig without deleting what was parented under it since.
        private static void ArmatureReplacement_ResetRestoresRemovedRigAndReferences()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                var accessory = fixture.sourceSpaceAccessory;
                var originalParent = accessory.root.transform.parent;
                var originalMesh = accessory.renderer.sharedMesh;
                var originalBoneNames = new List<string>();
                foreach (var bone in accessory.renderer.bones) originalBoneNames.Add(bone.name);
                accessory.chest.gameObject.AddComponent<RotationConstraint>();
                var stringRoot = NewChild(accessory.chest, "String L Root");
                stringRoot.localPosition = new Vector3(0.03f, -0.18f, 0.04f);
                var stringTip = NewChild(stringRoot, "String L Tip");
                stringTip.localPosition = new Vector3(0.01f, -0.22f, 0.02f);
                var unrelated = NewChild(fixture.target.root.transform, "Unrelated Reference");
                var probe = NewChild(accessory.root.transform, "Serialized Bone References")
                    .gameObject.AddComponent<SerializedBoneReferenceProbe>();
                probe.rootTransform = accessory.chest;
                probe.boneGameObject = accessory.bones[(int)RigBone.LeftUpperArm].gameObject;
                probe.boneArray = new[] { accessory.hips, stringTip };
                probe.boneList = new List<Transform> { accessory.chest, stringRoot };
                probe.unrelatedTransform = unrelated;
                var childNames = ChildNames(accessory.root.transform);

                var request = BuildMeshAndBlendshapeRequest(fixture, accessory.renderer, true);
                var comp = new ReFitEngine().Run(request);
                ReFitRendererState originalState = null;
                try
                {
                    AssertComputationSucceeded(comp);
                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report, out originalState);
                    AssertTrue(applied != null && originalState != null, "ApplyToScene returned no renderer or state.");
                    var rig = applied.rootBone;
                    while (rig != null && rig.parent != accessory.root.transform) rig = rig.parent;
                    AssertTrue(rig != null && accessory.hips == null, "The armature replacement did not rebuild the rig.");
                    AssertTrue(probe.rootTransform.IsChildOf(rig), "The probe was not rebound to the rebuilt rig.");
                    var userAdded = NewChild(FindRendererBone(applied, "Chest"), "User Added");

                    AssertTrue(originalState.Restore(applied, "ReFit test restore"), "Restore failed.");

                    AssertTrue(rig == null, "Reset left the rebuilt rig in place.");
                    AssertTrue(userAdded != null && userAdded.parent == accessory.root.transform,
                        "Reset deleted an object parented under the rebuilt rig after the refit instead of keeping it.");
                    AssertSame(accessory.root.transform.parent, originalParent, "Reset did not restore the asset's parent.");
                    AssertSame(accessory.renderer.sharedMesh, originalMesh, "Reset did not restore the original mesh.");
                    var bones = accessory.renderer.bones;
                    AssertTrue(bones.Length == originalBoneNames.Count, "Reset changed the renderer bone count.");
                    for (int i = 0; i < bones.Length; i++)
                        AssertTrue(bones[i] != null && bones[i].name == originalBoneNames[i] && bones[i].IsChildOf(accessory.root.transform),
                            $"Renderer bone {i} was not restored to '{originalBoneNames[i]}'.");
                    AssertTrue(ChildNames(accessory.root.transform) == childNames + ", User Added",
                        $"Reset left a different hierarchy under the asset: {ChildNames(accessory.root.transform)}.");

                    var hips = bones[(int)RigBone.Hips];
                    var chest = bones[(int)RigBone.Chest];
                    AssertTrue(chest.GetComponent<RotationConstraint>() != null, "Reset lost the component on a removed bone.");
                    AssertSame(probe.rootTransform, chest, "A Transform reference was not restored to the original chest.");
                    AssertSame(probe.boneGameObject, bones[(int)RigBone.LeftUpperArm].gameObject,
                        "A GameObject reference was not restored to the original arm.");
                    AssertSame(probe.boneArray[0], hips, "An array reference was not restored to the original hips.");
                    AssertSame(probe.boneList[0], chest, "A list reference was not restored to the original chest.");
                    AssertTrue(probe.boneList[1] != null && probe.boneList[1].name == "String L Root" && probe.boneList[1].parent == chest,
                        "A non-skinned helper reference was not restored to the original helper under the chest.");
                    AssertTrue(probe.boneArray[1] != null && probe.boneArray[1].parent == probe.boneList[1],
                        "A nested helper reference was not restored to the original helper tip.");
                    AssertLessOrEqual(Vector3.Distance(probe.boneList[1].localPosition, new Vector3(0.03f, -0.18f, 0.04f)), 0.0001f,
                        "The restored helper lost its local position.");
                    AssertSame(probe.unrelatedTransform, unrelated, "Reset changed an unrelated reference.");
                }
                finally
                {
                    originalState?.DiscardRemoved();
                    Undo.RevertAllDownToGroup(undoGroup);
                    DestroyComputationMesh(comp);
                }
            }
        }

        // Unity renders blendshapes frame by frame; ReFit's snapshots must see the same surface.
        private static void BlendShapeFrames_SnapshotMatchesUnity()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var go = new GameObject("ReFit blendshape frames");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
            var mesh = new Mesh();
            var baked = new Mesh();
            try
            {
                var vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
                mesh.vertices = vertices;
                mesh.triangles = new[] { 0, 1, 2 };
                Vector3[] Deltas(Vector3 d) => new[] { d, d * 2f, -d };
                mesh.AddBlendShapeFrame("single at 50", 50f, Deltas(Vector3.forward), null, null);
                mesh.AddBlendShapeFrame("two", 40f, Deltas(Vector3.right), null, null);
                mesh.AddBlendShapeFrame("two", 100f, Deltas(new Vector3(0.2f, 1f, 0f)), null, null);
                mesh.AddBlendShapeFrame("three", 20f, Deltas(Vector3.right), null, null);
                mesh.AddBlendShapeFrame("three", 60f, Deltas(Vector3.up), null, null);
                mesh.AddBlendShapeFrame("three", 80f, Deltas(Vector3.forward), null, null);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                foreach (var weights in new[]
                         {
                             new[] { 25f, 0f, 0f }, new[] { 100f, 0f, 0f }, new[] { 0f, 20f, 0f }, new[] { 0f, 70f, 0f },
                             new[] { 0f, 0f, 10f }, new[] { 0f, 0f, 25f }, new[] { 0f, 0f, 75f }, new[] { 0f, 0f, 100f },
                             new[] { 30f, 55f, 65f }, new[] { -20f, 0f, 0f }
                         })
                {
                    for (int s = 0; s < weights.Length; s++) renderer.SetBlendShapeWeight(s, weights[s]);
                    renderer.BakeMesh(baked);
                    var expected = baked.vertices;
                    var actual = MeshSnapshot.Capture(renderer, false, null, null).localVertices;
                    for (int i = 0; i < expected.Length; i++)
                        AssertLessOrEqual(Vector3.Distance(expected[i], actual[i]), 0.0001f,
                            $"Snapshot vertex {i} differs from Unity at weights {string.Join(", ", weights)}.");
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(baked);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // An in-between body frame is transferred as its own clothing frame at the same weight.
        private static void TransferredBlendshape_KeepsBodyFrames()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var clothing = fixture.targetSpaceAccessory;
                clothing.root.transform.SetParent(fixture.target.root.transform, true);
                var body = fixture.target.mesh;
                var sideways = new Vector3[body.vertexCount];
                var upward = new Vector3[body.vertexCount];
                for (int i = 0; i < sideways.Length; i++)
                {
                    sideways[i] = new Vector3(0.03f, 0f, 0f);
                    upward[i] = new Vector3(0f, 0.03f, 0f);
                }
                body.AddBlendShapeFrame("Two Frames", 40f, sideways, null, null);
                body.AddBlendShapeFrame("Two Frames", 100f, upward, null, null);

                var request = BuildBlendshapeOnlyRequest(fixture, clothing.renderer);
                request.targetBlendshape = "Two Frames";
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    int shape = comp.mesh.GetBlendShapeIndex(comp.secondaryShapeNames[0]);
                    AssertTrue(shape >= 0 && comp.mesh.GetBlendShapeFrameCount(shape) == 2,
                        "The transferred shape does not have the body shape's two frames.");
                    AssertTrue(Mathf.Approximately(comp.mesh.GetBlendShapeFrameWeight(shape, 0), 40f) &&
                               Mathf.Approximately(comp.mesh.GetBlendShapeFrameWeight(shape, 1), 100f),
                        "The transferred frames do not use the body shape's frame weights.");
                    var deltas = new Vector3[comp.mesh.vertexCount];
                    var frameMeans = new Vector3[2];
                    for (int f = 0; f < 2; f++)
                    {
                        comp.mesh.GetBlendShapeFrameVertices(shape, f, deltas, null, null);
                        var world = new Vector3();
                        foreach (var d in deltas) world += clothing.renderer.transform.TransformVector(d);
                        frameMeans[f] = world / deltas.Length;
                    }
                    AssertTrue(frameMeans[0].x > 0.01f && Mathf.Abs(frameMeans[0].y) < frameMeans[0].x * 0.5f,
                        $"The in-between clothing frame does not follow the sideways body frame: {frameMeans[0]:F4}.");
                    AssertTrue(frameMeans[1].y > 0.01f && Mathf.Abs(frameMeans[1].x) < frameMeans[1].y * 0.5f,
                        $"The full clothing frame does not follow the upward body frame: {frameMeans[1]:F4}.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        // Clothing skinned to the avatar's own armature: the "refitted asset" prefab must never be the avatar.
        private static void SavePrefab_NeverSavesTheAvatar()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var clothing = fixture.targetSpaceAccessory;
                clothing.renderer.transform.SetParent(fixture.target.root.transform, true);
                clothing.renderer.bones = fixture.target.bones;
                clothing.renderer.rootBone = fixture.target.hips;
                clothing.mesh.bindposes = BuildBindposes(clothing.renderer.transform, fixture.target.bones);
                var request = BuildBlendshapeOnlyRequest(fixture, clothing.renderer);
                var report = new ReFitReport();
                string path = null;
                try
                {
                    path = ReFitAssetPipeline.TrySavePrefab(request, new ReFitComputation(), clothing.renderer, "__ReFitTest", report);
                    AssertTrue(path == null, $"ReFit saved '{path}' although the asset is skinned to the avatar's armature.");
                    AssertReportContains(report, "prefab-skipped", "Skipping the prefab was not reported.");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
                }
            }
        }
    }
}
