using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        private static void RunArchitectureChecks(List<string> failures)
        {
            RunCase(failures, "BVH matches brute force with concurrent and reentrant queries", Bvh_MatchesBruteForce);
            RunCase(failures, "API snapshots mutable request options", Request_OptionsAreSnapshots);
            RunCase(failures, "MCB coroutine contract applies multiple shapes through the public service", Service_McbContractAppliesMultipleShapes);
            RunCase(failures, "Service applies output without a completion callback", Service_NullCallbackStillApplies);
            RunCase(failures, "Staging meshes are released after validation and abandoned jobs", Staging_ReleasesOwnedMeshes);
            RunCase(failures, "Cancelled engine jobs never bake a mesh", Engine_CancellationDoesNotBake);
            RunCase(failures, "Late cancellation and completion callbacks preserve output ownership", Engine_CallbackOwnership);
            RunCase(failures, "Unpacking an imported prefab restores connectivity on undo", Application_PrefabConnectivityUndo);
            RunCase(failures, "Failed armature application restores the original hierarchy and mesh", Application_FailureRollsBack);
            RunCase(failures, "Input edits with unchanged mesh counts invalidate asynchronous results", InputState_DetectsMeshEdits);
            RunCase(failures, "Transfer cache rejects changed settings and same-count shape data", Cache_RejectsChangedInputs);
            RunCase(failures, "Avatar ancestor names do not change fitting", Garment_AncestorNameIsIrrelevant);
            RunCase(failures, "Public tightness presets match loose, middle and tight policy", Presets_HaveExpectedEndpoints);
            RunCase(failures, "FBX v2 inserted joint preserves the v1 result within 1mm", Fbx_InsertedJointDoesNotDistortSurface);
        }

        private static void Bvh_MatchesBruteForce()
        {
            var random = new System.Random(31051);
            var vertices = new Vector3[192];
            var triangles = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                triangles[i] = i;
            }
            var bvh = SurfaceBvh.Build(new MeshSnapshot { worldVertices = vertices, triangles = triangles });
            Parallel.For(0, 128, i =>
            {
                var point = new Vector3(i / 127f, 0.6f, 0.3f);
                float expected = float.MaxValue;
                for (int t = 0; t < triangles.Length / 3; t += 2)
                    expected = Mathf.Min(expected, (SurfaceBvh.ClosestPointOnTriangle(point,
                        vertices[t * 3], vertices[t * 3 + 1], vertices[t * 3 + 2], out _) - point).magnitude);
                var hit = bvh.ClosestPoint(point, 10f, t =>
                {
                    if (t == 0) AssertTrue(bvh.ClosestPoint(point, 10f).found, "Reentrant query failed.");
                    return t % 2 == 0;
                });
                AssertTrue(hit.found && hit.triangle % 2 == 0, "BVH ignored the filter.");
                AssertLessOrEqual(Mathf.Abs(hit.distance - expected), 0.000001f, "BVH differs from brute-force distance.");
            });
        }

        private static void Request_OptionsAreSnapshots()
        {
            var request = new ReFitRequest { targetBlendshapes = new List<string> { "one" } };
            var copy = request.Clone();
            request.settings.maxProjectionDistance = 7f;
            request.targetBlendshapes.Add("two");
            AssertTrue(copy.targetBlendshapes.Count == 1 && copy.settings.maxProjectionDistance == 0.25f,
                "Cloned request shares mutable options.");
        }

        private static void Service_McbContractAppliesMultipleShapes()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, true);
                var body = fixture.target.renderer.sharedMesh;
                var deltas = new Vector3[body.vertexCount];
                body.GetBlendShapeFrameVertices(body.GetBlendShapeIndex(BodyShapeName), 0, deltas, null, null);
                for (int i = 0; i < deltas.Length; i++) deltas[i] *= 0.5f;
                body.AddBlendShapeFrame("SecondMuscle", 100f, deltas, null, null);
                request.targetBlendshapes = new List<string> { BodyShapeName, "SecondMuscle" };
                request.settings.savePrefab = false;
                var method = typeof(ReFitService).GetMethod("ExecuteCoroutine", new[]
                    { typeof(ReFitRequest), typeof(ReFitProgress), typeof(Action<ReFitResult>) });
                AssertTrue(method != null, "MCB's public three-argument coroutine entry point is missing.");
                ReFitResult result = null;
                bool debug = ReFitDebugService.Enabled;
                ReFitDebugService.Enabled = false;
                IEnumerator coroutine = null;
                try
                {
                    coroutine = (IEnumerator)method.Invoke(null, new object[] { request, null, new Action<ReFitResult>(r => result = r) });
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (coroutine.MoveNext())
                    {
                        if (timeout.Elapsed.TotalSeconds > 15) throw new Exception("Service coroutine did not finish.");
                        Thread.Sleep(1);
                    }
                    AssertTrue(result != null && result.success, "Public application failed: " + FormatReport(result?.report));
                    AssertTrue(result.secondaryShapeNames.Length == 2 && AssetDatabase.Contains(result.mesh),
                        "Service did not persist both transferred shapes.");
                    AssertRootBoneInRendererBones(result.sceneRenderer, "Public service");
                    AssertTrue(ReFitBlendshapeHistory.Read().Contains(BodyShapeName) && ReFitBlendshapeHistory.Read().Contains("SecondMuscle"),
                        "Successful multi-shape refit did not update recents.");
                }
                finally
                {
                    (coroutine as IDisposable)?.Dispose();
                    ReFitDebugService.Enabled = debug;
                    if (!string.IsNullOrEmpty(result?.meshAssetPath)) AssetDatabase.DeleteAsset(result.meshAssetPath);
                    else if (result?.mesh != null) Object.DestroyImmediate(result.mesh);
                }
            }
        }

        private static void Staging_ReleasesOwnedMeshes()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                int before = CountStagedMeshes();
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                new ReFitEngine().Validate(request);
                AssertTrue(CountStagedMeshes() == before, "Dry-run validation leaked a pose-baked mesh.");
                var coroutine = new ReFitEngine().RunCoroutine(request, null, _ => { });
                coroutine.MoveNext();
                (coroutine as IDisposable)?.Dispose();
                AssertTrue(CountStagedMeshes() == before, "Abandoned coroutine leaked a pose-baked mesh.");
            }
        }

        private static void Service_NullCallbackStillApplies()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var renderer = fixture.sourceSpaceAccessory.renderer;
                var original = renderer.sharedMesh;
                var request = BuildMeshAndBlendshapeRequest(fixture, renderer, true);
                request.settings.savePrefab = false;
                bool debug = ReFitDebugService.Enabled;
                ReFitDebugService.Enabled = false;
                var coroutine = ReFitService.ExecuteCoroutine(request, null, null);
                try
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (coroutine.MoveNext())
                    {
                        if (timer.Elapsed.TotalSeconds > 15) throw new Exception("Service coroutine did not finish.");
                        Thread.Sleep(1);
                    }
                    AssertTrue(renderer.sharedMesh != original && AssetDatabase.Contains(renderer.sharedMesh),
                        "Null callback skipped scene application or saving.");
                }
                finally
                {
                    (coroutine as IDisposable)?.Dispose();
                    ReFitDebugService.Enabled = debug;
                    if (renderer != null && renderer.sharedMesh != original)
                    {
                        string path = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                        if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
                        else Object.DestroyImmediate(renderer.sharedMesh);
                    }
                }
            }
        }

        private static int CountStagedMeshes()
        {
            int count = 0;
            foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                if (mesh.name.EndsWith("_ScenePoseDefault", StringComparison.Ordinal)) count++;
            return count;
        }

        private static void Engine_CancellationDoesNotBake()
        {
            using (var fixture = ReFitTestFixture.Create())
            using (var cancellation = new CancellationTokenSource())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                ReFitComputation result = null;
                var worker = new ReFitEngine().RunCoroutine(request, null, c => result = c, cancellation.Token);
                try
                {
                    AssertTrue(worker.MoveNext(), "Async job did not yield before baking.");
                    cancellation.Cancel();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (worker.MoveNext())
                    {
                        if (timer.Elapsed.TotalSeconds > 10) throw new Exception("Cancelled worker did not terminate.");
                        Thread.Sleep(1);
                    }
                    AssertTrue(result != null && !result.success && result.mesh == null, "Cancellation baked or accepted a mesh.");
                    AssertReportContains(result.report, "refit-cancelled", "Cancellation was not reported.");
                }
                finally { (worker as IDisposable)?.Dispose(); DestroyComputationMesh(result); }
            }
        }

        private static void Application_FailureRollsBack()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var renderer = fixture.sourceSpaceAccessory.renderer;
                var originalMesh = renderer.sharedMesh;
                var originalBones = renderer.bones;
                var originalRoot = renderer.rootBone;
                var originalParent = renderer.transform.parent;
                int hierarchyCount = fixture.sourceSpaceAccessory.root.GetComponentsInChildren<Transform>(true).Length;
                var request = BuildMeshAndBlendshapeRequest(fixture, renderer, true);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    comp.bones[0].path = new[] { int.MaxValue };
                    var result = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(result == null && comp.report.HasErrors, "Incomplete armature was accepted.");
                    AssertTrue(renderer.sharedMesh == originalMesh && renderer.rootBone == originalRoot &&
                        renderer.transform.parent == originalParent, "Failed application changed the renderer or its parent.");
                    AssertTrue(renderer.bones.Length == originalBones.Length, "Failed application changed the bone list.");
                    for (int i = 0; i < originalBones.Length; i++) AssertTrue(renderer.bones[i] == originalBones[i], "Original bone identity was lost.");
                    AssertTrue(fixture.sourceSpaceAccessory.root.GetComponentsInChildren<Transform>(true).Length == hierarchyCount,
                        "Failed application left a generated hierarchy behind.");
                    AssertTrue(renderer.GetComponent<ReFitGeneratedAssetMetadata>() == null, "Failed application left metadata behind.");
                }
                finally { DestroyComputationMesh(comp); }
            }
        }

        private static void Engine_CallbackOwnership()
        {
            using (var fixture = ReFitTestFixture.Create())
            using (var cancellation = new CancellationTokenSource())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var cancelled = new ReFitEngine().Run(request, (_, label) =>
                {
                    if (label == "Baking the mesh") cancellation.Cancel();
                }, cancellation.Token);
                AssertTrue(!cancelled.success && cancelled.mesh == null, "Late cancellation still baked a mesh.");
                var completed = new ReFitEngine().Run(request, (_, label) =>
                {
                    if (label == "Done") throw new InvalidOperationException("Test callback failure");
                });
                try
                {
                    AssertComputationSucceeded(completed);
                    AssertReportContains(completed.report, "progress-callback", "Completion callback failure was not isolated.");
                }
                finally { DestroyComputationMesh(completed); }
            }
        }

        private static void Application_PrefabConnectivityUndo()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxFixtureV1Path);
            if (prefab == null) throw new SkippedTestException("Authored v1 FBX is missing.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try
            {
                var renderer = instance.GetComponentInChildren<SkinnedMeshRenderer>();
                AssertTrue(renderer != null && renderer.transform != instance.transform, "Fixture needs a nested renderer.");
                var method = typeof(ReFitAssetPipeline).GetMethod("MakeRestructurable",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                AssertTrue((bool)method.Invoke(null, new object[] { renderer.transform, new ReFitReport() }), "Could not unpack fixture.");
                AssertTrue(!PrefabUtility.IsPartOfPrefabInstance(instance), "Fixture was not unpacked.");
                Undo.FlushUndoRecordObjects();
                Undo.RevertAllDownToGroup(group);
                AssertTrue(PrefabUtility.IsPartOfPrefabInstance(instance), "Rollback lost prefab connectivity.");
                AssertTrue(PrefabUtility.GetCorrespondingObjectFromSource(instance) == prefab, "Rollback restored the wrong prefab.");
            }
            finally
            {
                Undo.RevertAllDownToGroup(group);
                if (instance != null) Object.DestroyImmediate(instance);
            }
        }

        private static void InputState_DetectsMeshEdits()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var stamp = new ReFitInputState(request);
                AssertTrue(stamp.Unchanged(), "Untouched inputs were considered changed.");
                string identity = ReFitCacheIdentity.Renderer(request.assetRenderer, true);
                var vertices = request.assetRenderer.sharedMesh.vertices;
                vertices[0] += Vector3.right * 0.01f;
                request.assetRenderer.sharedMesh.vertices = vertices;
                AssertTrue(!stamp.Unchanged(), "Same-count vertex edit was ignored.");
                AssertTrue(identity != ReFitCacheIdentity.Renderer(request.assetRenderer, true), "Cache identity ignored vertex content.");
                stamp = new ReFitInputState(request);
                var triangles = request.assetRenderer.sharedMesh.triangles;
                int first = triangles[0]; triangles[0] = triangles[1]; triangles[1] = first;
                request.assetRenderer.sharedMesh.triangles = triangles;
                AssertTrue(!stamp.Unchanged(), "Same-count triangle edit was ignored.");
            }
        }

        private static void Cache_RejectsChangedInputs()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var sourceRequest = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, true);
                var source = new ReFitEngine().Run(sourceRequest);
                ReFitComputation cached = null, changed = null, edited = null;
                try
                {
                    AssertComputationSucceeded(source);
                    var renderer = ReFitAssetPipeline.ApplyToScene(sourceRequest, source, source.report);
                    var request = new ReFitRequest { assetRenderer = renderer, targetAvatar = fixture.target.root,
                        targetBodyRenderer = fixture.target.renderer, targetBlendshape = BodyShapeName,
                        mode = ReFitMode.Blendshape, settings = sourceRequest.settings.Clone() };
                    request.settings.replaceArmature = false;
                    cached = new ReFitEngine().Run(request);
                    AssertComputationSucceeded(cached);
                    AssertReportContains(cached.report, "target-space-cached-transferred-shape", "Unchanged transfer did not reuse its valid cache.");
                    string identity = ReFitCacheIdentity.Renderer(renderer, true);
                    int primary = renderer.sharedMesh.GetBlendShapeIndex(source.primaryShapeName);
                    float primaryWeight = renderer.GetBlendShapeWeight(primary);
                    renderer.SetBlendShapeWeight(primary, primaryWeight - 10f);
                    AssertTrue(identity != ReFitCacheIdentity.Renderer(renderer, true), "Primary refit slider did not invalidate base identity.");
                    renderer.SetBlendShapeWeight(primary, primaryWeight);
                    request.settings.transferredBlendshapeSmoothingIterations = 3;
                    request.settings.transferredBlendshapeSmoothingStrength = 0.8f;
                    changed = new ReFitEngine().Run(request);
                    AssertComputationSucceeded(changed);
                    AssertReportDoesNotContain(changed.report, "target-space-cached-transferred-shape", "Changed smoothing reused old deltas.");
                    request.settings = sourceRequest.settings.Clone();
                    var mesh = fixture.target.renderer.sharedMesh;
                    var deltas = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(BodyShapeName), 0, deltas, null, null);
                    for (int i = 0; i < deltas.Length; i++) deltas[i] *= 2f;
                    mesh.ClearBlendShapes();
                    mesh.AddBlendShapeFrame(BodyShapeName, 100f, deltas, null, null);
                    edited = new ReFitEngine().Run(request);
                    AssertComputationSucceeded(edited);
                    AssertReportDoesNotContain(edited.report, "target-space-cached-transferred-shape", "Changed same-count body shape reused old deltas.");
                }
                finally
                {
                    DestroyComputationMesh(source); DestroyComputationMesh(cached);
                    DestroyComputationMesh(changed); DestroyComputationMesh(edited);
                }
            }
        }

        private static void Garment_AncestorNameIsIrrelevant()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var before = new ReFitEngine().Run(request);
                fixture.sourceSpaceAccessory.root.name = "Avatar wearing pants and boots";
                var after = new ReFitEngine().Run(request);
                try { AssertEquivalentShapes(before, after, Matrix4x4.identity, 0.000001f); }
                finally { DestroyComputationMesh(before); DestroyComputationMesh(after); }
            }
        }

        private static void Presets_HaveExpectedEndpoints()
        {
            var settings = new ReFitSettings();
            ReFitSettingsPresets.ApplyTightness(settings, 0f);
            AssertTrue(settings.clearanceTightnessFactor == 0.85f && settings.clearanceSurfaceGuardIterations == 0, "Loose preset changed.");
            ReFitSettingsPresets.ApplyTightness(settings, 0.5f);
            AssertTrue(settings.clearanceTightnessFactor == 0.5f && settings.clearanceMaxTransferredTotalCorrection == 0.035f, "Middle preset changed.");
            ReFitSettingsPresets.ApplyTightness(settings, 1f);
            AssertLessOrEqual(Mathf.Abs(settings.clearanceTightnessFactor - 0.08f), 0.000001f, "Tight preset changed.");
        }

        private static void Fbx_InsertedJointDoesNotDistortSurface()
        {
            using (var first = FbxResultFixture.Create(FbxFixtureV1Path))
            using (var second = FbxResultFixture.Create(FbxFixtureV2DifferentArmaturePath))
            {
                var before = new ReFitEngine().Run(FbxRequest(first));
                var after = new ReFitEngine().Run(FbxRequest(second));
                try
                {
                    AssertReportContains(after.report, "source-pose-preserved", "Inserted-joint fixture was destructively reposed.");
                    AssertEquivalentShapes(before, after, first.clothingA.transform.localToWorldMatrix, 0.001f);
                }
                finally { DestroyComputationMesh(before); DestroyComputationMesh(after); }
            }
        }

        private static ReFitRequest FbxRequest(FbxResultFixture fixture) => new ReFitRequest
        {
            mode = ReFitMode.MeshAndBlendshape, assetRenderer = fixture.clothingA,
            sourceAvatar = fixture.sourceAvatar, targetAvatar = fixture.targetAvatar,
            sourceBodyRenderer = fixture.sourceBody, targetBodyRenderer = fixture.targetBody,
            targetBlendshape = FbxShapeName,
            settings = new ReFitSettings { falloffStartDistance = 0.08f, filterByNormal = false,
                filterByBoneRegion = false, prefixTransferredShapes = false,
                recalculateNormalDeltas = false, savePrefab = false, proportionWarningThreshold = 1f }
        };

        private static void AssertEquivalentShapes(ReFitComputation a, ReFitComputation b, Matrix4x4 world, float tolerance)
        {
            AssertComputationSucceeded(a); AssertComputationSucceeded(b);
            AssertTrue(a.mesh.vertexCount == b.mesh.vertexCount && a.mesh.blendShapeCount == b.mesh.blendShapeCount,
                "Equivalent inputs changed mesh layout.");
            var av = a.mesh.vertices; var bv = b.mesh.vertices;
            for (int i = 0; i < av.Length; i++)
                AssertLessOrEqual(world.MultiplyVector(av[i] - bv[i]).magnitude, tolerance, "Base vertex changed.");
            for (int s = 0; s < a.mesh.blendShapeCount; s++)
            {
                a.mesh.GetBlendShapeFrameVertices(s, 0, av, null, null);
                b.mesh.GetBlendShapeFrameVertices(s, 0, bv, null, null);
                for (int i = 0; i < av.Length; i++)
                    AssertLessOrEqual(world.MultiplyVector(av[i] - bv[i]).magnitude, tolerance, $"Shape {s}, vertex {i} differs.");
            }
        }
    }
}
