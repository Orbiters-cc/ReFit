using System;
using System.Collections.Generic;
using System.Reflection;
using Orbiters.XRayGizmos.Editor;
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
        private const string LocalizedPeakShapeName = "LocalizedRearDelt";
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
                    "Scale matching ignores skeleton pose outliers when body surfaces match",
                    ScaleMatching_EqualBodySurfacesIgnoreSkeletonPoseOutliers);
                RunCase(failures,
                    "Active non-target body blendshapes do not affect primary refit",
                    MeshAndBlendshape_ActiveNonTargetBodyShapes_DoNotAffectPrimaryRefit);
                RunCase(failures,
                    "Primary smoothing does not smooth transferred blendshapes",
                    TransferredBlendshapeSmoothing_IsIndependentFromPrimarySmoothing);
                RunCase(failures,
                    "Transferred blendshape smoothing preserves explicit peak control",
                    TransferredBlendshapeSmoothing_PreservesLocalizedPeaksWhenDisabled);
                RunCase(failures,
                    "Transferred blendshape keeps clothing outside shaped skin",
                    TransferredBlendshape_PreservesSignedSkinClearance);
                RunCase(failures,
                    "Clearance correction adaptively tightens expanded areas",
                    ClearanceCorrection_AdaptivelyTightensExpandedAreas);
                RunCase(failures,
                    "Clearance correction strong settings pull expanded areas closer",
                    ClearanceCorrection_StrongSettingsCanPullExpandedAreasMuchCloser);
                RunCase(failures,
                    "High tightness keeps upper-body garment hem deltas bounded",
                    ClearanceCorrection_HighTightnessKeepsUpperBodyHemBounded);
                RunCase(failures,
                    "Transferred blendshape stabilizes partially moved detached lace clusters",
                    TransferredBlendshape_StabilizesPartiallyMovedDetachedLaceClusters);
                RunCase(failures,
                    "Clearance correction propagates to disconnected garment islands",
                    ClearanceCorrection_PropagatesToDisconnectedGarmentIslands);
                RunCase(failures,
                    "Clearance correction support-propagates to ineligible detached islands",
                    ClearanceCorrection_SupportPropagatesToIneligibleDetachedIslands);
                RunCase(failures,
                    "Clearance correction keeps transferred cap after island propagation",
                    ClearanceCorrection_PostPropagationGuardRespectsTransferredTotalCap);
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
                    "Armature replacement adds target-derived non-deforming leaf helpers",
                    ArmatureReplacement_TargetChildCreatesLeafTailHelper);
                RunCase(failures,
                    "Armature leaf helpers use the next anatomical segment before deeper descendants",
                    ArmatureReplacement_LeafTailPrefersShinOverFoot);
                RunCase(failures,
                    "Armature replacement materializes child-first bone plans without hierarchy drift",
                    ArmatureReplacement_ChildFirstPlan_MaterializesWithoutDrift);
                RunCase(failures,
                    "Weight transfer preserves mapped original weights when projections cross regions",
                    WeightTransfer_PreservesMappedOriginalWeightsWhenProjectionCrossesRegions);
                RunCase(failures,
                    "Target-space accessory keeps source bone regions for projection filtering",
                    ProjectionDebug_TargetSpaceAccessoryClassifiesAssetRegions);
                RunCase(failures,
                    "Projection debug captures binding and weight decision data",
                    ProjectionDebug_CapturesBindingAndWeightDecisionData);
                RunCase(failures,
                    "Target surface chaining prefers near equivalent hits before normal filtering",
                    SurfaceBinding_TargetChainPrefersNearEquivalentHitBeforeNormalFilter);
                RunCase(failures,
                    "Wizard debug mode captures projection data even when rays are hidden",
                    ReFitWizard_DebugModeCapturesProjectionDataWhenGizmoHidden);
                RunCase(failures,
                    "Debug snapshots use independent baked mesh instances",
                    DebugSession_CapturesIndependentSnapshotMeshes);
                RunCase(failures,
                    "XRay extra gizmo registry exposes external toggles",
                    XRayExtraGizmoRegistry_RegistersAndTogglesExternalGizmo);
                RunCase(failures,
                    "Gravity detector recognizes body clothing candidates",
                    GravityRelaxation_DetectsBodyClothingCandidate);
                RunCase(failures,
                    "Gravity relaxation propagates upper torso clearance downward",
                    GravityRelaxation_PropagatesUpperTorsoClearanceDownward);
                RunCase(failures,
                    "Gravity preview uses normal weight with stronger baked deltas",
                    GravityPreview_UsesNormalWeightWithStrongerBakedDeltas);
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

        private static void GravityRelaxation_DetectsBodyClothingCandidate()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var candidate = ReFitGravityRelaxation.DetectCandidate(
                    fixture.targetSpaceAccessory.renderer,
                    fixture.target.root);
                AssertTrue(candidate.isCandidate,
                    $"Expected the target-space Hoodie renderer to be detected as body clothing. Score={candidate.score:0.###}");
                AssertGreater(candidate.score, 0.44f, "Body clothing candidate score was too low.");
            }
        }

        private static void GravityRelaxation_PropagatesUpperTorsoClearanceDownward()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var renderer = fixture.targetSpaceAccessory.renderer;
                AddSyntheticChestExpansion(renderer.sharedMesh, "refit");
                renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("refit"), 100f);

                var settings = ReFitGravityRelaxation.DefaultSettings;
                settings.minimumMeaningfulDelta = 0.0005f;
                var preview = ReFitGravityRelaxation.GeneratePreview(
                    renderer,
                    fixture.target.renderer,
                    fixture.target.root,
                    new[] { "refit" },
                    settings,
                    new ReFitReport());

                AssertTrue(preview != null && preview.frames != null && preview.frames.Length == 1,
                    "Gravity relaxation did not produce one preview frame for the synthetic refit shape.");

                var deltas = preview.frames[0].localDeltas;
                float lowerTorsoAverageZ = AverageLocalDeltaZ(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) <= 0.5f && v.y >= 0.45f && v.y <= 0.85f);
                float upperAnchorAverageZ = AverageLocalDeltaZ(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) <= 0.5f && v.y >= 1.05f && v.y <= 1.35f);
                float sleeveMax = MaxLocalDeltaMagnitude(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) >= 0.65f && v.y >= 0.85f && v.y <= 1.45f);
                float hoodMax = MaxLocalDeltaMagnitude(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) <= 0.55f && v.y >= 1.45f);
                float lowerTorsoMaxAbsX = MaxAbsLocalDeltaComponent(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) <= 0.5f && v.y >= 0.45f && v.y <= 0.85f,
                    delta => delta.x);
                float lowerTorsoMaxAbsY = MaxAbsLocalDeltaComponent(renderer.sharedMesh, deltas,
                    v => Mathf.Abs(v.x) <= 0.5f && v.y >= 0.45f && v.y <= 0.85f,
                    delta => delta.y);

                AssertGreater(lowerTorsoAverageZ, 0.018f,
                    "Gravity relaxation should push the lower torso hoodie surface forward.");
                AssertLessOrEqual(upperAnchorAverageZ, 0.003f,
                    "Gravity relaxation should not double-push the upper torso anchor area.");
                AssertLessOrEqual(sleeveMax, 0.001f,
                    "Gravity relaxation should not inflate sleeve or arm vertices.");
                AssertLessOrEqual(hoodMax, 0.001f,
                    "Gravity relaxation should not inflate high hood/head-area vertices.");
                AssertLessOrEqual(lowerTorsoMaxAbsX, 0.001f,
                    "Gravity relaxation should not add noisy sideways lower-torso deltas.");
                AssertLessOrEqual(lowerTorsoMaxAbsY, 0.001f,
                    "Gravity relaxation should not add noisy vertical lower-torso deltas.");
            }
        }

        private static void GravityPreview_UsesNormalWeightWithStrongerBakedDeltas()
        {
            AssertLessOrEqual(ReFitGravityPreviewService.MaximumGravityWeight, 100f,
                "Gravity preview should keep standard blendshape weight semantics.");
            AssertGreater(ReFitGravityRelaxation.DefaultSettings.bakedStrengthMultiplier, 2.99f,
                "Gravity relaxation should bake a stronger full-weight shape instead of relying on 200% preview weight.");
        }

        private static void RunFbxFixtureAgainstAuthoredResult(string fixturePath, string label)
        {
            using (var fixture = FbxResultFixture.Create(fixturePath))
            {
                bool asymmetricAuthoredCoverage =
                    fixturePath == FbxFixtureV1Path || fixturePath == FbxFixtureV2DifferentArmaturePath;
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
                        primarySmoothingIterations = ReFitSettings.DefaultPrimarySmoothingIterations,
                        primarySmoothingStrength = ReFitSettings.DefaultPrimarySmoothingStrength,
                        transferredBlendshapeSmoothingIterations = 0,
                        transferredBlendshapeSmoothingStrength = 0f,
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
                    {
                        AssertTrue(!RendererHasBone(generated, "middle arm"),
                            "The inserted clothing-only 'middle arm' bone should resolve onto the target armature, not be preserved as an extra deforming bone.");
                        AssertFbxPrimaryDeltaScale(comp, label, 0.08f);
                    }

                    SetBlendShapeWeight(generated, FbxShapeName, 0f);
                    SetBlendShapeWeight(fixture.expectedClothing, FbxShapeName, 0f);
                    AssertRendererBoundsCompatible($"{label} base", generated, fixture.expectedClothing);
                    var baseForward = MeasureSurfaceDistance(generated, fixture.expectedClothing);
                    var baseReverse = MeasureSurfaceDistance(fixture.expectedClothing, generated);
                    var baseQuality = MeasureTriangleQuality(originalClothing, generated);
                    Debug.Log($"[ReFit Tests] {label} base generated->expected {baseForward}");
                    Debug.Log($"[ReFit Tests] {label} base expected->generated {baseReverse}");
                    Debug.Log($"[ReFit Tests] {label} base triangle quality {baseQuality}");
                    if (asymmetricAuthoredCoverage)
                        AssertFbxSurfaceMetrics($"{label} base generated->expected", baseForward, 0.012f, 0.03f, 0.06f, 0.36f);
                    else
                        AssertFbxSurfaceMetrics($"{label} base generated->expected", baseForward, 0.008f, 0.014f, 0.015f, 0.13f);
                    if (asymmetricAuthoredCoverage)
                        AssertLessOrEqual(baseReverse.average, 0.08f,
                            $"{label} base authored-result coverage drift is too high.");
                    else
                        AssertFbxSurfaceMetrics($"{label} base expected->generated", baseReverse, 0.012f, 0.03f, 0.055f, 0.19f);
                    AssertTriangleQuality($"{label} base", baseQuality);

                    SetBlendShapeWeight(generated, FbxShapeName, 100f);
                    SetBlendShapeWeight(fixture.expectedClothing, FbxShapeName, 100f);
                    AssertRendererBoundsCompatible($"{label} shape", generated, fixture.expectedClothing);
                    var shapeForward = MeasureSurfaceDistance(generated, fixture.expectedClothing);
                    var shapeReverse = MeasureSurfaceDistance(fixture.expectedClothing, generated);
                    var shapeQuality = MeasureTriangleQuality(originalClothing, generated);
                    Debug.Log($"[ReFit Tests] {label} shape generated->expected {shapeForward}");
                    Debug.Log($"[ReFit Tests] {label} shape expected->generated {shapeReverse}");
                    Debug.Log($"[ReFit Tests] {label} shape triangle quality {shapeQuality}");
                    if (asymmetricAuthoredCoverage)
                    {
                        AssertFbxTransferredShapeProfile(label, generated, fixture.expectedClothing);
                        AssertFbxSurfaceMetrics($"{label} shape generated->expected", shapeForward, 0.14f, 0.2f, 0.42f, 0.65f);
                        AssertLessOrEqual(shapeReverse.average, 0.16f,
                            $"{label} shape authored-result coverage drift is too high.");
                    }
                    else
                    {
                        AssertFbxSurfaceMetrics($"{label} shape generated->expected", shapeForward, 0.008f, 0.014f, 0.015f, 0.13f);
                        AssertFbxSurfaceMetrics($"{label} shape expected->generated", shapeReverse, 0.012f, 0.03f, 0.055f, 0.19f);
                    }
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

        private static void ScaleMatching_EqualBodySurfacesIgnoreSkeletonPoseOutliers()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                fixture.target.bones[(int)RigBone.Head].position += new Vector3(0f, 0.55f, 0.05f);

                NewChild(fixture.source.bones[(int)RigBone.LeftUpperArm], "LeftHand").position = new Vector3(-1.35f, 1.25f, 0f);
                NewChild(fixture.source.bones[(int)RigBone.RightUpperArm], "RightHand").position = new Vector3(1.35f, 1.25f, 0f);
                NewChild(fixture.target.bones[(int)RigBone.LeftUpperArm], "LeftHand").position = new Vector3(-0.28f, 1.25f, 0.05f);
                NewChild(fixture.target.bones[(int)RigBone.RightUpperArm], "RightHand").position = new Vector3(0.28f, 1.25f, 0.05f);

                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);

                    float maxPrimaryDrift = MaxBlendShapeMagnitude(comp.mesh, comp.primaryShapeName);
                    Debug.Log($"[ReFit Tests] Primary refit drift with skeleton scale outliers: {maxPrimaryDrift * 1000f:0.###} mm");
                    AssertLessOrEqual(maxPrimaryDrift, PrimaryRefitDriftTolerance,
                        $"Skeleton-only pose/landmark differences must not create visible primary refit drift when the body meshes still overlap. " +
                        $"Max drift was {maxPrimaryDrift * 1000f:0.###} mm.");

                    AssertReportContains(comp.report, "scale-outlier-ignored",
                        "Expected ReFit to log that skeleton scale outliers were ignored in favor of the body surface bounds.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void MeshAndBlendshape_ActiveNonTargetBodyShapes_DoNotAffectPrimaryRefit()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var mesh = fixture.target.renderer.sharedMesh;
                var deltas = new Vector3[mesh.vertexCount];
                for (int i = 0; i < deltas.Length; i++)
                    deltas[i] = new Vector3(0f, 0f, 0.08f);
                mesh.AddBlendShapeFrame("ActiveButNotRequested", 100f, deltas, null, null);
                fixture.target.renderer.SetBlendShapeWeight(mesh.GetBlendShapeIndex("ActiveButNotRequested"), 100f);

                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    float maxPrimaryDrift = MaxBlendShapeMagnitude(comp.mesh, comp.primaryShapeName);
                    Debug.Log($"[ReFit Tests] Primary refit drift with active unrelated target shape: {maxPrimaryDrift * 1000f:0.###} mm");
                    AssertLessOrEqual(maxPrimaryDrift, PrimaryRefitDriftTolerance,
                        $"Current target body blendshape weights must not leak into the default mesh-to-mesh comparison. " +
                        $"Max drift was {maxPrimaryDrift * 1000f:0.###} mm.");
                    AssertTransferredMuscleShape(comp.mesh, SingleSecondaryShape(comp));
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void TransferredBlendshapeSmoothing_IsIndependentFromPrimarySmoothing()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AddLocalizedPeakBlendshape(fixture.target.mesh, LocalizedPeakShapeName);

                var baselineRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                baselineRequest.targetBlendshape = LocalizedPeakShapeName;
                baselineRequest.settings.primarySmoothingIterations = 0;
                baselineRequest.settings.primarySmoothingStrength = 0f;
                baselineRequest.settings.transferredBlendshapeSmoothingIterations = 0;
                baselineRequest.settings.transferredBlendshapeSmoothingStrength = 0f;

                var highPrimaryRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                highPrimaryRequest.targetBlendshape = LocalizedPeakShapeName;
                highPrimaryRequest.settings.primarySmoothingIterations = 8;
                highPrimaryRequest.settings.primarySmoothingStrength = 1f;
                highPrimaryRequest.settings.transferredBlendshapeSmoothingIterations = 0;
                highPrimaryRequest.settings.transferredBlendshapeSmoothingStrength = 0f;

                var baseline = new ReFitEngine().Run(baselineRequest);
                var highPrimary = new ReFitEngine().Run(highPrimaryRequest);
                try
                {
                    AssertComputationSucceeded(baseline);
                    AssertComputationSucceeded(highPrimary);

                    float maxDelta = MaxBlendShapeDifference(
                        baseline.mesh, SingleSecondaryShape(baseline),
                        highPrimary.mesh, SingleSecondaryShape(highPrimary));

                    AssertLessOrEqual(maxDelta, 0.0001f,
                        "Changing primary refit smoothing changed the transferred blendshape even though transferred smoothing was disabled.");
                }
                finally
                {
                    DestroyComputationMesh(baseline);
                    DestroyComputationMesh(highPrimary);
                }
            }
        }

        private static void TransferredBlendshapeSmoothing_PreservesLocalizedPeaksWhenDisabled()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AddLocalizedPeakBlendshape(fixture.target.mesh, LocalizedPeakShapeName);

                var noSmoothRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                noSmoothRequest.targetBlendshape = LocalizedPeakShapeName;
                noSmoothRequest.settings.transferredBlendshapeSmoothingIterations = 0;
                noSmoothRequest.settings.transferredBlendshapeSmoothingStrength = 0f;

                var smoothRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                smoothRequest.targetBlendshape = LocalizedPeakShapeName;
                smoothRequest.settings.transferredBlendshapeSmoothingIterations = 4;
                smoothRequest.settings.transferredBlendshapeSmoothingStrength = 0.6f;

                var noSmooth = new ReFitEngine().Run(noSmoothRequest);
                var smooth = new ReFitEngine().Run(smoothRequest);
                try
                {
                    AssertComputationSucceeded(noSmooth);
                    AssertComputationSucceeded(smooth);

                    float unsmoothedPeak = MaxMagnitudeInRegion(noSmooth.mesh, SingleSecondaryShape(noSmooth), IsLeftUpperSleeve);
                    float smoothedPeak = MaxMagnitudeInRegion(smooth.mesh, SingleSecondaryShape(smooth), IsLeftUpperSleeve);
                    Debug.Log($"[ReFit Tests] Localized transferred peak: unsmoothed={unsmoothedPeak:0.000000} smoothed={smoothedPeak:0.000000}");

                    AssertGreater(unsmoothedPeak, 0.055f,
                        "Disabled transferred smoothing did not preserve the localized body-shape peak.");
                    AssertGreater(unsmoothedPeak - smoothedPeak, 0.008f,
                        "Explicit transferred smoothing did not measurably flatten the localized peak; the test fixture is not exercising the control.");
                }
                finally
                {
                    DestroyComputationMesh(noSmooth);
                    DestroyComputationMesh(smooth);
                }
            }
        }

        private static void TransferredBlendshape_PreservesSignedSkinClearance()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                request.settings.transferredBlendshapeSmoothingIterations = 0;
                request.settings.transferredBlendshapeSmoothingStrength = 0f;

                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);

                    var metrics = MeasureTransferredClearance(fixture, comp, request.settings, null);

                    Debug.Log(
                        $"[ReFit Tests] Signed clearance: tested={metrics.tested}, flips={metrics.signFlips}, " +
                        $"sourceMin={metrics.minSourceClearance:0.000000}, desiredMin={metrics.minDesiredClearance:0.000000}, " +
                        $"shapedMin={metrics.minShapedClearance:0.000000}, maxBelowDesired={metrics.maxBelowDesired:0.000000}, " +
                        $"maxLoss={metrics.maxClearanceLoss:0.000000}");

                    AssertGreater(metrics.tested, 20,
                        "Signed-clearance test did not find enough high-confidence clothing/body projection pairs.");
                    AssertTrue(metrics.signFlips == 0,
                        $"Transferred blendshape moved {metrics.signFlips} clothing vertices under the shaped target skin.");
                    AssertGreater(metrics.minShapedClearance, request.settings.clearanceMinimumSafetyDistance,
                        "Transferred blendshape did not keep the clothing safely above the shaped target skin.");
                    AssertLessOrEqual(metrics.maxBelowDesired, 0.006f,
                        "Transferred blendshape fell too far below the adaptive clearance target.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void ClearanceCorrection_AdaptivelyTightensExpandedAreas()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var disabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                disabledRequest.settings.enableClearanceCorrection = false;
                var enabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                enabledRequest.settings.enableClearanceCorrection = true;

                var disabled = new ReFitEngine().Run(disabledRequest);
                var enabled = new ReFitEngine().Run(enabledRequest);
                try
                {
                    AssertComputationSucceeded(disabled);
                    AssertComputationSucceeded(enabled);
                    AssertTrue(enabled.clearanceCorrectionStats != null && enabled.clearanceCorrectionStats.HasCorrections,
                        "Clearance correction did not report any correction on the expanded-body fixture.");

                    Func<Vector3, bool> chest = v => Mathf.Abs(v.x) <= 0.5f && v.y >= 0.75f && v.y <= 1.35f;
                    var disabledMetrics = MeasureTransferredClearance(fixture, disabled, enabledRequest.settings, chest);
                    var enabledMetrics = MeasureTransferredClearance(fixture, enabled, enabledRequest.settings, chest);

                    Debug.Log(
                        $"[ReFit Tests] Adaptive clearance: disabledAvg={disabledMetrics.averageShapedClearance:0.000000}, " +
                        $"enabledAvg={enabledMetrics.averageShapedClearance:0.000000}, " +
                        $"enabledBelowDesired={enabledMetrics.maxBelowDesired:0.000000}, " +
                        $"stats={enabled.clearanceCorrectionStats.Summary("aggregate")}");

                    AssertGreater(disabledMetrics.tested, 4,
                        "Adaptive clearance test did not sample enough expanded chest vertices.");
                    AssertGreater(disabledMetrics.averageShapedClearance - enabledMetrics.averageShapedClearance, 0.002f,
                        "Clearance correction did not make expanded chest clothing measurably tighter.");
                    AssertGreater(enabledMetrics.minShapedClearance, enabledRequest.settings.clearanceMinimumSafetyDistance,
                        "Adaptive clearance correction pushed clothing too close to or under the body.");
                    AssertLessOrEqual(enabledMetrics.maxBelowDesired, 0.006f,
                        "Adaptive clearance correction did not stay close enough to its desired clearance.");
                }
                finally
                {
                    DestroyComputationMesh(disabled);
                    DestroyComputationMesh(enabled);
                }
            }
        }

        private static void ClearanceCorrection_StrongSettingsCanPullExpandedAreasMuchCloser()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var defaultRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                defaultRequest.settings.enableClearanceCorrection = true;

                var strongRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                strongRequest.settings.enableClearanceCorrection = true;
                strongRequest.settings.clearanceTightnessFactor = 0f;
                strongRequest.settings.clearanceMinimumSafetyDistance = 0.001f;
                strongRequest.settings.clearanceMaxInwardCorrection = 0.2f;
                strongRequest.settings.clearanceInwardStrength = 1f;
                strongRequest.settings.clearanceExpansionStart = 0f;
                strongRequest.settings.clearanceExpansionFull = 0.01f;
                strongRequest.settings.clearanceSmoothingIterations = 0;
                strongRequest.settings.clearanceSmoothingStrength = 0f;

                var defaultComp = new ReFitEngine().Run(defaultRequest);
                var strongComp = new ReFitEngine().Run(strongRequest);
                try
                {
                    AssertComputationSucceeded(defaultComp);
                    AssertComputationSucceeded(strongComp);

                    Func<Vector3, bool> chest = v => Mathf.Abs(v.x) <= 0.5f && v.y >= 0.75f && v.y <= 1.35f;
                    var defaultMetrics = MeasureTransferredClearance(fixture, defaultComp, defaultRequest.settings, chest);
                    var strongMetrics = MeasureTransferredClearance(fixture, strongComp, strongRequest.settings, chest);

                    Debug.Log(
                        $"[ReFit Tests] Strong clearance: defaultAvg={defaultMetrics.averageShapedClearance:0.000000}, " +
                        $"strongAvg={strongMetrics.averageShapedClearance:0.000000}, " +
                        $"strongMin={strongMetrics.minShapedClearance:0.000000}, strongDesiredMin={strongMetrics.minDesiredClearance:0.000000}");

                    AssertGreater(defaultMetrics.tested, 4,
                        "Strong clearance test did not sample enough expanded chest vertices.");
                    AssertGreater(defaultMetrics.averageShapedClearance - strongMetrics.averageShapedClearance, 0.004f,
                        "Strong clearance settings did not pull expanded chest clothing materially closer than the defaults.");
                    AssertGreater(strongMetrics.minShapedClearance, 0f,
                        "Strong clearance settings pushed clothing under the body.");
                }
                finally
                {
                    DestroyComputationMesh(defaultComp);
                    DestroyComputationMesh(strongComp);
                }
            }
        }

        private static void ClearanceCorrection_HighTightnessKeepsUpperBodyHemBounded()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                ApplyHighTightnessSettings(request.settings);

                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(comp.debugPrimaryRawLocalDeltas != null,
                        "High-tightness run did not keep raw primary debug deltas.");
                    AssertTrue(comp.debugSecondaryRawLocalDeltas != null && comp.debugSecondaryRawLocalDeltas.Length == 1 &&
                               comp.debugSecondaryRawLocalDeltas[0] != null,
                        "High-tightness run did not keep raw transferred debug deltas.");

                    var primary = GetBlendShapeDeltas(comp.mesh, comp.primaryShapeName);
                    var secondary = GetBlendShapeDeltas(comp.mesh, SingleSecondaryShape(comp));
                    var rawSecondary = comp.debugSecondaryRawLocalDeltas[0];
                    Func<Vector3, bool> lowerHem = v => Mathf.Abs(v.x) <= 0.55f && v.y <= 0.45f;
                    Func<Vector3, bool> torso = v => Mathf.Abs(v.x) <= 0.45f && v.y >= 0.75f && v.y <= 1.35f;

                    float primaryHemMax = MaxLocalDeltaMagnitude(comp.mesh, primary, lowerHem);
                    float secondaryHemMax = MaxLocalDeltaMagnitude(comp.mesh, secondary, lowerHem);
                    float boundaryCorrectionMax = MaxBoundaryCorrectionMagnitude(comp.mesh, rawSecondary, secondary);
                    float torsoCorrectionAverage = AverageCorrectionMagnitude(comp.mesh, rawSecondary, secondary, torso);

                    Debug.Log(
                        $"[ReFit Tests] High-tightness hem: primaryMax={primaryHemMax * 1000f:0.###}mm, " +
                        $"secondaryMax={secondaryHemMax * 1000f:0.###}mm, " +
                        $"boundaryCorrectionMax={boundaryCorrectionMax * 1000f:0.###}mm, " +
                        $"torsoCorrectionAvg={torsoCorrectionAverage * 1000f:0.###}mm, " +
                        $"stats={comp.clearanceCorrectionStats?.Summary("aggregate")}");

                    AssertLessOrEqual(primaryHemMax, 0.055f,
                        "High tightness created an excessive primary refit delta on the lower hem.");
                    AssertLessOrEqual(secondaryHemMax, 0.055f,
                        "High tightness created an excessive transferred-shape delta on the lower hem.");
                    AssertLessOrEqual(boundaryCorrectionMax, 0.012f,
                        "High tightness applied a large transferred clearance correction on an open boundary vertex.");
                    AssertGreater(torsoCorrectionAverage, 0.004f,
                        "High tightness did not apply a measurable transferred clearance correction on the torso.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void TransferredBlendshape_StabilizesPartiallyMovedDetachedLaceClusters()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AddPartialTransferLaceCluster(fixture.sourceSpaceAccessory.mesh, fixture.sourceSpaceAccessory.renderer);

                var disabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                disabledRequest.settings.enableClearanceCorrection = false;
                disabledRequest.settings.stabilizeDetachedTransferredComponents = false;

                var enabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                enabledRequest.settings.enableClearanceCorrection = false;
                enabledRequest.settings.stabilizeDetachedTransferredComponents = true;

                var disabled = new ReFitEngine().Run(disabledRequest);
                var enabled = new ReFitEngine().Run(enabledRequest);
                try
                {
                    AssertComputationSucceeded(disabled);
                    AssertComputationSucceeded(enabled);
                    AssertTrue(disabled.debugSecondaryRawLocalDeltas != null &&
                               disabled.debugSecondaryRawLocalDeltas.Length == 1 &&
                               disabled.debugSecondaryRawLocalDeltas[0] != null,
                        "Disabled detached-lace fixture did not expose raw transferred deltas.");
                    AssertTrue(enabled.debugSecondaryRawLocalDeltas != null &&
                               enabled.debugSecondaryRawLocalDeltas.Length == 1 &&
                               enabled.debugSecondaryRawLocalDeltas[0] != null,
                        "Enabled detached-lace fixture did not expose raw transferred deltas.");

                    var disabledRaw = disabled.debugSecondaryRawLocalDeltas[0];
                    var enabledRaw = enabled.debugSecondaryRawLocalDeltas[0];
                    Func<Vector3, bool> lace = v =>
                        Mathf.Abs(v.x) <= 0.13f && v.y >= 0.4f && v.y <= 1.32f && v.z >= 0.09f && v.z <= 0.13f;
                    Func<Vector3, bool> lowerLace = v => lace(v) && v.y <= 0.72f;
                    Func<Vector3, bool> laceSupport = v =>
                        !lace(v) && Mathf.Abs(v.x) <= 0.48f && v.y >= 0.36f && v.y <= 1.34f && v.z <= 0.085f;

                    var disabledArtifacts = MeasureShapeArtifacts(
                        disabled.mesh, disabled.debugPrimaryRawLocalDeltas, disabledRaw, lace);
                    var enabledArtifacts = MeasureShapeArtifacts(
                        enabled.mesh, enabled.debugPrimaryRawLocalDeltas, enabledRaw, lace);
                    var disabledSurface = MeasureSurfaceFollowRelation(
                        disabled.mesh, disabled.debugPrimaryRawLocalDeltas, disabledRaw, lace, laceSupport);
                    var enabledSurface = MeasureSurfaceFollowRelation(
                        enabled.mesh, enabled.debugPrimaryRawLocalDeltas, enabledRaw, lace, laceSupport);
                    float disabledLowerZ = AverageLocalDeltaZ(disabled.mesh, disabledRaw, lowerLace);
                    float enabledLowerZ = AverageLocalDeltaZ(enabled.mesh, enabledRaw, lowerLace);

                    Debug.Log(
                        $"[ReFit Tests] Detached transferred lace coherence: " +
                        $"disabledLowerZ={disabledLowerZ * 1000f:0.###}mm, " +
                        $"enabledLowerZ={enabledLowerZ * 1000f:0.###}mm, " +
                        $"disabledJump={disabledArtifacts.maxCorrectionJump * 1000f:0.###}mm, " +
                        $"enabledJump={enabledArtifacts.maxCorrectionJump * 1000f:0.###}mm, " +
                        $"disabledEdgeRatio={disabledArtifacts.maxEdgeRatio:0.###}, " +
                        $"enabledEdgeRatio={enabledArtifacts.maxEdgeRatio:0.###}, " +
                        $"disabledSurfaceResidual95={disabledSurface.p95SupportResidual * 1000f:0.###}mm, " +
                        $"enabledSurfaceResidual95={enabledSurface.p95SupportResidual * 1000f:0.###}mm, " +
                        $"disabledDistanceDrift95={disabledSurface.p95DistanceDrift * 1000f:0.###}mm, " +
                        $"enabledDistanceDrift95={enabledSurface.p95DistanceDrift * 1000f:0.###}mm");

                    AssertGreater(disabledArtifacts.maxEdgeRatio, 1.2f,
                        "Detached lace fixture did not reproduce the raw partial-transfer edge stretch.");
                    AssertGreater(disabledArtifacts.maxCorrectionJump, 0.02f,
                        "Detached lace fixture did not reproduce the raw partial-transfer delta discontinuity.");
                    AssertGreater(disabledSurface.p95SupportResidual, 0.018f,
                        "Detached lace fixture did not reproduce a measurable lace-to-shell transfer mismatch.");
                    AssertGreater(enabledLowerZ - disabledLowerZ, 0.018f,
                        "Detached lace coherence did not move the lower lace section with the supported section.");
                    AssertGreater(enabledArtifacts.minCorrection, 0.002f,
                        "Detached lace coherence left a near-static section in the lace cluster.");
                    AssertLessOrEqual(enabledSurface.p95SupportResidual, disabledSurface.p95SupportResidual * 0.72f,
                        "Detached lace coherence did not make the whole lace follow the nearby hoodie surface.");
                    AssertLessOrEqual(enabledSurface.p95DistanceDrift, 0.012f,
                        "Detached lace coherence changed the lace-to-hoodie surface distance too much.");
                    AssertLessOrEqual(enabledArtifacts.maxCorrectionJump, 0.012f,
                        "Detached lace coherence left a sharp transferred-delta jump in the lace cluster.");
                    AssertLessOrEqual(enabledArtifacts.maxEdgeRatio, 1.2f,
                        "Detached lace coherence left visible stretch/collapse in the raw transferred lace shape.");
                    AssertReportContains(enabled.report, "detached-component-coherence",
                        "Detached lace coherence did not emit its stabilization diagnostic.");
                }
                finally
                {
                    DestroyComputationMesh(disabled);
                    DestroyComputationMesh(enabled);
                }
            }
        }

        private static void ClearanceCorrection_PropagatesToDisconnectedGarmentIslands()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AddDetachedChestIsland(fixture.sourceSpaceAccessory.mesh, fixture.sourceSpaceAccessory.renderer);

                var disabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                ApplyHighTightnessSettings(disabledRequest.settings);
                disabledRequest.settings.clearancePropagateDisconnectedIslands = false;
                disabledRequest.settings.clearanceOpenBoundaryCorrectionScale = 0f;

                var enabledRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                ApplyHighTightnessSettings(enabledRequest.settings);
                enabledRequest.settings.clearancePropagateDisconnectedIslands = true;
                enabledRequest.settings.clearanceOpenBoundaryCorrectionScale = 0f;
                enabledRequest.settings.clearanceIslandPropagationStrength = 1f;
                enabledRequest.settings.clearanceIslandPropagationSearchDistance = 0.14f;
                enabledRequest.settings.clearanceMaxIslandPropagationCorrection = 0.08f;
                enabledRequest.settings.clearanceIslandPropagationMinDonorCorrection = 0.0005f;

                var disabled = new ReFitEngine().Run(disabledRequest);
                var enabled = new ReFitEngine().Run(enabledRequest);
                try
                {
                    AssertComputationSucceeded(disabled);
                    AssertComputationSucceeded(enabled);
                    AssertTrue(enabled.clearanceCorrectionStats != null &&
                               enabled.clearanceCorrectionStats.propagatedIslandGroups > 0,
                        "Disconnected island propagation did not report any propagated groups.");

                    var disabledSecondary = GetBlendShapeDeltas(disabled.mesh, SingleSecondaryShape(disabled));
                    var enabledSecondary = GetBlendShapeDeltas(enabled.mesh, SingleSecondaryShape(enabled));
                    var disabledRaw = disabled.debugSecondaryRawLocalDeltas[0];
                    var enabledRaw = enabled.debugSecondaryRawLocalDeltas[0];
                    Func<Vector3, bool> detachedIsland = v =>
                        Mathf.Abs(v.x) <= 0.24f && v.y >= 0.9f && v.y <= 1.3f && v.z >= 0.085f;
                    Func<Vector3, bool> torsoShell = v =>
                        Mathf.Abs(v.x) <= 0.45f && v.y >= 0.85f && v.y <= 1.35f && v.z < 0.06f;

                    float disabledIslandCorrection = AverageCorrectionMagnitude(
                        disabled.mesh, disabledRaw, disabledSecondary, detachedIsland);
                    float enabledIslandCorrection = AverageCorrectionMagnitude(
                        enabled.mesh, enabledRaw, enabledSecondary, detachedIsland);
                    float enabledTorsoCorrection = AverageCorrectionMagnitude(
                        enabled.mesh, enabledRaw, enabledSecondary, torsoShell);
                    float enabledMaxCorrection = MaxCorrectionMagnitude(
                        enabled.mesh, enabledRaw, enabledSecondary, v => true);
                    var enabledIslandArtifacts = MeasureCorrectionArtifacts(
                        enabled.mesh, enabledRaw, enabledSecondary, detachedIsland);

                    Debug.Log(
                        $"[ReFit Tests] Disconnected island propagation: disabledIsland={disabledIslandCorrection * 1000f:0.###}mm, " +
                        $"enabledIsland={enabledIslandCorrection * 1000f:0.###}mm, " +
                        $"enabledMax={enabledMaxCorrection * 1000f:0.###}mm, " +
                        $"enabledTorso={enabledTorsoCorrection * 1000f:0.###}mm, " +
                        $"islandMin={enabledIslandArtifacts.minCorrection * 1000f:0.###}mm, " +
                        $"islandJump={enabledIslandArtifacts.maxCorrectionJump * 1000f:0.###}mm, " +
                        $"islandEdgeRatio={enabledIslandArtifacts.maxEdgeRatio:0.###}, " +
                        $"stats={enabled.clearanceCorrectionStats.Summary("aggregate")}");

                    AssertGreater(enabledIslandCorrection - disabledIslandCorrection, 0.003f,
                        "Disconnected island propagation did not materially increase correction on the detached island.");
                    AssertGreater(enabledIslandCorrection, enabledTorsoCorrection * 0.3f,
                        "Detached island correction is still far below nearby corrected torso surface.");
                    AssertGreater(enabledIslandArtifacts.vertices, 20,
                        "Detached lace fixture did not contain enough vertices to measure whole-component propagation.");
                    AssertGreater(enabledIslandArtifacts.edges, 0,
                        "Detached lace artifact check did not measure any lace edges.");
                    AssertGreater(enabledIslandArtifacts.minCorrection, enabledIslandCorrection * 0.45f,
                        "Disconnected island propagation only affected part of the detached lace component.");
                    AssertLessOrEqual(enabledIslandArtifacts.maxCorrectionJump, 0.014f,
                        "Disconnected island propagation introduced a sharp correction jump inside the detached lace.");
                    AssertLessOrEqual(enabledIslandArtifacts.maxEdgeRatio, 1.35f,
                        "Disconnected island propagation stretched or collapsed the detached lace too much.");
                    AssertLessOrEqual(enabledMaxCorrection, enabledRequest.settings.clearanceMaxTransferredTotalCorrection + 0.006f,
                        "Disconnected island propagation let transferred clearance correction exceed its total per-group budget.");
                }
                finally
                {
                    DestroyComputationMesh(disabled);
                    DestroyComputationMesh(enabled);
                }
            }
        }

        private static void ClearanceCorrection_SupportPropagatesToIneligibleDetachedIslands()
        {
            const int shellColumns = 7;
            const int shellRows = 5;
            int shellCount = shellColumns * shellRows;
            int laceStart = shellCount;
            const int laceColumns = 2;
            const int laceRows = 14;
            const int laceCount = laceColumns * laceRows;
            int unsupportedStart = laceStart + laceCount;
            const int unsupportedCount = 3;
            int groupCount = shellCount + laceCount + unsupportedCount;

            var vertices = new Vector3[groupCount];
            var triangles = new List<int>();
            for (int y = 0; y < shellRows; y++)
            {
                for (int x = 0; x < shellColumns; x++)
                {
                    int i = y * shellColumns + x;
                    vertices[i] = new Vector3(
                        Mathf.Lerp(-0.32f, 0.32f, x / (float)(shellColumns - 1)),
                        Mathf.Lerp(0f, 0.9f, y / (float)(shellRows - 1)),
                        0.08f);
                }
            }

            for (int y = 0; y < shellRows - 1; y++)
            {
                for (int x = 0; x < shellColumns - 1; x++)
                {
                    int a = y * shellColumns + x;
                    int b = a + 1;
                    int c = a + shellColumns;
                    int d = c + 1;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(c);
                }
            }

            for (int y = 0; y < laceRows; y++)
            {
                float fy = y / (float)(laceRows - 1);
                for (int x = 0; x < laceColumns; x++)
                {
                    int i = laceStart + y * laceColumns + x;
                    vertices[i] = new Vector3(
                        x == 0 ? -0.035f : 0.035f,
                        Mathf.Lerp(0.06f, 0.88f, fy),
                        0.12f);
                }
            }

            for (int y = 0; y < laceRows - 1; y++)
            {
                int a = laceStart + y * laceColumns;
                int b = a + 1;
                int c = a + laceColumns;
                int d = c + 1;
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
                triangles.Add(b);
                triangles.Add(d);
                triangles.Add(c);
            }

            vertices[unsupportedStart] = new Vector3(0.9f, 0.1f, 0.12f);
            vertices[unsupportedStart + 1] = new Vector3(0.98f, 0.1f, 0.12f);
            vertices[unsupportedStart + 2] = new Vector3(0.94f, 0.2f, 0.12f);
            triangles.Add(unsupportedStart);
            triangles.Add(unsupportedStart + 1);
            triangles.Add(unsupportedStart + 2);

            var triangleArray = triangles.ToArray();
            var normals = new Vector3[groupCount];
            var groupRep = new int[groupCount];
            var groupOfVertex = new int[groupCount];
            var regions = new BodyRegion[groupCount];
            var referenceNormals = new Vector3[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                normals[i] = Vector3.forward;
                groupRep[i] = i;
                groupOfVertex[i] = i;
                regions[i] = BodyRegion.Torso;
                referenceNormals[i] = Vector3.forward;
            }

            var asset = new MeshSnapshot
            {
                localVertices = vertices,
                worldVertices = vertices,
                worldNormals = normals,
                triangles = triangleArray,
                groupRep = groupRep,
                groupOfVertex = groupOfVertex,
                groupAdjacency = BuildIdentityGroupAdjacency(groupCount, triangleArray)
            };
            var body = new MeshSnapshot
            {
                worldVertices = new[]
                {
                    new Vector3(-1.2f, -0.3f, 0f),
                    new Vector3(1.2f, -0.3f, 0f),
                    new Vector3(0f, 0.9f, 0f)
                },
                worldNormals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                triangles = new[] { 0, 1, 2 }
            };
            var bindings = new SurfaceBinding[groupCount];
            var eligible = new bool[groupCount];
            var sourceClearance = new float[groupCount];
            var sourceBodyPoint = new Vector3[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                var bodyPoint = new Vector3(vertices[i].x, vertices[i].y, 0f);
                bindings[i] = new SurfaceBinding
                {
                    valid = true,
                    triangle = 0,
                    bary = BarycentricOnSyntheticBodyTriangle(bodyPoint),
                    point = bodyPoint,
                    distance = 0f,
                    requestedRegion = BodyRegion.Torso,
                    hitRegion = BodyRegion.Torso,
                    normalDot = 1f
                };
                eligible[i] = i < shellCount;
                sourceClearance[i] = vertices[i].z;
                sourceBodyPoint[i] = new Vector3(vertices[i].x, vertices[i].y, 0f);
            }

            var profile = new ReFitClearanceCorrection.Profile
            {
                eligible = eligible,
                sourceClearance = sourceClearance,
                sourceBodyPoint = sourceBodyPoint,
                eligibleGroups = shellCount
            };
            var settings = new ReFitSettings
            {
                enableClearanceCorrection = true,
                clearanceTightnessFactor = 0.05f,
                clearanceMinimumSafetyDistance = 0.002f,
                clearanceExpansionStart = 0f,
                clearanceExpansionFull = 0.01f,
                clearanceMaxOutwardCorrection = 0.12f,
                clearanceMaxInwardCorrection = 0.18f,
                clearanceOutwardStrength = 1f,
                clearanceInwardStrength = 0f,
                clearanceSmoothingIterations = 0,
                clearanceSmoothingStrength = 0f,
                clearanceSurfaceGuardIterations = 0,
                clearanceSurfaceGuardStrength = 0f,
                clearanceMaxPrimaryTotalCorrection = 0.06f,
                clearanceMaxTransferredTotalCorrection = 0.09f,
                clearanceOpenBoundaryCorrectionScale = 1f,
                clearanceLowConfidenceCorrectionScale = 1f,
                clearanceTransferredInwardScale = 1f,
                upperBodyGarmentHemFollowScale = 1f,
                clearancePropagateDisconnectedIslands = true,
                clearanceIslandPropagationStrength = 1f,
                clearanceIslandPropagationSearchDistance = 0.2f,
                clearanceMaxIslandPropagationCorrection = 0.08f,
                clearanceIslandPropagationMinDonorCorrection = 0.0005f,
                filterByNormal = false,
                filterByBoneRegion = false
            };
            var mutableGroupDeltas = new Vector3[groupCount];
            var bodyShapeDeltas = new[]
            {
                Vector3.forward * 0.04f,
                Vector3.forward * 0.04f,
                Vector3.forward * 0.14f
            };
            var falloff = new float[groupCount];
            for (int i = 0; i < falloff.Length; i++)
                falloff[i] = i < shellCount ? 1f : 0f;

            var context = new ReFitClearanceCorrection.Context
            {
                transferredBlendshape = true,
                assetGroupRegions = regions,
                targetTriangleRegions = new[] { BodyRegion.Torso },
                referenceNormals = referenceNormals
            };

            var stats = ReFitClearanceCorrection.Apply(
                asset,
                body,
                bindings,
                profile,
                null,
                mutableGroupDeltas,
                bodyShapeDeltas,
                falloff,
                settings,
                context);

            int appliedLace = 0;
            for (int i = laceStart; i < laceStart + laceCount; i++)
            {
                if (mutableGroupDeltas[i].z > 0.006f)
                    appliedLace++;
            }
            var laceArtifacts = MeasureCorrectionArtifacts(vertices, triangleArray, mutableGroupDeltas, laceStart, laceCount);
            var laceSurface = MeasureSurfaceFollowRelation(
                vertices,
                triangleArray,
                null,
                mutableGroupDeltas,
                i => i >= laceStart && i < laceStart + laceCount,
                i => i >= 0 && i < shellCount);

            float unsupportedMax = 0f;
            for (int i = unsupportedStart; i < unsupportedStart + unsupportedCount; i++)
                unsupportedMax = Mathf.Max(unsupportedMax, mutableGroupDeltas[i].magnitude);

            int debugAppliedLace = 0;
            int debugRejectedUnsupported = 0;
            var debugGroups = stats.islandPropagationDebug != null ? stats.islandPropagationDebug.groups : null;
            if (debugGroups != null)
            {
                for (int i = 0; i < debugGroups.Length; i++)
                {
                    var point = debugGroups[i];
                    if (point == null)
                        continue;

                    if (point.groupIndex >= laceStart &&
                        point.groupIndex < laceStart + laceCount &&
                        point.status == ReFitIslandPropagationStatus.Applied)
                    {
                        debugAppliedLace++;
                    }
                    else if (point.groupIndex >= unsupportedStart &&
                             point.groupIndex < unsupportedStart + unsupportedCount &&
                             point.status != ReFitIslandPropagationStatus.Applied &&
                             point.status != ReFitIslandPropagationStatus.None)
                    {
                        debugRejectedUnsupported++;
                    }
                }
            }

            Debug.Log(
                $"[ReFit Tests] Detached support propagation: appliedLace={appliedLace}/{laceCount}, " +
                $"debugAppliedLace={debugAppliedLace}/{laceCount}, unsupportedMax={unsupportedMax * 1000f:0.###}mm, " +
                $"laceMin={laceArtifacts.minCorrection * 1000f:0.###}mm, " +
                $"laceAvg={laceArtifacts.averageCorrection * 1000f:0.###}mm, " +
                $"laceJump={laceArtifacts.maxCorrectionJump * 1000f:0.###}mm, " +
                $"laceEdgeRatio={laceArtifacts.maxEdgeRatio:0.###}, " +
                $"laceSurfaceResidual95={laceSurface.p95SupportResidual * 1000f:0.###}mm, " +
                $"laceDistanceDrift95={laceSurface.p95DistanceDrift * 1000f:0.###}mm, " +
                $"stats={stats.Summary("synthetic detached support")}");

            AssertTrue(stats.propagatedIslandGroups >= laceCount,
                "Detached support propagation did not report the lace component as propagated.");
            AssertTrue(laceArtifacts.vertices == laceCount,
                "Detached support artifact metrics did not cover the whole lace component.");
            AssertGreater(laceArtifacts.edges, 0,
                "Detached support artifact metrics did not measure any lace edges.");
            AssertGreater(laceArtifacts.minCorrection, 0.0005f,
                "Detached support propagation only applied to part of the lace component.");
            AssertLessOrEqual(laceSurface.p95SupportResidual, 0.006f,
                "Detached support propagation averaged the component instead of following the nearby shell correction field.");
            AssertLessOrEqual(laceSurface.p95DistanceDrift, 0.006f,
                "Detached support propagation changed the lace-to-shell surface distance too much.");
            AssertLessOrEqual(laceArtifacts.maxCorrectionJump, 0.006f,
                "Detached support propagation introduced a sharp correction jump inside the lace.");
            AssertLessOrEqual(laceArtifacts.maxEdgeRatio, 1.12f,
                "Detached support propagation stretched or collapsed the lace component.");
            AssertTrue(debugAppliedLace == laceCount,
                "Detached lace groups were not marked as applied in island propagation debug data.");
            AssertTrue(debugRejectedUnsupported == unsupportedCount,
                "Unsupported detached island was not reported as rejected in island propagation debug data.");
            AssertLessOrEqual(unsupportedMax, 0.0005f,
                "Disconnected island propagation affected an unsupported far-away island.");
        }

        private static void ClearanceCorrection_PostPropagationGuardRespectsTransferredTotalCap()
        {
            var asset = new MeshSnapshot
            {
                worldVertices = new[]
                {
                    new Vector3(-0.2f, 0f, 0.08f),
                    new Vector3(0.2f, 0f, 0.08f),
                    new Vector3(0f, 0.35f, 0.08f),
                    new Vector3(-0.16f, 0.05f, 0.24f),
                    new Vector3(0.16f, 0.05f, 0.24f),
                    new Vector3(0f, 0.3f, 0.24f)
                },
                worldNormals = new[]
                {
                    Vector3.forward, Vector3.forward, Vector3.forward,
                    Vector3.forward, Vector3.forward, Vector3.forward
                },
                triangles = new[]
                {
                    0, 1, 2,
                    3, 4, 5
                },
                groupRep = new[] { 0, 1, 2, 3, 4, 5 },
                groupOfVertex = new[] { 0, 1, 2, 3, 4, 5 },
                groupAdjacency = new[]
                {
                    new List<int> { 1, 2 },
                    new List<int> { 0, 2 },
                    new List<int> { 0, 1 },
                    new List<int> { 4, 5 },
                    new List<int> { 3, 5 },
                    new List<int> { 3, 4 }
                }
            };
            var body = new MeshSnapshot
            {
                worldVertices = new[]
                {
                    new Vector3(-0.5f, -0.2f, 0f),
                    new Vector3(0.5f, -0.2f, 0f),
                    new Vector3(0f, 0.6f, 0f)
                },
                worldNormals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                triangles = new[] { 0, 1, 2 }
            };
            var bindings = new SurfaceBinding[6];
            for (int i = 0; i < bindings.Length; i++)
            {
                bindings[i] = new SurfaceBinding
                {
                    valid = true,
                    triangle = 0,
                    bary = new Vector3(0.33f, 0.33f, 0.34f),
                    point = Vector3.zero,
                    distance = 0f,
                    requestedRegion = BodyRegion.Torso,
                    hitRegion = BodyRegion.Torso,
                    normalDot = 1f
                };
            }

            var profile = new ReFitClearanceCorrection.Profile
            {
                eligible = new[] { true, true, true, true, true, true },
                sourceClearance = new[] { 0.02f, 0.02f, 0.02f, 0.02f, 0.02f, 0.02f },
                sourceBodyPoint = new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero },
                eligibleGroups = 6
            };
            var settings = new ReFitSettings
            {
                enableClearanceCorrection = true,
                clearanceTightnessFactor = 0.05f,
                clearanceMinimumSafetyDistance = 0.002f,
                clearanceExpansionStart = 0f,
                clearanceExpansionFull = 0.01f,
                clearanceMaxOutwardCorrection = 0.12f,
                clearanceMaxInwardCorrection = 0.18f,
                clearanceInwardStrength = 0.82f,
                clearanceSmoothingIterations = 0,
                clearanceSmoothingStrength = 0f,
                clearanceSurfaceGuardIterations = 6,
                clearanceSurfaceGuardStrength = 1f,
                clearanceMaxSurfaceGuardCorrection = 0.08f,
                clearanceSurfaceGuardTriggerDistance = 0.00025f,
                clearanceSurfaceGuardEdgeSamples = 3,
                clearanceMaxTransferredTotalCorrection = 0.045f,
                clearancePropagateDisconnectedIslands = true,
                clearanceIslandPropagationStrength = 1f,
                clearanceIslandPropagationSearchDistance = 0.3f,
                clearanceMaxIslandPropagationCorrection = 0.045f,
                clearanceIslandPropagationMinDonorCorrection = 0.0005f,
                filterByNormal = false,
                filterByBoneRegion = false
            };
            var mutableGroupDeltas = new Vector3[6];
            var bodyShapeDeltas = new[] { Vector3.forward * 0.18f, Vector3.forward * 0.18f, Vector3.forward * 0.18f };
            var falloff = new[] { 1f, 1f, 1f, 0f, 0f, 0f };
            var context = new ReFitClearanceCorrection.Context
            {
                transferredBlendshape = true,
                assetGroupRegions = new[] { BodyRegion.Torso, BodyRegion.Torso, BodyRegion.Torso, BodyRegion.Torso, BodyRegion.Torso, BodyRegion.Torso },
                targetTriangleRegions = new[] { BodyRegion.Torso },
                referenceNormals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }
            };

            var stats = ReFitClearanceCorrection.Apply(
                asset,
                body,
                bindings,
                profile,
                null,
                mutableGroupDeltas,
                bodyShapeDeltas,
                falloff,
                settings,
                context);

            float maxCorrection = 0f;
            for (int i = 0; i < mutableGroupDeltas.Length; i++)
                maxCorrection = Mathf.Max(maxCorrection, mutableGroupDeltas[i].magnitude);

            float expectedFinalPenetration = 0f;
            for (int i = 0; i < 3; i++)
                expectedFinalPenetration = Mathf.Max(
                    expectedFinalPenetration,
                    0.18f - (asset.worldVertices[i].z + mutableGroupDeltas[i].z));

            Debug.Log(
                $"[ReFit Tests] Post-propagation cap: maxCorrection={maxCorrection * 1000f:0.###}mm, " +
                $"expectedFinalPenetration={expectedFinalPenetration * 1000f:0.###}mm, " +
                $"stats={stats.Summary("synthetic transferred")}");

            AssertTrue(stats.propagatedIslandGroups > 0,
                "Synthetic cap regression did not trigger disconnected island propagation.");
            AssertLessOrEqual(maxCorrection, settings.clearanceMaxTransferredTotalCorrection + 0.0005f,
                "Post-propagation safety/surface guard exceeded the transferred correction cap.");
            AssertGreater(stats.maxPenetrationAfter, 0.04f,
                "Final maxPenetrationAfter did not reflect the penetration left by the transferred correction cap.");
            AssertLessOrEqual(Mathf.Abs(stats.maxPenetrationAfter - expectedFinalPenetration), 0.002f,
                "Final maxPenetrationAfter does not match the capped output mesh.");
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

        private static void ArmatureReplacement_TargetChildCreatesLeafTailHelper()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var targetUpperArm = fixture.target.bones[(int)RigBone.LeftUpperArm];
                var targetLowerArm = NewChild(targetUpperArm, "LeftLowerArm");
                targetLowerArm.position = targetUpperArm.position + new Vector3(-0.32f, -0.04f, 0.02f);

                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, true);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(comp.leafTailHints != null && comp.leafTailHints.Length > 0,
                        "The computation did not record any leaf-tail hints.");

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertTrue(!RendererHasBone(applied, "LeftLowerArm"),
                        "The target-only lower arm was inserted as a deforming renderer bone instead of a helper child.");

                    var rebuiltUpperArm = FindRendererBone(applied, "LeftUpperArm");
                    AssertTrue(rebuiltUpperArm != null, "Could not find rebuilt LeftUpperArm renderer bone.");
                    var helper = FindDirectChildStartingWith(rebuiltUpperArm, "__ReFitLeafTail_LeftLowerArm");
                    AssertTrue(helper != null,
                        $"Rebuilt LeftUpperArm did not receive a target-derived non-deforming tail helper. Children: {ChildNames(rebuiltUpperArm)}");
                    AssertTrue(Array.IndexOf(applied.bones, helper) < 0,
                        "The leaf-tail helper must not be part of SkinnedMeshRenderer.bones.");

                    float expected = Vector3.Distance(targetUpperArm.position, targetLowerArm.position);
                    float actual = Vector3.Distance(rebuiltUpperArm.position, helper.position);
                    AssertLessOrEqual(Mathf.Abs(actual - expected), 0.01f,
                        $"Leaf-tail helper length should match the target child length. Expected {expected:0.###}m, got {actual:0.###}m.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void WeightTransfer_PreservesMappedOriginalWeightsWhenProjectionCrossesRegions()
        {
            var asset = new MeshSnapshot
            {
                localVertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                groupRep = new[] { 0, 1, 2 },
                groupOfVertex = new[] { 0, 1, 2 },
                boneWeights = new[]
                {
                    MakeWeight(0, 1f),
                    MakeWeight(0, 1f),
                    MakeWeight(2, 1f)
                },
                rigid = false
            };
            var body = new MeshSnapshot
            {
                triangles = new[] { 0, 1, 2 },
                boneWeights = new[]
                {
                    MakeWeight(3, 1f), // bad arm -> leg projection
                    MakeWeight(1, 1f), // compatible arm transition
                    MakeWeight(4, 1f)  // bad left-leg -> right-leg projection
                },
                rigid = false
            };
            var bindings = new[]
            {
                Binding(new Vector3(1f, 0f, 0f), BodyRegion.LeftLeg),
                Binding(new Vector3(0f, 1f, 0f), BodyRegion.LeftArm),
                Binding(new Vector3(0f, 0f, 1f), BodyRegion.RightLeg)
            };
            var boneRegions = new[]
            {
                BodyRegion.LeftArm,
                BodyRegion.LeftArm,
                BodyRegion.LeftLeg,
                BodyRegion.LeftLeg,
                BodyRegion.RightLeg
            };

            var weights = WeightTransfer.Transfer(asset, body, bindings,
                new[] { 3, 1, 4, 3, 4 },
                new[] { 0, 1, 2, 3, 4 },
                new[] { false, false, false, false, false },
                boneRegions, new[] { BodyRegion.LeftArm, BodyRegion.LeftArm, BodyRegion.LeftLeg },
                new ReFitSettings { keepExtraBoneVertices = false },
                new ReFitReport(), out var debug);

            AssertGreater(WeightOf(weights[0], 0), 0.99f,
                "An arm vertex accepted an incompatible leg projection instead of preserving the mapped original arm weight.");
            AssertTrue(debug.decisionsByVertex[0] == ReFitWeightDecision.Original,
                $"Expected incompatible arm/leg projection to use Original, got {debug.decisionsByVertex[0]}.");

            AssertGreater(WeightOf(weights[1], 0), 0.6f,
                "Compatible arm projection should keep a comparable share of the original mapped arm weight.");
            AssertGreater(WeightOf(weights[1], 1), 0.15f,
                "Compatible arm projection should still contribute target arm weight.");
            AssertTrue(debug.decisionsByVertex[1] == ReFitWeightDecision.Blended,
                $"Expected compatible arm projection to be blended, got {debug.decisionsByVertex[1]}.");

            AssertGreater(WeightOf(weights[2], 2), 0.99f,
                "A left-leg vertex accepted a right-leg projection instead of preserving its mapped original leg weight.");
            AssertTrue(debug.decisionsByVertex[2] == ReFitWeightDecision.Original,
                $"Expected left/right leg projection to use Original, got {debug.decisionsByVertex[2]}.");
        }

        private static void ArmatureReplacement_LeafTailPrefersShinOverFoot()
        {
            var root = new GameObject("__ReFitTest_LeafTailChain");
            try
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                var upperLeg = NewChild(root.transform, "Left leg");
                var shin = NewChild(upperLeg, "shin.L");
                var foot = NewChild(shin, "foot.L");
                upperLeg.position = Vector3.zero;
                shin.position = new Vector3(-0.03f, -0.34f, 0.04f);
                foot.position = new Vector3(-0.06f, -0.68f, -0.28f);

                var method = typeof(ReFitEngine).GetMethod("FindPreferredTailChild",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                AssertTrue(method != null, "Could not reflect ReFitEngine.FindPreferredTailChild.");

                var selected = method.Invoke(null, new object[] { upperLeg, new HashSet<Transform>() }) as Transform;
                AssertSame(selected, shin,
                    $"Left leg leaf-tail selection should use shin.L before deeper foot.L, got '{(selected != null ? selected.name : "null")}'.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void ProjectionDebug_TargetSpaceAccessoryClassifiesAssetRegions()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                fixture.targetSpaceAccessory.root.transform.SetParent(fixture.target.root.transform, true);
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.targetSpaceAccessory.renderer, true);
                request.settings.filterByBoneRegion = true;
                request.settings.captureProjectionDebug = true;
                request.settings.transferWeights = true;

                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(comp.projectionDebug != null && comp.projectionDebug.points != null &&
                               comp.projectionDebug.points.Length > 0,
                        "Target-space accessory did not capture projection debug points.");

                    int classified = 0;
                    foreach (var point in comp.projectionDebug.points)
                        if (point != null && point.assetRegion != BodyRegion.Unknown)
                            classified++;

                    AssertGreater(classified, 0,
                        "All target-space accessory projection points were classified as Unknown; asset bones were not mapped to source regions.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void ProjectionDebug_CapturesBindingAndWeightDecisionData()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, true);
                request.settings.transferWeights = true;
                request.settings.captureProjectionDebug = true;
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(comp.projectionDebug != null && comp.projectionDebug.points != null &&
                               comp.projectionDebug.points.Length > 0,
                        "Projection debug data was not captured.");

                    var point = comp.projectionDebug.points[0];
                    AssertTrue(point.sourceTriangle >= 0 || !point.sourceValid,
                        "Projection debug did not store a valid source triangle or invalid-source marker.");
                    AssertTrue(point.weightDecision != ReFitWeightDecision.None,
                        "Projection debug did not store a weight-transfer decision.");
                    AssertTrue(!string.IsNullOrEmpty(point.finalWeights),
                        "Projection debug did not store final weight details.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void SurfaceBinding_TargetChainPrefersNearEquivalentHitBeforeNormalFilter()
        {
            var body = new MeshSnapshot
            {
                worldVertices = new[]
                {
                    new Vector3(0f, 0f, 0.001f),
                    new Vector3(1f, 0f, 0.001f),
                    new Vector3(0f, 1f, 0.001f),
                    new Vector3(0f, 0f, 0.06f),
                    new Vector3(0f, 1f, 0.06f),
                    new Vector3(1f, 0f, 0.06f)
                },
                triangles = new[]
                {
                    0, 2, 1, // Near, but opposite the reference normal.
                    3, 4, 5  // Farther, but matching the reference normal.
                }
            };
            var bvh = SurfaceBvh.Build(body);
            var hit = SurfaceBindingSolver.BindPoint(
                new Vector3(0.2f, 0.2f, 0f),
                body,
                bvh,
                0.3f,
                BodyRegion.Torso,
                new[] { BodyRegion.Torso, BodyRegion.Torso },
                Vector3.forward,
                Mathf.Cos(35f * Mathf.Deg2Rad),
                true);

            AssertTrue(hit.valid, "Near-equivalent target chain binding did not find any surface.");
            AssertTrue(hit.triangle == 0,
                $"Target chain should use the near overlapping surface before normal filtering. It chose triangle {hit.triangle}.");
            AssertLessOrEqual(hit.distance, 0.01f,
                $"Near-equivalent target chain hit should stay within the overlap epsilon. Distance was {hit.distance:0.####}m.");
            AssertTrue(hit.usedRelaxedFallback,
                "The near-equivalent hit should be marked as relaxed so projection debug shows that the filters were bypassed.");
        }

        private static void ReFitWizard_DebugModeCapturesProjectionDataWhenGizmoHidden()
        {
            bool previousDebug = ReFitDebugService.Enabled;
            bool previousGizmo = ReFitProjectionGizmoService.Enabled;
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            try
            {
                using (var fixture = ReFitTestFixture.Create())
                {
                    ReFitDebugService.Enabled = true;
                    ReFitProjectionGizmoService.Enabled = false;

                    SetPrivateField(window, "mode", ReFitMode.MeshAndBlendshape);
                    SetPrivateField(window, "asset", fixture.sourceSpaceAccessory.renderer);
                    SetPrivateField(window, "sourceAvatar", fixture.source.root);
                    SetPrivateField(window, "targetAvatar", fixture.target.root);
                    SetPrivateField(window, "blendshape", BodyShapeName);
                    SetPrivateField(window, "settings", CreateDeterministicSettings(true));

                    var method = typeof(ReFitWizard).GetMethod("BuildRequest",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    AssertTrue(method != null, "Could not reflect ReFitWizard.BuildRequest.");

                    var request = method.Invoke(window, null) as ReFitRequest;
                    AssertTrue(request != null, "BuildRequest returned null.");
                    AssertTrue(request.settings.captureProjectionDebug,
                        "Debug mode should capture projection data even when the Scene view projection rays are hidden.");
                    AssertTrue(request.settings.maxProjectionDebugGroups <= 0,
                        "Wizard debug mode should capture every welded vertex group for Scene view group diagnostics.");
                }
            }
            finally
            {
                ReFitDebugService.Enabled = previousDebug;
                ReFitProjectionGizmoService.Enabled = previousGizmo;
                Object.DestroyImmediate(window);
            }
        }

        private static void DebugSession_CapturesIndependentSnapshotMeshes()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var report = new ReFitReport();
                var debug = new ReFitDebugSession(request, report);
                var debugMeshes = new List<Mesh>();
                try
                {
                    debug.Capture("first_state", fixture.sourceSpaceAccessory.renderer);
                    debug.Capture("second_state", fixture.sourceSpaceAccessory.renderer);

                    var renderers = debug.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    foreach (var renderer in renderers)
                    {
                        if (renderer == null || renderer.sharedMesh == null)
                            continue;
                        if (!renderer.transform.IsChildOf(debug.Root.transform))
                            continue;
                        debugMeshes.Add(renderer.sharedMesh);
                    }

                    AssertGreater(debugMeshes.Count, 1,
                        "Debug session did not create two snapshot renderers with meshes.");
                    AssertTrue(debugMeshes[0] != debugMeshes[1],
                        "Debug snapshots reused the same mesh instance.");
                    AssertTrue(!string.Equals(debugMeshes[0].name, debugMeshes[1].name, StringComparison.Ordinal),
                        $"Debug snapshot mesh names should identify their step, but both were '{debugMeshes[0].name}'.");
                }
                finally
                {
                    if (debug.Root != null)
                        Object.DestroyImmediate(debug.Root);
                    foreach (var mesh in debugMeshes)
                    {
                        if (mesh != null && !AssetDatabase.Contains(mesh))
                            Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

        private static void XRayExtraGizmoRegistry_RegistersAndTogglesExternalGizmo()
        {
            const string id = "orbiters.refit.tests.fake-extra-gizmo";
            bool previousProjection = ReFitProjectionGizmoService.Enabled;
            bool previousWeldedGroups = ReFitProjectionGizmoService.WeldedGroupsEnabled;
            bool enabled = false;
            XRayExternalGizmoRegistry.Register(id, "Fake ReFit gizmo", () => enabled, value => enabled = value);
            try
            {
                XRayExternalGizmoEntry found = null;
                foreach (var entry in XRayExternalGizmoRegistry.Entries)
                {
                    if (entry.Id == id)
                    {
                        found = entry;
                        break;
                    }
                }

                AssertTrue(found != null, "The registered external gizmo did not appear in XRayExternalGizmoRegistry.Entries.");
                found.SetEnabled(true);
                AssertTrue(enabled, "The registered external gizmo did not receive its enabled toggle.");
                XRayExternalGizmoRegistry.SetAll(false);
                AssertTrue(!enabled, "XRayExternalGizmoRegistry.SetAll(false) did not disable the registered gizmo.");
            }
            finally
            {
                XRayExternalGizmoRegistry.Unregister(id);
                ReFitProjectionGizmoService.Enabled = previousProjection;
                ReFitProjectionGizmoService.WeldedGroupsEnabled = previousWeldedGroups;
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
                primarySmoothingIterations = 0,
                primarySmoothingStrength = 0f,
                transferredBlendshapeSmoothingIterations = 0,
                transferredBlendshapeSmoothingStrength = 0f,
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

        private static void ApplyHighTightnessSettings(ReFitSettings settings)
        {
            settings.enableClearanceCorrection = true;
            settings.clearanceTightnessFactor = 0.08f;
            settings.clearanceMinimumSafetyDistance = 0.002f;
            settings.clearanceMaxOutwardCorrection = 0.12f;
            settings.clearanceMaxSurfaceGuardCorrection = 0.08f;
            settings.clearanceSurfaceGuardTriggerDistance = 0.00025f;
            settings.clearanceMaxInwardCorrection = 0.18f;
            settings.clearanceInwardStrength = 0.82f;
            settings.clearanceExpansionStart = 0.002f;
            settings.clearanceExpansionFull = 0.025f;
            settings.clearanceSmoothingIterations = 1;
            settings.clearanceSmoothingStrength = 0.35f;
            settings.clearanceSurfaceGuardIterations = 6;
            settings.clearanceSurfaceGuardStrength = 1f;
            settings.clearanceSurfaceGuardEdgeSamples = 3;
            settings.clearanceMaxPrimaryTotalCorrection = 0.06f;
            settings.clearanceMaxTransferredTotalCorrection = 0.045f;
            settings.clearanceTransferredInwardScale = 0.92f;
            settings.clearanceOpenBoundaryCorrectionScale = 0.06f;
            settings.clearanceLowConfidenceCorrectionScale = 0.28f;
            settings.upperBodyGarmentHemFollowScale = 0.18f;
            settings.clearancePropagateDisconnectedIslands = true;
            settings.clearanceIslandPropagationStrength = 0.85f;
            settings.clearanceIslandPropagationSearchDistance = 0.12f;
            settings.clearanceMaxIslandPropagationCorrection = 0.045f;
            settings.clearanceIslandPropagationMinDonorCorrection = 0.001f;
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

        private static float MaxBlendShapeDifference(Mesh a, string shapeA, Mesh b, string shapeB)
        {
            var deltasA = GetBlendShapeDeltas(a, shapeA);
            var deltasB = GetBlendShapeDeltas(b, shapeB);
            AssertTrue(deltasA.Length == deltasB.Length,
                $"Cannot compare blendshapes with different vertex counts: {deltasA.Length} vs {deltasB.Length}.");

            float max = 0f;
            for (int i = 0; i < deltasA.Length; i++)
                max = Mathf.Max(max, (deltasA[i] - deltasB[i]).magnitude);
            return max;
        }

        private static float MaxMagnitudeInRegion(Mesh mesh, string shapeName, Func<Vector3, bool> contains)
        {
            var vertices = mesh.vertices;
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            int count = 0;
            float max = 0f;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i])) continue;
                count++;
                max = Mathf.Max(max, deltas[i].magnitude);
            }

            AssertTrue(count > 0, $"No vertices matched the requested peak region for shape '{shapeName}'.");
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

        private static void AddSyntheticChestExpansion(Mesh mesh, string shapeName)
        {
            var vertices = mesh.vertices;
            var deltas = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                if (Mathf.Abs(vertex.x) <= 0.55f && vertex.y >= 1.0f && vertex.y <= 1.35f)
                    deltas[i] = new Vector3(0f, 0f, 0.11f);
            }
            mesh.AddBlendShapeFrame(shapeName, 100f, deltas, null, null);
        }

        private static void AddPartialTransferLaceCluster(Mesh mesh, SkinnedMeshRenderer renderer)
        {
            AssertTrue(mesh != null, "Cannot add a detached lace cluster to a null mesh.");
            AssertTrue(renderer != null, "Cannot add a detached lace cluster without its renderer.");
            AssertTrue(mesh.blendShapeCount == 0,
                "The detached-lace fixture should be built before blendshapes are added.");

            var oldVertices = mesh.vertices;
            var oldNormals = mesh.normals;
            var oldUv = mesh.uv;
            var oldWeights = mesh.boneWeights;
            var oldTriangles = mesh.triangles;
            int start = oldVertices.Length;
            const int stripCount = 2;
            const int laceColumns = 2;
            const int laceRows = 30;
            const int verticesPerStrip = laceColumns * laceRows;
            const int laceVertexCount = stripCount * verticesPerStrip;
            const int triangleIndicesPerStrip = (laceRows - 1) * 6;

            var vertices = new Vector3[start + laceVertexCount];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var weights = new BoneWeight[vertices.Length];
            Array.Copy(oldVertices, vertices, oldVertices.Length);
            Array.Copy(oldWeights, weights, oldWeights.Length);
            if (oldNormals != null && oldNormals.Length == oldVertices.Length)
                Array.Copy(oldNormals, normals, oldNormals.Length);
            else
                for (int i = 0; i < start; i++)
                    normals[i] = Vector3.forward;

            if (oldUv != null && oldUv.Length == oldVertices.Length)
                Array.Copy(oldUv, uv, oldUv.Length);

            for (int strip = 0; strip < stripCount; strip++)
            {
                float xCenter = strip == 0 ? -0.015f : 0.015f;
                for (int y = 0; y < laceRows; y++)
                {
                    float fy = y / (float)(laceRows - 1);
                    float curvedX = xCenter + Mathf.Sin(fy * Mathf.PI) * (strip == 0 ? -0.008f : 0.008f);
                    for (int x = 0; x < laceColumns; x++)
                    {
                        int local = strip * verticesPerStrip + y * laceColumns + x;
                        int i = start + local;
                        vertices[i] = new Vector3(
                            curvedX + (x == 0 ? -0.004f : 0.004f),
                            Mathf.Lerp(0.42f, 1.28f, fy),
                            0.112f);
                        normals[i] = Vector3.forward;
                        uv[i] = new Vector2(x, fy);
                        weights[i] = SourceAccessoryWeight(vertices[i]);
                    }
                }
            }

            var triangles = new int[oldTriangles.Length + stripCount * triangleIndicesPerStrip];
            Array.Copy(oldTriangles, triangles, oldTriangles.Length);
            int t = oldTriangles.Length;
            for (int strip = 0; strip < stripCount; strip++)
            {
                int stripStart = start + strip * verticesPerStrip;
                for (int y = 0; y < laceRows - 1; y++)
                {
                    int a = stripStart + y * laceColumns;
                    int b = a + 1;
                    int c = a + laceColumns;
                    int d = c + 1;
                    triangles[t++] = a;
                    triangles[t++] = b;
                    triangles[t++] = c;
                    triangles[t++] = b;
                    triangles[t++] = d;
                    triangles[t++] = c;
                }
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.boneWeights = weights;
            mesh.bindposes = BuildBindposes(renderer.transform, renderer.bones);
            mesh.RecalculateBounds();
            renderer.localBounds = mesh.bounds;
        }

        private static void AddDetachedChestIsland(Mesh mesh, SkinnedMeshRenderer renderer)
        {
            AssertTrue(mesh != null, "Cannot add a detached island to a null mesh.");
            AssertTrue(renderer != null, "Cannot add a detached island without its renderer.");
            AssertTrue(mesh.blendShapeCount == 0,
                "The detached-island fixture should be built before blendshapes are added.");

            var oldVertices = mesh.vertices;
            var oldNormals = mesh.normals;
            var oldUv = mesh.uv;
            var oldWeights = mesh.boneWeights;
            var oldTriangles = mesh.triangles;
            int start = oldVertices.Length;
            const int laceColumns = 2;
            const int laceRows = 16;
            const int laceVertexCount = laceColumns * laceRows;
            const int laceTriangleIndexCount = (laceRows - 1) * 6;

            var vertices = new Vector3[start + laceVertexCount];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var weights = new BoneWeight[vertices.Length];
            Array.Copy(oldVertices, vertices, oldVertices.Length);
            Array.Copy(oldWeights, weights, oldWeights.Length);
            if (oldNormals != null && oldNormals.Length == oldVertices.Length)
                Array.Copy(oldNormals, normals, oldNormals.Length);
            else
                for (int i = 0; i < start; i++)
                    normals[i] = Vector3.forward;

            if (oldUv != null && oldUv.Length == oldVertices.Length)
                Array.Copy(oldUv, uv, oldUv.Length);

            for (int y = 0; y < laceRows; y++)
            {
                float fy = y / (float)(laceRows - 1);
                for (int x = 0; x < laceColumns; x++)
                {
                    int i = start + y * laceColumns + x;
                    vertices[i] = new Vector3(
                        x == 0 ? -0.035f : 0.035f,
                        Mathf.Lerp(0.92f, 1.3f, fy),
                        0.105f);
                }
            }

            for (int i = start; i < vertices.Length; i++)
            {
                normals[i] = Vector3.forward;
                int local = i - start;
                int x = local % laceColumns;
                int y = local / laceColumns;
                uv[i] = new Vector2(x, y / (float)(laceRows - 1));
                weights[i] = SourceAccessoryWeight(vertices[i]);
            }

            var triangles = new int[oldTriangles.Length + laceTriangleIndexCount];
            Array.Copy(oldTriangles, triangles, oldTriangles.Length);
            int t = oldTriangles.Length;
            for (int y = 0; y < laceRows - 1; y++)
            {
                int a = start + y * laceColumns;
                int b = a + 1;
                int c = a + laceColumns;
                int d = c + 1;
                triangles[t++] = a;
                triangles[t++] = b;
                triangles[t++] = c;
                triangles[t++] = b;
                triangles[t++] = d;
                triangles[t++] = c;
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.boneWeights = weights;
            mesh.bindposes = BuildBindposes(renderer.transform, renderer.bones);
            mesh.RecalculateBounds();
            renderer.localBounds = mesh.bounds;
        }

        private static List<int>[] BuildIdentityGroupAdjacency(int groupCount, int[] triangles)
        {
            var sets = new HashSet<int>[groupCount];
            for (int i = 0; i < sets.Length; i++)
                sets[i] = new HashSet<int>();

            if (triangles != null)
            {
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    AddGroupEdge(sets, triangles[t], triangles[t + 1]);
                    AddGroupEdge(sets, triangles[t + 1], triangles[t + 2]);
                    AddGroupEdge(sets, triangles[t + 2], triangles[t]);
                }
            }

            var adjacency = new List<int>[groupCount];
            for (int i = 0; i < adjacency.Length; i++)
                adjacency[i] = new List<int>(sets[i]);
            return adjacency;
        }

        private static void AddGroupEdge(HashSet<int>[] sets, int a, int b)
        {
            if (sets == null || a < 0 || b < 0 || a == b || a >= sets.Length || b >= sets.Length)
                return;

            sets[a].Add(b);
            sets[b].Add(a);
        }

        private static float AverageLocalDeltaZ(Mesh mesh, Vector3[] deltas, Func<Vector3, bool> contains)
        {
            var vertices = mesh.vertices;
            float total = 0f;
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i]))
                    continue;
                total += deltas[i].z;
                count++;
            }

            AssertTrue(count > 0, "No vertices matched the requested delta region.");
            return total / count;
        }

        private static float MaxLocalDeltaMagnitude(Mesh mesh, Vector3[] deltas, Func<Vector3, bool> contains)
        {
            var vertices = mesh.vertices;
            float max = 0f;
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i]))
                    continue;
                max = Mathf.Max(max, deltas[i].magnitude);
                count++;
            }

            AssertTrue(count > 0, "No vertices matched the requested delta magnitude region.");
            return max;
        }

        private static float MaxBoundaryCorrectionMagnitude(Mesh mesh, Vector3[] raw, Vector3[] corrected)
        {
            AssertTrue(mesh != null, "Cannot measure boundary correction on a null mesh.");
            AssertTrue(raw != null && corrected != null && raw.Length == mesh.vertexCount && corrected.Length == mesh.vertexCount,
                "Boundary correction arrays must match the mesh vertex count.");

            var boundary = BuildBoundaryVertexMask(mesh);
            float max = 0f;
            int count = 0;
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                if (!boundary[i])
                    continue;
                count++;
                max = Mathf.Max(max, (corrected[i] - raw[i]).magnitude);
            }

            AssertTrue(count > 0, "No boundary vertices were found for the boundary correction check.");
            return max;
        }

        private static float AverageCorrectionMagnitude(
            Mesh mesh,
            Vector3[] raw,
            Vector3[] corrected,
            Func<Vector3, bool> contains)
        {
            AssertTrue(mesh != null, "Cannot measure correction on a null mesh.");
            AssertTrue(raw != null && corrected != null && raw.Length == mesh.vertexCount && corrected.Length == mesh.vertexCount,
                "Correction arrays must match the mesh vertex count.");

            var vertices = mesh.vertices;
            double total = 0d;
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i]))
                    continue;
                total += (corrected[i] - raw[i]).magnitude;
                count++;
            }

            AssertTrue(count > 0, "No vertices matched the requested correction region.");
            return (float)(total / count);
        }

        private static float MaxCorrectionMagnitude(
            Mesh mesh,
            Vector3[] raw,
            Vector3[] corrected,
            Func<Vector3, bool> contains)
        {
            AssertTrue(mesh != null, "Cannot measure correction on a null mesh.");
            AssertTrue(raw != null && corrected != null && raw.Length == mesh.vertexCount && corrected.Length == mesh.vertexCount,
                "Correction arrays must match the mesh vertex count.");

            var vertices = mesh.vertices;
            float max = 0f;
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i]))
                    continue;
                count++;
                max = Mathf.Max(max, (corrected[i] - raw[i]).magnitude);
            }

            AssertTrue(count > 0, "No vertices matched the requested max correction region.");
            return max;
        }

        private static CorrectionArtifactMetrics MeasureCorrectionArtifacts(
            Mesh mesh,
            Vector3[] raw,
            Vector3[] corrected,
            Func<Vector3, bool> contains)
        {
            AssertTrue(mesh != null, "Cannot measure correction artifacts on a null mesh.");
            AssertTrue(raw != null && corrected != null && raw.Length == mesh.vertexCount && corrected.Length == mesh.vertexCount,
                "Correction artifact arrays must match the mesh vertex count.");

            var vertices = mesh.vertices;
            var reference = new Vector3[vertices.Length];
            var corrections = new Vector3[vertices.Length];
            var mask = new bool[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                reference[i] = vertices[i] + raw[i];
                corrections[i] = corrected[i] - raw[i];
                mask[i] = contains(vertices[i]);
            }

            return MeasureCorrectionArtifacts(reference, mesh.triangles, corrections, mask);
        }

        private static CorrectionArtifactMetrics MeasureShapeArtifacts(
            Mesh mesh,
            Vector3[] baseDeltas,
            Vector3[] shapeDeltas,
            Func<Vector3, bool> contains)
        {
            AssertTrue(mesh != null, "Cannot measure shape artifacts on a null mesh.");
            AssertTrue(shapeDeltas != null && shapeDeltas.Length == mesh.vertexCount,
                "Shape artifact deltas must match the mesh vertex count.");
            AssertTrue(baseDeltas == null || baseDeltas.Length == mesh.vertexCount,
                "Shape artifact base deltas must match the mesh vertex count when provided.");

            var vertices = mesh.vertices;
            var reference = new Vector3[vertices.Length];
            var mask = new bool[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                reference[i] = vertices[i] + (baseDeltas != null ? baseDeltas[i] : Vector3.zero);
                mask[i] = contains(vertices[i]);
            }

            return MeasureCorrectionArtifacts(reference, mesh.triangles, shapeDeltas, mask);
        }

        private static SurfaceRelationMetrics MeasureSurfaceFollowRelation(
            Mesh mesh,
            Vector3[] baseDeltas,
            Vector3[] shapeDeltas,
            Func<Vector3, bool> receiver,
            Func<Vector3, bool> support)
        {
            AssertTrue(mesh != null, "Cannot measure surface relation on a null mesh.");
            AssertTrue(shapeDeltas != null && shapeDeltas.Length == mesh.vertexCount,
                "Surface relation shape deltas must match the mesh vertex count.");
            AssertTrue(baseDeltas == null || baseDeltas.Length == mesh.vertexCount,
                "Surface relation base deltas must match the mesh vertex count when provided.");
            AssertTrue(receiver != null && support != null,
                "Surface relation requires receiver and support predicates.");

            var vertices = mesh.vertices;
            var reference = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                reference[i] = vertices[i] + (baseDeltas != null ? baseDeltas[i] : Vector3.zero);

            var supportTriangles = new List<int>();
            var triangles = mesh.triangles;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t];
                int b = triangles[t + 1];
                int c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                    continue;
                if (!support(vertices[a]) || !support(vertices[b]) || !support(vertices[c]))
                    continue;

                supportTriangles.Add(a);
                supportTriangles.Add(b);
                supportTriangles.Add(c);
            }

            AssertTrue(supportTriangles.Count >= 3,
                "Surface relation did not find any support triangles under the receiver component.");

            var distanceDrifts = new List<float>();
            var supportResiduals = new List<float>();
            var metrics = new SurfaceRelationMetrics
            {
                minBaseDistance = float.PositiveInfinity
            };

            for (int i = 0; i < vertices.Length; i++)
            {
                if (!receiver(vertices[i]))
                    continue;

                bool found = false;
                float bestDistanceSq = float.PositiveInfinity;
                Vector3 bestPoint = Vector3.zero;
                Vector3 bestBary = Vector3.zero;
                int bestTri = -1;
                for (int t = 0; t + 2 < supportTriangles.Count; t += 3)
                {
                    int a = supportTriangles[t];
                    int b = supportTriangles[t + 1];
                    int c = supportTriangles[t + 2];
                    var point = ClosestPointOnTriangle(reference[i], reference[a], reference[b], reference[c], out var bary);
                    float distanceSq = (point - reference[i]).sqrMagnitude;
                    if (distanceSq >= bestDistanceSq)
                        continue;

                    found = true;
                    bestDistanceSq = distanceSq;
                    bestPoint = point;
                    bestBary = bary;
                    bestTri = t;
                }

                if (!found)
                    continue;

                int ia = supportTriangles[bestTri];
                int ib = supportTriangles[bestTri + 1];
                int ic = supportTriangles[bestTri + 2];
                var supportDelta = shapeDeltas[ia] * bestBary.x +
                                   shapeDeltas[ib] * bestBary.y +
                                   shapeDeltas[ic] * bestBary.z;
                float baseDistance = Mathf.Sqrt(bestDistanceSq);
                float shapedDistance = (reference[i] + shapeDeltas[i] - bestPoint - supportDelta).magnitude;
                float drift = Mathf.Abs(shapedDistance - baseDistance);
                float residual = (shapeDeltas[i] - supportDelta).magnitude;

                metrics.samples++;
                metrics.averageBaseDistance += baseDistance;
                metrics.minBaseDistance = Mathf.Min(metrics.minBaseDistance, baseDistance);
                metrics.maxBaseDistance = Mathf.Max(metrics.maxBaseDistance, baseDistance);
                metrics.averageDistanceDrift += drift;
                metrics.maxDistanceDrift = Mathf.Max(metrics.maxDistanceDrift, drift);
                metrics.averageSupportResidual += residual;
                metrics.maxSupportResidual = Mathf.Max(metrics.maxSupportResidual, residual);
                distanceDrifts.Add(drift);
                supportResiduals.Add(residual);
            }

            AssertTrue(metrics.samples > 0, "Surface relation did not find any receiver vertices.");
            metrics.averageBaseDistance /= metrics.samples;
            metrics.averageDistanceDrift /= metrics.samples;
            metrics.averageSupportResidual /= metrics.samples;
            if (float.IsPositiveInfinity(metrics.minBaseDistance))
                metrics.minBaseDistance = 0f;
            distanceDrifts.Sort();
            supportResiduals.Sort();
            metrics.p95DistanceDrift = Percentile(distanceDrifts, 0.95f);
            metrics.p95SupportResidual = Percentile(supportResiduals, 0.95f);
            return metrics;
        }

        private static SurfaceRelationMetrics MeasureSurfaceFollowRelation(
            Vector3[] vertices,
            int[] triangles,
            Vector3[] baseDeltas,
            Vector3[] shapeDeltas,
            Func<int, bool> receiver,
            Func<int, bool> support)
        {
            AssertTrue(vertices != null && triangles != null,
                "Cannot measure surface relation without vertices and triangles.");
            AssertTrue(shapeDeltas != null && shapeDeltas.Length == vertices.Length,
                "Surface relation shape deltas must match the vertex count.");
            AssertTrue(baseDeltas == null || baseDeltas.Length == vertices.Length,
                "Surface relation base deltas must match the vertex count when provided.");
            AssertTrue(receiver != null && support != null,
                "Surface relation requires receiver and support predicates.");

            var reference = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                reference[i] = vertices[i] + (baseDeltas != null ? baseDeltas[i] : Vector3.zero);

            var supportTriangles = new List<int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t];
                int b = triangles[t + 1];
                int c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                    continue;
                if (!support(a) || !support(b) || !support(c))
                    continue;

                supportTriangles.Add(a);
                supportTriangles.Add(b);
                supportTriangles.Add(c);
            }

            AssertTrue(supportTriangles.Count >= 3,
                "Surface relation did not find any support triangles under the receiver component.");

            var distanceDrifts = new List<float>();
            var supportResiduals = new List<float>();
            var metrics = new SurfaceRelationMetrics
            {
                minBaseDistance = float.PositiveInfinity
            };

            for (int i = 0; i < vertices.Length; i++)
            {
                if (!receiver(i))
                    continue;

                bool found = false;
                float bestDistanceSq = float.PositiveInfinity;
                Vector3 bestPoint = Vector3.zero;
                Vector3 bestBary = Vector3.zero;
                int bestTri = -1;
                for (int t = 0; t + 2 < supportTriangles.Count; t += 3)
                {
                    int a = supportTriangles[t];
                    int b = supportTriangles[t + 1];
                    int c = supportTriangles[t + 2];
                    var point = ClosestPointOnTriangle(reference[i], reference[a], reference[b], reference[c], out var bary);
                    float distanceSq = (point - reference[i]).sqrMagnitude;
                    if (distanceSq >= bestDistanceSq)
                        continue;

                    found = true;
                    bestDistanceSq = distanceSq;
                    bestPoint = point;
                    bestBary = bary;
                    bestTri = t;
                }

                if (!found)
                    continue;

                int ia = supportTriangles[bestTri];
                int ib = supportTriangles[bestTri + 1];
                int ic = supportTriangles[bestTri + 2];
                var supportDelta = shapeDeltas[ia] * bestBary.x +
                                   shapeDeltas[ib] * bestBary.y +
                                   shapeDeltas[ic] * bestBary.z;
                float baseDistance = Mathf.Sqrt(bestDistanceSq);
                float shapedDistance = (reference[i] + shapeDeltas[i] - bestPoint - supportDelta).magnitude;
                float drift = Mathf.Abs(shapedDistance - baseDistance);
                float residual = (shapeDeltas[i] - supportDelta).magnitude;

                metrics.samples++;
                metrics.averageBaseDistance += baseDistance;
                metrics.minBaseDistance = Mathf.Min(metrics.minBaseDistance, baseDistance);
                metrics.maxBaseDistance = Mathf.Max(metrics.maxBaseDistance, baseDistance);
                metrics.averageDistanceDrift += drift;
                metrics.maxDistanceDrift = Mathf.Max(metrics.maxDistanceDrift, drift);
                metrics.averageSupportResidual += residual;
                metrics.maxSupportResidual = Mathf.Max(metrics.maxSupportResidual, residual);
                distanceDrifts.Add(drift);
                supportResiduals.Add(residual);
            }

            AssertTrue(metrics.samples > 0, "Surface relation did not find any receiver vertices.");
            metrics.averageBaseDistance /= metrics.samples;
            metrics.averageDistanceDrift /= metrics.samples;
            metrics.averageSupportResidual /= metrics.samples;
            if (float.IsPositiveInfinity(metrics.minBaseDistance))
                metrics.minBaseDistance = 0f;
            distanceDrifts.Sort();
            supportResiduals.Sort();
            metrics.p95DistanceDrift = Percentile(distanceDrifts, 0.95f);
            metrics.p95SupportResidual = Percentile(supportResiduals, 0.95f);
            return metrics;
        }

        private static Vector3 BarycentricOnSyntheticBodyTriangle(Vector3 point)
        {
            var a = new Vector3(-1.2f, -0.3f, 0f);
            var b = new Vector3(1.2f, -0.3f, 0f);
            var c = new Vector3(0f, 0.9f, 0f);
            ClosestPointOnTriangle(point, a, b, c, out var bary);
            return bary;
        }

        private static Vector3 ClosestPointOnTriangle(
            Vector3 p,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            out Vector3 bary)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                bary = new Vector3(1f, 0f, 0f);
                return a;
            }

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                bary = new Vector3(0f, 1f, 0f);
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                bary = new Vector3(1f - v, v, 0f);
                return a + ab * v;
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                bary = new Vector3(0f, 0f, 1f);
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                bary = new Vector3(1f - w, 0f, w);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0f, 1f - w, w);
                return b + (c - b) * w;
            }

            float denom = 1f / (va + vb + vc);
            float y = vb * denom;
            float z = vc * denom;
            bary = new Vector3(1f - y - z, y, z);
            return a + ab * y + ac * z;
        }

        private static CorrectionArtifactMetrics MeasureCorrectionArtifacts(
            Vector3[] reference,
            int[] triangles,
            Vector3[] corrections,
            int start,
            int count)
        {
            AssertTrue(reference != null && corrections != null && reference.Length == corrections.Length,
                "Correction artifact arrays must have matching lengths.");
            var mask = new bool[reference.Length];
            int end = Mathf.Min(reference.Length, start + Mathf.Max(0, count));
            for (int i = Mathf.Max(0, start); i < end; i++)
                mask[i] = true;
            return MeasureCorrectionArtifacts(reference, triangles, corrections, mask);
        }

        private static CorrectionArtifactMetrics MeasureCorrectionArtifacts(
            Vector3[] reference,
            int[] triangles,
            Vector3[] corrections,
            bool[] mask)
        {
            AssertTrue(reference != null && corrections != null && mask != null &&
                       reference.Length == corrections.Length && reference.Length == mask.Length,
                "Correction artifact inputs must have matching lengths.");

            var metrics = new CorrectionArtifactMetrics
            {
                minCorrection = float.PositiveInfinity,
                maxEdgeRatio = 1f
            };

            for (int i = 0; i < reference.Length; i++)
            {
                if (!mask[i])
                    continue;

                float magnitude = corrections[i].magnitude;
                metrics.vertices++;
                metrics.averageCorrection += magnitude;
                metrics.minCorrection = Mathf.Min(metrics.minCorrection, magnitude);
                metrics.maxCorrection = Mathf.Max(metrics.maxCorrection, magnitude);
            }

            AssertTrue(metrics.vertices > 0, "No vertices matched the requested correction artifact region.");
            metrics.averageCorrection /= metrics.vertices;
            if (float.IsPositiveInfinity(metrics.minCorrection))
                metrics.minCorrection = 0f;

            var edges = new HashSet<ulong>();
            if (triangles != null)
            {
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    AccumulateCorrectionEdgeArtifact(reference, corrections, mask, edges, triangles[t], triangles[t + 1], ref metrics);
                    AccumulateCorrectionEdgeArtifact(reference, corrections, mask, edges, triangles[t + 1], triangles[t + 2], ref metrics);
                    AccumulateCorrectionEdgeArtifact(reference, corrections, mask, edges, triangles[t + 2], triangles[t], ref metrics);
                }
            }

            return metrics;
        }

        private static void AccumulateCorrectionEdgeArtifact(
            Vector3[] reference,
            Vector3[] corrections,
            bool[] mask,
            HashSet<ulong> edges,
            int a,
            int b,
            ref CorrectionArtifactMetrics metrics)
        {
            if (a < 0 || b < 0 || a >= reference.Length || b >= reference.Length || !mask[a] || !mask[b])
                return;

            ulong key = EdgeKey(a, b);
            if (!edges.Add(key))
                return;

            float before = (reference[b] - reference[a]).magnitude;
            if (before <= 1e-6f)
                return;

            float after = (reference[b] + corrections[b] - reference[a] - corrections[a]).magnitude;
            float ratio = Mathf.Max(after / before, before / Mathf.Max(after, 1e-6f));
            metrics.edges++;
            metrics.maxEdgeRatio = Mathf.Max(metrics.maxEdgeRatio, ratio);
            metrics.maxCorrectionJump = Mathf.Max(metrics.maxCorrectionJump, (corrections[b] - corrections[a]).magnitude);
        }

        private static ulong EdgeKey(int a, int b)
        {
            uint lo = (uint)Mathf.Min(a, b);
            uint hi = (uint)Mathf.Max(a, b);
            return ((ulong)lo << 32) | hi;
        }

        private static bool[] BuildBoundaryVertexMask(Mesh mesh)
        {
            var triangles = mesh.triangles;
            var counts = new Dictionary<ulong, int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                AddBoundaryEdge(counts, triangles[t], triangles[t + 1]);
                AddBoundaryEdge(counts, triangles[t + 1], triangles[t + 2]);
                AddBoundaryEdge(counts, triangles[t + 2], triangles[t]);
            }

            var boundary = new bool[mesh.vertexCount];
            foreach (var entry in counts)
            {
                if (entry.Value != 1)
                    continue;
                int a = (int)(entry.Key >> 32);
                int b = (int)(entry.Key & 0xffffffff);
                if (a >= 0 && a < boundary.Length) boundary[a] = true;
                if (b >= 0 && b < boundary.Length) boundary[b] = true;
            }
            return boundary;
        }

        private static void AddBoundaryEdge(Dictionary<ulong, int> counts, int a, int b)
        {
            if (a < 0 || b < 0 || a == b)
                return;
            if (a > b)
            {
                int tmp = a;
                a = b;
                b = tmp;
            }

            ulong key = ((ulong)(uint)a << 32) | (uint)b;
            counts.TryGetValue(key, out int count);
            counts[key] = count + 1;
        }

        private static float MaxAbsLocalDeltaComponent(
            Mesh mesh,
            Vector3[] deltas,
            Func<Vector3, bool> contains,
            Func<Vector3, float> component)
        {
            var vertices = mesh.vertices;
            float max = 0f;
            int count = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i]))
                    continue;
                max = Mathf.Max(max, Mathf.Abs(component(deltas[i])));
                count++;
            }

            AssertTrue(count > 0, "No vertices matched the requested delta component region.");
            return max;
        }

        private static Vector3[] WorldShapeDeltas(MeshSnapshot snapshot, Vector3[] localDeltas)
        {
            AssertTrue(snapshot != null, "Cannot convert shape deltas without a mesh snapshot.");
            AssertTrue(localDeltas != null && localDeltas.Length == snapshot.localVertices.Length,
                "Shape delta count does not match the target snapshot vertex count.");

            var world = new Vector3[localDeltas.Length];
            for (int i = 0; i < world.Length; i++)
                world[i] = snapshot.skinMatrices[i].MultiplyVector(localDeltas[i]);
            return world;
        }

        private static ClearanceMetrics MeasureTransferredClearance(
            ReFitTestFixture fixture,
            ReFitComputation comp,
            ReFitSettings settings,
            Func<Vector3, bool> contains)
        {
            var shapeName = SingleSecondaryShape(comp);
            var assetShapeDeltas = GetBlendShapeDeltas(comp.mesh, shapeName);
            Vector3[] primaryDeltas = null;
            if (!string.IsNullOrEmpty(comp.primaryShapeName))
                primaryDeltas = GetBlendShapeDeltas(comp.mesh, comp.primaryShapeName);

            var bodyShapeDeltas = GetBlendShapeDeltas(fixture.target.mesh, BodyShapeName);
            int bodyShapeIndex = fixture.target.mesh.GetBlendShapeIndex(BodyShapeName);
            var targetOverrides = new Dictionary<int, float> { { bodyShapeIndex, 0f } };
            var report = new ReFitReport();
            var targetBasis = MeshSnapshot.Capture(fixture.target.renderer, false, targetOverrides, report);
            var assetBasis = MeshSnapshot.Capture(fixture.sourceSpaceAccessory.renderer, false, null, report);
            var targetBvh = SurfaceBvh.Build(targetBasis);
            var worldBodyDeltas = WorldShapeDeltas(targetBasis, bodyShapeDeltas);
            var metrics = new ClearanceMetrics
            {
                minSourceClearance = float.MaxValue,
                minDesiredClearance = float.MaxValue,
                minShapedClearance = float.MaxValue
            };
            double shapedTotal = 0d;

            for (int i = 0; i < assetBasis.worldVertices.Length; i++)
            {
                if (contains != null && !contains(assetBasis.localVertices[i]))
                    continue;

                var hit = targetBvh.ClosestPoint(assetBasis.worldVertices[i], 0.4f, null);
                if (!hit.found) continue;

                var normal = targetBasis.BaryNormal(hit.triangle, hit.bary);
                float sourceClearance = Vector3.Dot(assetBasis.worldVertices[i] - hit.position, normal);
                if (sourceClearance < 0.015f) continue;

                var bodyDelta = SampleWorldShapeDelta(targetBasis, worldBodyDeltas, hit.triangle, hit.bary);
                var shapedBodyPoint = hit.position + bodyDelta;
                var primaryDelta = primaryDeltas != null
                    ? assetBasis.skinMatrices[i].MultiplyVector(primaryDeltas[i])
                    : Vector3.zero;
                var shapedAssetPoint = assetBasis.worldVertices[i] +
                                       primaryDelta +
                                       assetBasis.skinMatrices[i].MultiplyVector(assetShapeDeltas[i]);
                float shapedClearance = Vector3.Dot(shapedAssetPoint - shapedBodyPoint, normal);
                float expansion = Mathf.Max(0f, Vector3.Dot(bodyDelta, normal));
                float desiredClearance = AdaptiveDesiredClearance(sourceClearance, expansion, settings);

                metrics.tested++;
                metrics.minSourceClearance = Mathf.Min(metrics.minSourceClearance, sourceClearance);
                metrics.minDesiredClearance = Mathf.Min(metrics.minDesiredClearance, desiredClearance);
                metrics.minShapedClearance = Mathf.Min(metrics.minShapedClearance, shapedClearance);
                metrics.maxClearanceLoss = Mathf.Max(metrics.maxClearanceLoss, sourceClearance - shapedClearance);
                metrics.maxBelowDesired = Mathf.Max(metrics.maxBelowDesired, desiredClearance - shapedClearance);
                metrics.averageExpansion += expansion;
                shapedTotal += shapedClearance;
                if (shapedClearance <= 0f) metrics.signFlips++;
            }

            if (metrics.tested > 0)
            {
                metrics.averageShapedClearance = (float)(shapedTotal / metrics.tested);
                metrics.averageExpansion /= metrics.tested;
            }
            return metrics;
        }

        private static float AdaptiveDesiredClearance(float sourceClearance, float expansion, ReFitSettings settings)
        {
            settings = settings ?? new ReFitSettings();
            float safe = Mathf.Max(0f, settings.clearanceMinimumSafetyDistance);
            float source = Mathf.Max(sourceClearance, safe);
            float tight = Mathf.Max(safe, source * Mathf.Clamp01(settings.clearanceTightnessFactor));
            float t = Smooth01(settings.clearanceExpansionStart, settings.clearanceExpansionFull, expansion);
            return Mathf.Lerp(source, tight, t);
        }

        private static float Smooth01(float start, float end, float value)
        {
            if (Mathf.Abs(end - start) <= 1e-6f)
                return value >= end ? 1f : 0f;
            float t = Mathf.Clamp01((value - start) / (end - start));
            return t * t * (3f - 2f * t);
        }

        private static Vector3 SampleWorldShapeDelta(MeshSnapshot snapshot, Vector3[] worldDeltas, int triangle, Vector3 bary)
        {
            int t = triangle * 3;
            return worldDeltas[snapshot.triangles[t]] * bary.x +
                   worldDeltas[snapshot.triangles[t + 1]] * bary.y +
                   worldDeltas[snapshot.triangles[t + 2]] * bary.z;
        }

        private static bool IsLeftUpperSleeve(Vector3 vertex)
        {
            return vertex.x <= -0.7f && vertex.y >= 0.95f && vertex.y <= 1.45f;
        }

        private static void AddLocalizedPeakBlendshape(Mesh mesh, string shapeName)
        {
            AssertTrue(mesh.GetBlendShapeIndex(shapeName) < 0,
                $"Mesh '{mesh.name}' already contains blendshape '{shapeName}'.");

            var vertices = mesh.vertices;
            var deltas = new Vector3[vertices.Length];
            int affected = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                float dx = (v.x + 0.9f) / 0.3f;
                float dy = (v.y - 1.2f) / 0.3f;
                float weight = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                if (weight <= 0f) continue;

                deltas[i] = new Vector3(-0.08f, 0f, 0.08f) * weight;
                affected++;
            }

            AssertGreater(affected, 0, "Localized peak fixture did not affect any target body vertices.");
            mesh.AddBlendShapeFrame(shapeName, 100f, deltas, null, null);
        }

        private static SurfaceMetrics MeasureSurfaceDistance(SkinnedMeshRenderer from, SkinnedMeshRenderer to)
        {
            var report = new ReFitReport();
            var fromSnap = MeshSnapshot.Capture(from, false, null, report);
            var toSnap = MeshSnapshot.Capture(to, false, null, report);
            var toBvh = SurfaceBvh.Build(toSnap);
            var fromBounds = BoundsOf(fromSnap.worldVertices);
            var toBounds = BoundsOf(toSnap.worldVertices);
            var distances = new List<float>(fromSnap.worldVertices.Length);
            double sum = 0d;
            double sumSq = 0d;
            float max = 0f;

            for (int i = 0; i < fromSnap.worldVertices.Length; i++)
            {
                var hit = toBvh.ClosestPoint(fromSnap.worldVertices[i], 20f, null);
                AssertTrue(hit.found,
                    $"No closest point found for vertex {i} on '{from.name}'. " +
                    $"From bounds {FormatBounds(fromBounds)}, to bounds {FormatBounds(toBounds)}.");
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

        private static void AssertRendererBoundsCompatible(string label, SkinnedMeshRenderer generated,
            SkinnedMeshRenderer expected)
        {
            var report = new ReFitReport();
            var generatedSnap = MeshSnapshot.Capture(generated, false, null, report);
            var expectedSnap = MeshSnapshot.Capture(expected, false, null, report);
            var generatedBounds = BoundsOf(generatedSnap.worldVertices);
            var expectedBounds = BoundsOf(expectedSnap.worldVertices);

            float centerDistance = Vector3.Distance(generatedBounds.center, expectedBounds.center);
            float expectedMagnitude = Mathf.Max(expectedBounds.size.magnitude, 0.001f);
            float maxCenterDistance = Mathf.Max(1f, expectedMagnitude * 0.75f);
            AssertLessOrEqual(centerDistance, maxCenterDistance,
                $"{label} generated bounds are not in the same space as the authored result. " +
                $"Generated {FormatBounds(generatedBounds)}, expected {FormatBounds(expectedBounds)}.");

            float sizeRatio = MaxSizeRatio(generatedBounds.size, expectedBounds.size);
            AssertLessOrEqual(sizeRatio, 4f,
                $"{label} generated bounds size differs implausibly from the authored result. " +
                $"Generated {FormatBounds(generatedBounds)}, expected {FormatBounds(expectedBounds)}.");
        }

        private static void AssertFbxPrimaryDeltaScale(ReFitComputation comp, string label, float maxAllowed)
        {
            AssertTrue(comp != null && comp.mesh != null, $"{label} computation did not produce a mesh.");
            AssertTrue(!string.IsNullOrEmpty(comp.primaryShapeName),
                $"{label} computation did not produce a primary refit blendshape.");
            float maxPrimary = MaxBlendShapeMagnitude(comp.mesh, comp.primaryShapeName);
            Debug.Log($"[ReFit Tests] {label} primary refit max local delta: {maxPrimary:0.######}");
            AssertLessOrEqual(maxPrimary, maxAllowed,
                $"{label} primary refit delta is implausibly large for the authored fixture. " +
                $"Max local delta was {maxPrimary:0.######}; this usually means skinned-pose or FBX scale was baked into the refit shape.");
        }

        private static void AssertFbxTransferredShapeProfile(string label, SkinnedMeshRenderer generated,
            SkinnedMeshRenderer expected)
        {
            var generatedProfile = MeasureShapeMagnitude(generated.sharedMesh, FbxShapeName);
            var expectedProfile = MeasureShapeMagnitude(expected.sharedMesh, FbxShapeName);
            Debug.Log($"[ReFit Tests] {label} generated shape profile {generatedProfile}");
            Debug.Log($"[ReFit Tests] {label} expected shape profile {expectedProfile}");

            AssertLessOrEqual(Mathf.Abs(generatedProfile.average - expectedProfile.average), 0.00035f,
                $"{label} transferred shape average magnitude does not match the authored result.");
            AssertLessOrEqual(Mathf.Abs(generatedProfile.max - expectedProfile.max), 0.0005f,
                $"{label} transferred shape max magnitude does not match the authored result.");
            AssertLessOrEqual(Mathf.Abs(generatedProfile.nonzeroFraction - expectedProfile.nonzeroFraction), 0.12f,
                $"{label} transferred shape affects a very different share of the mesh than the authored result.");
        }

        private static ShapeMagnitudeMetrics MeasureShapeMagnitude(Mesh mesh, string shapeName)
        {
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            var metrics = new ShapeMagnitudeMetrics();
            if (deltas.Length == 0) return metrics;

            for (int i = 0; i < deltas.Length; i++)
            {
                float magnitude = deltas[i].magnitude;
                metrics.average += magnitude;
                metrics.max = Mathf.Max(metrics.max, magnitude);
                if (magnitude > 1e-6f)
                    metrics.nonzero++;
            }

            metrics.average /= deltas.Length;
            metrics.nonzeroFraction = metrics.nonzero / (float)deltas.Length;
            return metrics;
        }

        private static Bounds BoundsOf(Vector3[] points)
        {
            AssertTrue(points != null && points.Length > 0, "Cannot compute bounds for an empty point set.");
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++)
                bounds.Encapsulate(points[i]);
            return bounds;
        }

        private static float MaxSizeRatio(Vector3 a, Vector3 b)
        {
            return Mathf.Max(
                AxisRatio(a.x, b.x),
                Mathf.Max(AxisRatio(a.y, b.y), AxisRatio(a.z, b.z)));
        }

        private static float AxisRatio(float a, float b)
        {
            a = Mathf.Abs(a);
            b = Mathf.Abs(b);
            if (a <= 1e-5f && b <= 1e-5f) return 1f;
            return Mathf.Max(a, b) / Mathf.Max(Mathf.Min(a, b), 1e-5f);
        }

        private static string FormatBounds(Bounds bounds)
        {
            return $"center={bounds.center.ToString("F3")}, size={bounds.size.ToString("F3")}";
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

        private static Transform FindRendererBone(SkinnedMeshRenderer renderer, string boneName)
        {
            var bones = renderer.bones;
            if (bones == null) return null;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && string.Equals(bones[i].name, boneName, StringComparison.OrdinalIgnoreCase))
                    return bones[i];
            return null;
        }

        private static Transform FindDirectChildStartingWith(Transform parent, string prefix)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name.StartsWith(prefix, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        private static string ChildNames(Transform parent)
        {
            if (parent == null) return "<null>";
            var names = new List<string>();
            for (int i = 0; i < parent.childCount; i++)
                names.Add(parent.GetChild(i).name);
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "<none>";
        }

        private static SurfaceBinding Binding(Vector3 bary, BodyRegion hitRegion)
        {
            return new SurfaceBinding
            {
                valid = true,
                triangle = 0,
                bary = bary,
                point = Vector3.zero,
                distance = 0f,
                requestedRegion = hitRegion,
                hitRegion = hitRegion,
                normalDot = 1f
            };
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

        private static void AssertReportContains(ReFitReport report, string code, string message)
        {
            if (report != null)
            {
                foreach (var entry in report.messages)
                    if (entry != null && entry.code == code)
                        return;
            }

            throw new Exception(message + "\n" + FormatReport(report));
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            AssertTrue(field != null, $"Could not reflect field '{fieldName}' on '{target.GetType().Name}'.");
            field.SetValue(target, value);
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

        private struct ClearanceMetrics
        {
            public int tested;
            public int signFlips;
            public float minSourceClearance;
            public float minDesiredClearance;
            public float minShapedClearance;
            public float maxBelowDesired;
            public float maxClearanceLoss;
            public float averageShapedClearance;
            public float averageExpansion;
        }

        private struct ShapeMagnitudeMetrics
        {
            public float average;
            public float max;
            public int nonzero;
            public float nonzeroFraction;

            public override string ToString()
            {
                return $"avg={average:0.000000} max={max:0.000000} nonzero={nonzero} fraction={nonzeroFraction:0.000}";
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

        private struct CorrectionArtifactMetrics
        {
            public int vertices;
            public int edges;
            public float minCorrection;
            public float averageCorrection;
            public float maxCorrection;
            public float maxCorrectionJump;
            public float maxEdgeRatio;
        }

        private struct SurfaceRelationMetrics
        {
            public int samples;
            public float minBaseDistance;
            public float averageBaseDistance;
            public float maxBaseDistance;
            public float averageDistanceDrift;
            public float p95DistanceDrift;
            public float maxDistanceDrift;
            public float averageSupportResidual;
            public float p95SupportResidual;
            public float maxSupportResidual;
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
