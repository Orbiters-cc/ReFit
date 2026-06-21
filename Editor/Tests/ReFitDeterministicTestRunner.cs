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
                    "XRay extra gizmo registry exposes external toggles",
                    XRayExtraGizmoRegistry_RegistersAndTogglesExternalGizmo);
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

                    var shapeName = SingleSecondaryShape(comp);
                    var assetShapeDeltas = GetBlendShapeDeltas(comp.mesh, shapeName);
                    var bodyShapeDeltas = GetBlendShapeDeltas(fixture.target.mesh, BodyShapeName);
                    int bodyShapeIndex = fixture.target.mesh.GetBlendShapeIndex(BodyShapeName);
                    var targetOverrides = new Dictionary<int, float> { { bodyShapeIndex, 0f } };
                    var report = new ReFitReport();
                    var targetBasis = MeshSnapshot.Capture(fixture.target.renderer, false, targetOverrides, report);
                    var assetBasis = MeshSnapshot.Capture(fixture.sourceSpaceAccessory.renderer, false, null, report);
                    var targetBvh = SurfaceBvh.Build(targetBasis);
                    var worldBodyDeltas = WorldShapeDeltas(targetBasis, bodyShapeDeltas);

                    int tested = 0;
                    int signFlips = 0;
                    float minSourceClearance = float.MaxValue;
                    float minShapedClearance = float.MaxValue;
                    float maxClearanceLoss = 0f;

                    for (int i = 0; i < assetBasis.worldVertices.Length; i++)
                    {
                        var hit = targetBvh.ClosestPoint(assetBasis.worldVertices[i], 0.4f, null);
                        if (!hit.found) continue;

                        var normal = targetBasis.BaryNormal(hit.triangle, hit.bary);
                        float sourceClearance = Vector3.Dot(assetBasis.worldVertices[i] - hit.position, normal);
                        if (sourceClearance < 0.015f) continue;

                        var shapedBodyPoint = hit.position + SampleWorldShapeDelta(targetBasis, worldBodyDeltas, hit.triangle, hit.bary);
                        var shapedAssetPoint = assetBasis.worldVertices[i] + assetBasis.skinMatrices[i].MultiplyVector(assetShapeDeltas[i]);
                        float shapedClearance = Vector3.Dot(shapedAssetPoint - shapedBodyPoint, normal);

                        tested++;
                        minSourceClearance = Mathf.Min(minSourceClearance, sourceClearance);
                        minShapedClearance = Mathf.Min(minShapedClearance, shapedClearance);
                        maxClearanceLoss = Mathf.Max(maxClearanceLoss, sourceClearance - shapedClearance);
                        if (shapedClearance <= 0f) signFlips++;
                    }

                    Debug.Log(
                        $"[ReFit Tests] Signed clearance: tested={tested}, flips={signFlips}, " +
                        $"sourceMin={minSourceClearance:0.000000}, shapedMin={minShapedClearance:0.000000}, " +
                        $"maxLoss={maxClearanceLoss:0.000000}");

                    AssertGreater(tested, 20,
                        "Signed-clearance test did not find enough high-confidence clothing/body projection pairs.");
                    AssertTrue(signFlips == 0,
                        $"Transferred blendshape moved {signFlips} clothing vertices under the shaped target skin.");
                    AssertGreater(minShapedClearance, 0.012f,
                        "Transferred blendshape did not keep the clothing safely above the shaped target skin.");
                    AssertLessOrEqual(maxClearanceLoss, 0.008f,
                        "Transferred blendshape lost too much clothing/body clearance compared with the default fit.");
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
                }
            }
            finally
            {
                ReFitDebugService.Enabled = previousDebug;
                ReFitProjectionGizmoService.Enabled = previousGizmo;
                Object.DestroyImmediate(window);
            }
        }

        private static void XRayExtraGizmoRegistry_RegistersAndTogglesExternalGizmo()
        {
            const string id = "orbiters.refit.tests.fake-extra-gizmo";
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
