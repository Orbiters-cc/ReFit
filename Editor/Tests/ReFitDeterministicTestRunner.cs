using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Deterministic in-memory ReFit checks. These intentionally avoid Unity Test Framework dependencies so they
    /// can be run from the menu, batchmode, CI, or by agents with only an editor executeMethod call.
    /// </summary>
    public static class ReFitDeterministicTestRunner
    {
        private const string BodyShapeName = "TestMuscle";
        private const float PrimaryRefitDriftTolerance = 0.002f;
        private const float ChestForwardDeltaMinimum = 0.06f;
        private const float ArmOutwardDeltaMinimum = 0.02f;
        private const float WaistDeltaMaximum = 0.012f;
        private const string FbxFixtureV1Path = "Packages/orbiters.refit/ReFit unit test v1.fbx";
        private const string FbxFixtureV2DifferentArmaturePath = "Packages/orbiters.refit/ReFit unit test v2 clothing with different armature.fbx";
        private const string FbxShapeName = "custom blendshape";

        [MenuItem("Tools/Orbiters/ReFit/Run Deterministic Tests")]
        public static void RunFromMenu()
        {
            try
            {
                RunOrThrow();
                EditorUtility.DisplayDialog("ReFit deterministic tests", "All deterministic ReFit tests passed.", "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("ReFit deterministic tests failed", e.Message, "OK");
            }
        }

        public static void RunBatchMode()
        {
            try
            {
                RunOrThrow();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void RunOrThrow()
        {
            var previousSelection = Selection.activeObject;
            var failures = new List<string>();
            try
            {
                RunCase(failures,
                    "MeshAndBlendshape equal body surfaces do not create primary refit drift",
                    MeshAndBlendshape_EqualSurfaces_NoPrimaryDrift_TransfersMuscle);
                RunCase(failures,
                    "Blendshape-only transfer works on target-space clothing and preserves root bone",
                    BlendshapeOnly_TargetSpaceAccessory_TransfersMuscle_PreservesRootBone);
                RunCase(failures,
                    "Mesh refit with armature replacement disabled preserves clothing root bone",
                    MeshAndBlendshape_ArmatureReplacementDisabled_PreservesRootBone);
                RunCase(failures,
                    "Armature replacement removes stale accessory skeleton",
                    MeshAndBlendshape_ArmatureReplacement_RemovesStaleAccessorySkeleton);
                RunCase(failures,
                    "Armature replacement materializes child-first bone plans without hierarchy drift",
                    ArmatureReplacement_ChildFirstPlan_MaterializesWithoutDrift);
                RunCase(failures,
                    "Armature replacement cleans rerun target-space stale skeleton",
                    ArmatureReplacement_RerunTargetSpace_RemovesUnusedLocalSkeleton);
                RunCase(failures,
                    "Humanoid alias bone names map to target armature bones",
                    HumanoidAliases_MapAccessoryBonesToAvatarBones);
                RunCase(failures,
                    "FBX fixture reproduces authored B clothing result",
                    FbxFixture_ReproducesAuthoredResultClothing);
                RunCase(failures,
                    "FBX fixture supports clothing with an inserted armature bone",
                    FbxFixture_DifferentClothingArmature_ReproducesAuthoredResultClothing);

                if (failures.Count > 0)
                    throw new Exception("[ReFit Tests] Failed deterministic checks:\n" + string.Join("\n", failures));

                Debug.Log("[ReFit Tests] All deterministic ReFit tests passed.");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        private static void FbxFixture_ReproducesAuthoredResultClothing()
        {
            RunFbxFixtureAgainstAuthoredResult(FbxFixtureV1Path, "FBX v1");
        }

        private static void FbxFixture_DifferentClothingArmature_ReproducesAuthoredResultClothing()
        {
            RunFbxFixtureAgainstAuthoredResult(FbxFixtureV2DifferentArmaturePath, "FBX v2");
        }

        private static void RunFbxFixtureAgainstAuthoredResult(string fixturePath, string label)
        {
            using (var fixture = FbxResultFixture.Create(fixturePath))
            {
                var request = new ReFitRequest
                {
                    mode = ReFitMode.MeshAndBlendshape,
                    assetRenderer = fixture.clothingA,
                    sourceAvatar = fixture.sourceAvatar,
                    targetAvatar = fixture.targetAvatar,
                    sourceBodyRenderer = fixture.sourceBody,
                    targetBodyRenderer = fixture.targetBody,
                    targetBlendshape = FbxShapeName,
                    settings = new ReFitSettings
                    {
                        maxProjectionDistance = 0.25f,
                        falloffStartDistance = 0.08f,
                        smoothingIterations = 0,
                        smoothingStrength = 0f,
                        filterByNormal = false,
                        filterByBoneRegion = false,
                        transferWeights = true,
                        replaceArmature = true,
                        keepExtraBoneVertices = true,
                        blendshapeName = "refit",
                        prefixTransferredShapes = false,
                        offsetMode = OffsetMode.Translate,
                        recalculateNormalDeltas = false,
                        savePrefab = false,
                        proportionWarningThreshold = 1f
                    }
                };

                var originalClothing = MeshSnapshot.Capture(fixture.clothingA, false, null, new ReFitReport());
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    var generated = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(generated != null, "ApplyToScene returned no renderer for the FBX fixture.");
                    AssertRootBoneInRendererBones(generated, label);
                    if (fixturePath == FbxFixtureV2DifferentArmaturePath)
                        AssertTrue(!RendererHasBone(generated, "middle arm"),
                            "The inserted clothing-only 'middle arm' bone should resolve onto the target armature, not be preserved as an extra deforming bone.");

                    var baseForward = MeasureSurfaceDistance(generated, fixture.expectedClothing);
                    var baseReverse = MeasureSurfaceDistance(fixture.expectedClothing, generated);
                    var baseQuality = MeasureTriangleQuality(originalClothing, generated);
                    Debug.Log($"[ReFit Tests] {label} base generated->expected {baseForward}");
                    Debug.Log($"[ReFit Tests] {label} base expected->generated {baseReverse}");
                    Debug.Log($"[ReFit Tests] {label} base triangle quality {baseQuality}");
                    AssertFbxSurfaceMetrics($"{label} base generated->expected", baseForward, 0.008f, 0.014f, 0.015f, 0.13f);
                    AssertFbxSurfaceMetrics($"{label} base expected->generated", baseReverse, 0.012f, 0.03f, 0.055f, 0.19f);
                    AssertTriangleQuality($"{label} base", baseQuality);

                    SetBlendShapeWeight(generated, FbxShapeName, 100f);
                    SetBlendShapeWeight(fixture.expectedClothing, FbxShapeName, 100f);
                    var shapeForward = MeasureSurfaceDistance(generated, fixture.expectedClothing);
                    var shapeReverse = MeasureSurfaceDistance(fixture.expectedClothing, generated);
                    var shapeQuality = MeasureTriangleQuality(originalClothing, generated);
                    Debug.Log($"[ReFit Tests] {label} shape generated->expected {shapeForward}");
                    Debug.Log($"[ReFit Tests] {label} shape expected->generated {shapeReverse}");
                    Debug.Log($"[ReFit Tests] {label} shape triangle quality {shapeQuality}");
                    AssertFbxSurfaceMetrics($"{label} shape generated->expected", shapeForward, 0.008f, 0.014f, 0.015f, 0.13f);
                    AssertFbxSurfaceMetrics($"{label} shape expected->generated", shapeReverse, 0.012f, 0.03f, 0.055f, 0.19f);
                    AssertTriangleQuality($"{label} shape", shapeQuality);
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void HumanoidAliases_MapAccessoryBonesToAvatarBones()
        {
            var avatar = new GameObject("__ReFitTest_AliasAvatar");
            var accessory = new GameObject("__ReFitTest_AliasAccessory");
            try
            {
                avatar.hideFlags = HideFlags.HideAndDontSave;
                accessory.hideFlags = HideFlags.HideAndDontSave;

                var upperArm = NewChild(avatar.transform, "upper_arm.L");
                var forearm = NewChild(upperArm, "forearm.L");
                NewChild(avatar.transform, "Chest");

                var leftArm = NewChild(accessory.transform, "Left arm");
                var leftElbow = NewChild(leftArm, "Left elbow");
                var chestUp = NewChild(accessory.transform, "Chest Up");

                var map = HumanoidBoneMapper.MatchBonesByName(accessory.GetComponentsInChildren<Transform>(true), avatar.transform);
                AssertSame(map[leftArm], upperArm, "Accessory 'Left arm' should map to avatar 'upper_arm.L'.");
                AssertSame(map[leftElbow], forearm, "Accessory 'Left elbow' should map to avatar 'forearm.L'.");
                AssertTrue(map.ContainsKey(chestUp) && map[chestUp] == null,
                    "Accessory 'Chest Up' should be preserved when the target has no UpperChest bone.");
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(accessory);
            }
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.hideFlags = HideFlags.HideAndDontSave;
            child.transform.SetParent(parent, false);
            return child.transform;
        }

        private static void RunCase(List<string> failures, string name, Action test)
        {
            try
            {
                test();
                Debug.Log($"[ReFit Tests] PASS: {name}");
            }
            catch (Exception e)
            {
                failures.Add($"- {name}: {e.Message}");
                Debug.LogError($"[ReFit Tests] FAIL: {name}\n{e}");
            }
        }

        private static void MeshAndBlendshape_EqualSurfaces_NoPrimaryDrift_TransfersMuscle()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AssertFixtureActuallyDiffers(fixture);

                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(!string.IsNullOrEmpty(comp.primaryShapeName),
                        "MeshAndBlendshape mode did not produce the primary refit blendshape.");

                    float maxPrimaryDrift = MaxBlendShapeMagnitude(comp.mesh, comp.primaryShapeName);
                    Debug.Log($"[ReFit Tests] Primary refit max drift: {maxPrimaryDrift * 1000f:0.###} mm");
                    AssertLessOrEqual(maxPrimaryDrift, PrimaryRefitDriftTolerance,
                        $"Expected no visible primary refit drift when source and target body surfaces are identical. " +
                        $"Max drift was {maxPrimaryDrift * 1000f:0.###} mm.");

                    AssertTransferredMuscleShape(comp.mesh, SingleSecondaryShape(comp));
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void BlendshapeOnly_TargetSpaceAccessory_TransfersMuscle_PreservesRootBone()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var originalRootBone = fixture.targetSpaceAccessory.renderer.rootBone;
                var request = BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(string.IsNullOrEmpty(comp.primaryShapeName),
                        "Blendshape-only mode should not produce a primary refit blendshape.");

                    AssertTransferredMuscleShape(comp.mesh, SingleSecondaryShape(comp));

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertSame(applied.rootBone, originalRootBone,
                        $"Blendshape-only apply changed the clothing root bone from {PathOf(originalRootBone)} to {PathOf(applied.rootBone)}.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void MeshAndBlendshape_ArmatureReplacementDisabled_PreservesRootBone()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var originalRootBone = fixture.sourceSpaceAccessory.renderer.rootBone;
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(!comp.armatureReplaced,
                        "The computation replaced the armature even though settings.replaceArmature was false.");

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertSame(applied.rootBone, originalRootBone,
                        $"Apply with armature replacement disabled changed the clothing root bone from {PathOf(originalRootBone)} to {PathOf(applied.rootBone)}.");
                    AssertTrue(applied.rootBone != fixture.target.hips,
                        "Apply with armature replacement disabled rebound the clothing root bone to the target avatar hips.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void MeshAndBlendshape_ArmatureReplacement_RemovesStaleAccessorySkeleton()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var oldAccessoryHips = fixture.sourceSpaceAccessory.hips;
                var oldAccessoryChest = fixture.sourceSpaceAccessory.chest;
                var targetOnlyLowerLeg = NewChild(fixture.target.hips, "LeftLowerLeg");
                AppendRendererBone(fixture.target, targetOnlyLowerLeg);
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, true);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(comp.armatureReplaced, "The computation did not build a replacement armature plan.");

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertTrue(applied.rootBone != oldAccessoryHips,
                        "The applied renderer kept the old accessory root bone after armature replacement.");
                    AssertTrue(applied.rootBone != fixture.target.hips,
                        "The applied renderer should use a rebuilt clothing-owned hips bone, not the target avatar hips transform.");
                    AssertTrue(applied.rootBone != null && applied.rootBone.IsChildOf(fixture.sourceSpaceAccessory.root.transform),
                        $"The applied renderer root bone should be under the rebuilt clothing armature, but was '{PathOf(applied.rootBone)}'.");
                    AssertTrue(Array.IndexOf(applied.bones, fixture.target.hips) < 0,
                        "The applied renderer still binds directly to the target avatar hips instead of a rebuilt bone.");
                    AssertTrue(RendererHasBone(applied, "Hips"),
                        "The rebuilt clothing armature does not include a hips bone.");
                    AssertTrue(!RendererHasBone(applied, "LeftLowerLeg"),
                        "The rebuilt clothing armature included a target-only lower leg bone that has no clothing equivalent.");
                    AssertTrue(oldAccessoryHips == null && oldAccessoryChest == null,
                        "The stale source-space accessory skeleton was left in the scene after armature replacement.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void ArmatureReplacement_ChildFirstPlan_MaterializesWithoutDrift()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var renderer = fixture.sourceSpaceAccessory.renderer;
                var targetChest = fixture.target.bones[(int)RigBone.Chest];
                var targetHips = fixture.target.bones[(int)RigBone.Hips];
                var targetSpine = fixture.target.bones[(int)RigBone.Spine];
                var orderedTargetBones = new[] { targetChest, targetHips, targetSpine };

                var mesh = Object.Instantiate(renderer.sharedMesh);
                mesh.name = renderer.sharedMesh.name + "_ChildFirstBonePlan";
                mesh.hideFlags = HideFlags.HideAndDontSave;
                var weights = new BoneWeight[mesh.vertexCount];
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = new BoneWeight { boneIndex0 = 1, weight0 = 1f };
                mesh.boneWeights = weights;
                mesh.bindposes = BuildBindposes(renderer.transform, orderedTargetBones);

                var refs = new ReFitBoneRef[orderedTargetBones.Length];
                for (int i = 0; i < orderedTargetBones.Length; i++)
                {
                    refs[i] = new ReFitBoneRef
                    {
                        origin = ReFitBoneOrigin.Target,
                        path = ReFitUtility.IndexPath(orderedTargetBones[i], fixture.target.root.transform)
                    };
                }

                var request = BuildMeshAndBlendshapeRequest(fixture, renderer, true);
                var comp = new ReFitComputation
                {
                    success = true,
                    report = new ReFitReport(),
                    mesh = mesh,
                    armatureReplaced = true,
                    bones = refs,
                    rootBoneIndex = 1,
                    assetRendererPath = ReFitUtility.IndexPath(renderer.transform, fixture.sourceSpaceAccessory.root.transform)
                };

                try
                {
                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertTrue(applied.bones.Length == orderedTargetBones.Length,
                        $"Expected {orderedTargetBones.Length} rebuilt bones, got {applied.bones.Length}.");
                    AssertSame(applied.rootBone, applied.bones[1], "Root bone did not follow the requested child-first hips index.");
                    AssertSame(applied.bones[0].parent, applied.bones[2], "Chest was not parented under rebuilt Spine.");
                    AssertSame(applied.bones[2].parent, applied.bones[1], "Spine was not parented under rebuilt Hips.");

                    for (int i = 0; i < orderedTargetBones.Length; i++)
                    {
                        AssertLessOrEqual(Vector3.Distance(applied.bones[i].position, orderedTargetBones[i].position), 0.0001f,
                            $"Rebuilt bone {i}:{applied.bones[i].name} drifted from its target blueprint.");
                        AssertLessOrEqual(Quaternion.Angle(applied.bones[i].rotation, orderedTargetBones[i].rotation), 0.01f,
                            $"Rebuilt bone {i}:{applied.bones[i].name} rotation drifted from its target blueprint.");
                    }

                    var bindposes = applied.sharedMesh.bindposes;
                    AssertTrue(bindposes != null && bindposes.Length == applied.bones.Length,
                        "Applied mesh bindpose count does not match rebuilt bone count.");
                    for (int i = 0; i < applied.bones.Length; i++)
                    {
                        var expected = applied.bones[i].worldToLocalMatrix * applied.transform.localToWorldMatrix;
                        AssertLessOrEqual(MatrixMaxAbsDelta(bindposes[i], expected), 0.0001f,
                            $"Bindpose {i}:{applied.bones[i].name} does not match the final rebuilt transform.");
                    }
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void ArmatureReplacement_RerunTargetSpace_RemovesUnusedLocalSkeleton()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                fixture.sourceSpaceAccessory.root.transform.SetParent(fixture.target.root.transform, true);
                var renderer = fixture.sourceSpaceAccessory.renderer;
                var staleHips = fixture.sourceSpaceAccessory.hips;
                var staleChest = fixture.sourceSpaceAccessory.chest;
                var localExtraBone = NewChild(staleChest, "Hood string");

                var appliedBones = new Transform[fixture.target.bones.Length + 1];
                Array.Copy(fixture.target.bones, appliedBones, fixture.target.bones.Length);
                appliedBones[appliedBones.Length - 1] = localExtraBone;
                renderer.bones = appliedBones;
                renderer.rootBone = fixture.target.hips;

                var mesh = Object.Instantiate(renderer.sharedMesh);
                mesh.name = renderer.sharedMesh.name + "_TargetBound";
                mesh.hideFlags = HideFlags.HideAndDontSave;
                mesh.bindposes = BuildBindposes(renderer.transform, appliedBones);

                var refs = new ReFitBoneRef[appliedBones.Length];
                for (int i = 0; i < fixture.target.bones.Length; i++)
                {
                    refs[i] = new ReFitBoneRef
                    {
                        origin = ReFitBoneOrigin.Target,
                        path = ReFitUtility.IndexPath(fixture.target.bones[i], fixture.target.root.transform)
                    };
                }
                refs[refs.Length - 1] = new ReFitBoneRef
                {
                    origin = ReFitBoneOrigin.Asset,
                    path = ReFitUtility.IndexPath(localExtraBone, fixture.sourceSpaceAccessory.root.transform)
                };

                var request = BuildMeshAndBlendshapeRequest(fixture, renderer, true);
                var comp = new ReFitComputation
                {
                    success = true,
                    report = new ReFitReport(),
                    mesh = mesh,
                    armatureReplaced = true,
                    bones = refs,
                    rootBoneIndex = (int)RigBone.Hips,
                    assetRendererPath = ReFitUtility.IndexPath(renderer.transform, fixture.sourceSpaceAccessory.root.transform)
                };

                try
                {
                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertTrue(applied.rootBone != fixture.target.hips,
                        "The applied renderer should use a rebuilt clothing-owned hips bone, not the target avatar hips transform.");
                    AssertTrue(applied.rootBone != null && applied.rootBone.IsChildOf(fixture.sourceSpaceAccessory.root.transform),
                        $"The applied renderer root bone should be under the rebuilt clothing armature, but was '{PathOf(applied.rootBone)}'.");
                    AssertTrue(staleHips == null && staleChest == null,
                        "An unused local accessory skeleton container survived armature replacement.");
                    AssertTrue(Array.IndexOf(applied.bones, fixture.target.hips) < 0,
                        "The applied renderer still binds directly to the target avatar hips instead of a rebuilt bone.");
                    AssertTrue(RendererHasBone(applied, "Hood string"),
                        "The cleanup deleted or unbound an accessory-only preserved bone.");
                    AssertTrue(localExtraBone == null || Array.IndexOf(applied.bones, localExtraBone) < 0,
                        "The renderer should bind to the rebuilt copy of the accessory-only bone, not the original stale transform.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void AppendRendererBone(SkinnedSample sample, Transform bone)
        {
            var oldBones = sample.renderer.bones;
            var bones = new Transform[oldBones.Length + 1];
            Array.Copy(oldBones, bones, oldBones.Length);
            bones[bones.Length - 1] = bone;

            var oldMesh = sample.mesh;
            var mesh = Object.Instantiate(oldMesh);
            mesh.name = oldMesh.name + "_ExtraTargetBone";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.bindposes = BuildBindposes(sample.renderer.transform, bones);
            sample.renderer.bones = bones;
            sample.renderer.sharedMesh = mesh;
            sample.mesh = mesh;
            Object.DestroyImmediate(oldMesh);
        }

        private static ReFitRequest BuildMeshAndBlendshapeRequest(
            ReFitTestFixture fixture, SkinnedMeshRenderer accessory, bool replaceArmature)
        {
            return new ReFitRequest
            {
                mode = ReFitMode.MeshAndBlendshape,
                assetRenderer = accessory,
                sourceAvatar = fixture.source.root,
                targetAvatar = fixture.target.root,
                sourceBodyRenderer = fixture.source.renderer,
                targetBodyRenderer = fixture.target.renderer,
                targetBlendshape = BodyShapeName,
                settings = CreateDeterministicSettings(replaceArmature)
            };
        }

        private static ReFitRequest BuildBlendshapeOnlyRequest(ReFitTestFixture fixture, SkinnedMeshRenderer accessory)
        {
            return new ReFitRequest
            {
                mode = ReFitMode.Blendshape,
                assetRenderer = accessory,
                targetAvatar = fixture.target.root,
                targetBodyRenderer = fixture.target.renderer,
                targetBlendshape = BodyShapeName,
                settings = CreateDeterministicSettings(false)
            };
        }

        private static ReFitSettings CreateDeterministicSettings(bool replaceArmature)
        {
            return new ReFitSettings
            {
                maxProjectionDistance = 0.3f,
                falloffStartDistance = 0.08f,
                smoothingIterations = 0,
                smoothingStrength = 0f,
                filterByNormal = false,
                filterByBoneRegion = false,
                transferWeights = false,
                replaceArmature = replaceArmature,
                keepExtraBoneVertices = true,
                blendshapeName = "refit",
                prefixTransferredShapes = false,
                offsetMode = OffsetMode.Translate,
                recalculateNormalDeltas = false,
                savePrefab = false,
                proportionWarningThreshold = 1f
            };
        }

        private static void AssertFixtureActuallyDiffers(ReFitTestFixture fixture)
        {
            float chestBoneOffset = Vector3.Distance(fixture.source.chest.position, fixture.target.chest.position);
            AssertGreater(chestBoneOffset, 0.05f,
                "The deterministic fixture is invalid: source and target chest bones are not meaningfully different.");

            var sourceWeight = WeightAtClosestVertex(fixture.source.renderer.sharedMesh, new Vector2(0f, 1.05f));
            var targetWeight = WeightAtClosestVertex(fixture.target.renderer.sharedMesh, new Vector2(0f, 1.05f));
            float weightDifference = WeightDifference(sourceWeight, targetWeight);
            AssertGreater(weightDifference, 0.2f,
                "The deterministic fixture is invalid: source and target body skin weights are too similar.");
        }

        private static void AssertTransferredMuscleShape(Mesh mesh, string shapeName)
        {
            var chest = MeasureRegion(mesh, shapeName, v => Mathf.Abs(v.x) <= 0.35f && v.y >= 0.85f && v.y <= 1.3f);
            var leftArm = MeasureRegion(mesh, shapeName, v => v.x <= -0.75f && v.y >= 1f && v.y <= 1.5f);
            var rightArm = MeasureRegion(mesh, shapeName, v => v.x >= 0.75f && v.y >= 1f && v.y <= 1.5f);
            var waist = MeasureRegion(mesh, shapeName, v => Mathf.Abs(v.x) <= 0.45f && v.y <= 0.45f);

            Debug.Log(
                $"[ReFit Tests] {shapeName}: chest avg z {chest.average.z * 1000f:0.###} mm, " +
                $"left arm outward {leftArm.averageOutward * 1000f:0.###} mm, " +
                $"right arm outward {rightArm.averageOutward * 1000f:0.###} mm, " +
                $"waist max {waist.maxMagnitude * 1000f:0.###} mm");

            AssertGreater(chest.average.z, ChestForwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the chest far enough.");
            AssertGreater(leftArm.averageOutward, ArmOutwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the left upper arm far enough.");
            AssertGreater(rightArm.averageOutward, ArmOutwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the right upper arm far enough.");
            AssertLessOrEqual(waist.maxMagnitude, WaistDeltaMaximum,
                $"Transferred shape '{shapeName}' moved the waist even though the target body shape has no waist delta.");
        }

        private static string SingleSecondaryShape(ReFitComputation comp)
        {
            AssertTrue(comp.secondaryShapeNames != null && comp.secondaryShapeNames.Length == 1,
                "Expected exactly one transferred blendshape.");
            AssertTrue(!string.IsNullOrEmpty(comp.secondaryShapeNames[0]),
                "Transferred blendshape name was empty.");
            return comp.secondaryShapeNames[0];
        }

        private static void AssertComputationSucceeded(ReFitComputation comp)
        {
            AssertTrue(comp != null, "ReFit returned no computation.");
            AssertTrue(comp.success, "ReFit computation failed.\n" + FormatReport(comp.report));
            AssertTrue(comp.mesh != null, "ReFit computation succeeded without producing a mesh.");
        }

        private static string FormatReport(ReFitReport report)
        {
            if (report == null || report.messages.Count == 0) return "(no report messages)";
            var lines = new List<string>();
            foreach (var message in report.messages)
                lines.Add(message.ToString());
            return string.Join("\n", lines);
        }

        private static float MaxBlendShapeMagnitude(Mesh mesh, string shapeName)
        {
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            float max = 0f;
            for (int i = 0; i < deltas.Length; i++)
                max = Mathf.Max(max, deltas[i].magnitude);
            return max;
        }

        private static RegionMetrics MeasureRegion(Mesh mesh, string shapeName, Func<Vector3, bool> contains)
        {
            var vertices = mesh.vertices;
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            var metrics = new RegionMetrics();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i])) continue;

                metrics.count++;
                metrics.average += deltas[i];
                metrics.maxMagnitude = Mathf.Max(metrics.maxMagnitude, deltas[i].magnitude);
                if (Mathf.Abs(vertices[i].x) > 1e-4f)
                    metrics.averageOutward += Mathf.Sign(vertices[i].x) * deltas[i].x;
            }

            AssertTrue(metrics.count > 0, $"No vertices matched the requested region for shape '{shapeName}'.");
            metrics.average /= metrics.count;
            metrics.averageOutward /= metrics.count;
            return metrics;
        }

        private static Vector3[] GetBlendShapeDeltas(Mesh mesh, string shapeName)
        {
            AssertTrue(mesh != null, "Cannot read blendshape deltas from a null mesh.");
            int shapeIndex = mesh.GetBlendShapeIndex(shapeName);
            AssertTrue(shapeIndex >= 0, $"Mesh '{mesh.name}' does not contain blendshape '{shapeName}'.");
            int frame = mesh.GetBlendShapeFrameCount(shapeIndex) - 1;
            AssertTrue(frame >= 0, $"Blendshape '{shapeName}' has no frames.");
            var deltas = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frame, deltas, null, null);
            return deltas;
        }

        private static SurfaceMetrics MeasureSurfaceDistance(SkinnedMeshRenderer from, SkinnedMeshRenderer to)
        {
            var report = new ReFitReport();
            var fromSnap = MeshSnapshot.Capture(from, false, null, report);
            var toSnap = MeshSnapshot.Capture(to, false, null, report);
            var toBvh = SurfaceBvh.Build(toSnap);
            var distances = new List<float>(fromSnap.worldVertices.Length);
            double sum = 0d;
            double sumSq = 0d;
            float max = 0f;

            for (int i = 0; i < fromSnap.worldVertices.Length; i++)
            {
                var hit = toBvh.ClosestPoint(fromSnap.worldVertices[i], 20f, null);
                AssertTrue(hit.found, $"No closest point found for vertex {i} on '{from.name}'.");
                distances.Add(hit.distance);
                sum += hit.distance;
                sumSq += hit.distance * hit.distance;
                max = Mathf.Max(max, hit.distance);
            }

            distances.Sort();
            return new SurfaceMetrics
            {
                average = (float)(sum / distances.Count),
                rms = (float)Math.Sqrt(sumSq / distances.Count),
                p95 = Percentile(distances, 0.95f),
                p99 = Percentile(distances, 0.99f),
                max = max
            };
        }

        private static TriangleQualityMetrics MeasureTriangleQuality(MeshSnapshot reference, SkinnedMeshRenderer deformed)
        {
            AssertTrue(reference != null, "Cannot measure triangle quality without a reference snapshot.");
            var report = new ReFitReport();
            var deformedSnap = MeshSnapshot.Capture(deformed, false, null, report);
            AssertTrue(reference.worldVertices.Length == deformedSnap.worldVertices.Length,
                $"Triangle quality requires matching vertex counts. Expected {reference.worldVertices.Length}, got {deformedSnap.worldVertices.Length}.");
            AssertTrue(reference.triangles.Length == deformedSnap.triangles.Length,
                $"Triangle quality requires matching triangle topology. Expected {reference.triangles.Length / 3}, got {deformedSnap.triangles.Length / 3}.");

            var metrics = new TriangleQualityMetrics();
            for (int t = 0; t < reference.triangles.Length; t += 3)
            {
                int a = reference.triangles[t];
                int b = reference.triangles[t + 1];
                int c = reference.triangles[t + 2];
                AssertTrue(a == deformedSnap.triangles[t] &&
                           b == deformedSnap.triangles[t + 1] &&
                           c == deformedSnap.triangles[t + 2],
                    "Triangle quality requires identical triangle indices before and after ReFit.");

                TriangleStats(reference.worldVertices, a, b, c,
                    out var refMinEdge, out var refMaxEdge, out var refArea, out var refAspect);
                TriangleStats(deformedSnap.worldVertices, a, b, c,
                    out var deformedMinEdge, out var deformedMaxEdge, out var deformedArea, out var deformedAspect);
                if (refMaxEdge <= 1e-6f || refArea <= 1e-10f) continue;

                metrics.checkedTriangles++;
                metrics.maxEdgeGrowth = Mathf.Max(metrics.maxEdgeGrowth, deformedMaxEdge / refMaxEdge);
                metrics.maxEdgeShrink = Mathf.Max(metrics.maxEdgeShrink, refMaxEdge / Mathf.Max(deformedMaxEdge, 1e-8f));
                metrics.maxAreaGrowth = Mathf.Max(metrics.maxAreaGrowth, deformedArea / refArea);
                metrics.maxAreaShrink = Mathf.Max(metrics.maxAreaShrink, refArea / Mathf.Max(deformedArea, 1e-10f));
                metrics.maxAspectGrowth = Mathf.Max(metrics.maxAspectGrowth, deformedAspect / Mathf.Max(refAspect, 1e-6f));
            }

            AssertTrue(metrics.checkedTriangles > 0, "Triangle quality did not find any measurable triangles.");
            return metrics;
        }

        private static void TriangleStats(
            Vector3[] vertices,
            int a,
            int b,
            int c,
            out float minEdge,
            out float maxEdge,
            out float area,
            out float aspect)
        {
            var ab = (vertices[b] - vertices[a]).magnitude;
            var bc = (vertices[c] - vertices[b]).magnitude;
            var ca = (vertices[a] - vertices[c]).magnitude;
            minEdge = Mathf.Max(Mathf.Min(ab, Mathf.Min(bc, ca)), 1e-8f);
            maxEdge = Mathf.Max(ab, Mathf.Max(bc, ca));
            area = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude * 0.5f;
            aspect = maxEdge / minEdge;
        }

        private static float Percentile(List<float> sortedValues, float percentile)
        {
            AssertTrue(sortedValues != null && sortedValues.Count > 0, "Cannot measure an empty percentile set.");
            int index = Mathf.Clamp(Mathf.RoundToInt((sortedValues.Count - 1) * percentile), 0, sortedValues.Count - 1);
            return sortedValues[index];
        }

        private static void AssertFbxSurfaceMetrics(
            string label,
            SurfaceMetrics metrics,
            float maxAverage,
            float maxRms,
            float maxP95,
            float maxMax)
        {
            AssertLessOrEqual(metrics.average, maxAverage, $"{label} average distance is too high.");
            AssertLessOrEqual(metrics.rms, maxRms, $"{label} RMS distance is too high.");
            AssertLessOrEqual(metrics.p95, maxP95, $"{label} p95 distance is too high.");
            AssertLessOrEqual(metrics.max, maxMax, $"{label} max distance is too high.");
        }

        private static void AssertTriangleQuality(string label, TriangleQualityMetrics metrics)
        {
            AssertLessOrEqual(metrics.maxEdgeGrowth, 8f, $"{label} has an implausibly stretched triangle edge.");
            AssertLessOrEqual(metrics.maxEdgeShrink, 8f, $"{label} has an implausibly collapsed triangle edge.");
            AssertLessOrEqual(metrics.maxAreaGrowth, 40f, $"{label} has an implausibly inflated triangle area.");
            AssertLessOrEqual(metrics.maxAreaShrink, 80f, $"{label} has an implausibly collapsed triangle area.");
            AssertLessOrEqual(metrics.maxAspectGrowth, 12f, $"{label} has an implausibly distorted triangle aspect ratio.");
        }

        private static void SetBlendShapeWeight(SkinnedMeshRenderer renderer, string shapeName, float weight)
        {
            int shapeIndex = renderer.sharedMesh.GetBlendShapeIndex(shapeName);
            AssertTrue(shapeIndex >= 0, $"Mesh '{renderer.sharedMesh.name}' does not contain blendshape '{shapeName}'.");
            renderer.SetBlendShapeWeight(shapeIndex, weight);
        }

        private static bool RendererHasBone(SkinnedMeshRenderer renderer, string boneName)
        {
            var bones = renderer.bones;
            if (bones == null) return false;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && string.Equals(bones[i].name, boneName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void AssertRootBoneInRendererBones(SkinnedMeshRenderer renderer, string label)
        {
            AssertTrue(renderer.rootBone != null, $"{label} renderer root bone is null after armature replacement.");
            var bones = renderer.bones;
            AssertTrue(bones != null && bones.Length > 0, $"{label} renderer has no bones after armature replacement.");
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] == renderer.rootBone)
                    return;

            throw new Exception($"{label} renderer root bone '{renderer.rootBone.name}' is not part of its replaced bone array.");
        }

        private static BoneWeight WeightAtClosestVertex(Mesh mesh, Vector2 target)
        {
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            AssertTrue(weights != null && weights.Length == vertices.Length,
                $"Mesh '{mesh.name}' has no usable bone weights.");

            int bestIndex = -1;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < vertices.Length; i++)
            {
                float sqr = (new Vector2(vertices[i].x, vertices[i].y) - target).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                bestIndex = i;
            }

            AssertTrue(bestIndex >= 0, $"Mesh '{mesh.name}' has no vertices.");
            return weights[bestIndex];
        }

        private static float WeightDifference(BoneWeight a, BoneWeight b)
        {
            float diff = 0f;
            for (int bone = 0; bone < 8; bone++)
                diff += Mathf.Abs(WeightOf(a, bone) - WeightOf(b, bone));
            return diff;
        }

        private static float WeightOf(BoneWeight weight, int bone)
        {
            float value = 0f;
            if (weight.boneIndex0 == bone) value += weight.weight0;
            if (weight.boneIndex1 == bone) value += weight.weight1;
            if (weight.boneIndex2 == bone) value += weight.weight2;
            if (weight.boneIndex3 == bone) value += weight.weight3;
            return value;
        }

        private static void DestroyComputationMesh(ReFitComputation comp)
        {
            if (comp != null && comp.mesh != null)
            {
                Object.DestroyImmediate(comp.mesh);
                comp.mesh = null;
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertSame(Object actual, Object expected, string message)
        {
            if (actual != expected) throw new Exception(message);
        }

        private static void AssertGreater(float actual, float expectedMinimum, string message)
        {
            if (!(actual > expectedMinimum))
                throw new Exception($"{message} Expected > {expectedMinimum:0.####}, got {actual:0.####}.");
        }

        private static void AssertLessOrEqual(float actual, float expectedMaximum, string message)
        {
            if (!(actual <= expectedMaximum))
                throw new Exception($"{message} Expected <= {expectedMaximum:0.####}, got {actual:0.####}.");
        }

        private static float MatrixMaxAbsDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float max = 0f;
            for (int i = 0; i < 16; i++)
                max = Mathf.Max(max, Mathf.Abs(a[i] - b[i]));
            return max;
        }

        private static string PathOf(Transform transform)
        {
            if (transform == null) return "<null>";
            var parts = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private struct RegionMetrics
        {
            public int count;
            public Vector3 average;
            public float averageOutward;
            public float maxMagnitude;
        }

        private struct SurfaceMetrics
        {
            public float average;
            public float rms;
            public float p95;
            public float p99;
            public float max;

            public override string ToString()
            {
                return $"avg={average:0.000000} rms={rms:0.000000} p95={p95:0.000000} p99={p99:0.000000} max={max:0.000000}";
            }
        }

        private struct TriangleQualityMetrics
        {
            public int checkedTriangles;
            public float maxEdgeGrowth;
            public float maxEdgeShrink;
            public float maxAreaGrowth;
            public float maxAreaShrink;
            public float maxAspectGrowth;

            public override string ToString()
            {
                return $"triangles={checkedTriangles} edgeGrow={maxEdgeGrowth:0.000} edgeShrink={maxEdgeShrink:0.000} areaGrow={maxAreaGrowth:0.000} areaShrink={maxAreaShrink:0.000} aspectGrow={maxAspectGrowth:0.000}";
            }
        }

        private sealed class FbxResultFixture : IDisposable
        {
            public GameObject sourceAvatar;
            public GameObject clothingRoot;
            public GameObject targetAvatar;
            public GameObject expectedRoot;
            public SkinnedMeshRenderer sourceBody;
            public SkinnedMeshRenderer clothingA;
            public SkinnedMeshRenderer targetBody;
            public SkinnedMeshRenderer expectedClothing;

            public static FbxResultFixture Create(string fixturePath)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fixturePath);
                AssertTrue(prefab != null, $"Missing FBX fixture at '{fixturePath}'.");

                var fixture = new FbxResultFixture
                {
                    sourceAvatar = InstantiateCleanRoot(prefab, "Armature", "Body"),
                    clothingRoot = InstantiateCleanRoot(prefab, "Armature Clothing for A", "Body Clothing for A"),
                    targetAvatar = InstantiateCleanRoot(prefab, "Armature custom edit", "Body custom edit"),
                    expectedRoot = InstantiateCleanRoot(prefab, "Armature Result Clothing for B", "Body Result Clothing for B")
                };

                fixture.sourceAvatar.name = "__ReFitFbx_SourceA";
                fixture.clothingRoot.name = "__ReFitFbx_ClothingA";
                fixture.targetAvatar.name = "__ReFitFbx_TargetB";
                fixture.expectedRoot.name = "__ReFitFbx_ExpectedClothingB";

                fixture.sourceBody = RequireRenderer(fixture.sourceAvatar, "Body");
                fixture.clothingA = RequireRenderer(fixture.clothingRoot, "Body Clothing for A");
                fixture.targetBody = RequireRenderer(fixture.targetAvatar, "Body custom edit");
                fixture.expectedClothing = RequireRenderer(fixture.expectedRoot, "Body Result Clothing for B");
                return fixture;
            }

            private static GameObject InstantiateCleanRoot(GameObject prefab, params string[] keepChildren)
            {
                var root = Object.Instantiate(prefab);
                MarkHideAndDontSave(root);
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    var child = root.transform.GetChild(i);
                    bool keep = false;
                    for (int k = 0; k < keepChildren.Length; k++)
                    {
                        if (child.name == keepChildren[k])
                        {
                            keep = true;
                            break;
                        }
                    }

                    if (!keep)
                        Object.DestroyImmediate(child.gameObject);
                }

                return root;
            }

            private static void MarkHideAndDontSave(GameObject root)
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                    transforms[i].gameObject.hideFlags = HideFlags.HideAndDontSave;
            }

            private static SkinnedMeshRenderer RequireRenderer(GameObject root, string rendererName)
            {
                var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i].name == rendererName)
                        return renderers[i];
                throw new Exception($"Could not find renderer '{rendererName}' under '{root.name}'.");
            }

            public void Dispose()
            {
                if (sourceAvatar != null) Object.DestroyImmediate(sourceAvatar);
                if (clothingRoot != null) Object.DestroyImmediate(clothingRoot);
                if (targetAvatar != null) Object.DestroyImmediate(targetAvatar);
                if (expectedRoot != null) Object.DestroyImmediate(expectedRoot);
                sourceAvatar = null;
                clothingRoot = null;
                targetAvatar = null;
                expectedRoot = null;
            }
        }

        private sealed class ReFitTestFixture : IDisposable
        {
            public SkinnedSample source;
            public SkinnedSample target;
            public SkinnedSample sourceSpaceAccessory;
            public SkinnedSample targetSpaceAccessory;

            public static ReFitTestFixture Create()
            {
                var sourcePose = RigPose.Source();
                var targetPose = RigPose.Target();

                return new ReFitTestFixture
                {
                    source = SkinnedSample.CreateAvatar(
                        "__ReFitTest_SourceA", "Body", 9, 7, 0f, sourcePose, SourceBodyWeight, false),
                    target = SkinnedSample.CreateAvatar(
                        "__ReFitTest_TargetB", "Body", 17, 13, 0f, targetPose, TargetBodyWeight, true),
                    sourceSpaceAccessory = SkinnedSample.CreateAvatar(
                        "__ReFitTest_SourceSpaceHoodie", "Hoodie", 9, 7, 0.035f, sourcePose, SourceAccessoryWeight, false),
                    targetSpaceAccessory = SkinnedSample.CreateAvatar(
                        "__ReFitTest_TargetSpaceHoodie", "Hoodie", 9, 7, 0.035f, targetPose, TargetAccessoryWeight, false)
                };
            }

            public void Dispose()
            {
                sourceSpaceAccessory?.Dispose();
                targetSpaceAccessory?.Dispose();
                source?.Dispose();
                target?.Dispose();
            }
        }

        private sealed class SkinnedSample : IDisposable
        {
            public GameObject root;
            public SkinnedMeshRenderer renderer;
            public Mesh mesh;
            public Transform[] bones;
            public Transform hips => bones[(int)RigBone.Hips];
            public Transform chest => bones[(int)RigBone.Chest];

            public static SkinnedSample CreateAvatar(
                string rootName,
                string rendererName,
                int columns,
                int rows,
                float zOffset,
                RigPose pose,
                Func<Vector3, BoneWeight> weightFactory,
                bool includeMuscleShape)
            {
                var root = new GameObject(rootName);
                root.hideFlags = HideFlags.HideAndDontSave;
                var bones = CreateBones(root.transform, pose);

                var rendererObject = new GameObject(rendererName);
                rendererObject.hideFlags = HideFlags.HideAndDontSave;
                rendererObject.transform.SetParent(root.transform, false);
                var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();

                var mesh = CreateGridMesh($"{rendererName}_{columns}x{rows}", columns, rows, zOffset, weightFactory, includeMuscleShape);
                renderer.sharedMesh = mesh;
                renderer.bones = bones;
                renderer.rootBone = bones[(int)RigBone.Hips];
                mesh.bindposes = BuildBindposes(renderer.transform, bones);
                renderer.localBounds = mesh.bounds;
                renderer.updateWhenOffscreen = true;

                return new SkinnedSample
                {
                    root = root,
                    renderer = renderer,
                    mesh = mesh,
                    bones = bones
                };
            }

            public void Dispose()
            {
                if (root != null) Object.DestroyImmediate(root);
                if (mesh != null) Object.DestroyImmediate(mesh);
                root = null;
                renderer = null;
                mesh = null;
                bones = null;
            }
        }

        private enum RigBone
        {
            Hips = 0,
            Spine = 1,
            Chest = 2,
            Head = 3,
            LeftUpperArm = 4,
            RightUpperArm = 5
        }

        private struct RigPose
        {
            public Vector3 hips;
            public Vector3 spine;
            public Vector3 chest;
            public Vector3 head;
            public Vector3 leftUpperArm;
            public Vector3 rightUpperArm;

            public static RigPose Source()
            {
                return new RigPose
                {
                    hips = new Vector3(0f, 0f, 0f),
                    spine = new Vector3(0f, 0.45f, 0f),
                    chest = new Vector3(0f, 1.0f, 0f),
                    head = new Vector3(0f, 1.65f, 0f),
                    leftUpperArm = new Vector3(-0.72f, 1.18f, 0f),
                    rightUpperArm = new Vector3(0.72f, 1.18f, 0f)
                };
            }

            public static RigPose Target()
            {
                return new RigPose
                {
                    hips = new Vector3(0f, 0f, 0f),
                    spine = new Vector3(0f, 0.53f, 0.025f),
                    chest = new Vector3(0.035f, 1.08f, -0.035f),
                    head = new Vector3(0f, 1.65f, 0f),
                    leftUpperArm = new Vector3(-0.86f, 1.29f, 0.015f),
                    rightUpperArm = new Vector3(0.82f, 1.12f, -0.02f)
                };
            }
        }

        private static Transform[] CreateBones(Transform root, RigPose pose)
        {
            var bones = new Transform[6];
            bones[(int)RigBone.Hips] = CreateBone("Hips", root, pose.hips);
            bones[(int)RigBone.Spine] = CreateBone("Spine", bones[(int)RigBone.Hips], pose.spine);
            bones[(int)RigBone.Chest] = CreateBone("Chest", bones[(int)RigBone.Spine], pose.chest);
            bones[(int)RigBone.Head] = CreateBone("Head", bones[(int)RigBone.Chest], pose.head);
            bones[(int)RigBone.LeftUpperArm] = CreateBone("LeftUpperArm", bones[(int)RigBone.Chest], pose.leftUpperArm);
            bones[(int)RigBone.RightUpperArm] = CreateBone("RightUpperArm", bones[(int)RigBone.Chest], pose.rightUpperArm);
            return bones;
        }

        private static Transform CreateBone(string name, Transform parent, Vector3 worldPosition)
        {
            var bone = new GameObject(name).transform;
            bone.gameObject.hideFlags = HideFlags.HideAndDontSave;
            bone.SetParent(parent, true);
            bone.position = worldPosition;
            bone.rotation = Quaternion.identity;
            bone.localScale = Vector3.one;
            return bone;
        }

        private static Matrix4x4[] BuildBindposes(Transform rendererTransform, Transform[] bones)
        {
            var bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                bindposes[i] = bones[i].worldToLocalMatrix * rendererTransform.localToWorldMatrix;
            return bindposes;
        }

        private static Mesh CreateGridMesh(
            string name,
            int columns,
            int rows,
            float zOffset,
            Func<Vector3, BoneWeight> weightFactory,
            bool includeMuscleShape)
        {
            var vertices = new Vector3[columns * rows];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var weights = new BoneWeight[vertices.Length];

            int index = 0;
            for (int y = 0; y < rows; y++)
            {
                float fy = rows == 1 ? 0f : (float)y / (rows - 1);
                for (int x = 0; x < columns; x++)
                {
                    float fx = columns == 1 ? 0f : (float)x / (columns - 1);
                    var vertex = new Vector3(Mathf.Lerp(-1.2f, 1.2f, fx), Mathf.Lerp(0f, 1.6f, fy), zOffset);
                    vertices[index] = vertex;
                    normals[index] = Vector3.forward;
                    uvs[index] = new Vector2(fx, fy);
                    weights[index] = weightFactory(vertex);
                    index++;
                }
            }

            var triangles = new List<int>((columns - 1) * (rows - 1) * 6);
            for (int y = 0; y < rows - 1; y++)
            {
                for (int x = 0; x < columns - 1; x++)
                {
                    int a = y * columns + x;
                    int b = a + 1;
                    int c = (y + 1) * columns + x;
                    int d = c + 1;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(c);
                }
            }

            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles.ToArray(),
                boneWeights = weights
            };

            if (includeMuscleShape)
            {
                var deltas = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    deltas[i] = MuscleDelta(vertices[i]);
                mesh.AddBlendShapeFrame(BodyShapeName, 100f, deltas, null, null);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 MuscleDelta(Vector3 vertex)
        {
            var delta = Vector3.zero;

            if (Mathf.Abs(vertex.x) <= 0.5f && vertex.y >= 0.75f && vertex.y <= 1.35f)
                delta += new Vector3(Mathf.Sign(vertex.x) * 0.015f, 0f, 0.1f);

            if (Mathf.Abs(vertex.x) >= 0.68f && vertex.y >= 0.9f && vertex.y <= 1.55f)
                delta += new Vector3(Mathf.Sign(vertex.x) * 0.04f, 0f, 0.055f);

            return delta;
        }

        private static BoneWeight SourceBodyWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.85f, (int)RigBone.Chest, 0.15f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.8f, (int)RigBone.Spine, 0.2f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.7f, (int)RigBone.Hips, 0.3f);
            return MakeWeight((int)RigBone.Chest, 0.85f, (int)RigBone.Spine, 0.15f);
        }

        private static BoneWeight TargetBodyWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.55f, (int)RigBone.Chest, 0.35f, (int)RigBone.Spine, 0.1f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.55f, (int)RigBone.Spine, 0.45f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.5f, (int)RigBone.Chest, 0.3f, (int)RigBone.Hips, 0.2f);
            return MakeWeight((int)RigBone.Chest, 0.55f, (int)RigBone.Spine, 0.4f, (int)RigBone.Hips, 0.05f);
        }

        private static BoneWeight SourceAccessoryWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.7f, (int)RigBone.Chest, 0.3f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.6f, (int)RigBone.Spine, 0.4f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.6f, (int)RigBone.Chest, 0.3f, (int)RigBone.Hips, 0.1f);
            return MakeWeight((int)RigBone.Chest, 0.7f, (int)RigBone.Spine, 0.3f);
        }

        private static BoneWeight TargetAccessoryWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.65f, (int)RigBone.Chest, 0.25f, (int)RigBone.Spine, 0.1f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.65f, (int)RigBone.Spine, 0.35f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.55f, (int)RigBone.Chest, 0.35f, (int)RigBone.Hips, 0.1f);
            return MakeWeight((int)RigBone.Chest, 0.65f, (int)RigBone.Spine, 0.35f);
        }

        private static int ArmBone(Vector3 vertex)
        {
            return vertex.x < 0f ? (int)RigBone.LeftUpperArm : (int)RigBone.RightUpperArm;
        }

        private static BoneWeight MakeWeight(
            int bone0,
            float weight0,
            int bone1 = 0,
            float weight1 = 0f,
            int bone2 = 0,
            float weight2 = 0f,
            int bone3 = 0,
            float weight3 = 0f)
        {
            float total = weight0 + weight1 + weight2 + weight3;
            if (total <= 1e-6f)
                return new BoneWeight { boneIndex0 = (int)RigBone.Hips, weight0 = 1f };

            return new BoneWeight
            {
                boneIndex0 = bone0,
                weight0 = weight0 / total,
                boneIndex1 = bone1,
                weight1 = weight1 / total,
                boneIndex2 = bone2,
                weight2 = weight2 / total,
                boneIndex3 = bone3,
                weight3 = weight3 / total
            };
        }
    }
}
