using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The ReFit geometry engine. Produces a <see cref="ReFitComputation"/> (new mesh with "refit"
    /// blendshape(s), optional new skinning) from a <see cref="ReFitRequest"/> without saving assets or
    /// touching the scene — the editor layer applies the result.
    ///
    /// Two entry points:
    /// <see cref="Run"/> — synchronous (blocks until done);
    /// <see cref="RunCoroutine"/> — editor-coroutine friendly: scene access happens up front on the main
    /// thread, the heavy geometry runs on a background thread, and the mesh is baked back on the main thread.
    /// </summary>
    public partial class ReFitEngine
    {
        private const int DetachedCoherenceMinGroups = 8;
        private const float DetachedCoherenceMinMotion = 0.006f;
        private const float DetachedCoherenceMaxP95EdgeRatio = 1.35f;
        private const float DetachedCoherenceMaxEdgeRatio = 2.0f;
        private const float DetachedCoherenceMaxP95DeltaJump = 0.012f;
        private const float DetachedCoherencePartialMotionRatio = 0.55f;
        private const float DetachedCoherenceMovingSampleRatio = 0.55f;
        private const float DetachedCoherenceMaxClusterDistance = 0.06f;
        private const float DetachedCoherenceSurfaceFollowStrength = 1f;
        private const int DetachedCoherenceSurfaceFillIterations = 48;
        private const int DetachedCoherenceSurfaceSmoothIterations = 3;
        private const float DetachedCoherenceSurfaceSmoothStrength = 0.35f;

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Runs the full computation synchronously.</summary>
        public ReFitComputation Run(ReFitRequest request, ReFitProgress progress = null) =>
            Run(request, progress, CancellationToken.None);

        public ReFitComputation Run(ReFitRequest request, ReFitProgress progress,
            CancellationToken cancellationToken)
        {
            State state = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = Prepare(request, progress);
                state.cancellationToken = cancellationToken;
                if (state.failed) return state.comp;
                ComputeGeometry(state);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(0.85f, "Baking the mesh");
                cancellationToken.ThrowIfCancellationRequested();
                Bake(state);
            }
            catch (OperationCanceledException)
            {
                if (state == null) state = new State();
                state.comp.success = false;
                state.comp.report.Warn("refit-cancelled", "ReFit was cancelled before application.");
            }
            catch (Exception e)
            {
                if (state == null) state = new State();
                state.comp.success = false;
                state.comp.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
            }
            finally { DestroyOwnedTemplate(state); }
            NotifyCompletion(progress, state.comp.report);
            return state.comp;
        }

        private static void NotifyCompletion(ReFitProgress progress, ReFitReport report)
        {
            // Output already exists; a UI callback must not prevent its owner receiving it.
            try { progress?.Invoke(1f, "Done"); }
            catch (Exception e) { report.Warn("progress-callback", e.Message); }
        }

        /// <summary>
        /// Runs the computation as an editor coroutine: yield it from any coroutine runner. The heavy geometry
        /// is computed on a background thread so the editor stays responsive. <paramref name="onComplete"/> is
        /// invoked on the main thread with the result.
        /// </summary>
        public IEnumerator RunCoroutine(ReFitRequest request, ReFitProgress progress, Action<ReFitComputation> onComplete) =>
            RunCoroutine(request, progress, onComplete, CancellationToken.None);

        public IEnumerator RunCoroutine(ReFitRequest request, ReFitProgress progress, Action<ReFitComputation> onComplete,
            CancellationToken cancellationToken) => RunCoroutineCore(request?.Clone(), progress, onComplete, cancellationToken);

        private IEnumerator RunCoroutineCore(ReFitRequest request, ReFitProgress progress, Action<ReFitComputation> onComplete,
            CancellationToken cancellationToken)
        {
            State state;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = Prepare(request, progress);
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ReFitComputation();
                cancelled.report.Warn("refit-cancelled", "ReFit was cancelled before staging.");
                onComplete?.Invoke(cancelled);
                yield break;
            }
            catch (Exception e)
            {
                var comp = new ReFitComputation();
                comp.report.Error("staging-exception", $"Unexpected error while staging: {e.Message}");
                onComplete?.Invoke(comp);
                yield break;
            }

            if (state.failed)
            {
                onComplete?.Invoke(state.comp);
                yield break;
            }

            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                state.cancellationToken = lifetime.Token;
                try
                {
                    var task = Task.Run(() =>
                    {
                        try { ComputeGeometry(state); }
                        catch (Exception e) { state.backgroundError = e; }
                    });

                    while (!task.IsCompleted)
                    {
                        progress?.Invoke(0.25f + Mathf.Clamp01(state.backgroundProgress) * 0.6f, state.backgroundLabel);
                        yield return null;
                    }

                    if (state.cancellationToken.IsCancellationRequested)
                    {
                        state.comp.report.Warn("refit-cancelled", "ReFit was cancelled before application.");
                        onComplete?.Invoke(state.comp);
                        yield break;
                    }
                    if (state.backgroundError != null)
                    {
                        state.comp.report.Error("refit-exception",
                            $"Unexpected error during computation: {state.backgroundError.Message}\n{state.backgroundError.StackTrace}");
                        onComplete?.Invoke(state.comp);
                        yield break;
                    }

                    progress?.Invoke(0.9f, "Baking the mesh");
                    try
                    {
                        state.cancellationToken.ThrowIfCancellationRequested();
                        Bake(state);
                    }
                    catch (OperationCanceledException)
                    {
                        state.comp.report.Warn("refit-cancelled", "ReFit was cancelled before baking.");
                    }
                    catch (Exception e)
                    {
                        state.comp.report.Error("refit-exception", $"Unexpected error while baking: {e.Message}");
                    }
                    NotifyCompletion(progress, state.comp.report);
                    onComplete?.Invoke(state.comp);
                }
                finally
                {
                    // Disposing the enumerator must not leave a worker producing a result nobody owns.
                    // Workers use captured arrays only; cancellation is observed at phase boundaries.
                    lifetime.Cancel();
                    DestroyOwnedTemplate(state);
                }
            }
        }

        /// <summary>
        /// Dry run: stages the avatars, checks armature matching, proportions and blendshape availability, and
        /// returns the diagnostics without computing any geometry.
        /// </summary>
        public ReFitReport Validate(ReFitRequest request)
        {
            var report = new ReFitReport();
            if (!ValidateRequest(request, report)) return report;
            using (var stage = PoseNormalizer.CreateStage(request, report))
            {
                if (stage == null) return report;
                ProportionChecker.Check(stage, request.settings ?? new ReFitSettings(), report);
                var names = RequestedShapeNames(request);
                if (names.Count > 0 && stage.targetBody != null && stage.targetBody.sharedMesh != null)
                {
                    int found = 0;
                    foreach (var name in names)
                    {
                        if (stage.targetBody.sharedMesh.GetBlendShapeIndex(name) >= 0) found++;
                        else report.Warn("blendshape-not-found", $"Blendshape '{name}' was not found on the target body '{stage.targetBody.name}'.");
                    }
                    if (found == 0 && request.mode != ReFitMode.MeshToMesh)
                        report.Error("blendshapes-missing", "None of the requested blendshapes exist on the target body.");
                }
            }
            return report;
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private class ShapeTask
        {
            public string sourceName;
            public int shapeIndex;
            public float mirrorWeight;
            /// <summary>Mesh-local frame deltas on the target body (captured on the main thread).</summary>
            public Vector3[] frameDeltas;
            public string frameIdentity;
            // background results
            public Vector3[] rawLocalDeltas;
            public Vector3[] localDeltas;
            public Vector3[] normalDeltas;
        }

        private class State
        {
            public ReFitRequest request;
            public Mesh ownedTemplateMesh;
            public ReFitSettings settings;
            public ReFitComputation comp = new ReFitComputation();
            public bool failed;
            public bool wantMesh;
            public bool replace;
            public bool sourceIsTarget;

            public MeshSnapshot asset;
            public MeshSnapshot sourceBody;   // null when not wantMesh; == targetBasis when source == target
            public MeshSnapshot targetBasis;
            public SurfaceBvh stagedSourceBvh;
            public SurfaceBvh targetBvh;
            public List<ShapeTask> shapes = new List<ShapeTask>();
            public bool assetIsLikelyUpperBodyGarment;

            public BodyRegion[] assetGroupRegions;
            public BodyRegion[] sourceTriRegions;
            public BodyRegion[] targetTriRegions;

            // bone plan (resolved on the main thread; weights computed in the background)
            public Matrix4x4[] newBindposes;
            public int[] bodyBoneToNew;
            public int[] assetBoneToNew;
            public bool[] assetBoneIsExtra;
            public BodyRegion[] newBoneRegions;
            public bool transferWeights;

            // background outputs
            public Vector3[] primaryRawLocalDeltas;
            public Vector3[] primaryLocalDeltas;
            public Vector3[] primaryNormalDeltas;
            public Vector3[] targetSpaceBaseLocalDeltas;
            public Vector3[] targetSpaceBaseGroupDeltas;
            public string[] targetSpaceBaseShapeNames;
            public ReFitGeneratedAssetMetadataData targetSpaceMetadata;
            public string targetIdentity;
            public string settingsIdentity;
            public string bindingSettingsIdentity;
            public BoneWeight[] newWeights;
            public ReFitWeightTransferDebugInfo weightDebug;
            public float[] upperBodyHemWeights;
            public float[] openBoundaryWeights;

            // background progress
            public volatile string backgroundLabel = "Computing...";
            public float backgroundProgress;
            public Exception backgroundError;
            public CancellationToken cancellationToken;
            public readonly System.Diagnostics.Stopwatch phaseTimer = new System.Diagnostics.Stopwatch();

            public ReFitReport Report => comp.report;
        }

        private struct DetachedShapeArtifactMetrics
        {
            public int groups;
            public int edges;
            public float minMotion;
            public float averageMotion;
            public float maxMotion;
            public float p95EdgeRatio;
            public float maxEdgeRatio;
            public float p95DeltaJump;
            public float maxDeltaJump;
        }

        private struct DetachedComponentBounds
        {
            public Vector3 min;
            public Vector3 max;
            public bool valid;
        }

        private sealed class DetachedSurfaceSupport
        {
            public MeshSnapshot surface;
            public SurfaceBvh bvh;
            public int[] triangleComponents;
            public BodyRegion[] triangleRegions;
        }

        // ------------------------------------------------------------------
        // Phase 1: Prepare (main thread — all scene/Unity-object access)
        // ------------------------------------------------------------------

        private State Prepare(ReFitRequest request, ReFitProgress progress)
        {
            request = request?.Clone();
            var state = new State { request = request, settings = request?.settings ?? new ReFitSettings() };
            var report = state.Report;

            if (!ValidateRequest(request, report)) { state.failed = true; return state; }

            progress?.Invoke(0.02f, "Staging avatars");
            using (var stage = PoseNormalizer.CreateStage(request, report))
            {
                if (stage == null) { state.failed = true; return state; }
                state.comp.assetRendererPath = stage.assetRendererPath;
                state.sourceIsTarget = stage.sourceIsTarget;
                state.wantMesh = request.mode != ReFitMode.Blendshape;
                state.assetIsLikelyUpperBodyGarment = state.settings.garmentKind == ReFitGarmentKind.UpperBody ||
                    (state.settings.garmentKind == ReFitGarmentKind.Auto && IsLikelyUpperBodyGarment(stage.assetRenderer));

                ProportionChecker.Check(stage, state.settings, report);

                // Resolve requested blendshapes
                var requestedNames = RequestedShapeNames(request);
                foreach (var name in requestedNames)
                {
                    int idx = stage.targetBody.sharedMesh.GetBlendShapeIndex(name);
                    if (idx < 0)
                    {
                        report.Warn("blendshape-not-found",
                            $"Blendshape '{name}' was not found on the target body '{stage.targetBody.name}'; skipped.");
                        continue;
                    }
                    state.shapes.Add(new ShapeTask
                    {
                        sourceName = name,
                        shapeIndex = idx,
                        mirrorWeight = stage.targetBody.GetBlendShapeWeight(idx)
                    });
                }
                if (request.mode != ReFitMode.MeshToMesh && state.shapes.Count == 0)
                {
                    report.Error("blendshapes-missing", "None of the requested blendshapes exist on the target body.");
                    state.failed = true;
                    return state;
                }

                // Snapshots
                progress?.Invoke(0.08f, "Capturing meshes");
                var originalTarget = request.targetBodyRenderer;
                if (originalTarget == null)
                {
                    var path = ReFitUtility.IndexPath(stage.targetBody.transform, stage.targetRoot.transform);
                    var originalTransform = ReFitUtility.ResolvePath(request.targetAvatar.transform, path);
                    originalTarget = originalTransform != null ? originalTransform.GetComponent<SkinnedMeshRenderer>() : null;
                }
                state.targetIdentity = ReFitCacheIdentity.Renderer(originalTarget, false);
                state.settingsIdentity = ReFitCacheIdentity.Settings(state.settings);
                state.bindingSettingsIdentity = ReFitCacheIdentity.BindingSettings(state.settings);
                Dictionary<int, float> assetShapeOverrides = null;
                if (ShouldUseTargetSpaceBaseBlendshape(request, stage))
                {
                    state.targetSpaceMetadata = CopyGeneratedMetadata(stage.assetRenderer);
                    if (state.targetSpaceMetadata != null &&
                        (string.IsNullOrEmpty(state.targetSpaceMetadata.targetIdentity) ||
                         state.targetSpaceMetadata.targetIdentity != state.targetIdentity ||
                         state.targetSpaceMetadata.bindingSettingsIdentity != state.bindingSettingsIdentity ||
                         state.targetSpaceMetadata.assetIdentity != ReFitCacheIdentity.Renderer(request.assetRenderer, true)))
                    {
                        report.Warn("transfer-cache-invalidated", "Mesh topology, skinning or pose changed (or provenance is missing); rebuilding target bindings.");
                        state.targetSpaceMetadata = null;
                    }
                    assetShapeOverrides = BuildTargetSpaceBaseBlendshapeOverrides(
                        stage.assetRenderer,
                        requestedNames,
                        state.settings,
                        state.targetSpaceMetadata?.primaryShapeName,
                        out state.targetSpaceBaseLocalDeltas,
                        out state.targetSpaceBaseShapeNames);
                    if (state.targetSpaceBaseShapeNames != null && state.targetSpaceBaseShapeNames.Length > 0)
                    {
                        report.Info("target-space-base-blendshape",
                            $"Using active base blendshape(s) '{string.Join(", ", state.targetSpaceBaseShapeNames)}' as the already-fitted target-space deformation.");
                    }
                }

                state.asset = MeshSnapshot.Capture(stage.assetRenderer, true, assetShapeOverrides, report);
                if (state.targetSpaceBaseLocalDeltas != null)
                    state.targetSpaceBaseGroupDeltas = ToRendererWorldGroupDeltas(
                        state.asset,
                        state.targetSpaceBaseLocalDeltas,
                        state.targetSpaceMetadata);
                state.targetBasis = MeshSnapshot.Capture(stage.targetBody, false,
                    BuildZeroBlendShapeOverrides(stage.targetBody), report);
                if (state.wantMesh)
                    state.sourceBody = stage.sourceBody == stage.targetBody
                        ? state.targetBasis
                        : stage.sourceSurface ?? MeshSnapshot.Capture(stage.sourceBody, false,
                            BuildZeroBlendShapeOverrides(stage.sourceBody), report);
                state.stagedSourceBvh = stage.sourceSurfaceIndex;

                // Per-shape mesh-local frame deltas (main thread: Mesh API)
                int vertexCount = state.targetBasis.localVertices.Length;
                var bodyMesh = stage.targetBody.sharedMesh;
                foreach (var shape in state.shapes)
                {
                    shape.frameDeltas = new Vector3[vertexCount];
                    int frame = bodyMesh.GetBlendShapeFrameCount(shape.shapeIndex) - 1;
                    bodyMesh.GetBlendShapeFrameVertices(shape.shapeIndex, frame, shape.frameDeltas, null, null);
                    shape.frameIdentity = ReFitCacheIdentity.Shape(shape.frameDeltas);
                }

                // Regions
                progress?.Invoke(0.16f, "Classifying body regions");
                var sourceRegions = HumanoidBoneMapper.ClassifyBones(stage.sourceRoot, stage.sourceHumanMap);
                var targetRegions = stage.sourceIsTarget ? sourceRegions : HumanoidBoneMapper.ClassifyBones(stage.targetRoot, stage.targetHumanMap);
                state.targetTriRegions = TriangleRegions(state.targetBasis, targetRegions);
                state.sourceTriRegions = state.wantMesh && state.sourceBody != state.targetBasis
                    ? TriangleRegions(state.sourceBody, sourceRegions)
                    : state.targetTriRegions;
                state.assetGroupRegions = AssetGroupRegions(state.asset, stage, sourceRegions);

                // Bone plan (no weight transfer here — that runs in the background)
                state.replace = state.settings.replaceArmature && state.wantMesh && !stage.sourceIsTarget && !state.asset.rigid;
                state.transferWeights = state.settings.transferWeights;
                if (state.replace)
                {
                    progress?.Invoke(0.2f, "Resolving the target armature");
                    state.replace = BuildBonePlan(stage, state.asset, state.targetBasis, state.comp,
                        sourceRegions, targetRegions, report,
                        out state.newBindposes, out state.bodyBoneToNew, out state.assetBoneToNew,
                        out state.assetBoneIsExtra, out state.newBoneRegions);
                    if (!state.replace)
                        report.Warn("armature-replace-skipped", "Could not build the target bone plan; keeping the asset's original armature.");
                }
                state.comp.armatureReplaced = state.replace;
                state.ownedTemplateMesh = stage.TakeAssetMesh();
            } // stage disposed: everything below works on captured arrays only

            return state;
        }

        private static void DestroyOwnedTemplate(State state)
        {
            if (state?.ownedTemplateMesh != null) UnityEngine.Object.DestroyImmediate(state.ownedTemplateMesh);
            if (state != null) state.ownedTemplateMesh = null;
        }

        private static bool ShouldUseTargetSpaceBaseBlendshape(ReFitRequest request, NormalizedStage stage)
        {
            return request != null &&
                   stage != null &&
                   request.mode == ReFitMode.Blendshape &&
                   stage.sourceIsTarget &&
                   stage.assetInTargetSpace;
        }

        private static Dictionary<int, float> BuildTargetSpaceBaseBlendshapeOverrides(
            SkinnedMeshRenderer renderer,
            List<string> requestedTargetShapes,
            ReFitSettings settings,
            string metadataPrimaryShapeName,
            out Vector3[] baseLocalDeltas,
            out string[] baseShapeNames)
        {
            baseLocalDeltas = null;
            baseShapeNames = null;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0)
                return null;

            var names = new List<string>();
            var overrides = new Dictionary<int, float>();
            var accumulated = new Vector3[mesh.vertexCount];
            var frameDeltas = new Vector3[mesh.vertexCount];
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float weight = renderer.GetBlendShapeWeight(s) / 100f;
                if (Mathf.Abs(weight) <= 1e-4f)
                    continue;

                string shapeName = mesh.GetBlendShapeName(s);
                if (!IsTargetSpaceBaseBlendshapeName(shapeName, requestedTargetShapes, settings, metadataPrimaryShapeName))
                    continue;

                int frame = mesh.GetBlendShapeFrameCount(s) - 1;
                if (frame < 0)
                    continue;

                Array.Clear(frameDeltas, 0, frameDeltas.Length);
                mesh.GetBlendShapeFrameVertices(s, frame, frameDeltas, null, null);
                for (int i = 0; i < accumulated.Length; i++)
                    accumulated[i] += frameDeltas[i] * weight;

                overrides[s] = 0f;
                names.Add(shapeName);
            }

            if (names.Count == 0)
                return null;

            baseLocalDeltas = accumulated;
            baseShapeNames = names.ToArray();
            return overrides;
        }

        private static bool IsTargetSpaceBaseBlendshapeName(
            string shapeName,
            List<string> requestedTargetShapes,
            ReFitSettings settings,
            string metadataPrimaryShapeName)
        {
            if (string.IsNullOrEmpty(shapeName) || settings == null)
                return false;

            if (!string.IsNullOrEmpty(metadataPrimaryShapeName))
                return string.Equals(shapeName, metadataPrimaryShapeName, StringComparison.Ordinal);

            string baseName = settings.blendshapeName;
            if (string.IsNullOrEmpty(baseName))
                return false;

            if (requestedTargetShapes != null)
            {
                for (int i = 0; i < requestedTargetShapes.Count; i++)
                {
                    string requested = requestedTargetShapes[i];
                    if (string.IsNullOrEmpty(requested))
                        continue;

                    string generated = settings.prefixTransferredShapes ? $"{baseName}_{requested}" : requested;
                    if (string.Equals(shapeName, generated, StringComparison.Ordinal))
                        return false;
                }
            }

            if (string.Equals(shapeName, baseName, StringComparison.Ordinal))
                return true;

            return shapeName.StartsWith(baseName + " ", StringComparison.Ordinal);
        }

        private static ReFitGeneratedAssetMetadataData CopyGeneratedMetadata(SkinnedMeshRenderer renderer)
        {
            var component = renderer != null ? renderer.GetComponent<ReFitGeneratedAssetMetadata>() : null;
            return component != null && component.data != null ? component.data.Clone() : null;
        }

        private static Vector3[] ToRendererWorldGroupDeltas(
            MeshSnapshot asset,
            Vector3[] localDeltas,
            ReFitGeneratedAssetMetadataData metadata)
        {
            if (asset == null || localDeltas == null || asset.groupRep == null)
                return null;

            int groupCount = asset.GroupCount;
            var groupDeltas = new Vector3[groupCount];
            var localToWorld = metadata != null && metadata.hasDeltaWorldToLocal
                ? metadata.deltaWorldToLocal.inverse
                : asset.rendererLocalToWorld;
            Parallel.For(0, groupCount, g =>
            {
                int rep = asset.groupRep[g];
                if (rep < 0 || rep >= localDeltas.Length)
                    return;

                groupDeltas[g] = localToWorld.MultiplyVector(localDeltas[rep]);
            });
            return groupDeltas;
        }

        private static SurfaceBinding[] BuildTransferBindingsFromMetadata(State state, MeshSnapshot targetBasis)
        {
            var metadata = state?.targetSpaceMetadata;
            var asset = state?.asset;
            if (metadata == null || asset == null || targetBasis == null ||
                metadata.transferTriangles == null || metadata.transferBarycentrics == null ||
                metadata.groupCount != asset.GroupCount ||
                metadata.targetBodyVertexCount != targetBasis.localVertices.Length ||
                metadata.targetBodyTriangleIndexCount != targetBasis.triangles.Length)
                return null;

            int groupCount = asset.GroupCount;
            if (metadata.transferTriangles.Length < groupCount || metadata.transferBarycentrics.Length < groupCount)
                return null;

            var bindings = new SurfaceBinding[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                int triangle = metadata.transferTriangles[g];
                if (triangle < 0 || triangle * 3 + 2 >= targetBasis.triangles.Length)
                    continue;

                var bary = metadata.transferBarycentrics[g];
                bindings[g] = new SurfaceBinding
                {
                    valid = true,
                    triangle = triangle,
                    bary = bary,
                    point = targetBasis.BaryPoint(triangle, bary),
                    distance = 0f,
                    normalDot = 1f
                };
            }

            return bindings;
        }

        private static void ApplyMetadataFalloff(ReFitGeneratedAssetMetadataData metadata, float[] falloff)
        {
            if (metadata == null || metadata.falloff == null || falloff == null ||
                metadata.falloff.Length < falloff.Length)
                return;

            Array.Copy(metadata.falloff, falloff, falloff.Length);
        }

        private static float[] CloneMetadataWeights(float[] weights, int groupCount)
        {
            if (weights == null || groupCount <= 0 || weights.Length < groupCount)
                return null;

            var clone = new float[groupCount];
            Array.Copy(weights, clone, groupCount);
            return clone;
        }

        private static Vector3 TargetShapeDeltaToWorld(
            State state,
            MeshSnapshot targetBasis,
            int vertex,
            Vector3 localDelta)
        {
            var metadata = state?.targetSpaceMetadata;
            var matrices = metadata?.targetBodyShapeBoneMatrices;
            var valid = metadata?.targetBodyShapeBoneValid;
            var weights = targetBasis?.boneWeights;
            if (metadata != null &&
                matrices != null &&
                valid != null &&
                weights != null &&
                vertex >= 0 &&
                vertex < weights.Length &&
                metadata.targetBodyBoneCount == matrices.Length &&
                valid.Length >= matrices.Length &&
                matrices.Length > 0)
            {
                Vector3 result = Vector3.zero;
                float total = 0f;
                var bw = weights[vertex];
                AccumulateMetadataShapeDelta(ref result, ref total, matrices, valid, bw.boneIndex0, bw.weight0, localDelta);
                AccumulateMetadataShapeDelta(ref result, ref total, matrices, valid, bw.boneIndex1, bw.weight1, localDelta);
                AccumulateMetadataShapeDelta(ref result, ref total, matrices, valid, bw.boneIndex2, bw.weight2, localDelta);
                AccumulateMetadataShapeDelta(ref result, ref total, matrices, valid, bw.boneIndex3, bw.weight3, localDelta);
                if (total > 1e-6f)
                    return Mathf.Abs(total - 1f) > 1e-4f ? result / total : result;
            }

            return targetBasis.skinMatrices[vertex].MultiplyVector(localDelta);
        }

        private static void AccumulateMetadataShapeDelta(
            ref Vector3 result,
            ref float total,
            Matrix4x4[] matrices,
            bool[] valid,
            int index,
            float weight,
            Vector3 localDelta)
        {
            if (weight <= 0f || index < 0 || index >= matrices.Length || !valid[index])
                return;

            result += matrices[index].MultiplyVector(localDelta) * weight;
            total += weight;
        }

        private static Vector3[] FindCachedTransferredLocalDeltas(
            ReFitGeneratedAssetMetadataData metadata,
            string sourceName,
            int vertexCount,
            string frameIdentity,
            string settingsIdentity)
        {
            if (metadata?.transferredShapes == null || string.IsNullOrEmpty(sourceName) || vertexCount <= 0 ||
                metadata.settingsIdentity != settingsIdentity)
                return null;

            for (int i = 0; i < metadata.transferredShapes.Length; i++)
            {
                var shape = metadata.transferredShapes[i];
                if (shape == null ||
                    shape.frameIdentity != frameIdentity ||
                    !string.Equals(shape.sourceName, sourceName, StringComparison.Ordinal) ||
                    shape.localDeltas == null ||
                    shape.localDeltas.Length != vertexCount)
                    continue;

                return (Vector3[])shape.localDeltas.Clone();
            }

            return null;
        }

        private static ReFitClearanceCorrection.Profile BuildClearanceProfileFromMetadata(
            State state,
            MeshSnapshot targetBasis,
            SurfaceBinding[] targetBindings,
            Vector3[] primaryGroupDeltas)
        {
            var metadata = state?.targetSpaceMetadata;
            var asset = state?.asset;
            if (metadata == null || asset == null || targetBasis == null ||
                metadata.clearanceEligible == null ||
                metadata.sourceClearance == null ||
                metadata.groupCount != asset.GroupCount ||
                metadata.clearanceEligible.Length < asset.GroupCount ||
                metadata.sourceClearance.Length < asset.GroupCount)
                return null;

            var profile = new ReFitClearanceCorrection.Profile
            {
                eligible = new bool[asset.GroupCount],
                sourceClearance = new float[asset.GroupCount],
                sourceBodyPoint = new Vector3[asset.GroupCount]
            };

            var localToWorld = asset.rendererLocalToWorld;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                if (!metadata.clearanceEligible[g])
                    continue;

                profile.eligible[g] = true;
                profile.sourceClearance[g] = metadata.sourceClearance[g];
                if (metadata.sourcePrimaryExpansion != null &&
                    metadata.sourcePrimaryExpansion.Length > g &&
                    targetBindings != null &&
                    targetBindings.Length > g &&
                    targetBindings[g].valid)
                {
                    var normal = targetBasis.BaryNormal(targetBindings[g].triangle, targetBindings[g].bary);
                    profile.sourceBodyPoint[g] = targetBindings[g].point - normal * metadata.sourcePrimaryExpansion[g];
                }
                else if (metadata.sourceBodyPointFromRefitLocalOffset != null &&
                         metadata.sourceBodyPointFromRefitLocalOffset.Length > g)
                {
                    int rep = asset.groupRep[g];
                    var primary = primaryGroupDeltas != null && g < primaryGroupDeltas.Length
                        ? primaryGroupDeltas[g]
                        : Vector3.zero;
                    profile.sourceBodyPoint[g] =
                        asset.worldVertices[rep] + primary +
                        localToWorld.MultiplyVector(metadata.sourceBodyPointFromRefitLocalOffset[g]);
                }
                else if (metadata.sourceBodyPointLocal != null && metadata.sourceBodyPointLocal.Length > g)
                {
                    profile.sourceBodyPoint[g] = localToWorld.MultiplyPoint3x4(metadata.sourceBodyPointLocal[g]);
                }
                else
                {
                    profile.eligible[g] = false;
                    continue;
                }
                profile.eligibleGroups++;
            }

            return profile.eligibleGroups > 0 ? profile : null;
        }

        private static ReFitGeneratedAssetMetadataData BuildGeneratedMetadata(
            State state,
            MeshSnapshot targetBasis,
            SurfaceBinding[] transferBindings,
            float[] falloff,
            ReFitClearanceCorrection.Profile clearanceProfile,
            Vector3[] primaryGroupDeltas)
        {
            var asset = state?.asset;
            if (asset == null || targetBasis == null || transferBindings == null || falloff == null ||
                clearanceProfile == null || asset.GroupCount == 0)
                return null;

            int groupCount = asset.GroupCount;
            if (transferBindings.Length < groupCount || falloff.Length < groupCount)
                return null;

            var data = new ReFitGeneratedAssetMetadataData
            {
                targetIdentity = state.targetIdentity,
                settingsIdentity = state.settingsIdentity,
                bindingSettingsIdentity = state.bindingSettingsIdentity,
                groupCount = groupCount,
                targetBodyVertexCount = targetBasis.localVertices.Length,
                targetBodyTriangleIndexCount = targetBasis.triangles.Length,
                targetBodyBoneCount = targetBasis.boneMatrices != null ? targetBasis.boneMatrices.Length : 0,
                hasDeltaWorldToLocal = true,
                deltaWorldToLocal = asset.rendererWorldToLocal,
                targetBodyShapeBoneMatrices = targetBasis.boneMatrices != null
                    ? (Matrix4x4[])targetBasis.boneMatrices.Clone()
                    : null,
                targetBodyShapeBoneValid = targetBasis.boneMatrixValid != null
                    ? (bool[])targetBasis.boneMatrixValid.Clone()
                    : null,
                transferTriangles = new int[groupCount],
                transferBarycentrics = new Vector3[groupCount],
                falloff = new float[groupCount],
                upperBodyHemWeights = CloneMetadataWeights(state.upperBodyHemWeights, groupCount),
                openBoundaryWeights = CloneMetadataWeights(state.openBoundaryWeights, groupCount),
                clearanceEligible = new bool[groupCount],
                sourceClearance = new float[groupCount],
                sourcePrimaryExpansion = new float[groupCount],
                sourceBodyPointLocal = new Vector3[groupCount],
                sourceBodyPointFromRefitLocalOffset = new Vector3[groupCount]
            };

            var worldToLocal = asset.rendererWorldToLocal;
            for (int g = 0; g < groupCount; g++)
            {
                var binding = transferBindings[g];
                data.transferTriangles[g] = binding.valid ? binding.triangle : -1;
                data.transferBarycentrics[g] = binding.valid ? binding.bary : Vector3.zero;
                data.falloff[g] = falloff[g];

                bool eligible = g < clearanceProfile.eligible.Length && clearanceProfile.eligible[g];
                data.clearanceEligible[g] = eligible;
                if (!eligible)
                    continue;

                data.sourceClearance[g] = clearanceProfile.sourceClearance[g];
                data.sourceBodyPointLocal[g] = worldToLocal.MultiplyPoint3x4(clearanceProfile.sourceBodyPoint[g]);
                if (binding.valid)
                {
                    var normal = targetBasis.BaryNormal(binding.triangle, binding.bary);
                    data.sourcePrimaryExpansion[g] =
                        Vector3.Dot(binding.point - clearanceProfile.sourceBodyPoint[g], normal);
                }
                int rep = asset.groupRep[g];
                var primary = primaryGroupDeltas != null && g < primaryGroupDeltas.Length
                    ? primaryGroupDeltas[g]
                    : Vector3.zero;
                data.sourceBodyPointFromRefitLocalOffset[g] =
                    worldToLocal.MultiplyVector(clearanceProfile.sourceBodyPoint[g] - asset.worldVertices[rep] - primary);
            }

            return data;
        }

        private static List<string> RequestedShapeNames(ReFitRequest request)
        {
            var names = new List<string>();
            if (request == null) return names;
            if (request.targetBlendshapes != null && request.targetBlendshapes.Count > 0)
            {
                foreach (var n in request.targetBlendshapes)
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            else if (!string.IsNullOrEmpty(request.targetBlendshape))
            {
                names.Add(request.targetBlendshape);
            }
            return names;
        }

        private static Dictionary<int, float> BuildZeroBlendShapeOverrides(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0)
                return null;

            var overrides = new Dictionary<int, float>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++)
                overrides[i] = 0f;
            return overrides;
        }

        private static bool ValidateRequest(ReFitRequest request, ReFitReport report)
        {
            if (request == null) { report.Error("bad-request", "Request is null."); return false; }
            if (request.assetRenderer == null) { report.Error("missing-asset", "No asset renderer set."); return false; }
            if (request.targetAvatar == null) { report.Error("missing-target", "No target avatar set."); return false; }
            if (request.mode != ReFitMode.Blendshape && request.sourceAvatar == null)
            {
                report.Error("missing-source", "Mesh re-fit needs the avatar the asset was made for (source avatar).");
                return false;
            }
            if (request.mode != ReFitMode.MeshToMesh && RequestedShapeNames(request).Count == 0)
            {
                report.Error("missing-blendshape", "Blendshape transfer needs at least one target body blendshape name.");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Phase 2: ComputeGeometry (thread-safe — captured arrays + pure math only)
        // ------------------------------------------------------------------

        private void ComputeGeometry(State state)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            int groupCount = asset.GroupCount;
            int vertexCount = asset.localVertices.Length;
            state.openBoundaryWeights =
                CloneMetadataWeights(state.targetSpaceMetadata?.openBoundaryWeights, groupCount) ??
                BuildOpenBoundaryWeights(asset);

            // Spatial indices
            SetBackgroundProgress(state, 0.05f, "Building spatial indices");
            var bvhTarget = SurfaceBvh.Build(targetBasis);
            state.targetBvh = bvhTarget;
            var bvhSource = state.wantMesh && state.sourceBody != targetBasis
                ? state.stagedSourceBvh ?? SurfaceBvh.Build(state.sourceBody) : bvhTarget;

            // Bindings to the first surface (source body for mesh refit, target body otherwise)
            SetBackgroundProgress(state, 0.15f, "Binding the asset to the body");
            var firstSnap = state.wantMesh ? state.sourceBody : targetBasis;
            var firstBvh = state.wantMesh ? bvhSource : bvhTarget;
            var firstTriRegions = state.wantMesh ? state.sourceTriRegions : state.targetTriRegions;
            var bindings = SurfaceBindingSolver.ComputeGroupBindings(
                asset, firstSnap, firstBvh, settings, state.assetGroupRegions, firstTriRegions, state.Report);

            // Chain onto the target surface
            var targetBindings = bindings;
            if (state.wantMesh)
            {
                SetBackgroundProgress(state, 0.3f, "Projecting onto the target body");
                targetBindings = new SurfaceBinding[groupCount];
                float cosMax = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
                float chainRange = Mathf.Max(settings.maxProjectionDistance * 2f, 0.05f);
                var localBindings = bindings;
                var localTarget = targetBindings;
                Parallel.For(0, groupCount, g =>
                {
                    if (!localBindings[g].valid) { localTarget[g].valid = false; return; }
                    var nA = firstSnap.BaryNormal(localBindings[g].triangle, localBindings[g].bary);
                    var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                    localTarget[g] = SurfaceBindingSolver.BindPoint(
                        localBindings[g].point, targetBasis, bvhTarget, chainRange,
                        region, settings.filterByBoneRegion ? state.targetTriRegions : null,
                        nA, cosMax, settings.filterByNormal);
                });
            }

            // Falloff weight per group (asset distance to its base surface)
            var falloff = new float[groupCount];
            for (int g = 0; g < groupCount; g++)
                falloff[g] = bindings[g].valid
                    ? DeltaField.Falloff(bindings[g].distance, settings.falloffStartDistance, settings.maxProjectionDistance)
                    : 0f;
            var clearanceProfile = ReFitClearanceCorrection.BuildProfile(asset, firstSnap, bindings, settings);
            var metadataTransferBindings = BuildTransferBindingsFromMetadata(state, targetBasis);
            if (metadataTransferBindings != null)
            {
                ApplyMetadataFalloff(state.targetSpaceMetadata, falloff);
                var metadataProfile = BuildClearanceProfileFromMetadata(
                    state,
                    targetBasis,
                    metadataTransferBindings,
                    state.targetSpaceBaseGroupDeltas);
                if (metadataProfile != null)
                    clearanceProfile = metadataProfile;
            }
            state.upperBodyHemWeights =
                CloneMetadataWeights(state.targetSpaceMetadata?.upperBodyHemWeights, groupCount) ??
                BuildUpperBodyHemWeights(state);

            // ---- Mesh deformation field --------------------------------------------------
            Vector3[] primaryGroupDeltas = null;
            var transferBindings = targetBindings;
            if (state.wantMesh)
            {
                SetBackgroundProgress(state, 0.4f, "Computing the deformation");
                primaryGroupDeltas = new Vector3[groupCount];
                Parallel.For(0, groupCount, g =>
                {
                    if (!bindings[g].valid || !targetBindings[g].valid) { primaryGroupDeltas[g] = Vector3.zero; return; }
                    var cpA = bindings[g].point;
                    var cpB = targetBindings[g].point;
                    Vector3 d;
                    if (settings.offsetMode == OffsetMode.RotateWithNormal)
                    {
                        var p = asset.worldVertices[asset.groupRep[g]];
                        var nA = firstSnap.FaceNormal(bindings[g].triangle);
                        var nB = targetBasis.FaceNormal(targetBindings[g].triangle);
                        d = cpB + Quaternion.FromToRotation(nA, nB) * (p - cpA) - p;
                    }
                    else
                    {
                        d = cpB - cpA;
                    }
                    primaryGroupDeltas[g] = d * falloff[g];
                });
                DeltaField.Smooth(primaryGroupDeltas, asset.groupAdjacency,
                    settings.primarySmoothingIterations, settings.primarySmoothingStrength);
                ApplyUpperBodyHemDamping(primaryGroupDeltas, state.upperBodyHemWeights, settings);
                state.primaryRawLocalDeltas = ToLocalDeltas(state, primaryGroupDeltas, true);

                var clearanceStats = ReFitClearanceCorrection.Apply(
                    asset,
                    targetBasis,
                    targetBindings,
                    clearanceProfile,
                    null,
                    primaryGroupDeltas,
                    null,
                    falloff,
                    settings,
                    BuildClearanceContext(state, targetBasis, targetBindings, false));
                AddClearanceStats(state, clearanceStats, "primary refit");

                state.primaryLocalDeltas = ToLocalDeltas(state, primaryGroupDeltas, true);
                if (settings.recalculateNormalDeltas)
                    state.primaryNormalDeltas = NormalDeltas(asset, state.primaryLocalDeltas, null);

                transferBindings = BindRefittedAssetToTarget(state, bvhTarget, primaryGroupDeltas, targetBindings);
            }
            else if (state.targetSpaceBaseGroupDeltas != null)
            {
                primaryGroupDeltas = state.targetSpaceBaseGroupDeltas;
                transferBindings = metadataTransferBindings ?? targetBindings;
            }

            if (state.wantMesh)
                state.comp.generatedMetadata = BuildGeneratedMetadata(
                    state,
                    targetBasis,
                    transferBindings,
                    falloff,
                    clearanceProfile,
                    primaryGroupDeltas);

            // ---- Blendshape transfer fields ----------------------------------------------
            if (state.shapes.Count > 0)
            {
                // World-space delta of each target body vertex for a shape: M(v) applied to the frame delta.
                int bodyVerts = targetBasis.localVertices.Length;
                for (int s = 0; s < state.shapes.Count; s++)
                {
                    var shape = state.shapes[s];
                    SetBackgroundProgress(state, 0.5f + 0.35f * s / state.shapes.Count, $"Transferring '{shape.sourceName}'");

                    var worldShapeDelta = new Vector3[bodyVerts];
                    Parallel.For(0, bodyVerts, i =>
                        worldShapeDelta[i] = TargetShapeDeltaToWorld(state, targetBasis, i, shape.frameDeltas[i]));

                    var groupDeltas = new Vector3[groupCount];
                    Parallel.For(0, groupCount, g =>
                    {
                        if (!transferBindings[g].valid) { groupDeltas[g] = Vector3.zero; return; }
                        int t = transferBindings[g].triangle * 3;
                        var bary = transferBindings[g].bary;
                        var d = worldShapeDelta[targetBasis.triangles[t]] * bary.x
                              + worldShapeDelta[targetBasis.triangles[t + 1]] * bary.y
                              + worldShapeDelta[targetBasis.triangles[t + 2]] * bary.z;
                        groupDeltas[g] = d * falloff[g];
                    });
                    DeltaField.Smooth(groupDeltas, asset.groupAdjacency,
                        settings.transferredBlendshapeSmoothingIterations,
                        settings.transferredBlendshapeSmoothingStrength);
                    ApplyUpperBodyHemDamping(groupDeltas, state.upperBodyHemWeights, settings);
                    ApplyDetachedTransferredComponentCoherence(state, primaryGroupDeltas, groupDeltas, shape.sourceName);
                    shape.rawLocalDeltas = ToLocalDeltas(state, groupDeltas, false);

                    var cachedLocalDeltas = FindCachedTransferredLocalDeltas(
                        state.targetSpaceMetadata,
                        shape.sourceName,
                        vertexCount, shape.frameIdentity, state.settingsIdentity);
                    if (cachedLocalDeltas != null)
                    {
                        shape.localDeltas = cachedLocalDeltas;
                        state.Report.Info("target-space-cached-transferred-shape",
                            $"Reused the previously generated tightness-corrected transfer for '{shape.sourceName}'.");
                        if (settings.recalculateNormalDeltas)
                            shape.normalDeltas = NormalDeltas(asset, shape.localDeltas, state.primaryLocalDeltas);
                        shape.frameDeltas = null;
                        continue;
                    }

                    var clearanceStats = ReFitClearanceCorrection.Apply(
                        asset,
                        targetBasis,
                        transferBindings,
                        clearanceProfile,
                        primaryGroupDeltas,
                        groupDeltas,
                        worldShapeDelta,
                        falloff,
                        settings,
                        BuildClearanceContext(state, targetBasis, transferBindings, true));
                    AddClearanceStats(state, clearanceStats, $"transferred '{shape.sourceName}'");

                    shape.localDeltas = ToLocalDeltas(state, groupDeltas, false);
                    if (settings.recalculateNormalDeltas)
                        shape.normalDeltas = NormalDeltas(asset, shape.localDeltas, state.primaryLocalDeltas);
                    shape.frameDeltas = null; // free
                }
            }

            // ---- Skin weights --------------------------------------------------------------
            if (state.replace)
            {
                SetBackgroundProgress(state, 0.9f, "Transferring skin weights");
                state.newWeights = state.transferWeights
                    ? WeightTransfer.Transfer(asset, targetBasis, transferBindings, state.bodyBoneToNew,
                        state.assetBoneToNew, state.assetBoneIsExtra, state.newBoneRegions, state.assetGroupRegions,
                        settings, state.Report, out state.weightDebug)
                    : RemapAllOriginal(asset, state.assetBoneToNew);
            }

            if (settings.captureProjectionDebug)
                state.comp.projectionDebug = BuildProjectionDebugData(state, bindings, transferBindings, falloff);

            SetBackgroundProgress(state, 1f, "Finishing");
        }

        private static void AddClearanceStats(State state, ReFitClearanceCorrectionStats stats, string label)
        {
            if (state == null || stats == null || !stats.HasCorrections)
                return;

            if (state.comp.clearanceCorrectionStats == null)
                state.comp.clearanceCorrectionStats = new ReFitClearanceCorrectionStats();
            state.comp.clearanceCorrectionStats.Add(stats);
            if (stats.islandPropagationDebug != null)
            {
                if (!string.IsNullOrEmpty(label) && label.StartsWith("primary", StringComparison.OrdinalIgnoreCase))
                    state.comp.primaryIslandPropagationDebug = stats.islandPropagationDebug;
                else
                    state.comp.transferredIslandPropagationDebug = stats.islandPropagationDebug;
            }
            state.Report?.Info("clearance-correction", stats.Summary(label));
        }

        private static ReFitClearanceCorrection.Context BuildClearanceContext(
            State state,
            MeshSnapshot targetBody,
            SurfaceBinding[] bindings,
            bool transferredBlendshape)
        {
            return new ReFitClearanceCorrection.Context
            {
                targetBodyIndex = state.targetBvh,
                transferredBlendshape = transferredBlendshape,
                assetGroupRegions = state.assetGroupRegions,
                targetTriangleRegions = state.targetTriRegions,
                referenceNormals = BuildBindingNormals(targetBody, bindings, state.asset.GroupCount),
                bindingRelaxed = BuildBindingRelaxed(bindings, state.asset.GroupCount),
                bindingNormalDots = BuildBindingNormalDots(bindings, state.asset.GroupCount),
                openBoundaryWeights = state.openBoundaryWeights,
                upperBodyHemWeights = state.upperBodyHemWeights
            };
        }

        private static Vector3[] BuildBindingNormals(MeshSnapshot body, SurfaceBinding[] bindings, int groupCount)
        {
            if (body == null || bindings == null)
                return null;

            var normals = new Vector3[groupCount];
            int count = Mathf.Min(groupCount, bindings.Length);
            for (int g = 0; g < count; g++)
            {
                if (!bindings[g].valid)
                    continue;
                normals[g] = body.BaryNormal(bindings[g].triangle, bindings[g].bary);
            }
            return normals;
        }

        private static bool[] BuildBindingRelaxed(SurfaceBinding[] bindings, int groupCount)
        {
            if (bindings == null)
                return null;

            var relaxed = new bool[groupCount];
            int count = Mathf.Min(groupCount, bindings.Length);
            for (int g = 0; g < count; g++)
                relaxed[g] = bindings[g].valid && bindings[g].usedRelaxedFallback;
            return relaxed;
        }

        private static float[] BuildBindingNormalDots(SurfaceBinding[] bindings, int groupCount)
        {
            if (bindings == null)
                return null;

            var dots = new float[groupCount];
            int count = Mathf.Min(groupCount, bindings.Length);
            for (int g = 0; g < count; g++)
                dots[g] = bindings[g].valid ? bindings[g].normalDot : 0f;
            return dots;
        }

        private static float[] BuildOpenBoundaryWeights(MeshSnapshot asset)
        {
            if (asset == null || asset.triangles == null || asset.groupOfVertex == null ||
                asset.groupAdjacency == null || asset.GroupCount == 0)
                return null;

            var edgeCounts = new Dictionary<ulong, int>();
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                AddVertexEdge(edgeCounts, asset.triangles[t], asset.triangles[t + 1]);
                AddVertexEdge(edgeCounts, asset.triangles[t + 1], asset.triangles[t + 2]);
                AddVertexEdge(edgeCounts, asset.triangles[t + 2], asset.triangles[t]);
            }

            var weights = new float[asset.GroupCount];
            var exact = new bool[asset.GroupCount];
            foreach (var entry in edgeCounts)
            {
                if (entry.Value != 1)
                    continue;

                int a = (int)(entry.Key >> 32);
                int b = (int)(entry.Key & 0xffffffff);
                MarkBoundaryVertex(asset, a, weights, exact);
                MarkBoundaryVertex(asset, b, weights, exact);
            }

            bool any = false;
            for (int g = 0; g < exact.Length; g++)
            {
                if (!exact[g])
                    continue;

                any = true;
                var neighbors = asset.groupAdjacency[g];
                for (int n = 0; n < neighbors.Count; n++)
                {
                    int neighbor = neighbors[n];
                    if (neighbor >= 0 && neighbor < weights.Length && weights[neighbor] < 0.65f)
                        weights[neighbor] = 0.65f;
                }
            }

            for (int g = 0; g < exact.Length; g++)
            {
                if (!exact[g])
                    continue;

                var neighbors = asset.groupAdjacency[g];
                for (int n = 0; n < neighbors.Count; n++)
                {
                    int neighbor = neighbors[n];
                    if (neighbor < 0 || neighbor >= asset.groupAdjacency.Length)
                        continue;

                    var secondRing = asset.groupAdjacency[neighbor];
                    for (int s = 0; s < secondRing.Count; s++)
                    {
                        int second = secondRing[s];
                        if (second >= 0 && second < weights.Length && weights[second] < 0.25f)
                            weights[second] = 0.25f;
                    }
                }
            }

            return any ? weights : null;
        }

        private static void AddVertexEdge(Dictionary<ulong, int> edgeCounts, int a, int b)
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
            edgeCounts.TryGetValue(key, out int count);
            edgeCounts[key] = count + 1;
        }

        private static void MarkBoundaryVertex(MeshSnapshot asset, int vertex, float[] weights, bool[] exact)
        {
            if (vertex < 0 || vertex >= asset.groupOfVertex.Length)
                return;

            int group = asset.groupOfVertex[vertex];
            if (group < 0 || group >= weights.Length)
                return;

            weights[group] = 1f;
            exact[group] = true;
        }

        private static float[] BuildUpperBodyHemWeights(State state)
        {
            if (state == null || state.asset == null || !state.assetIsLikelyUpperBodyGarment)
                return null;

            var asset = state.asset;
            if (asset.worldVertices == null || asset.groupRep == null || asset.GroupCount == 0)
                return null;

            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                int vertex = asset.groupRep[g];
                float y = asset.worldVertices[vertex].y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            float height = maxY - minY;
            if (height <= 1e-4f)
                return null;

            float full = minY + height * 0.22f;
            float fade = minY + height * 0.52f;
            var weights = new float[asset.GroupCount];
            int active = 0;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                var region = state.assetGroupRegions != null && g < state.assetGroupRegions.Length
                    ? state.assetGroupRegions[g]
                    : BodyRegion.Unknown;
                if (region == BodyRegion.LeftArm || region == BodyRegion.RightArm || region == BodyRegion.Head)
                    continue;

                int vertex = asset.groupRep[g];
                float y = asset.worldVertices[vertex].y;
                float weight = 1f - Smooth01(full, fade, y);
                if (weight <= 1e-4f)
                    continue;

                weights[g] = weight;
                active++;
            }

            return active > 0 ? weights : null;
        }

        private static void ApplyUpperBodyHemDamping(Vector3[] deltas, float[] hemWeights, ReFitSettings settings)
        {
            if (deltas == null || hemWeights == null || settings == null)
                return;

            float scale = Mathf.Clamp01(settings.upperBodyGarmentHemFollowScale);
            int count = Mathf.Min(deltas.Length, hemWeights.Length);
            for (int g = 0; g < count; g++)
            {
                float weight = Mathf.Clamp01(hemWeights[g]);
                if (weight <= 0f)
                    continue;
                deltas[g] *= Mathf.Lerp(1f, scale, weight);
            }
        }

        private static bool IsLikelyUpperBodyGarment(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
                return false;

            string name = ReFitUtility.NormalizeName(renderer.name + " " + renderer.sharedMesh?.name);
            string[] negative =
            {
                "pants", "shorts", "trouser", "skirt", "legging", "sock", "shoe", "boot", "glove"
            };
            for (int i = 0; i < negative.Length; i++)
                if (name.Contains(negative[i]))
                    return false;

            string[] positive =
            {
                "hoodie", "hood", "sweatshirt", "shirt", "tshirt", "jacket", "coat",
                "sweater", "vest", "top", "pullover", "tanktop", "crop"
            };
            for (int i = 0; i < positive.Length; i++)
                if (name.Contains(positive[i]))
                    return true;

            return false;
        }

        private static float Smooth01(float start, float end, float value)
        {
            if (Mathf.Abs(end - start) <= 1e-6f)
                return value >= end ? 1f : 0f;
            float t = Mathf.Clamp01((value - start) / (end - start));
            return t * t * (3f - 2f * t);
        }

        private static void ApplyDetachedTransferredComponentCoherence(
            State state,
            Vector3[] primaryGroupDeltas,
            Vector3[] transferredGroupDeltas,
            string shapeName)
        {
            var asset = state.asset;
            var settings = state.settings;
            if (asset == null || settings == null || !settings.stabilizeDetachedTransferredComponents ||
                transferredGroupDeltas == null || asset.groupAdjacency == null || asset.groupRep == null ||
                asset.worldVertices == null || asset.GroupCount <= 0)
                return;

            int groupCount = Mathf.Min(asset.GroupCount, transferredGroupDeltas.Length);
            var componentByGroup = BuildDetachedComponentMap(asset, groupCount, out var componentSizes, out int largestComponentSize);
            if (componentByGroup == null || componentSizes == null || componentSizes.Length <= 1 || largestComponentSize <= 0)
                return;

            int receiverSizeLimit = Mathf.Max(64, Mathf.RoundToInt(largestComponentSize * 0.25f));
            var componentGroups = BuildComponentGroupLists(componentByGroup, componentSizes, groupCount);
            var reference = BuildDetachedCoherenceReferencePositions(asset, primaryGroupDeltas, groupCount);
            var componentBounds = BuildComponentBounds(componentGroups, reference);
            var surfaceSupport = BuildDetachedSurfaceSupport(state, reference, componentByGroup, componentSizes, groupCount);
            var eligibleComponents = new bool[componentSizes.Length];
            for (int component = 0; component < componentSizes.Length; component++)
            {
                int size = componentSizes[component];
                eligibleComponents[component] = size >= DetachedCoherenceMinGroups &&
                                                size < largestComponentSize &&
                                                size <= receiverSizeLimit &&
                                                componentBounds[component].valid;
            }

            var clusters = BuildDetachedComponentClusters(
                eligibleComponents,
                componentBounds,
                Mathf.Min(DetachedCoherenceMaxClusterDistance, Mathf.Max(0.025f, settings.maxProjectionDistance * 0.2f)));

            int stabilizedClusters = 0;
            int stabilizedGroups = 0;
            float beforeMaxEdgeRatio = 1f;
            float afterMaxEdgeRatio = 1f;
            float beforeMaxJump = 0f;
            float afterMaxJump = 0f;

            for (int c = 0; c < clusters.Count; c++)
            {
                var cluster = clusters[c];
                var groups = CollectClusterGroups(cluster, componentGroups);
                if (groups.Count < DetachedCoherenceMinGroups)
                    continue;

                var before = MeasureDetachedShapeArtifacts(groups, asset, reference, transferredGroupDeltas);
                beforeMaxEdgeRatio = Mathf.Max(beforeMaxEdgeRatio, before.maxEdgeRatio);
                beforeMaxJump = Mathf.Max(beforeMaxJump, before.maxDeltaJump);
                if (!ShouldStabilizeDetachedCluster(before))
                    continue;

                var rigidTarget = CoherentDetachedClusterDelta(groups, transferredGroupDeltas, before.maxMotion);
                if (rigidTarget.magnitude < DetachedCoherenceMinMotion)
                    continue;

                var targetDeltas = BuildDetachedSurfaceFollowDeltas(
                    state,
                    groups,
                    cluster,
                    componentByGroup,
                    componentSizes,
                    reference,
                    transferredGroupDeltas,
                    surfaceSupport,
                    Mathf.Clamp(settings.maxProjectionDistance * 0.35f, 0.06f, 0.18f),
                    rigidTarget,
                    out int supportedGroups);

                if (targetDeltas == null || supportedGroups <= 0)
                {
                    targetDeltas = new Vector3[groups.Count];
                    for (int i = 0; i < targetDeltas.Length; i++)
                        targetDeltas[i] = rigidTarget;
                }

                int changed = 0;
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    var target = targetDeltas[i];
                    if ((transferredGroupDeltas[g] - target).sqrMagnitude <= 1e-10f)
                        continue;

                    transferredGroupDeltas[g] = target;
                    changed++;
                }

                if (changed <= 0)
                    continue;

                var after = MeasureDetachedShapeArtifacts(groups, asset, reference, transferredGroupDeltas);
                afterMaxEdgeRatio = Mathf.Max(afterMaxEdgeRatio, after.maxEdgeRatio);
                afterMaxJump = Mathf.Max(afterMaxJump, after.maxDeltaJump);
                stabilizedClusters++;
                stabilizedGroups += changed;
            }

            if (stabilizedGroups > 0)
            {
                state.Report.Info(
                    "detached-component-coherence",
                    $"Stabilized {stabilizedGroups} detached transferred groups in {stabilizedClusters} component cluster(s) for '{shapeName}' " +
                    $"(maxEdgeRatio {beforeMaxEdgeRatio:0.###}->{afterMaxEdgeRatio:0.###}, " +
                    $"maxDeltaJump {beforeMaxJump * 1000f:0.###}mm->{afterMaxJump * 1000f:0.###}mm).");
            }
        }

        private static Vector3[] BuildDetachedCoherenceReferencePositions(
            MeshSnapshot asset,
            Vector3[] primaryGroupDeltas,
            int groupCount)
        {
            var reference = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                int rep = asset.groupRep[g];
                var primary = primaryGroupDeltas != null && g < primaryGroupDeltas.Length
                    ? primaryGroupDeltas[g]
                    : Vector3.zero;
                reference[g] = asset.worldVertices[rep] + primary;
            }

            return reference;
        }

        private static DetachedSurfaceSupport BuildDetachedSurfaceSupport(
            State state,
            Vector3[] reference,
            int[] componentByGroup,
            int[] componentSizes,
            int groupCount)
        {
            var asset = state != null ? state.asset : null;
            if (asset == null || reference == null || componentByGroup == null || componentSizes == null ||
                asset.triangles == null || asset.groupOfVertex == null || groupCount <= 0)
                return null;

            var donorTriangles = new List<int>();
            var donorComponents = new List<int>();
            var donorRegions = new List<BodyRegion>();
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                int ga = GroupForAssetVertex(asset, asset.triangles[t], groupCount);
                int gb = GroupForAssetVertex(asset, asset.triangles[t + 1], groupCount);
                int gc = GroupForAssetVertex(asset, asset.triangles[t + 2], groupCount);
                if (ga < 0 || gb < 0 || gc < 0 || ga == gb || gb == gc || ga == gc)
                    continue;

                int component = componentByGroup[ga];
                if (component < 0 || component >= componentSizes.Length ||
                    componentByGroup[gb] != component ||
                    componentByGroup[gc] != component)
                    continue;

                donorTriangles.Add(ga);
                donorTriangles.Add(gb);
                donorTriangles.Add(gc);
                donorComponents.Add(component);
                donorRegions.Add(DetachedTriangleRegion(state, ga, gb, gc));
            }

            if (donorTriangles.Count < 3)
                return null;

            var triangles = donorTriangles.ToArray();
            var surface = new MeshSnapshot
            {
                worldVertices = reference,
                worldNormals = ComputeDetachedWorldNormals(reference, triangles),
                triangles = triangles
            };

            return new DetachedSurfaceSupport
            {
                surface = surface,
                bvh = SurfaceBvh.Build(surface),
                triangleComponents = donorComponents.ToArray(),
                triangleRegions = donorRegions.ToArray()
            };
        }

        private static Vector3[] BuildDetachedSurfaceFollowDeltas(
            State state,
            List<int> groups,
            List<int> clusterComponents,
            int[] componentByGroup,
            int[] componentSizes,
            Vector3[] reference,
            Vector3[] currentDeltas,
            DetachedSurfaceSupport support,
            float searchDistance,
            Vector3 rigidTarget,
            out int supportedGroups)
        {
            supportedGroups = 0;
            if (state == null || groups == null || groups.Count == 0 || componentByGroup == null ||
                componentSizes == null || reference == null || currentDeltas == null || support == null ||
                support.surface == null || support.bvh == null || support.triangleComponents == null ||
                searchDistance <= 1e-5f)
                return null;

            var clusterSet = new HashSet<int>(clusterComponents);
            var localIndex = BuildLocalGroupIndex(groups, componentByGroup.Length);
            var targets = new Vector3[groups.Count];
            var valid = new bool[groups.Count];

            for (int i = 0; i < groups.Count; i++)
            {
                int g = groups[i];
                if (g < 0 || g >= componentByGroup.Length || g >= reference.Length)
                    continue;

                int receiverComponent = componentByGroup[g];
                int receiverSize = receiverComponent >= 0 && receiverComponent < componentSizes.Length
                    ? componentSizes[receiverComponent]
                    : 0;
                var receiverRegion = DetachedGroupRegion(state, g);
                var hit = support.bvh.ClosestPoint(reference[g], searchDistance, triangle =>
                {
                    if (triangle < 0 || triangle >= support.triangleComponents.Length)
                        return false;

                    int donorComponent = support.triangleComponents[triangle];
                    if (donorComponent < 0 || donorComponent >= componentSizes.Length ||
                        clusterSet.Contains(donorComponent) ||
                        componentSizes[donorComponent] < receiverSize)
                        return false;

                    var donorRegion = support.triangleRegions != null && triangle < support.triangleRegions.Length
                        ? support.triangleRegions[triangle]
                        : BodyRegion.Unknown;
                    return HumanoidBoneMapper.RegionsCompatible(receiverRegion, donorRegion);
                });

                if (!hit.found || !IsStableDetachedSurfaceSupport(reference[g], support.surface, hit))
                    continue;

                int tri = hit.triangle * 3;
                if (tri + 2 >= support.surface.triangles.Length)
                    continue;

                int ga = support.surface.triangles[tri];
                int gb = support.surface.triangles[tri + 1];
                int gc = support.surface.triangles[tri + 2];
                if (ga < 0 || gb < 0 || gc < 0 ||
                    ga >= currentDeltas.Length || gb >= currentDeltas.Length || gc >= currentDeltas.Length)
                    continue;

                var surfaceDelta = currentDeltas[ga] * hit.bary.x +
                                   currentDeltas[gb] * hit.bary.y +
                                   currentDeltas[gc] * hit.bary.z;
                float follow = DetachedCoherenceSurfaceFollowStrength *
                               Mathf.Lerp(1f, 0.95f, Smooth01(0f, searchDistance, hit.distance));
                targets[i] = Vector3.Lerp(rigidTarget, surfaceDelta, Mathf.Clamp01(follow));
                valid[i] = true;
                supportedGroups++;
            }

            if (supportedGroups <= 0)
                return null;

            FillDetachedSurfaceFollowTargets(groups, localIndex, state.asset.groupAdjacency, targets, valid, rigidTarget);
            SmoothDetachedSurfaceFollowTargets(groups, localIndex, state.asset.groupAdjacency, targets, valid);
            ConstrainDetachedSurfaceFollowArtifacts(groups, state.asset, reference, currentDeltas, rigidTarget, targets, valid);
            return targets;
        }

        private static int[] BuildLocalGroupIndex(List<int> groups, int groupCount)
        {
            var localIndex = new int[groupCount];
            for (int i = 0; i < localIndex.Length; i++)
                localIndex[i] = -1;

            for (int i = 0; i < groups.Count; i++)
            {
                int g = groups[i];
                if (g >= 0 && g < localIndex.Length)
                    localIndex[g] = i;
            }

            return localIndex;
        }

        private static void FillDetachedSurfaceFollowTargets(
            List<int> groups,
            int[] localIndex,
            List<int>[] adjacency,
            Vector3[] targets,
            bool[] valid,
            Vector3 fallback)
        {
            if (groups == null || localIndex == null || adjacency == null || targets == null || valid == null)
                return;

            var fill = new Vector3[targets.Length];
            var fillValid = new bool[targets.Length];
            for (int iteration = 0; iteration < DetachedCoherenceSurfaceFillIterations; iteration++)
            {
                bool changed = false;
                Array.Clear(fillValid, 0, fillValid.Length);
                for (int i = 0; i < groups.Count; i++)
                {
                    if (valid[i])
                        continue;

                    int g = groups[i];
                    if (g < 0 || g >= adjacency.Length || adjacency[g] == null)
                        continue;

                    Vector3 total = Vector3.zero;
                    int count = 0;
                    var neighbors = adjacency[g];
                    for (int n = 0; n < neighbors.Count; n++)
                    {
                        int nb = neighbors[n];
                        if (nb < 0 || nb >= localIndex.Length)
                            continue;

                        int local = localIndex[nb];
                        if (local < 0 || !valid[local])
                            continue;

                        total += targets[local];
                        count++;
                    }

                    if (count <= 0)
                        continue;

                    fill[i] = total / count;
                    fillValid[i] = true;
                    changed = true;
                }

                if (!changed)
                    break;

                for (int i = 0; i < fillValid.Length; i++)
                {
                    if (!fillValid[i])
                        continue;

                    targets[i] = fill[i];
                    valid[i] = true;
                }
            }

            for (int i = 0; i < targets.Length; i++)
            {
                if (!valid[i])
                {
                    targets[i] = fallback;
                    valid[i] = true;
                }
            }
        }

        private static void SmoothDetachedSurfaceFollowTargets(
            List<int> groups,
            int[] localIndex,
            List<int>[] adjacency,
            Vector3[] targets,
            bool[] valid)
        {
            if (groups == null || localIndex == null || adjacency == null || targets == null || valid == null)
                return;

            var buffer = new Vector3[targets.Length];
            for (int iteration = 0; iteration < DetachedCoherenceSurfaceSmoothIterations; iteration++)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    if (!valid[i])
                    {
                        buffer[i] = targets[i];
                        continue;
                    }

                    int g = groups[i];
                    Vector3 total = targets[i];
                    int count = 1;
                    if (g >= 0 && g < adjacency.Length && adjacency[g] != null)
                    {
                        var neighbors = adjacency[g];
                        for (int n = 0; n < neighbors.Count; n++)
                        {
                            int nb = neighbors[n];
                            if (nb < 0 || nb >= localIndex.Length)
                                continue;

                            int local = localIndex[nb];
                            if (local < 0 || !valid[local])
                                continue;

                            total += targets[local];
                            count++;
                        }
                    }

                    var average = total / count;
                    buffer[i] = Vector3.Lerp(targets[i], average, DetachedCoherenceSurfaceSmoothStrength);
                }

                Array.Copy(buffer, targets, targets.Length);
            }
        }

        private static void ConstrainDetachedSurfaceFollowArtifacts(
            List<int> groups,
            MeshSnapshot asset,
            Vector3[] reference,
            Vector3[] currentDeltas,
            Vector3 rigidTarget,
            Vector3[] targets,
            bool[] valid)
        {
            if (groups == null || asset == null || reference == null || currentDeltas == null || targets == null)
                return;

            var candidate = (Vector3[])currentDeltas.Clone();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    if (g >= 0 && g < candidate.Length && (valid == null || valid[i]))
                        candidate[g] = targets[i];
                }

                var metrics = MeasureDetachedShapeArtifacts(groups, asset, reference, candidate);
                if (metrics.edges == 0 ||
                    (metrics.p95EdgeRatio <= DetachedCoherenceMaxP95EdgeRatio &&
                     metrics.maxEdgeRatio <= DetachedCoherenceMaxEdgeRatio &&
                     metrics.p95DeltaJump <= DetachedCoherenceMaxP95DeltaJump))
                    return;

                SmoothDetachedSurfaceFollowTargets(groups, BuildLocalGroupIndex(groups, currentDeltas.Length),
                    asset.groupAdjacency, targets, valid);
                if (attempt >= 2)
                {
                    for (int i = 0; i < targets.Length; i++)
                        targets[i] = Vector3.Lerp(targets[i], rigidTarget, 0.12f);
                }
            }
        }

        private static bool IsStableDetachedSurfaceSupport(Vector3 receiverPoint, MeshSnapshot donorSurface, SurfaceBvh.Hit hit)
        {
            if (donorSurface == null || !hit.found)
                return false;

            var supportNormal = donorSurface.BaryNormal(hit.triangle, hit.bary);
            if (supportNormal.sqrMagnitude <= 1e-12f)
                return false;

            float side = Vector3.Dot(receiverPoint - hit.position, supportNormal);
            float backfaceTolerance = Mathf.Max(0.005f, hit.distance * 0.25f);
            return side >= -backfaceTolerance;
        }

        private static int GroupForAssetVertex(MeshSnapshot asset, int vertex, int groupCount)
        {
            if (asset == null || asset.groupOfVertex == null || vertex < 0 || vertex >= asset.groupOfVertex.Length)
                return -1;
            int group = asset.groupOfVertex[vertex];
            return group >= 0 && group < groupCount ? group : -1;
        }

        private static BodyRegion DetachedTriangleRegion(State state, int ga, int gb, int gc)
        {
            var region = BodyRegion.Unknown;
            region = PickDetachedCompatibleRegion(region, DetachedGroupRegion(state, ga));
            region = PickDetachedCompatibleRegion(region, DetachedGroupRegion(state, gb));
            region = PickDetachedCompatibleRegion(region, DetachedGroupRegion(state, gc));
            return region;
        }

        private static BodyRegion DetachedGroupRegion(State state, int group)
        {
            return state != null && state.assetGroupRegions != null &&
                   group >= 0 && group < state.assetGroupRegions.Length
                ? state.assetGroupRegions[group]
                : BodyRegion.Unknown;
        }

        private static BodyRegion PickDetachedCompatibleRegion(BodyRegion current, BodyRegion candidate)
        {
            if (candidate == BodyRegion.Unknown)
                return current;
            if (current == BodyRegion.Unknown || current == candidate)
                return candidate;
            return BodyRegion.Unknown;
        }

        private static Vector3[] ComputeDetachedWorldNormals(Vector3[] vertices, int[] triangles)
        {
            var normals = new Vector3[vertices != null ? vertices.Length : 0];
            if (vertices == null || triangles == null)
                return normals;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int ia = triangles[t];
                int ib = triangles[t + 1];
                int ic = triangles[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    continue;

                var normal = Vector3.Cross(vertices[ib] - vertices[ia], vertices[ic] - vertices[ia]);
                normals[ia] += normal;
                normals[ib] += normal;
                normals[ic] += normal;
            }

            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].sqrMagnitude > 1e-12f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        private static bool ShouldStabilizeDetachedCluster(DetachedShapeArtifactMetrics metrics)
        {
            if (metrics.groups < DetachedCoherenceMinGroups || metrics.maxMotion < DetachedCoherenceMinMotion)
                return false;

            if (metrics.edges > 0 &&
                (metrics.p95EdgeRatio > DetachedCoherenceMaxP95EdgeRatio ||
                 metrics.maxEdgeRatio > DetachedCoherenceMaxEdgeRatio ||
                 metrics.p95DeltaJump > DetachedCoherenceMaxP95DeltaJump))
                return true;

            return metrics.minMotion < metrics.averageMotion * DetachedCoherencePartialMotionRatio &&
                   metrics.maxMotion > metrics.averageMotion * 1.35f;
        }

        private static Vector3 CoherentDetachedClusterDelta(List<int> groups, Vector3[] deltas, float maxMotion)
        {
            float threshold = Mathf.Max(DetachedCoherenceMinMotion, maxMotion * DetachedCoherenceMovingSampleRatio);
            Vector3 total = Vector3.zero;
            float totalWeight = 0f;

            for (int i = 0; i < groups.Count; i++)
            {
                int g = groups[i];
                var delta = deltas[g];
                float magnitude = delta.magnitude;
                if (magnitude < threshold)
                    continue;

                total += delta * magnitude;
                totalWeight += magnitude;
            }

            if (totalWeight <= 1e-6f)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var delta = deltas[groups[i]];
                    float magnitude = delta.magnitude;
                    if (magnitude <= 1e-6f)
                        continue;

                    total += delta * magnitude;
                    totalWeight += magnitude;
                }
            }

            return totalWeight > 1e-6f ? total / totalWeight : Vector3.zero;
        }

        private static DetachedShapeArtifactMetrics MeasureDetachedShapeArtifacts(
            List<int> groups,
            MeshSnapshot asset,
            Vector3[] reference,
            Vector3[] deltas)
        {
            var inCluster = new HashSet<int>(groups);
            var edgeRatios = new List<float>();
            var deltaJumps = new List<float>();
            var metrics = new DetachedShapeArtifactMetrics
            {
                minMotion = float.PositiveInfinity,
                maxEdgeRatio = 1f
            };

            for (int i = 0; i < groups.Count; i++)
            {
                int g = groups[i];
                float motion = deltas[g].magnitude;
                metrics.groups++;
                metrics.averageMotion += motion;
                metrics.minMotion = Mathf.Min(metrics.minMotion, motion);
                metrics.maxMotion = Mathf.Max(metrics.maxMotion, motion);

                var neighbors = g < asset.groupAdjacency.Length ? asset.groupAdjacency[g] : null;
                if (neighbors == null)
                    continue;

                for (int n = 0; n < neighbors.Count; n++)
                {
                    int nb = neighbors[n];
                    if (nb <= g || !inCluster.Contains(nb) || nb >= reference.Length || nb >= deltas.Length)
                        continue;

                    float before = (reference[nb] - reference[g]).magnitude;
                    if (before > 1e-6f)
                    {
                        float after = ((reference[nb] + deltas[nb]) - (reference[g] + deltas[g])).magnitude;
                        float ratio = after / before;
                        if (ratio < 1f)
                            ratio = before / Mathf.Max(after, 1e-6f);
                        edgeRatios.Add(ratio);
                        metrics.maxEdgeRatio = Mathf.Max(metrics.maxEdgeRatio, ratio);
                    }

                    float jump = (deltas[nb] - deltas[g]).magnitude;
                    deltaJumps.Add(jump);
                    metrics.maxDeltaJump = Mathf.Max(metrics.maxDeltaJump, jump);
                    metrics.edges++;
                }
            }

            if (metrics.groups > 0)
                metrics.averageMotion /= metrics.groups;
            if (float.IsPositiveInfinity(metrics.minMotion))
                metrics.minMotion = 0f;

            metrics.p95EdgeRatio = Percentile(edgeRatios, 0.95f, 1f);
            metrics.p95DeltaJump = Percentile(deltaJumps, 0.95f, 0f);
            return metrics;
        }

        private static float Percentile(List<float> values, float percentile, float fallback)
        {
            if (values == null || values.Count == 0)
                return fallback;

            values.Sort();
            int index = Mathf.Clamp(Mathf.CeilToInt(values.Count * Mathf.Clamp01(percentile)) - 1, 0, values.Count - 1);
            return values[index];
        }

        private static int[] BuildDetachedComponentMap(
            MeshSnapshot asset,
            int groupCount,
            out int[] componentSizes,
            out int largestComponentSize)
        {
            componentSizes = null;
            largestComponentSize = 0;
            if (asset == null || asset.groupAdjacency == null || groupCount <= 0)
                return null;

            var components = new int[groupCount];
            for (int i = 0; i < components.Length; i++)
                components[i] = -1;

            var sizes = new List<int>();
            var queue = new Queue<int>();
            for (int start = 0; start < groupCount; start++)
            {
                if (components[start] >= 0)
                    continue;

                int component = sizes.Count;
                int size = 0;
                components[start] = component;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue();
                    size++;
                    var neighbors = g < asset.groupAdjacency.Length ? asset.groupAdjacency[g] : null;
                    if (neighbors == null)
                        continue;

                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int n = neighbors[i];
                        if (n < 0 || n >= groupCount || components[n] >= 0)
                            continue;

                        components[n] = component;
                        queue.Enqueue(n);
                    }
                }

                largestComponentSize = Mathf.Max(largestComponentSize, size);
                sizes.Add(size);
            }

            componentSizes = sizes.ToArray();
            return components;
        }

        private static List<int>[] BuildComponentGroupLists(int[] componentByGroup, int[] componentSizes, int groupCount)
        {
            var groups = new List<int>[componentSizes.Length];
            for (int i = 0; i < groups.Length; i++)
                groups[i] = new List<int>(Mathf.Max(1, componentSizes[i]));

            for (int g = 0; g < groupCount; g++)
            {
                int component = componentByGroup[g];
                if (component >= 0 && component < groups.Length)
                    groups[component].Add(g);
            }

            return groups;
        }

        private static DetachedComponentBounds[] BuildComponentBounds(List<int>[] componentGroups, Vector3[] reference)
        {
            var bounds = new DetachedComponentBounds[componentGroups.Length];
            for (int component = 0; component < componentGroups.Length; component++)
            {
                var groups = componentGroups[component];
                if (groups == null || groups.Count == 0)
                    continue;

                var b = new DetachedComponentBounds
                {
                    min = reference[groups[0]],
                    max = reference[groups[0]],
                    valid = true
                };

                for (int i = 1; i < groups.Count; i++)
                {
                    var p = reference[groups[i]];
                    b.min = Vector3.Min(b.min, p);
                    b.max = Vector3.Max(b.max, p);
                }

                bounds[component] = b;
            }

            return bounds;
        }

        private static List<List<int>> BuildDetachedComponentClusters(
            bool[] eligibleComponents,
            DetachedComponentBounds[] componentBounds,
            float maxDistance)
        {
            var clusters = new List<List<int>>();
            var visited = new bool[eligibleComponents.Length];
            var queue = new Queue<int>();

            for (int start = 0; start < eligibleComponents.Length; start++)
            {
                if (!eligibleComponents[start] || visited[start])
                    continue;

                var cluster = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int component = queue.Dequeue();
                    cluster.Add(component);

                    for (int other = 0; other < eligibleComponents.Length; other++)
                    {
                        if (!eligibleComponents[other] || visited[other])
                            continue;

                        if (BoundsDistance(componentBounds[component], componentBounds[other]) > maxDistance)
                            continue;

                        visited[other] = true;
                        queue.Enqueue(other);
                    }
                }

                clusters.Add(cluster);
            }

            return clusters;
        }

        private static float BoundsDistance(DetachedComponentBounds a, DetachedComponentBounds b)
        {
            if (!a.valid || !b.valid)
                return float.PositiveInfinity;

            float dx = AxisGap(a.min.x, a.max.x, b.min.x, b.max.x);
            float dy = AxisGap(a.min.y, a.max.y, b.min.y, b.max.y);
            float dz = AxisGap(a.min.z, a.max.z, b.min.z, b.max.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float AxisGap(float minA, float maxA, float minB, float maxB)
        {
            if (minA > maxB) return minA - maxB;
            if (minB > maxA) return minB - maxA;
            return 0f;
        }

        private static List<int> CollectClusterGroups(List<int> cluster, List<int>[] componentGroups)
        {
            var groups = new List<int>();
            for (int i = 0; i < cluster.Count; i++)
            {
                var componentGroupsList = componentGroups[cluster[i]];
                if (componentGroupsList != null)
                    groups.AddRange(componentGroupsList);
            }

            return groups;
        }

        private static SurfaceBinding[] BindRefittedAssetToTarget(
            State state,
            SurfaceBvh bvhTarget,
            Vector3[] primaryGroupDeltas,
            SurfaceBinding[] fallbackBindings)
        {
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var settings = state.settings;
            int groupCount = asset.GroupCount;
            var bindings = new SurfaceBinding[groupCount];
            float cosMax = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
            float range = Mathf.Max(settings.maxProjectionDistance * 2f, 0.05f);

            Parallel.For(0, groupCount, g =>
            {
                int rep = asset.groupRep[g];
                var queryPoint = asset.worldVertices[rep] +
                                 (primaryGroupDeltas != null && g < primaryGroupDeltas.Length
                                     ? primaryGroupDeltas[g]
                                     : Vector3.zero);
                var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                var binding = SurfaceBindingSolver.BindPoint(
                    queryPoint, targetBasis, bvhTarget, range,
                    region, settings.filterByBoneRegion ? state.targetTriRegions : null,
                    asset.worldNormals[rep], cosMax, settings.filterByNormal);

                if (!binding.valid && fallbackBindings != null && g < fallbackBindings.Length)
                    binding = fallbackBindings[g];
                bindings[g] = binding;
            });

            return bindings;
        }

        private static void SetBackgroundProgress(State state, float t, string label)
        {
            state.cancellationToken.ThrowIfCancellationRequested();
            if (state.phaseTimer.IsRunning)
                state.Report.Info("phase-timing", $"{state.backgroundLabel}: {state.phaseTimer.Elapsed.TotalMilliseconds:F1} ms");
            state.phaseTimer.Restart();
            state.backgroundProgress = t;
            state.backgroundLabel = label;
        }

        private static ReFitProjectionDebugData BuildProjectionDebugData(
            State state, SurfaceBinding[] sourceBindings, SurfaceBinding[] targetBindings, float[] falloff)
        {
            var asset = state.asset;
            if (asset == null || sourceBindings == null || targetBindings == null || falloff == null)
                return null;

            int groupCount = Mathf.Min(asset.GroupCount, Mathf.Min(sourceBindings.Length, targetBindings.Length));
            int max = state.settings.maxProjectionDebugGroups;
            int captureCount = max > 0 ? Mathf.Min(max, groupCount) : groupCount;
            var points = new ReFitProjectionDebugPoint[captureCount];
            var weightDebug = state.weightDebug;

            for (int g = 0; g < captureCount; g++)
            {
                int vertex = asset.groupRep[g];
                var source = sourceBindings[g];
                var target = targetBindings[g];
                var point = new ReFitProjectionDebugPoint
                {
                    groupIndex = g,
                    vertexIndex = vertex,
                    assetLocalPoint = asset.localVertices[vertex],
                    sourceHitLocalPoint = source.valid
                        ? asset.rendererWorldToLocal.MultiplyPoint3x4(source.point)
                        : asset.localVertices[vertex],
                    targetHitLocalPoint = target.valid
                        ? asset.rendererWorldToLocal.MultiplyPoint3x4(target.point)
                        : asset.localVertices[vertex],
                    sourceTriangle = source.triangle,
                    targetTriangle = target.triangle,
                    sourceBarycentric = source.bary,
                    targetBarycentric = target.bary,
                    sourceDistance = source.distance,
                    targetDistance = target.distance,
                    falloff = falloff[g],
                    normalDot = target.valid ? target.normalDot : source.normalDot,
                    weldedVertexCount = asset.groupMembers != null && g < asset.groupMembers.Length && asset.groupMembers[g] != null
                        ? asset.groupMembers[g].Count
                        : 1,
                    assetRegion = state.assetGroupRegions != null && g < state.assetGroupRegions.Length
                        ? state.assetGroupRegions[g]
                        : BodyRegion.Unknown,
                    sourceHitRegion = source.hitRegion,
                    targetHitRegion = target.hitRegion,
                    sourceUsedRelaxedFallback = source.usedRelaxedFallback,
                    targetUsedRelaxedFallback = target.usedRelaxedFallback,
                    sourceValid = source.valid,
                    targetValid = target.valid
                };

                if (weightDebug != null)
                {
                    if (weightDebug.decisionsByVertex != null && vertex < weightDebug.decisionsByVertex.Length)
                        point.weightDecision = weightDebug.decisionsByVertex[vertex];
                    if (weightDebug.projectedByGroup != null && g < weightDebug.projectedByGroup.Length &&
                        weightDebug.projectedValidByGroup != null && g < weightDebug.projectedValidByGroup.Length &&
                        weightDebug.projectedValidByGroup[g])
                        point.projectedWeights = FormatWeights(weightDebug.projectedByGroup[g], state.comp.bones);
                    if (weightDebug.originalByVertex != null && vertex < weightDebug.originalByVertex.Length &&
                        weightDebug.originalValidByVertex != null && vertex < weightDebug.originalValidByVertex.Length &&
                        weightDebug.originalValidByVertex[vertex])
                        point.originalWeights = FormatWeights(weightDebug.originalByVertex[vertex], state.comp.bones);
                    if (weightDebug.finalByVertex != null && vertex < weightDebug.finalByVertex.Length)
                        point.finalWeights = FormatWeights(weightDebug.finalByVertex[vertex], state.comp.bones);
                }

                if (!source.valid || !target.valid)
                    point.note = "unbound";
                else if (source.usedRelaxedFallback || target.usedRelaxedFallback)
                    point.note = "relaxed fallback";

                points[g] = point;
            }

            return new ReFitProjectionDebugData { points = points };
        }

        private static string FormatWeights(BoneWeight weight, ReFitBoneRef[] bones)
        {
            var sb = new System.Text.StringBuilder(96);
            AppendWeight(sb, weight.boneIndex0, weight.weight0, bones);
            AppendWeight(sb, weight.boneIndex1, weight.weight1, bones);
            AppendWeight(sb, weight.boneIndex2, weight.weight2, bones);
            AppendWeight(sb, weight.boneIndex3, weight.weight3, bones);
            return sb.Length > 0 ? sb.ToString() : "-";
        }

        private static void AppendWeight(System.Text.StringBuilder sb, int index, float weight, ReFitBoneRef[] bones)
        {
            if (weight <= 0.0001f || index < 0) return;
            if (sb.Length > 0) sb.Append(", ");
            string name = index < (bones != null ? bones.Length : 0) && bones[index] != null && !string.IsNullOrEmpty(bones[index].name)
                ? bones[index].name
                : ("bone" + index);
            sb.Append(name).Append('=').Append((weight * 100f).ToString("0.#")).Append('%');
        }

        /// <summary>Converts per-group world deltas into per-vertex mesh-space blendshape deltas.</summary>
        private static Vector3[] ToLocalDeltas(State state, Vector3[] groupDeltas, bool isPositionFrame)
        {
            var asset = state.asset;
            int vertexCount = asset.localVertices.Length;
            var result = new Vector3[vertexCount];
            var w2l = state.targetSpaceMetadata != null && state.targetSpaceMetadata.hasDeltaWorldToLocal
                ? state.targetSpaceMetadata.deltaWorldToLocal
                : asset.rendererWorldToLocal;
            bool rendererLocalDeltas = state.replace || state.targetSpaceMetadata != null;
            Parallel.For(0, vertexCount, i =>
            {
                var dWorld = groupDeltas[asset.groupOfVertex[i]];
                if (rendererLocalDeltas)
                {
                    // New bindposes are captured in the staged pose: at rest the rebuilt armature maps mesh
                    // space through the renderer transform. Store only the requested surface displacement here.
                    // Re-baking asset.worldVertices would turn an existing skinned-pose offset into a refit delta.
                    result[i] = w2l.MultiplyVector(dWorld);
                }
                else
                {
                    // Original skinning kept: un-skin the world delta through the inverse skinning matrix.
                    result[i] = asset.skinMatrices[i].inverse.MultiplyVector(dWorld);
                }
            });
            return result;
        }

        // ------------------------------------------------------------------
        // Phase 3: Bake (main thread — Mesh API)
        // ------------------------------------------------------------------

        private void Bake(State state)
        {
            var comp = state.comp;
            var settings = state.settings;
            var newMesh = UnityEngine.Object.Instantiate(state.asset.mesh);
            comp.mesh = newMesh;
            newMesh.name = state.asset.mesh.name.Replace("(Clone)", "") + "_ReFit";

            if (state.primaryLocalDeltas != null)
            {
                comp.primaryShapeName = UniqueShapeName(newMesh, settings.blendshapeName);
                newMesh.AddBlendShapeFrame(comp.primaryShapeName, 100f, state.primaryLocalDeltas, state.primaryNormalDeltas, null);
                comp.debugPrimaryRawLocalDeltas = state.primaryRawLocalDeltas;
                if (comp.generatedMetadata != null)
                    comp.generatedMetadata.primaryShapeName = comp.primaryShapeName;
            }

            if (state.shapes.Count > 0)
            {
                comp.secondaryShapeNames = new string[state.shapes.Count];
                comp.secondarySourceShapeNames = new string[state.shapes.Count];
                comp.secondaryMirrorWeights = new float[state.shapes.Count];
                comp.debugSecondaryRawLocalDeltas = new Vector3[state.shapes.Count][];
                if (comp.generatedMetadata != null)
                    comp.generatedMetadata.transferredShapes = new ReFitGeneratedTransferredShapeMetadata[state.shapes.Count];
                for (int s = 0; s < state.shapes.Count; s++)
                {
                    var shape = state.shapes[s];
                    string desired = settings.prefixTransferredShapes
                        ? $"{settings.blendshapeName}_{shape.sourceName}"
                        : shape.sourceName;
                    var name = UniqueShapeName(newMesh, desired);
                    newMesh.AddBlendShapeFrame(name, 100f, shape.localDeltas, shape.normalDeltas, null);
                    comp.secondarySourceShapeNames[s] = shape.sourceName;
                    comp.secondaryShapeNames[s] = name;
                    comp.secondaryMirrorWeights[s] = shape.mirrorWeight;
                    comp.debugSecondaryRawLocalDeltas[s] = shape.rawLocalDeltas;
                    if (comp.generatedMetadata?.transferredShapes != null)
                    {
                        comp.generatedMetadata.transferredShapes[s] = new ReFitGeneratedTransferredShapeMetadata
                        {
                            frameIdentity = shape.frameIdentity,
                            generatedName = name,
                            sourceName = shape.sourceName,
                            localDeltas = shape.localDeltas != null
                                ? (Vector3[])shape.localDeltas.Clone()
                                : null
                        };
                    }
                }
            }

            if (state.replace)
            {
                newMesh.boneWeights = state.newWeights;
                newMesh.bindposes = state.newBindposes;
            }

            comp.mesh = newMesh;
            comp.success = !comp.report.HasErrors;
        }

        // ------------------------------------------------------------------
        // Regions
        // ------------------------------------------------------------------

        private static BodyRegion[] TriangleRegions(MeshSnapshot body, Dictionary<Transform, BodyRegion> boneRegions)
        {
            var perBone = new BodyRegion[body.bones != null ? body.bones.Length : 0];
            for (int k = 0; k < perBone.Length; k++)
            {
                perBone[k] = BodyRegion.Unknown;
                if (body.bones[k] != null && boneRegions.TryGetValue(body.bones[k], out var r)) perBone[k] = r;
            }
            var perVertex = VertexRegions(body, perBone);

            int triCount = body.triangles.Length / 3;
            var perTri = new BodyRegion[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var ra = perVertex[body.triangles[t * 3]];
                var rb = perVertex[body.triangles[t * 3 + 1]];
                var rc = perVertex[body.triangles[t * 3 + 2]];
                perTri[t] = ra == rb || ra == rc ? ra : (rb == rc ? rb : ra);
            }
            return perTri;
        }

        private static BodyRegion[] VertexRegions(MeshSnapshot snap, BodyRegion[] perBone)
        {
            int n = snap.localVertices.Length;
            var regions = new BodyRegion[n];
            bool hasWeights = !snap.rigid && snap.boneWeights != null && snap.boneWeights.Length == n;
            for (int i = 0; i < n; i++)
            {
                regions[i] = BodyRegion.Unknown;
                if (!hasWeights) continue;
                var bw = snap.boneWeights[i];
                float best = 0f;
                PickRegion(bw.boneIndex0, bw.weight0, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex1, bw.weight1, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex2, bw.weight2, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex3, bw.weight3, perBone, ref best, ref regions[i]);
            }
            return regions;
        }

        private static void PickRegion(int idx, float w, BodyRegion[] perBone, ref float best, ref BodyRegion region)
        {
            if (w <= best || idx < 0 || idx >= perBone.Length) return;
            if (perBone[idx] == BodyRegion.Unknown) return;
            best = w;
            region = perBone[idx];
        }

        private static BodyRegion[] AssetGroupRegions(MeshSnapshot asset, NormalizedStage stage, Dictionary<Transform, BodyRegion> sourceRegions)
        {
            var perBone = new BodyRegion[asset.bones != null ? asset.bones.Length : 0];
            for (int k = 0; k < perBone.Length; k++)
            {
                perBone[k] = BodyRegion.Unknown;
                var bone = asset.bones[k];
                if (bone == null) continue;
                if (stage.assetOnSourceAvatar)
                {
                    if (sourceRegions.TryGetValue(bone, out var r)) perBone[k] = r;
                }
                else if (stage.assetBoneToSource.TryGetValue(bone, out var src) && src != null &&
                         sourceRegions.TryGetValue(src, out var r2))
                {
                    perBone[k] = r2;
                }
            }
            var perVertex = VertexRegions(asset, perBone);
            var perGroup = new BodyRegion[asset.GroupCount];
            for (int g = 0; g < perGroup.Length; g++) perGroup[g] = perVertex[asset.groupRep[g]];
            return perGroup;
        }


        // ------------------------------------------------------------------
        // Blendshape helpers (thread-safe)
        // ------------------------------------------------------------------

        private static string UniqueShapeName(Mesh mesh, string desired)
        {
            if (string.IsNullOrEmpty(desired)) desired = "refit";
            if (mesh.GetBlendShapeIndex(desired) < 0) return desired;
            for (int i = 2; ; i++)
            {
                var candidate = $"{desired}_{i}";
                if (mesh.GetBlendShapeIndex(candidate) < 0) return candidate;
            }
        }

        /// <summary>
        /// Per-vertex normal deltas so the shape lights correctly at full weight:
        /// delta = normals(displaced) - reference normals, with welding-group averaging.
        /// <paramref name="baseDeltas"/> is the already-applied previous frame (for stacked shapes), may be null.
        /// </summary>
        private static Vector3[] NormalDeltas(MeshSnapshot asset, Vector3[] frameDeltas, Vector3[] baseDeltas)
        {
            int n = asset.localVertices.Length;
            var before = new Vector3[n];
            var after = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var b = asset.localVertices[i];
                if (baseDeltas != null) b += baseDeltas[i];
                before[i] = b;
                after[i] = b + frameDeltas[i];
            }
            var afterNormals = WeldedNormals(after, asset);
            Vector3[] reference = baseDeltas == null ? asset.baseNormals : WeldedNormals(before, asset);

            var deltas = new Vector3[n];
            for (int i = 0; i < n; i++)
                deltas[i] = afterNormals[i] - reference[i];
            return deltas;
        }

        private static Vector3[] WeldedNormals(Vector3[] verts, MeshSnapshot asset)
        {
            var normals = new Vector3[verts.Length];
            var tris = asset.triangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var fn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                normals[a] += fn; normals[b] += fn; normals[c] += fn;
            }
            var groupSum = new Vector3[asset.GroupCount];
            for (int i = 0; i < verts.Length; i++) groupSum[asset.groupOfVertex[i]] += normals[i];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = groupSum[asset.groupOfVertex[i]];
                normals[i] = v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.up;
            }
            return normals;
        }
    }
}
