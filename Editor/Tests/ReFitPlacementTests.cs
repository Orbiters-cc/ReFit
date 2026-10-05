using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    // Clothing dropped onto an avatar keeps its own armature, with the avatar's bone names, under the avatar root; and
    // avatars stand anywhere in a scene. Results must not depend on either: a nested copy once lost the avatar's world
    // offset during staging, and its armature stood in for the avatar's skeleton.
    public static partial class ReFitDeterministicTestRunner
    {
        private struct Placement
        {
            public string label;
            public Vector3 position;
            public Quaternion rotation;
            public float scale;
        }

        private static readonly Placement[] Placements =
        {
            new Placement { label = "at the origin", position = Vector3.zero, rotation = Quaternion.identity, scale = 1f },
            new Placement { label = "beside the origin", position = new Vector3(-1f, 0f, 0f), rotation = Quaternion.identity, scale = 1f },
            new Placement { label = "far away and turned", position = new Vector3(12.5f, 3f, -40f), rotation = Quaternion.Euler(0f, 137f, 0f), scale = 1f },
        };

        private static void RunPlacementChecks(List<string> failures)
        {
            RunCase(failures, "Clothing nested under an avatar away from the origin stays on it while staged", Staging_NestedClothingKeepsWorldPose);
            RunCase(failures, "A clothing armature copied with the avatar never stands in for its skeleton", Staging_ClothingCopyIsNotTheSkeleton);
            RunCase(failures, "Transferred body blendshapes follow the body wherever the avatar stands", Transfer_IndependentOfAvatarPlacement);
        }

        private static ReFitTestFixture NestedClothingFixture(Placement placement)
        {
            var fixture = ReFitTestFixture.Create();
            fixture.targetSpaceAccessory.root.transform.SetParent(fixture.target.root.transform, true);
            fixture.target.root.transform.SetPositionAndRotation(placement.position, placement.rotation);
            fixture.target.root.transform.localScale = Vector3.one * placement.scale;
            return fixture;
        }

        internal static void Staging_NestedClothingKeepsWorldPose()
        {
            var placements = Placements.Append(new Placement
                { label = "scaled and turned", position = new Vector3(3f, 0f, 2f), rotation = Quaternion.Euler(0f, -60f, 0f), scale = 1.3f });
            foreach (var placement in placements)
                using (var fixture = NestedClothingFixture(placement))
                {
                    var report = new ReFitReport();
                    var stage = PoseNormalizer.CreateStage(BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer), report);
                    try
                    {
                        AssertTrue(stage != null && stage.assetInTargetSpace, $"{placement.label}: the nested clothing was not staged in its avatar's space.\n{FormatReport(report)}");
                        AssertLessOrEqual(MaxWorldDistance(fixture.targetSpaceAccessory.renderer, stage.assetRenderer), 0.0001f,
                            $"{placement.label}: the staged clothing left its avatar.");
                        AssertLessOrEqual(MaxWorldDistance(fixture.target.renderer, stage.targetBody), 0.0001f,
                            $"{placement.label}: the staged body moved.");
                    }
                    finally { stage?.Dispose(); }
                }
        }

        internal static void Staging_ClothingCopyIsNotTheSkeleton()
        {
            using (var fixture = NestedClothingFixture(Placements[1]))
            {
                var report = new ReFitReport();
                var stage = PoseNormalizer.CreateStage(BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer), report);
                try
                {
                    AssertTrue(stage != null && stage.targetExcludedAssetRoot != null, "The avatar's own copy of the clothing was not set apart.\n" + FormatReport(report));
                    foreach (var pair in stage.assetBoneToSource)
                        AssertTrue(pair.Value == null || !pair.Value.IsChildOf(stage.targetExcludedAssetRoot),
                            $"Clothing bone '{pair.Key.name}' matched its own copy instead of the avatar's skeleton.");
                    var hips = stage.assetRenderer.bones[(int)RigBone.Hips];
                    AssertTrue(stage.assetBoneToSource.TryGetValue(hips, out var match) && match == stage.targetRoot.transform.Find("Hips"),
                        "The clothing's hips did not match the avatar's hips.");
                }
                finally { stage?.Dispose(); }
            }
        }

        internal static void Transfer_IndependentOfAvatarPlacement()
        {
            Vector3[] reference = null;
            foreach (var placement in Placements)
                using (var fixture = NestedClothingFixture(placement))
                {
                    var comp = new ReFitEngine().Run(BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer));
                    try
                    {
                        AssertComputationSucceeded(comp);
                        AssertTrue(comp.report.messages.All(m => m.code != "unbound-vertices"),
                            $"{placement.label}: clothing found no body surface to follow.\n{FormatReport(comp.report)}");
                        var deltas = GetBlendShapeDeltas(comp.mesh, BodyShapeName);
                        AssertGreaterOrEqual(FollowRatio(fixture, deltas), 0.75f, $"{placement.label}: the clothing does not follow the body's shape.");
                        if (reference == null) { reference = deltas; continue; }
                        float worst = 0f;
                        for (int v = 0; v < deltas.Length; v++) worst = Mathf.Max(worst, (deltas[v] - reference[v]).magnitude);
                        AssertLessOrEqual(worst, 0.001f, $"{placement.label}: the result changed with where the avatar stands.");
                    }
                    finally { DestroyComputationMesh(comp); }
                }
        }

        // How much of the body's shape motion beneath each clothing vertex the clothing keeps (1: all of it). Both
        // meshes share the avatar's local frame in the fixture, so their blendshape deltas compare directly.
        private static float FollowRatio(ReFitTestFixture fixture, Vector3[] clothingDeltas)
        {
            var body = fixture.target.mesh;
            var bodyDeltas = GetBlendShapeDeltas(body, BodyShapeName);
            var bodyVertices = body.vertices;
            var clothingVertices = fixture.targetSpaceAccessory.mesh.vertices;
            float sum = 0f;
            int count = 0;
            for (int v = 0; v < clothingVertices.Length; v++)
            {
                int nearest = 0;
                float best = float.MaxValue;
                for (int b = 0; b < bodyVertices.Length; b++)
                {
                    float d = (bodyVertices[b] - clothingVertices[v]).sqrMagnitude;
                    if (d < best) { best = d; nearest = b; }
                }
                var bodyDelta = bodyDeltas[nearest];
                if (bodyDelta.magnitude < 0.002f) continue;
                sum += Vector3.Dot(clothingDeltas[v], bodyDelta.normalized) / bodyDelta.magnitude;
                count++;
            }
            AssertTrue(count > 0, "The fixture's body shape does not move under the clothing.");
            return sum / count;
        }

        private static float MaxWorldDistance(SkinnedMeshRenderer expected, SkinnedMeshRenderer actual)
        {
            var a = MeshSnapshot.Capture(expected, false, null, null).worldVertices;
            var b = MeshSnapshot.Capture(actual, false, null, null).worldVertices;
            AssertTrue(a.Length == b.Length, $"'{actual.name}' changed its vertex count while staged.");
            float worst = 0f;
            for (int v = 0; v < a.Length; v++) worst = Mathf.Max(worst, (a[v] - b[v]).magnitude);
            return worst;
        }

        private static void AssertGreaterOrEqual(float actual, float expectedMinimum, string message)
        {
            if (!(actual >= expectedMinimum))
                throw new Exception($"{message} Expected >= {expectedMinimum:0.####}, got {actual:0.####}.");
        }
    }

    /// <summary>Test Runner entry points for the placement regressions (the full suite runs from Tools > Orbiters > ReFit).</summary>
    public sealed class ReFitPlacementTests
    {
        [Test] public void NestedClothingKeepsItsWorldPoseWhileStaged() => ReFitDeterministicTestRunner.Staging_NestedClothingKeepsWorldPose();
        [Test] public void ClothingCopyIsNotTheAvatarSkeleton() => ReFitDeterministicTestRunner.Staging_ClothingCopyIsNotTheSkeleton();
        [Test] public void TransferIsIndependentOfAvatarPlacement() => ReFitDeterministicTestRunner.Transfer_IndependentOfAvatarPlacement();
    }
}
