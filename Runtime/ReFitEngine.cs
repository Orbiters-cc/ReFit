using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

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
        private readonly int shapeWorkers;

        /// <summary>Limits concurrent blendshape frames; zero chooses a bounded CPU-based default.</summary>
        public ReFitEngine() : this(0) { }

        public ReFitEngine(int maxShapeWorkers)
        {
            shapeWorkers = maxShapeWorkers > 0 ? Math.Min(8, maxShapeWorkers)
                : Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
        }
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
                var watch = System.Diagnostics.Stopwatch.StartNew();
                state = Prepare(request, progress);
                ReportPhase(state, "Staging", watch);
                state.cancellationToken = cancellationToken;
                if (state.failed) return state.comp;
                ComputeGeometry(state);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(0.85f, "Baking the mesh");
                cancellationToken.ThrowIfCancellationRequested();
                watch.Restart();
                Bake(state);
                ReportPhase(state, "Baking", watch);
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

        // A main-thread phase's time, reported as the background phases are (phase-timing).
        private static void ReportPhase(State state, string phase, System.Diagnostics.Stopwatch watch) =>
            state?.Report.Info("phase-timing", $"{phase}: {watch.Elapsed.TotalMilliseconds:F1} ms");

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
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = Prepare(request, progress);
                ReportPhase(state, "Staging", watch);
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
                        watch.Restart();
                        Bake(state);
                        ReportPhase(state, "Baking", watch);
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
                ValidateShapes(request, stage.targetBody, report);
            }
            return report;
        }

        /// <summary>
        /// The part of <see cref="Validate"/> that stages the avatars (armature matching, proportions): it does not depend on
        /// the requested blendshapes, so it holds for every shape choice of the same avatars and asset.
        /// </summary>
        public ReFitReport ValidateStaging(ReFitRequest request)
        {
            var report = new ReFitReport();
            if (!ValidateRequest(request, report, false)) return report;
            using (var stage = PoseNormalizer.CreateStage(request, report))
                if (stage != null) ProportionChecker.Check(stage, request.settings ?? new ReFitSettings(), report);
            return report;
        }

        /// <summary>The part of <see cref="Validate"/> about the requested blendshapes, on the target body (no staging).</summary>
        public static void ValidateShapes(ReFitRequest request, SkinnedMeshRenderer targetBody, ReFitReport report)
        {
            if (request != null && request.mode != ReFitMode.MeshToMesh && RequestedShapeNames(request).Count == 0)
            {
                report.Error("missing-blendshape", "Blendshape transfer needs at least one target body blendshape name.");
                return;
            }
            var names = RequestedShapeNames(request);
            if (names.Count == 0 || targetBody == null || targetBody.sharedMesh == null) return;
            int found = 0;
            foreach (var name in names)
            {
                if (targetBody.sharedMesh.GetBlendShapeIndex(name) >= 0) found++;
                else report.Warn("blendshape-not-found", $"Blendshape '{name}' was not found on the target body '{targetBody.name}'.");
            }
            if (found == 0 && request.mode != ReFitMode.MeshToMesh)
                report.Error("blendshapes-missing", "None of the requested blendshapes exist on the target body.");
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private class ShapeTask
        {
            public string sourceName;
            public int shapeIndex;
            public float mirrorWeight;
            /// <summary>
            /// How much of its frame the target shows (0..1, as Unity applies the shape at <see cref="mirrorWeight"/>): the target
            /// is fitted with it, and the clothing shape is the drape taking it away, per frame. 0: fitted at zero, the clothing
            /// shape is the drape adding it.
            /// </summary>
            public float shown;
            /// <summary>The body shape's frames, each transferred on its own so in-between frames keep their geometry.</summary>
            public List<ShapeFrame> frames = new List<ShapeFrame>();
        }

        private class ShapeFrame
        {
            public float weight;
            /// <summary>Mesh-local frame deltas on the target body (captured on the main thread).</summary>
            public Vector3[] deltas;
            public string identity;
            // background results
            public Vector3[] rawLocalDeltas;
            public Vector3[] localDeltas;
            public Vector3[] normalDeltas;
            public Vector3[] tangentDeltas;
            public readonly List<Vector3[]> layerDeltas = new List<Vector3[]>();
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
            public ReFitTubeField tubes;
            public MeshSnapshot sourceBody;   // null when not wantMesh; == targetBasis when source == target
            public MeshSnapshot targetBasis;
            /// <summary>The target without the followed shapes it shows (see <see cref="ShapeTask.shown"/>), or null when it shows none.</summary>
            public MeshSnapshot targetBare;
            public readonly List<MeshSnapshot> coverageLayers = new List<MeshSnapshot>();
            // Each layer's vertices as authored (before its own refit), when the layer order is kept, and whether it was refitted.
            public readonly List<Vector3[]> coverageLayersAuthored = new List<Vector3[]>();
            public readonly List<bool> coverageLayersRefitted = new List<bool>();
            // Each layer's name, read on the main thread for the coverage diagnostics.
            public readonly List<string> coverageLayerNames = new List<string>();
            // Clipped groups another surface covers as authored: the body coverage leaves them in place. Covers of every group:
            // in shapes, fabric stays beneath what lies over it.
            public bool[] hiddenGroups;
            // Different-base coverage's last step, for the refit and each transferred frame.
            public ReFitDrape drape;
            // The clothing was made for another body than the source (see AuthoredUnder): its limbs cover what came through them.
            public bool madeForAnother;
            public ReFitSurfaceCoverage.Cover?[] hiddenCovers;
            public MeshBvh targetBvh;
            /// <summary>The source body laid onto the target (mesh refits between two bodies), or null.</summary>
            public ReFitBodyRegistration registration;
            /// <summary>Per source body bone, the transform carrying it onto the target skeleton (see SkeletonRetarget), or null.</summary>
            public Matrix4x4[] sourceBoneRetarget;
            /// <summary>The clothing fitted like cloth (see <see cref="ReFitSettings.clothFit"/>), or null.</summary>
            public ReFitClothFit clothFit;
            /// <summary>The target counterparts met along the source's normal, kept for a fit that cannot use the registration.</summary>
            public SurfaceBinding[] chainedAlongNormal;
            // Each group's matching point on the target body for its source match (the same anatomical point), for debugging.
            public SurfaceBinding[] chainedTargetBindings;
            public List<ShapeTask> shapes = new List<ShapeTask>();
            public bool assetIsLikelyUpperBodyGarment;

            public BodyRegion[] assetGroupRegions;
            public BodyRegion[] sourceTriRegions;
            /// <summary>The source's triangle regions with the shoulders in the torso: the limbs a sleeve or a leg lies over.</summary>
            public BodyRegion[] sourceLimbRegions;
            public BodyRegion[] targetTriRegions;
            // Per target vertex, how much of its skinning is the legs', the feet not (LegShares).
            public float[] targetLegShares;
            // Every region of each triangle (SurfaceBindingSolver.RegionMask bits), for binding across transition bands.
            public int[] sourceTriRegionMasks;
            public int[] targetTriRegionMasks;
            // For matches from the other body or the fitted clothing: a limb's region also accepts torso skin.
            public int[] targetTriCrossMasks;

            // bone plan (resolved on the main thread; weights computed in the background)
            public Matrix4x4[] newBindposes;
            public int[] bodyBoneToNew;
            public int[] sourceBoneToNew;
            public int[] assetBoneToNew;
            public bool[] assetBoneIsExtra;
            public BodyRegion[] newBoneRegions;
            public bool transferWeights;

            // background outputs
            public Vector3[] primaryRawLocalDeltas;
            public Vector3[] primaryLocalDeltas;
            public Vector3[] primaryNormalDeltas;
            public Vector3[] primaryTangentDeltas;
            public Vector3[] restLocalDeltas, restNormalDeltas, restTangentDeltas;
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

            public State ForShape()
            {
                var worker = (State)MemberwiseClone();
                worker.comp = new ReFitComputation();
                return worker;
            }
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
            public MeshBvh bvh;
            public int[] triangleComponents;
            // Per triangle, its component's size (int.MinValue for no component): the support rule's rank.
            public int[] triangleRanks;
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
            // With Burst compilation off the [BurstCompile] entries run as managed code. The solver and smoothing kernels compute
            // in double with bit-equal managed twins, so they agree; the hierarchy's queries (float, Toolkit's BvhKernels) may round
            // differently in the last bit, and every stage reads them: the fit is then not exactly the one a machine with Burst
            // on makes. Registrations computed so are cached under a key of their own (ReFitBodyRegistration.Build).
            if (!Unity.Burst.BurstCompiler.IsEnabled)
                report.Warn("burst-off", "Burst compilation is off (Jobs > Burst): ReFit runs slower, and its surface queries may round differently, so results can differ slightly from a machine with Burst on.");

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

                // Per-shape mesh-local frame deltas (main thread: Mesh API)
                var bodyMesh = stage.targetBody.sharedMesh;
                int vertexCount = bodyMesh.vertexCount;
                foreach (var shape in state.shapes)
                {
                    int frameCount = bodyMesh.GetBlendShapeFrameCount(shape.shapeIndex);
                    for (int f = 0; f < frameCount; f++)
                    {
                        var deltas = new Vector3[vertexCount];
                        bodyMesh.GetBlendShapeFrameVertices(shape.shapeIndex, f, deltas, null, null);
                        shape.frames.Add(new ShapeFrame
                        {
                            weight = bodyMesh.GetBlendShapeFrameWeight(shape.shapeIndex, f),
                            deltas = deltas,
                            identity = ReFitCacheIdentity.Shape(deltas)
                        });
                    }
                }
                // Clothing fitted from another body is fitted on the target as it shows, the followed shapes it shows included:
                // their clothing shapes take them away from that fit. Clothing given shapes alone made with followed shapes
                // the target shows gets them taken away the same way, and its rest gives them back (see MadeWithShownShapes).
                // The others are fitted at zero.
                bool targetSpace = ShouldUseTargetSpaceBaseBlendshape(request, stage);
                var metadata = targetSpace ? CopyGeneratedMetadata(stage.assetRenderer) : null;
                var made = state.wantMesh ? null : request.madeWithShapes;
                var zeroed = state.wantMesh
                    ? ReFitWornShapes.FittedAtZero(stage.targetBody, request.wornBodyShapes, requestedNames, stage.sourceBody != stage.targetBody)
                    : ShapesOnlyFittedAtZero(request, stage, requestedNames, metadata, report);
                var followedShown = new List<string>();
                foreach (var shape in state.shapes)
                {
                    if (zeroed.Contains(shape.sourceName)) continue;
                    shape.shown = ReFitWornShapes.Shown(stage.targetBody, shape.sourceName, request.wornBodyShapes, made);
                    float weight = made != null && made.TryGetValue(shape.sourceName, out float madeWeight) ? madeWeight : shape.mirrorWeight;
                    followedShown.Add(shape.sourceName + " " + weight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (followedShown.Count > 0)
                    report.Info("followed-shapes-shown", state.wantMesh
                        ? "Fitted with the shapes it follows as the target shows them (" + string.Join(", ", followedShown) + "): their clothing shapes take them away from the fit."
                        : "Made with the shapes it follows (" + string.Join(", ", followedShown) + "): their clothing shapes take them away, and its rest gives them back.");

                // Snapshots
                progress?.Invoke(0.08f, "Capturing meshes");
                var originalTarget = request.targetBodyRenderer;
                if (originalTarget == null)
                {
                    var path = ReFitUtility.IndexPath(stage.targetBody.transform, stage.targetRoot.transform);
                    var originalTransform = ReFitUtility.ResolvePath(request.targetAvatar.transform, path);
                    originalTarget = originalTransform != null ? originalTransform.GetComponent<SkinnedMeshRenderer>() : null;
                }
                // Shapes added to clothing fitted across bases are covered and draped as the fit's own were, whoever asks.
                if (metadata != null && metadata.coverDifferentBaseBody && !state.settings.coverDifferentBaseBody)
                {
                    state.settings.coverDifferentBaseBody = true;
                    report.Info("coverage-kept", "The clothing was fitted from another base: its new shapes are covered and draped like the fit.");
                }
                // Covered as made for another body, its new shapes keep the guards the fit had: its pieces follow the fabric, its
                // sleeves and legs cover the limbs that came through them.
                if (metadata != null && metadata.madeForAnother) state.madeForAnother = true;
                state.targetIdentity = ReFitCacheIdentity.Renderer(originalTarget, false);
                string worn = ReFitWornShapes.Describe(stage.targetBody, request.wornBodyShapes);
                if (worn.Length > 0)
                {
                    // The body as worn is another body: fits made on the bare one are not reused.
                    state.targetIdentity += "|worn:" + worn;
                    report.Info("worn-body-shapes", "Fitted on the bodies as the clothing is worn: " + worn + ".");
                }
                string shown = ReFitWornShapes.DescribeShown(stage.targetBody, request.wornBodyShapes, zeroed, made);
                if (shown.Length > 0)
                {
                    state.targetIdentity += "|shown:" + shown;
                    report.Info("shown-body-shapes", "Fitted on the target as it shows: " + shown + ".");
                }
                state.settingsIdentity = ReFitCacheIdentity.Settings(state.settings);
                state.bindingSettingsIdentity = ReFitCacheIdentity.BindingSettings(state.settings);
                Dictionary<int, float> assetShapeOverrides = null;
                if (targetSpace)
                {
                    state.targetSpaceMetadata = metadata;
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
                    ReFitWornShapes.AsShown(stage.targetBody, request.wornBodyShapes, zeroed, made), report);
                if (followedShown.Count > 0)
                    state.targetBare = MeshSnapshot.Capture(stage.targetBody, false,
                        ReFitWornShapes.AsShown(stage.targetBody, request.wornBodyShapes, requestedNames), report);
                if (state.wantMesh)
                    state.sourceBody = stage.sourceBody == stage.targetBody
                        ? state.targetBasis
                        : stage.sourceSurface ?? MeshSnapshot.Capture(stage.sourceBody, false,
                            ReFitWornShapes.Overrides(stage.sourceBody, request.wornBodyShapes), report);

                // Regions
                progress?.Invoke(0.16f, "Classifying body regions");
                var sourceRegions = HumanoidBoneMapper.ClassifyBones(stage.sourceRoot, stage.sourceHumanMap);
                var targetRegions = stage.sourceIsTarget ? sourceRegions : HumanoidBoneMapper.ClassifyBones(stage.targetRoot, stage.targetHumanMap);
                state.targetTriRegions = TriangleRegions(state.targetBasis, targetRegions);
                state.targetLegShares = LegShares(state.targetBasis, targetRegions, stage.targetHumanMap);
                state.targetTriRegionMasks = TriangleRegionMasks(state.targetBasis, targetRegions);
                state.targetTriCrossMasks = LimbsAcceptTorso(state.targetTriRegionMasks);
                state.sourceTriRegionMasks = state.wantMesh && state.sourceBody != state.targetBasis
                    ? TriangleRegionMasks(state.sourceBody, sourceRegions)
                    : state.targetTriRegionMasks;
                state.sourceTriRegions = state.wantMesh && state.sourceBody != state.targetBasis
                    ? TriangleRegions(state.sourceBody, sourceRegions)
                    : state.targetTriRegions;
                if (state.wantMesh && state.sourceBody != state.targetBasis)
                    state.sourceLimbRegions = TriangleRegions(state.sourceBody, HumanoidBoneMapper.ClassifyBones(stage.sourceRoot, stage.sourceHumanMap, true));
                // Shapes added to a fit made for another body drape from the fit over the target: its limbs are the target's.
                else if (state.madeForAnother && !state.wantMesh)
                    state.sourceLimbRegions = TriangleRegions(state.targetBasis, HumanoidBoneMapper.ClassifyBones(stage.targetRoot, stage.targetHumanMap, true));
                state.assetGroupRegions = AssetGroupRegions(state.asset, stage, sourceRegions);
                if (state.wantMesh && state.sourceBody != state.targetBasis && state.settings.registerBodies)
                    state.sourceBoneRetarget = SkeletonRetarget(stage, stage.sourceBody);

                // Bone plan (no weight transfer here — that runs in the background)
                state.replace = state.settings.replaceArmature && state.wantMesh && !stage.sourceIsTarget && !state.asset.rigid;
                if (state.replace && ArmatureMatchesTarget(stage, state.asset, out int sameBones))
                {
                    state.replace = false;
                    report.Info("armature-kept-same-skeleton",
                        $"Kept the clothing's armature: its {sameBones} weighted bones already stand on the target's skeleton.");
                }
                state.transferWeights = state.settings.transferWeights;
                if (state.replace)
                {
                    progress?.Invoke(0.2f, "Resolving the target armature");
                    state.replace = BuildBonePlan(stage, state.asset, state.targetBasis, state.sourceBody,
                        state.comp, sourceRegions, targetRegions, report,
                        out state.newBindposes, out state.bodyBoneToNew, out state.sourceBoneToNew, out state.assetBoneToNew,
                        out state.assetBoneIsExtra, out state.newBoneRegions);
                    if (!state.replace)
                        report.Warn("armature-replace-skipped", "Could not build the target bone plan; keeping the asset's original armature.");
                }
                state.comp.armatureReplaced = state.replace;
                CaptureCoverageLayers(state, stage);
                state.ownedTemplateMesh = stage.TakeAssetMesh();
            } // stage disposed: everything below works on captured arrays only

            return state;
        }

        private static void DestroyOwnedTemplate(State state)
        {
            if (state?.ownedTemplateMesh != null) UnityEngine.Object.DestroyImmediate(state.ownedTemplateMesh);
            if (state != null) state.ownedTemplateMesh = null;
        }

        /// <summary>
        /// The followed shapes clothing given shapes alone is fitted at zero for: those the target does not show, those a refit
        /// of it already follows (its refit shape gives them back), and those it was not made with. What it was made with is
        /// what the request says (<see cref="ReFitRequest.madeWithShapes"/>), else which body it lies over
        /// (<see cref="ReFitWornShapes.MadeWithShown"/>): made on the base before the body was shaped, it follows the shape
        /// from zero; made over the shape, it is taken away and given back.
        /// </summary>
        private static List<string> ShapesOnlyFittedAtZero(ReFitRequest request, NormalizedStage stage, List<string> followed,
            ReFitGeneratedAssetMetadataData metadata, ReFitReport report)
        {
            var body = stage.targetBody;
            var made = request.madeWithShapes;
            var zeroed = ReFitWornShapes.FittedAtZero(body, request.wornBodyShapes, followed, true, made);
            var refitted = new HashSet<string>(metadata?.transferredShapes?.Select(shape => shape.sourceName) ?? Enumerable.Empty<string>());
            var unknown = new List<string>();
            foreach (var name in followed)
            {
                if (zeroed.Contains(name)) continue;
                if (refitted.Contains(name)) zeroed.Add(name);
                else if (made == null || !made.ContainsKey(name)) unknown.Add(name);
            }
            if (unknown.Count == 0) return zeroed;

            // The clothing as it is worn over the body as it shows those shapes.
            var shownBody = MeshSnapshot.Capture(body, false, ReFitWornShapes.AsShown(body, request.wornBodyShapes, zeroed, made), null);
            var index = shownBody.Index();
            var cloth = MeshSnapshot.SkinnedPositions(stage.assetRenderer, null);
            var mesh = body.sharedMesh;
            var frame = new Vector3[mesh.vertexCount];
            foreach (var name in unknown)
            {
                float shown = ReFitWornShapes.Shown(body, name, request.wornBodyShapes);
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex(name), 0, frame, null, null);
                var motion = new Vector3[frame.Length];
                for (int i = 0; i < frame.Length; i++) motion[i] = shownBody.skinMatrices[i].MultiplyVector(frame[i]) * shown;
                bool? with = ReFitWornShapes.MadeWithShown(cloth, shownBody, index, motion);
                if (with != true) zeroed.Add(name);
                report.Info("made-with-shape", with == null
                    ? $"'{name}' moves too little of the body under the clothing to tell what it was made with: it follows the shape from zero."
                    : with.Value
                        ? $"The clothing lies over the body with '{name}' as it shows: made with it, the shape is taken away and its rest gives it back."
                        : $"The clothing lies over the body without '{name}': made before it was shown, it follows the shape from zero.");
            }
            return zeroed;
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
            var scratch = new Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Scratch(mesh.vertexCount);
            bool clamp = Orbiters.Toolkit.Meshes.BlendShapeEvaluation.ClampWeights;
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                float weight = renderer.GetBlendShapeWeight(s);
                if (Mathf.Abs(weight) <= 1e-2f)
                    continue;

                string shapeName = mesh.GetBlendShapeName(s);
                if (!IsTargetSpaceBaseBlendshapeName(shapeName, requestedTargetShapes, settings, metadataPrimaryShapeName))
                    continue;

                if (mesh.GetBlendShapeFrameCount(s) == 0)
                    continue;

                Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Add(mesh, s, weight, clamp, accumulated, null, scratch);

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
                coverDifferentBaseBody = state.settings.coverDifferentBaseBody,
                madeForAnother = state.madeForAnother,
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

        // Per target vertex, how the followed shapes the target shows move it (world): the target less the target without them.
        private static Vector3[] ShownMotion(MeshSnapshot target, MeshSnapshot bare)
        {
            var motion = new Vector3[target.worldVertices.Length];
            for (int i = 0; i < motion.Length; i++) motion[i] = target.worldVertices[i] - bare.worldVertices[i];
            return motion;
        }

        // Each group's counterpart on a target, met along the source's normal (see SurfaceBindingSolver.ChainPoint).
        private static SurfaceBinding[] Chained(State state, SurfaceBinding[] bindings, MeshSnapshot firstSnap, MeshSnapshot target, MeshBvh index)
        {
            var settings = state.settings;
            float cosMax = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
            float chainRange = Mathf.Max(settings.maxProjectionDistance * 2f, 0.05f);
            var chained = new SurfaceBinding[bindings.Length];
            Parallel.For(0, bindings.Length, g =>
            {
                if (!bindings[g].valid) return;
                var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                chained[g] = SurfaceBindingSolver.ChainPoint(bindings[g].point, firstSnap.BaryNormal(bindings[g].triangle, bindings[g].bary),
                    target, index, chainRange, region, settings.filterByBoneRegion ? state.targetTriRegions : null, cosMax,
                    settings.filterByNormal, state.targetTriCrossMasks);
            });
            return chained;
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

        private static bool ValidateRequest(ReFitRequest request, ReFitReport report, bool shapes = true)
        {
            if (request == null) { report.Error("bad-request", "Request is null."); return false; }
            if (request.assetRenderer == null) { report.Error("missing-asset", "No asset renderer set."); return false; }
            if (request.targetAvatar == null) { report.Error("missing-target", "No target avatar set."); return false; }
            if (request.mode != ReFitMode.Blendshape && request.sourceAvatar == null)
            {
                report.Error("missing-source", "Mesh re-fit needs the avatar the asset was made for (source avatar).");
                return false;
            }
            if (shapes && request.mode != ReFitMode.MeshToMesh && RequestedShapeNames(request).Count == 0)
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
            // The stages and what each hands the next: the bodies' hierarchies, the correspondence (bindings, the registration,
            // falloff), the primary fit (by one of three paths), the shape frames, the skin weights.
            IndexBodies(state);
            var correspondence = Correspond(state);
            var fit = FitPrimary(state, correspondence);
            if (state.shapes.Count > 0) TransferShapes(state, correspondence, fit);
            if (state.replace) TransferSkinWeights(state, correspondence);
            if (state.settings.captureProjectionDebug)
                state.comp.projectionDebug = BuildProjectionDebugData(state, correspondence.bindings, fit.transferBindings, correspondence.falloff);
            SetBackgroundProgress(state, 1f, "Finishing");
        }

        /// <summary>What ties the clothing to the bodies (<see cref="Correspond"/>).</summary>
        private sealed class Correspondence
        {
            /// <summary>The surface the clothing is bound to first (the source body for a mesh refit, else the target) and its hierarchy.</summary>
            public MeshSnapshot firstSnap;
            public MeshBvh firstBvh;
            /// <summary>Each welded group's point on the first surface, and its counterpart on the target.</summary>
            public SurfaceBinding[] bindings, targetBindings;
            /// <summary>How the followed shapes the target shows move it, and the target without them it is laid onto (or the target).</summary>
            public Vector3[] shownMotion;
            public MeshSnapshot laidOnto;
            public MeshBvh laidIndex;
            /// <summary>The source laid onto <see cref="laidOnto"/>, before the shown shapes carry it (or null).</summary>
            public ReFitBodyRegistration laid;
            /// <summary>Per group, how much of the motion it takes (its distance to the first surface).</summary>
            public float[] falloff;
            public ReFitClearanceCorrection.Profile clearanceProfile;
            /// <summary>The target bindings saved with an earlier fit of this clothing (shapes added to it), or null.</summary>
            public SurfaceBinding[] metadataTransferBindings;
        }

        /// <summary>The primary fit and what the shape frames need of it (<see cref="FitPrimary"/>).</summary>
        private sealed class FitResult
        {
            /// <summary>Per welded group, the world motion of the fit (null: shapes only, no fit).</summary>
            public Vector3[] primaryGroupDeltas;
            /// <summary>Each group's point on the target as fitted, for the frames' body motion.</summary>
            public SurfaceBinding[] transferBindings;
            /// <summary>The coverage of the body (different-base passes) and of each garment worn under it.</summary>
            public ReFitSurfaceCoverage coverage;
            public List<ReFitSurfaceCoverage> layerCoverage;
        }

        // The garment's own data, the enclosed skin of the target and every body's hierarchy (state.targetBvh).
        private static void IndexBodies(State state)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            int groupCount = asset.GroupCount;
            state.tubes = ReFitTubeField.Build(asset, settings.preserveClosedTubes);
            state.openBoundaryWeights =
                CloneMetadataWeights(state.targetSpaceMetadata?.openBoundaryWeights, groupCount) ??
                BuildOpenBoundaryWeights(asset);

            // Spatial indices
            SetBackgroundProgress(state, 0.05f, "Building spatial indices");
            // Clothing fitted across bases covers the outside of the target: skin enclosed in another part of it (a chest
            // under added breasts) must not be matched, covered or skinned to.
            bool hidesEnclosed = settings.coverDifferentBaseBody && settings.enableClearanceCorrection;
            // Bodies with the same content as an earlier refit's (the outfit's other pieces) share what was derived from them.
            ReFitBodyCache.Attach(targetBasis);
            ReFitBodyCache.Attach(state.targetBare);
            if (state.wantMesh) ReFitBodyCache.Attach(state.sourceBody);
            int enclosed = hidesEnclosed ? targetBasis.HideEnclosedSkin() : 0;
            // Skin enclosed under large muscles is not enclosed without them.
            if (hidesEnclosed && state.targetBare != null) state.targetBare.HideEnclosedSkin();
            if (enclosed > 0)
                state.Report.Info("enclosed-skin", $"Target body: {enclosed} triangles of skin enclosed in another part of the body are ignored.");
            state.targetBvh = targetBasis.Index();
        }

        // Binds the clothing to the first surface, carries the bindings onto the target (through the registration of two
        // bodies), and derives the falloff and the clearance profile.
        private static Correspondence Correspond(State state)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var bvhTarget = state.targetBvh;
            var bvhSource = state.wantMesh && state.sourceBody != targetBasis ? state.sourceBody.Index() : bvhTarget;
            int groupCount = asset.GroupCount;
            // Bindings to the first surface (source body for mesh refit, target body otherwise)
            SetBackgroundProgress(state, 0.15f, "Binding the asset to the body");
            var firstSnap = state.wantMesh ? state.sourceBody : targetBasis;
            var firstBvh = state.wantMesh ? bvhSource : bvhTarget;
            var firstTriRegions = state.wantMesh ? state.sourceTriRegions : state.targetTriRegions;
            var firstTriRegionMasks = state.wantMesh ? state.sourceTriRegionMasks : state.targetTriRegionMasks;
            var bindings = SurfaceBindingSolver.ComputeGroupBindings(
                asset, firstSnap, firstBvh, settings, state.assetGroupRegions, firstTriRegions, state.Report, state.tubes.groups,
                firstTriRegionMasks);

            // Chain onto the target surface
            var targetBindings = bindings;
            // The target without the followed shapes it shows (muscles at 100), or the target: the bodies alike.
            var shownMotion = state.wantMesh && state.targetBare != null ? ShownMotion(targetBasis, state.targetBare) : null;
            var laidOnto = shownMotion != null ? state.targetBare : targetBasis;
            var laidIndex = shownMotion != null ? laidOnto.Index() : bvhTarget;
            ReFitBodyRegistration laid = null;
            if (state.wantMesh)
            {
                // Two bodies: the source laid onto the target gives each source point its counterpart. Two copies of one body
                // are one body: matched as the same body is, without laying one onto the other.
                if (settings.registerBodies && state.sourceBody != targetBasis && !SameSurface(state.sourceBody, targetBasis))
                {
                    SetBackgroundProgress(state, 0.2f, "Laying the bodies onto each other");
                    // Onto the bodies alike, then carried by the shown shapes with the skin it lies on: laid onto large muscles,
                    // the source reached too little of them.
                    var start = state.sourceBoneRetarget != null ? ReFitBodyRegistration.Retarget(state.sourceBody, state.sourceBoneRetarget) : null;
                    if (start != null)
                    {
                        // Only where it brings the source closer: bodies alike over different joints stay where they lie.
                        float before = MeanDistance(state.sourceBody.worldVertices, laidIndex), after = MeanDistance(start, laidIndex);
                        float most = 0f;
                        for (int v = 0; v < start.Length; v++) most = Mathf.Max(most, (start[v] - state.sourceBody.worldVertices[v]).magnitude);
                        if (after < before - RetargetGain)
                            state.Report.Info("skeleton-retarget", $"The skeletons carry the source body onto the target (up to {most * 1000f:0} mm; " +
                                $"{before * 1000f:0.0} mm from it on average before, {after * 1000f:0.0} mm after).");
                        else
                        {
                            state.Report.Info("skeleton-retarget", $"The skeletons would not bring the bodies closer ({before * 1000f:0.0} mm apart on " +
                                $"average, {after * 1000f:0.0} mm carried): laid onto each other from where they stand.");
                            start = null;
                        }
                    }
                    state.registration = laid = ReFitBodyRegistration.Build(state.sourceBody, state.sourceTriRegionMasks, laidOnto, laidIndex,
                        state.targetTriCrossMasks, state.cancellationToken, t => state.backgroundProgress = 0.2f + 0.1f * t, start);
                    state.Report.Info("body-registration", $"The source body lies on the target within {state.registration.medianGap * 1000f:0.0} mm " +
                        $"(95 % within {state.registration.p95Gap * 1000f:0.0} mm)" + (shownMotion != null ? ", the followed shapes it shows left out, then carried by them." : "."));
                    if (shownMotion != null) state.registration = state.registration.Carried(laidOnto, laidIndex, shownMotion, targetBasis);
                    state.Report.Info("registration-timing", state.registration.timing);
                }
                SetBackgroundProgress(state, 0.3f, "Projecting onto the target body");
                targetBindings = new SurfaceBinding[groupCount];
                var localBindings = bindings;
                var localTarget = targetBindings;
                var registration = state.registration;
                var chained = Chained(state, bindings, firstSnap, targetBasis, bvhTarget);
                var disagrees = new bool[groupCount];
                // How far the laid source is from the target skin under each clothing point near the skin (-1: not measured).
                var reach = new float[groupCount];
                Parallel.For(0, groupCount, g =>
                {
                    reach[g] = -1f;
                    if (!localBindings[g].valid) { localTarget[g].valid = false; return; }
                    var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                    localTarget[g] = chained[g];
                    if (registration == null) return;
                    bool near = localBindings[g].distance <= ClothingNearSkin;
                    if (RegisteredPoint(registration, localBindings[g], targetBasis, bvhTarget, state.targetTriRegions, region, out var registered))
                    {
                        localTarget[g] = registered;
                        disagrees[g] = chained[g].valid && near && (chained[g].point - registered.point).magnitude > RegistrationDisagreement;
                    }
                    // Laid farther than RegisteredReach from any target skin, the source cannot agree with the skin met along the
                    // normal (that skin is on the target): the registration does not reach there.
                    else disagrees[g] = chained[g].valid && near;
                    if (near)
                    {
                        // Every point near the skin is measured, reached or not, up to the limit.
                        var laid = registration.Point(localBindings[g].triangle, localBindings[g].bary);
                        var onTarget = bvhTarget.ClosestPoint(laid, RegistrationReachLimit);
                        reach[g] = onTarget.Found ? onTarget.Distance : RegistrationReachLimit;
                    }
                });
                if (registration != null)
                {
                    // Where the clothing lies, the laid source must reach the target skin, or agree with the skin met along the
                    // source's normal: bodies too different there (hips twice the base's, a large sculpted edit), where the
                    // registration cannot reach, are fitted by the passes made for them. Laid within RegistrationReach, it is
                    // trusted over the normal, which meets fluff, feathers or another layer of a body sculpted in sheets.
                    int near = 0, apart = 0;
                    var reaches = new List<float>();
                    for (int g = 0; g < groupCount; g++)
                    {
                        if (!localBindings[g].valid || localBindings[g].distance > ClothingNearSkin) continue;
                        near++;
                        if (disagrees[g]) apart++;
                        if (reach[g] >= 0f) reaches.Add(reach[g]);
                    }
                    float share = near > 0 ? (float)apart / near : 0f;
                    reaches.Sort();
                    float reached = reaches.Count > 0 ? reaches[Mathf.Min(reaches.Count - 1, (int)(reaches.Count * .9f))] : float.MaxValue;
                    if (share > settings.registrationDisagreementLimit && reached > RegistrationReach)
                    {
                        state.Report.Info("body-registration-unused", $"The bodies differ too much under the clothing to lay one onto the other " +
                            $"({share * 100f:0} % of it lies {RegistrationDisagreement * 100f:0} cm or more from where the skin along the normal is, " +
                            $"and the laid source is {reached * 1000f:0} mm from the target there for 10 % of it): fitted by the different-base passes.");
                        state.registration = null;
                        Array.Copy(chained, targetBindings, groupCount);
                    }
                    else
                    {
                        state.Report.Info("body-registration-used", $"{share * 100f:0.0} % of the clothing lies {RegistrationDisagreement * 100f:0} cm or more " +
                                                                  $"from where the skin along the normal is; the laid source is within {reached * 1000f:0.0} mm " +
                                                                  "of the target under 90 % of it.");
                        state.chainedAlongNormal = chained;
                    }
                }
            }
            state.chainedTargetBindings = targetBindings;

            // Falloff weight per group (asset distance to its base surface)
            var falloff = new float[groupCount];
            for (int g = 0; g < groupCount; g++)
                falloff[g] = bindings[g].valid
                    ? DeltaField.Falloff(bindings[g].distance, settings.falloffStartDistance, settings.maxProjectionDistance)
                    : 0f;
            var clearanceProfile = ReFitClearanceCorrection.BuildProfile(asset, firstSnap, bindings, settings, firstBvh);
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

            // Tubes have their own centerline contact constraints; independent surface pushes collapse them.
            if (clearanceProfile != null)
                for (int g = 0; g < groupCount; g++)
                    if (state.tubes.groups[g] && clearanceProfile.eligible[g])
                    {
                        clearanceProfile.eligible[g] = false;
                        clearanceProfile.eligibleGroups--;
                    }

            return new Correspondence
            {
                firstSnap = firstSnap,
                firstBvh = firstBvh,
                bindings = bindings,
                targetBindings = targetBindings,
                shownMotion = shownMotion,
                laidOnto = laidOnto,
                laidIndex = laidIndex,
                laid = laid,
                falloff = falloff,
                clearanceProfile = clearanceProfile,
                metadataTransferBindings = metadataTransferBindings,
            };
        }

        // The primary fit, by the cloth fit (two bodies laid onto each other), the different-base passes (bodies that cannot
        // be), or none (shapes added to clothing on the target); then what the frames need.
        private static FitResult FitPrimary(State state, Correspondence c)
        {
            var settings = state.settings;
            Vector3[] clothFitted = state.wantMesh && state.registration != null && settings.clothFit ? TryClothFit(state, c) : null;
            var fit = state.clothFit != null ? FitAsCloth(state, c, clothFitted)
                : state.wantMesh ? FitAcrossBases(state, c)
                : FitShapesOnly(state, c);
            FinishFit(state, c, fit);
            return fit;
        }

        // The cloth fit's motion of each group, or null (and the different-base passes take over) where it cannot fit.
        private static Vector3[] TryClothFit(State state, Correspondence c)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var bvhTarget = state.targetBvh;
            int groupCount = asset.GroupCount;
            var bindings = c.bindings;
            var targetBindings = c.targetBindings;
            var firstSnap = c.firstSnap;
            var falloff = c.falloff;
            Vector3[] primaryGroupDeltas;
            SetBackgroundProgress(state, 0.4f, "Draping the clothing over the body");
            state.clothFit = new ReFitClothFit(asset, bindings, firstSnap, state.assetGroupRegions, state.cancellationToken);
            primaryGroupDeltas = state.clothFit.Fit(firstSnap, state.sourceTriRegionMasks, state.registration, targetBasis, bvhTarget,
                state.targetTriRegions, falloff, state.Report, t => state.backgroundProgress = 0.4f + 0.08f * t);
            if (primaryGroupDeltas == null)
            {
                // Bodies the registration could not lay onto each other: the different-base passes, from the skin met
                // along the normal.
                state.clothFit = null;
                state.registration = null;
                if (state.chainedAlongNormal != null) Array.Copy(state.chainedAlongNormal, targetBindings, groupCount);
            }
            return primaryGroupDeltas;
        }

        // The cloth fit's motion, covered across bases when asked, then what the frames drape from.
        private static FitResult FitAsCloth(State state, Correspondence c, Vector3[] primaryGroupDeltas)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var bvhTarget = state.targetBvh;
            var bindings = c.bindings;
            var targetBindings = c.targetBindings;
            var firstSnap = c.firstSnap;
            var firstBvh = c.firstBvh;
            var transferBindings = targetBindings;
            if (settings.coverDifferentBaseBody && settings.enableClearanceCorrection)
            {
                // Coverage asked for clothing made for the source keeps the cloth fit: what came through it as made (a
                // strap pressing in, fluff through a collar) was meant to. Clothing made for another body and resized onto
                // this one lies under much of the source skin as authored: nothing there was meant, and the cloth fit
                // would keep all of it. The different-base passes cover the target after the cloth fit, as they cover
                // clothing placed on the target itself.
                var steps = new StepTimer();
                // Resized from another body, a garment can lie under little of the source (only the calves through a pair of
                // trousers), as little as clothing made for the source with a strap pressing in: the caller's word decides.
                float under = AuthoredUnder(asset, bindings, firstSnap);
                state.madeForAnother = settings.madeForAnotherBody || under > MadeForAnotherShare;
                if (state.madeForAnother)
                {
                    state.Report.Info("cloth-fit-covered", (settings.madeForAnotherBody ? "Made for another body and resized onto this one: " : "") +
                                                           $"{under * 100f:0} % of the clothing near the skin lay more than {MadeForAnotherDepth * 1000f:0} mm " +
                                                           "under the body it is fitted from" + (settings.madeForAnotherBody ? "; " : ": made for another body, ") +
                                                           "it is pushed out of the target after the fit.");
                    CoverBody(state, firstSnap, firstBvh, bvhTarget, primaryGroupDeltas, steps);
                }
                else
                    state.Report.Info("cloth-fit-covered", $"{under * 100f:0.0} % of the clothing near the skin lay more than {MadeForAnotherDepth * 1000f:0} mm " +
                                                           "under the body it is fitted from: made for it, the cloth fit keeps it as made.");
                CoverLayers(state, primaryGroupDeltas);
                steps.Lap("layers");
                state.Report.Info("deformation-timing", "Covering the target: " + steps);
            }
            state.primaryRawLocalDeltas = ToLocalDeltas(state, Declip(state, primaryGroupDeltas, bindings, firstSnap, firstBvh, bvhTarget), true);
            state.primaryLocalDeltas = state.primaryRawLocalDeltas;
            if (settings.recalculateNormalDeltas)
                (state.primaryNormalDeltas, state.primaryTangentDeltas) = SurfaceDeltas(asset, state.primaryLocalDeltas, null);
            transferBindings = BindRefittedAssetToTarget(state, bvhTarget, primaryGroupDeltas, targetBindings);
            if (state.shapes.Count > 0)
            {
                SetBackgroundProgress(state, 0.48f, "Preparing the body shapes");
                state.clothFit.PrepareShapes(primaryGroupDeltas, targetBasis, state.targetTriCrossMasks);
            }
            return new FitResult { primaryGroupDeltas = primaryGroupDeltas, transferBindings = transferBindings };
        }

        // The different-base passes: the bound displacement, smoothed, then clearance, tubes, coverage and drape.
        private static FitResult FitAcrossBases(State state, Correspondence c)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var bvhTarget = state.targetBvh;
            int groupCount = asset.GroupCount;
            var bindings = c.bindings;
            var targetBindings = c.targetBindings;
            var firstSnap = c.firstSnap;
            var firstBvh = c.firstBvh;
            var falloff = c.falloff;
            var clearanceProfile = c.clearanceProfile;
            var shownMotion = c.shownMotion;
            var laidOnto = c.laidOnto;
            var laidIndex = c.laidIndex;
            Vector3[] primaryGroupDeltas;
            var transferBindings = targetBindings;
            if (shownMotion != null)
            {
                // The passes made for bodies that cannot be laid onto each other fit at zero, as before: their clearance and
                // coverage only push the fabric out, so a shape taken away left the clothing as far out as it had pushed it
                // (the FBX fixture's sculpt: 28 mm from its authored result). The shapes are transferred adding them.
                targetBasis = state.targetBasis = laidOnto;
                bvhTarget = state.targetBvh = laidIndex;
                targetBindings = state.chainedTargetBindings = Chained(state, bindings, firstSnap, targetBasis, bvhTarget);
                ShownAtZero(state);
                shownMotion = null;
                state.Report.Info("followed-shapes-zero", "Fitted by the different-base passes: the shapes it follows are fitted at zero and added by its own shapes.");
            }
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
                    var turn = state.registration != null ? state.registration.Turn(bindings[g].triangle) : Quaternion.FromToRotation(nA, nB);
                    d = cpB + turn * (p - cpA) - p;
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

            var steps = new StepTimer();
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
            steps.Lap("clearance");
            AddClearanceStats(state, clearanceStats, "primary refit");
            state.tubes.Apply(asset, targetBasis, bvhTarget, null, null, primaryGroupDeltas, settings, state.Report, "primary refit");
            steps.Lap("tubes");

            if (settings.coverDifferentBaseBody && settings.madeForAnotherBody && !state.madeForAnother)
            {
                // The cloth fit (which decides it otherwise) did not run or fell back: the caller's word decides, so its
                // pieces follow the fabric the coverage moves and its sleeves and legs cover the limbs that came through.
                state.madeForAnother = true;
                state.Report.Info("made-for-another-covered", "Made for another body and resized onto this one: fitted by the different-base passes, " +
                                                       "its pieces carried with the fabric and its limbs covered.");
            }
            CoverBody(state, firstSnap, firstBvh, bvhTarget, primaryGroupDeltas, steps);
            state.Report.Info("deformation-timing", "The fit: " + steps);
            CoverLayers(state, primaryGroupDeltas);
            state.primaryLocalDeltas = ToLocalDeltas(state, Declip(state, primaryGroupDeltas, bindings, firstSnap, firstBvh, bvhTarget), true);
            if (settings.recalculateNormalDeltas)
                (state.primaryNormalDeltas, state.primaryTangentDeltas) = SurfaceDeltas(asset, state.primaryLocalDeltas, null);

            transferBindings = BindRefittedAssetToTarget(state, bvhTarget, primaryGroupDeltas, targetBindings);
            // Fitted at zero on the target without the shown shapes: the correspondence is the one onto it.
            c.targetBindings = targetBindings;
            c.shownMotion = shownMotion;
            return new FitResult { primaryGroupDeltas = primaryGroupDeltas, transferBindings = transferBindings };
        }

        // Shapes added to clothing already on the target: the earlier fit (as saved) is the primary motion, if any.
        private static FitResult FitShapesOnly(State state, Correspondence c)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var bvhTarget = state.targetBvh;
            var targetBindings = c.targetBindings;
            var metadataTransferBindings = c.metadataTransferBindings;
            Vector3[] primaryGroupDeltas = null;
            var transferBindings = targetBindings;
            if (state.targetSpaceBaseGroupDeltas != null)
            {
                primaryGroupDeltas = state.targetSpaceBaseGroupDeltas;
                transferBindings = metadataTransferBindings ?? targetBindings;
                // Shapes added to a fit draped over the target drape like the fit's own: the skin the fit covers stays covered.
                if (settings.coverDifferentBaseBody && settings.enableClearanceCorrection && state.shapes.Count > 0)
                    state.drape = new ReFitDrape(asset, primaryGroupDeltas, targetBasis, bvhTarget, targetBasis, state.targetTriRegions,
                        state.assetGroupRegions, state.tubes.groups, null, state.madeForAnother ? state.sourceLimbRegions : null);
            }
            return new FitResult { primaryGroupDeltas = primaryGroupDeltas, transferBindings = transferBindings };
        }

        // The generated metadata of a mesh refit, and the coverage the frames drape with.
        private static void FinishFit(State state, Correspondence c, FitResult fit)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var transferBindings = fit.transferBindings;
            var falloff = c.falloff;
            var clearanceProfile = c.clearanceProfile;
            var primaryGroupDeltas = fit.primaryGroupDeltas;
            if (state.wantMesh)
                state.comp.generatedMetadata = BuildGeneratedMetadata(
                    state,
                    targetBasis,
                    transferBindings,
                    falloff,
                    clearanceProfile,
                    primaryGroupDeltas);

            fit.coverage = state.shapes.Count > 0 && state.clothFit == null
                ? ReFitSurfaceCoverage.Build(asset, targetBasis, primaryGroupDeltas,
                    state.assetGroupRegions, state.tubes.groups, settings, state.targetTriRegions, hidden: state.hiddenGroups,
                    carryPieces: state.madeForAnother) : null;
            fit.layerCoverage = new List<ReFitSurfaceCoverage>();
            for (int layer = 0; layer < state.coverageLayers.Count; layer++)
                fit.layerCoverage.Add(ReFitSurfaceCoverage.Build(asset, state.coverageLayers[layer], primaryGroupDeltas,
                    state.assetGroupRegions, state.tubes.groups, settings, authored: state.coverageLayersAuthored[layer],
                    innerGarment: state.coverageLayerNames[layer], carryPieces: state.madeForAnother));
        }

        // What a frame worker reads of the fit (read only, shared by the frames).
        private sealed class FrameInputs
        {
            public ReFitSettings settings;
            public MeshSnapshot asset, targetBasis;
            public MeshBvh bvhTarget;
            public int groupCount, vertexCount, bodyVerts;
            public float[] falloff;
            public ReFitClearanceCorrection.Profile clearanceProfile;
            public Vector3[] primaryGroupDeltas;
            public SurfaceBinding[] transferBindings;
            public ReFitSurfaceCoverage coverage;
            public List<ReFitSurfaceCoverage> layerCoverage;
            public Vector3 garmentMin, garmentMax;
            public float frameReach;
            public WeldedNormalsCache normals;
            public Lazy<DetachedCoherence> detachedCoherence;
            // The clearance pass's context, from the transfer bindings: the same for every frame (read only).
            public ReFitClearanceCorrection.Context clearanceContext;
        }

        // Every frame of every requested shape, transferred on its own, at most shapeWorkers at once.
        private void TransferShapes(State state, Correspondence c, FitResult fit)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            var primaryGroupDeltas = fit.primaryGroupDeltas;
            // World-space delta of each target body vertex for a shape: M(v) applied to the frame delta.
            int bodyVerts = targetBasis.localVertices.Length;
            var work = state.shapes.SelectMany(shape => shape.frames.Select(frame => (shape, frame))).ToArray();
            var outputs = new ReFitComputation[work.Length];
            int completed = 0;
            // Where the clothing lies (as authored and as fitted), and how far from it any stage looks at the body.
            GarmentBounds(asset, primaryGroupDeltas, out var garmentMin, out var garmentMax);
            float frameReach = FrameReach(settings);
            var inputs = new FrameInputs
            {
                settings = settings, asset = asset, targetBasis = targetBasis, bvhTarget = state.targetBvh, groupCount = asset.GroupCount,
                vertexCount = asset.localVertices.Length, bodyVerts = bodyVerts, falloff = c.falloff, clearanceProfile = c.clearanceProfile,
                primaryGroupDeltas = primaryGroupDeltas, transferBindings = fit.transferBindings, coverage = fit.coverage,
                layerCoverage = fit.layerCoverage, garmentMin = garmentMin, garmentMax = garmentMax, frameReach = frameReach,
                normals = new WeldedNormalsCache(asset, state.primaryLocalDeltas),
                // Built by the first frame that needs it, then shared (thread safe).
                detachedCoherence = new Lazy<DetachedCoherence>(() => DetachedCoherence.Prepare(state, primaryGroupDeltas, asset.GroupCount)),
                clearanceContext = BuildClearanceContext(state, targetBasis, fit.transferBindings, true),
            };
            SetBackgroundProgress(state, 0.5f, $"Transferring blendshapes: 0/{work.Length}");
            // Frames own their arrays. Shared snapshots, BVHs and coverage supports are read-only.
            // Bound concurrent frames to avoid multiplying the temporary memory by the shape count.
            Parallel.For(0, work.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = shapeWorkers,
                CancellationToken = state.cancellationToken
            }, index =>
            {
                var shape = work[index].shape;
                var frame = work[index].frame;
                var worker = state.ForShape();
                outputs[index] = worker.comp;
                try { TransferFrame(worker, shape, frame, inputs); }
                finally
                {
                    lock (outputs)
                    {
                        completed++;
                        state.backgroundProgress = 0.5f + 0.35f * completed / work.Length;
                        state.backgroundLabel = $"Transferred blendshapes: {completed}/{work.Length} / {shape.sourceName}";
                    }
                }
            });
            // Merge diagnostics in input order, independent of worker completion order.
            foreach (var output in outputs)
            {
                state.Report.messages.AddRange(output.report.messages);
                if (output.clearanceCorrectionStats != null)
                {
                    if (state.comp.clearanceCorrectionStats == null)
                        state.comp.clearanceCorrectionStats = new ReFitClearanceCorrectionStats();
                    state.comp.clearanceCorrectionStats.Add(output.clearanceCorrectionStats);
                }
                if (output.transferredIslandPropagationDebug != null)
                    state.comp.transferredIslandPropagationDebug = output.transferredIslandPropagationDebug;
            }
            GiveBackShown(state);
            GiveBackShownToRest(state);
        }

        // One frame of a shape, on its worker's copy of the state: the clothing's motion for it, per frame.
        private static void TransferFrame(State worker, ShapeTask shape, ShapeFrame frame, FrameInputs inputs)
        {
            var settings = inputs.settings;
            var asset = inputs.asset;
            var targetBasis = inputs.targetBasis;
            var bvhTarget = inputs.bvhTarget;
            int groupCount = inputs.groupCount, vertexCount = inputs.vertexCount, bodyVerts = inputs.bodyVerts;
            var falloff = inputs.falloff;
            var clearanceProfile = inputs.clearanceProfile;
            var primaryGroupDeltas = inputs.primaryGroupDeltas;
            var transferBindings = inputs.transferBindings;
            var coverage = inputs.coverage;
            var layerCoverage = inputs.layerCoverage;
            var garmentMin = inputs.garmentMin;
            var garmentMax = inputs.garmentMax;
            float frameReach = inputs.frameReach;
            // A shape the target shows is taken away from the fit (the body moved by -shown of the frame): the clothing
            // frame is what that moves the clothing, reversed and per frame (see PerFrame).
            float motion = shape.shown > 0f ? -shape.shown : 1f;
            var worldShapeDelta = new Vector3[bodyVerts];
            // Ranges of body vertices per task, not a delegate call per vertex.
            Parallel.For(0, (bodyVerts + 4095) / 4096, range =>
            {
                for (int i = range * 4096, end = Math.Min(bodyVerts, (range + 1) * 4096); i < end; i++)
                    worldShapeDelta[i] = TargetShapeDeltaToWorld(worker, targetBasis, i, frame.deltas[i]) * motion;
            });
            if (!FrameReaches(targetBasis, worldShapeDelta, worker.coverageLayers, frame.layerDeltas, garmentMin, garmentMax, frameReach))
            {
                // A viseme under trousers: nothing the frame moves is within reach of any stage, so the clothing stays
                // as fitted (exactly: no fabric point takes a motion, and the passes are not asked to re-correct what the
                // fit left once per shape).
                frame.rawLocalDeltas = new Vector3[vertexCount];
                frame.localDeltas = frame.rawLocalDeltas;
                if (settings.recalculateNormalDeltas)
                {
                    frame.normalDeltas = new Vector3[vertexCount];
                    frame.tangentDeltas = asset.baseTangents != null ? new Vector3[vertexCount] : null;
                }
                frame.deltas = null;
                worker.Report.Info("frame-out-of-reach", $"'{shape.sourceName}' (frame at {frame.weight:0.#}) moves no skin within " +
                                                         $"{frameReach * 100f:0} cm of the clothing: it stays as fitted.");
                return;
            }
            if (worker.clothFit != null)
            {
                // Draped over the shaped body from the fitted clothing, like the fit.
                var draped = worker.clothFit.Shape(targetBasis, worldShapeDelta, falloff, worker.Report, shape.sourceName,
                    settings.transferredBlendshapeSmoothingIterations, settings.transferredBlendshapeSmoothingStrength);
                if (layerCoverage.Count > 0)
                {
                    // Over the garments worn under it as they take the shape too.
                    var drapedRaw = (Vector3[])draped.Clone();
                    for (int layer = 0; layer < layerCoverage.Count; layer++)
                        layerCoverage[layer]?.Apply(worker.coverageLayers[layer], frame.layerDeltas[layer], draped, drapedRaw, settings,
                            worker.Report, shape.sourceName);
                    ReFitSurfaceCoverage.KeepUnderCovers(asset, primaryGroupDeltas, draped, worker.hiddenCovers, worker.coverageLayers, frame.layerDeltas);
                }
                frame.rawLocalDeltas = PerFrame(ToLocalDeltas(worker, draped, false), motion);
                frame.localDeltas = frame.rawLocalDeltas;
                if (settings.recalculateNormalDeltas)
                    (frame.normalDeltas, frame.tangentDeltas) = SurfaceDeltas(asset, frame.localDeltas, FrameBase(worker.primaryLocalDeltas, frame.localDeltas, shape.shown), inputs.normals);
                frame.deltas = null;
                return;
            }

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
            // Hem restraint stabilizes source-to-target proportion fitting. An asset already
            // on the target must receive the complete authored shape displacement instead.
            if (!worker.sourceIsTarget)
                ApplyUpperBodyHemDamping(groupDeltas, worker.upperBodyHemWeights, settings);
            ApplyDetachedTransferredComponentCoherence(worker, inputs.detachedCoherence, groupDeltas, shape.sourceName);
            frame.rawLocalDeltas = PerFrame(ToLocalDeltas(worker, groupDeltas, false), motion);

            var cachedLocalDeltas = FindCachedTransferredLocalDeltas(
                worker.targetSpaceMetadata,
                shape.sourceName,
                vertexCount, frame.identity, worker.settingsIdentity);
            if (cachedLocalDeltas != null)
            {
                frame.localDeltas = cachedLocalDeltas;
                worker.Report.Info("target-space-cached-transferred-shape",
                    $"Reused the previously generated tightness-corrected transfer for '{shape.sourceName}'.");
                if (settings.recalculateNormalDeltas)
                    (frame.normalDeltas, frame.tangentDeltas) = SurfaceDeltas(asset, frame.localDeltas, FrameBase(worker.primaryLocalDeltas, frame.localDeltas, shape.shown), inputs.normals);
                frame.deltas = null;
                return;
            }

            var steps = new StepTimer();
            var coverageRawDeltas = coverage != null ? (Vector3[])groupDeltas.Clone() : null;
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
                inputs.clearanceContext);
            steps.Lap("clearance");
            AddClearanceStats(worker, clearanceStats, $"transferred '{shape.sourceName}'");
            worker.tubes.Apply(asset, targetBasis, bvhTarget, worldShapeDelta, primaryGroupDeltas, groupDeltas,
                settings, worker.Report, $"transferred '{shape.sourceName}'");
            steps.Lap("tubes");
            coverage?.Apply(targetBasis, worldShapeDelta, groupDeltas, coverageRawDeltas, settings, worker.Report, shape.sourceName);
            steps.Lap("coverage");
            // The drape knows only the body: the layers' coverage and the covers come after it and keep the layer order.
            worker.drape?.Apply(targetBasis, worldShapeDelta, primaryGroupDeltas, groupDeltas, worker.Report, shape.sourceName,
                worker.cancellationToken);
            steps.Lap("drape");
            for (int layer = 0; layer < layerCoverage.Count; layer++)
                layerCoverage[layer]?.Apply(worker.coverageLayers[layer], frame.layerDeltas[layer], groupDeltas,
                    coverageRawDeltas ?? (Vector3[])groupDeltas.Clone(), settings, worker.Report, shape.sourceName);
            ReFitSurfaceCoverage.KeepUnderCovers(asset, primaryGroupDeltas, groupDeltas, worker.hiddenCovers, worker.coverageLayers, frame.layerDeltas);
            steps.Lap("layers");
            worker.Report.Info("deformation-timing", $"'{shape.sourceName}': " + steps);

            frame.localDeltas = PerFrame(ToLocalDeltas(worker, groupDeltas, false), motion);
            if (settings.recalculateNormalDeltas)
                (frame.normalDeltas, frame.tangentDeltas) = SurfaceDeltas(asset, frame.localDeltas, FrameBase(worker.primaryLocalDeltas, frame.localDeltas, shape.shown), inputs.normals);
            frame.deltas = null; // free
        }

        // The clothing's skin weights from the bodies' (armature replaced).
        private static void TransferSkinWeights(State state, Correspondence c)
        {
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            int groupCount = asset.GroupCount;
            var bindings = c.bindings;
            var targetBindings = c.targetBindings;
            var firstSnap = c.firstSnap;
            var falloff = c.falloff;
            var shownMotion = c.shownMotion;
            var laidOnto = c.laidOnto;
            var laidIndex = c.laidIndex;
            var laid = c.laid;
            SetBackgroundProgress(state, 0.9f, "Transferring skin weights");
            // Skin weights are matched between the bodies alike: on the target without the shapes it shows (the same skin,
            // moved by them), as the target bindings were found on it.
            var weightBindings = targetBindings;
            if (shownMotion != null && state.transferWeights)
            {
                weightBindings = new SurfaceBinding[groupCount];
                var into = weightBindings;
                var used = state.registration != null ? laid : null;
                var chainedBare = Chained(state, bindings, firstSnap, laidOnto, laidIndex);
                Parallel.For(0, groupCount, g =>
                {
                    if (!bindings[g].valid || !targetBindings[g].valid) { into[g] = targetBindings[g]; return; }
                    var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                    if (used != null && RegisteredPoint(used, bindings[g], laidOnto, laidIndex, state.targetTriRegions, region, out var registered))
                    {
                        into[g] = registered;
                        return;
                    }
                    into[g] = chainedBare[g].valid ? chainedBare[g] : targetBindings[g];
                });
            }
            // The source match and its chained target match are the same anatomical point on both bodies.
            state.newWeights = state.transferWeights
                ? WeightTransfer.Transfer(asset, state.sourceBody, bindings, state.sourceBoneToNew,
                    targetBasis, weightBindings, state.bodyBoneToNew, falloff,
                    state.assetBoneToNew, state.assetBoneIsExtra, state.newBoneRegions, state.assetGroupRegions,
                    settings, state.Report, out state.weightDebug)
                : RemapAllOriginal(asset, state.assetBoneToNew);
        }

        // How far from the clothing a stage of a shape frame can look at the body, with room to spare. The farthest is the
        // drape: from skin within its 0.2 m reach of the fabric, a way into the body twice as long (0.6 m from the fabric);
        // then the bindings (twice maxProjectionDistance, 0.5 m by default, through which a frame moves each group) and the
        // clearance guard's range. The cloth fit's motion samples (0.15 m), the coverage (0.2 m) and the tubes
        // (maxProjectionDistance) reach less. Smoothing spreads only motion some group took directly, so it adds nothing.
        private const float FrameReachLeast = .6f;

        private static float FrameReach(ReFitSettings settings) => Mathf.Max(FrameReachLeast,
            Mathf.Max(settings.maxProjectionDistance * 2f, settings.clearanceMaxInwardCorrection + settings.clearanceMaxOutwardCorrection +
                                                            settings.clearanceMinimumSafetyDistance + .02f)) + .05f;

        // The bounds of the clothing's welded groups as authored and as fitted.
        private static void GarmentBounds(MeshSnapshot asset, Vector3[] fit, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int g = 0; g < asset.GroupCount; g++)
            {
                var p = asset.worldVertices[asset.groupRep[g]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                if (fit == null) continue;
                min = Vector3.Min(min, p + fit[g]);
                max = Vector3.Max(max, p + fit[g]);
            }
        }

        /// <summary>
        /// Whether a frame moves anything within <paramref name="reach"/> of the clothing's bounds: a triangle of the body with a
        /// moved corner (all three corners, where they rest and where the frame takes them: a point inside it moves too), or
        /// such a triangle of a garment worn under it by that garment's own version of the frame.
        /// </summary>
        internal static bool FrameReaches(MeshSnapshot body, Vector3[] bodyMotion, IReadOnlyList<MeshSnapshot> layers,
            IReadOnlyList<Vector3[]> layerMotion, Vector3 garmentMin, Vector3 garmentMax, float reach)
        {
            var low = garmentMin - new Vector3(reach, reach, reach);
            var high = garmentMax + new Vector3(reach, reach, reach);
            if (Reaches(body.worldVertices, body.triangles, bodyMotion, low, high)) return true;
            if (layers != null && layerMotion != null)
                for (int layer = 0; layer < layers.Count && layer < layerMotion.Count; layer++)
                    if (Reaches(layers[layer].worldVertices, layers[layer].triangles, layerMotion[layer], low, high)) return true;
            return false;
        }

        private static bool Reaches(Vector3[] vertices, int[] triangles, Vector3[] motion, Vector3 low, Vector3 high)
        {
            if (motion == null || triangles == null) return false;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (!Moves(motion[a]) && !Moves(motion[b]) && !Moves(motion[c])) continue;
                var min = Vector3.Min(Vector3.Min(vertices[a], vertices[b]), vertices[c]);
                var max = Vector3.Max(Vector3.Max(vertices[a], vertices[b]), vertices[c]);
                min = Vector3.Min(min, Vector3.Min(Vector3.Min(vertices[a] + motion[a], vertices[b] + motion[b]), vertices[c] + motion[c]));
                max = Vector3.Max(max, Vector3.Max(Vector3.Max(vertices[a] + motion[a], vertices[b] + motion[b]), vertices[c] + motion[c]));
                if (min.x <= high.x && min.y <= high.y && min.z <= high.z && max.x >= low.x && max.y >= low.y && max.z >= low.z) return true;
            }
            return false;

            bool Moves(Vector3 d) => d.x != 0f || d.y != 0f || d.z != 0f;
        }

        // Clothing points close to the source skin (within ClothingNearSkin) whose laid counterpart lies farther than
        // RegistrationDisagreement from the skin met along the source's normal; above registrationDisagreementLimit of them the
        // registration is not used.
        // Where the time of a deformation goes, step by step (diagnostics).
        // Clothing fitted across bases covers the target: pushed out of its body where it lies under it, then draped over the
        // skin still showing through. The garments worn under it come after (CoverLayers): the drape knows only the body.
        /// <summary>Debug: the clothing's group deltas after each step covering the target, on that target (tests only).</summary>
        internal static Action<string, MeshSnapshot, MeshSnapshot, Vector3[]> CoverStepCaptured;

        private static void CoverBody(State state, MeshSnapshot firstSnap, MeshBvh firstBvh, MeshBvh bvhTarget, Vector3[] primaryGroupDeltas,
            StepTimer steps)
        {
            CoverStepCaptured?.Invoke("before", state.asset, state.targetBasis, primaryGroupDeltas);
            var settings = state.settings;
            var asset = state.asset;
            var targetBasis = state.targetBasis;
            if (settings.coverDifferentBaseBody && settings.coverageKeepsLayerOrder)
            {
                state.hiddenCovers = new ReFitSurfaceCoverage.Cover?[asset.GroupCount];
                state.hiddenGroups = ReFitSurfaceCoverage.HiddenGroups(asset, targetBasis, bvhTarget, AuthoredLayers(state), null, state.hiddenCovers);
                steps.Lap("hidden layers");
            }
            ReFitSurfaceCoverage.RepairPrimary(asset, targetBasis, primaryGroupDeltas,
                state.assetGroupRegions, state.tubes.groups, settings, state.Report, state.targetTriRegions, hidden: state.hiddenGroups,
                carryPieces: state.madeForAnother, fitted: state.clothFit != null, legs: state.targetLegShares);
            steps.Lap("coverage");
            CoverStepCaptured?.Invoke("coverage", asset, targetBasis, primaryGroupDeltas);
            if (!settings.coverDifferentBaseBody || !settings.enableClearanceCorrection) return;
            SetBackgroundProgress(state, 0.42f, "Draping the clothing over the body");
            state.drape = new ReFitDrape(asset, null, firstSnap, firstBvh, targetBasis, state.targetTriRegions, state.assetGroupRegions, state.tubes.groups,
                state.hiddenGroups, state.madeForAnother ? state.sourceLimbRegions : null);
            steps.Lap("drape setup");
            state.drape.Apply(targetBasis, null, null, primaryGroupDeltas, state.Report, "different-base coverage", state.cancellationToken,
                t => state.backgroundProgress = 0.42f + 0.06f * t);
            steps.Lap("drape");
            CoverStepCaptured?.Invoke("drape", asset, targetBasis, primaryGroupDeltas);
        }

        // Over the garments worn under the clothing, in their authored order: the last word on the layers.
        private static void CoverLayers(State state, Vector3[] primaryGroupDeltas)
        {
            for (int layer = 0; layer < state.coverageLayers.Count; layer++)
                ReFitSurfaceCoverage.RepairPrimary(state.asset, state.coverageLayers[layer], primaryGroupDeltas,
                    state.assetGroupRegions, state.tubes.groups, state.settings, state.Report, null, state.coverageLayersAuthored[layer],
                    innerGarment: state.coverageLayerNames[layer], carryPieces: state.madeForAnother,
                    fitted: state.clothFit != null);
            if (state.coverageLayers.Count > 0) CoverStepCaptured?.Invoke("layers", state.asset, state.targetBasis, primaryGroupDeltas);
        }

        // "Fix clipping" (settings.declipAfterFit): what the fit left inside the target's body, or closer to it than the room asked,
        // is pushed out along the body's distance field and smoothed over the fabric (ReFitDeclip). Clothing made for the body it
        // is fitted from keeps what lay under that body as made (a strap pressing in): only what the fit added is fixed. The fixed
        // deltas are the rest mesh's only: the shapes, their coverage and the saved binding keep the fit as it was, so each shape
        // moves the clothing as without the fix, from a rest that only moved out (draped from the fixed fit instead, the latex
        // corset clipped more under its shapes: 219 -> 293). Returns the fixed copy, or the deltas themselves when off.
        private static Vector3[] Declip(State state, Vector3[] primaryGroupDeltas, SurfaceBinding[] bindings, MeshSnapshot madeOn, MeshBvh madeOnIndex,
            MeshBvh bvhTarget)
        {
            var settings = state.settings;
            if (!settings.declipAfterFit || primaryGroupDeltas == null) return primaryGroupDeltas;
            primaryGroupDeltas = (Vector3[])primaryGroupDeltas.Clone();
            SetBackgroundProgress(state, 0.48f, "Fixing clipping");
            float room = Mathf.Max(0f, settings.declipRoomMm) * 0.001f;
            bool keepAuthored = !state.madeForAnother && !settings.madeForAnotherBody;
            var rooms = ReFitDeclip.Rooms(state.asset, bindings, madeOn, room, keepAuthored);
            var centreRooms = ReFitDeclip.CentreRooms(state.asset, madeOn, madeOnIndex, room, keepAuthored);
            var result = ReFitDeclip.Apply(state.asset, primaryGroupDeltas, state.targetBasis, bvhTarget, rooms, centreRooms,
                ReFitDeclip.DefaultIterations, state.cancellationToken);
            state.Report.Info("declip", result.ToString());
            CoverStepCaptured?.Invoke("declip", state.asset, state.targetBasis, primaryGroupDeltas);
            return primaryGroupDeltas;
        }

        // Clothing lying under the skin it is fitted from by more than this, over more than this share of it near the skin, was
        // made for another body (see AuthoredUnder).
        private const float MadeForAnotherDepth = 0.005f, MadeForAnotherShare = 0.1f;

        /// <summary>
        /// The share of the clothing within <see cref="ClothingNearSkin"/> of the skin it is fitted from that lay more than
        /// <see cref="MadeForAnotherDepth"/> under it as authored: next to none for clothing made for that body (a strap pressing
        /// in, a collar under fluff), much of it for clothing made for another body and resized onto this one.
        /// </summary>
        private static float AuthoredUnder(MeshSnapshot asset, SurfaceBinding[] bindings, MeshSnapshot body)
        {
            int near = 0, under = 0;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                if (!bindings[g].valid) continue;
                var p = asset.worldVertices[asset.groupRep[g]];
                float gap = Vector3.Dot(p - bindings[g].point, body.BaryNormal(bindings[g].triangle, bindings[g].bary));
                if (Mathf.Abs(gap) > ClothingNearSkin) continue;
                near++;
                if (gap < -MadeForAnotherDepth) under++;
            }
            return near > 0 ? (float)under / near : 0f;
        }

        private sealed class StepTimer
        {
            private readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            private readonly List<string> laps = new List<string>();

            public void Lap(string step)
            {
                laps.Add($"{step} {watch.ElapsedMilliseconds} ms");
                watch.Restart();
            }

            public override string ToString() => string.Join(", ", laps);
        }

        private const float ClothingNearSkin = 0.1f, RegistrationDisagreement = 0.03f;
        // The laid source within this of the target skin under 90 % of the clothing reaches it there (measured up to the limit).
        private const float RegistrationReach = 0.01f, RegistrationReachLimit = 0.2f;

        // The skeletons' carry is used when it brings the source this much closer to the target on average.
        private const float RetargetGain = 0.002f;

        // Mean distance of (a sample of) points to a surface, up to 30 cm each.
        private static float MeanDistance(Vector3[] points, MeshBvh surface)
        {
            int step = Mathf.Max(1, points.Length / 4000), count = 0;
            double total = 0;
            for (int i = 0; i < points.Length; i += step)
            {
                var hit = surface.ClosestPoint(points[i], .3f);
                total += hit.Found ? hit.Distance : .3f;
                count++;
            }
            return count > 0 ? (float)(total / count) : 0f;
        }

        // Two bodies whose vertices lie within this of each other (same topology) are the same body.
        private const float SameSurfaceDistance = 0.0005f;

        private static bool SameSurface(MeshSnapshot a, MeshSnapshot b)
        {
            if (a == null || b == null || a.worldVertices.Length != b.worldVertices.Length || a.triangles.Length != b.triangles.Length) return false;
            for (int i = 0; i < a.worldVertices.Length; i++)
                if ((a.worldVertices[i] - b.worldVertices[i]).sqrMagnitude > SameSurfaceDistance * SameSurfaceDistance) return false;
            return true;
        }

        // How far the registered point may lie from the target skin and still be taken (then pressed onto it along the skin's
        // normal, so it keeps its place along the skin).
        private const float RegisteredReach = 0.03f;

        /// <summary>
        /// The target counterpart of a source binding through the registration: the registered point, pressed onto the target
        /// skin nearest to it along that skin's normal. False when no target skin is near (the caller falls back to chaining).
        /// </summary>
        private static bool RegisteredPoint(ReFitBodyRegistration registration, SurfaceBinding source, MeshSnapshot target, MeshBvh targetIndex,
            BodyRegion[] targetRegions, BodyRegion region, out SurfaceBinding binding)
        {
            binding = default;
            var point = registration.Point(source.triangle, source.bary);
            var hit = targetIndex.ClosestPoint(point, RegisteredReach);
            if (!hit.Found) return false;
            var normal = target.FaceNormal(hit.TriangleOrNone);
            binding = new SurfaceBinding
            {
                valid = true,
                triangle = hit.TriangleOrNone,
                bary = hit.Bary,
                point = point + normal * Vector3.Dot(hit.Position - point, normal),
                distance = hit.Distance,
                requestedRegion = region,
                hitRegion = targetRegions != null && hit.TriangleOrNone < targetRegions.Length ? targetRegions[hit.TriangleOrNone] : BodyRegion.Unknown,
                normalDot = Vector3.Dot(registration.Turn(source.triangle) * registration.source.FaceNormal(source.triangle), normal)
            };
            return true;
        }

        // What may cover the asset: refitted layers where they are now (outside the body only), the others as authored (a
        // refit only moves them outward, so what they cover stays covered).
        private static List<(MeshSnapshot surface, bool refitted)> AuthoredLayers(State state)
        {
            var layers = new List<(MeshSnapshot, bool)>();
            for (int i = 0; i < state.coverageLayers.Count; i++)
                layers.Add((state.coverageLayers[i], state.coverageLayersRefitted[i]));
            return layers;
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
                measureFinalPenetration = state.settings.captureProjectionDebug,
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

        /// <summary>
        /// What the detached-component coherence derives from the clothing and its fit alone, the same for every frame of a
        /// transfer: the components, their groups, reference positions and bounds, the donor surface and the clusters.
        /// Read only once built.
        /// </summary>
        private sealed class DetachedCoherence
        {
            public int groupCount;
            public int[] componentByGroup, componentSizes;
            public List<int>[] componentGroups;
            public Vector3[] reference;
            public DetachedSurfaceSupport surfaceSupport;
            public List<List<int>> clusters;

            /// <summary>The setup for clothing of <paramref name="groupCount"/> groups fitted by <paramref name="primaryGroupDeltas"/>, or null when nothing can be stabilized.</summary>
            public static DetachedCoherence Prepare(State state, Vector3[] primaryGroupDeltas, int groupCount)
            {
                var asset = state.asset;
                var settings = state.settings;
                var componentByGroup = BuildDetachedComponentMap(asset, groupCount, out var componentSizes, out int largestComponentSize);
                if (componentByGroup == null || componentSizes == null || componentSizes.Length <= 1 || largestComponentSize <= 0)
                    return null;

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
                                                    !state.tubes.groups[componentGroups[component][0]] &&
                                                    size < largestComponentSize &&
                                                    size <= receiverSizeLimit &&
                                                    componentBounds[component].valid;
                }

                var clusters = BuildDetachedComponentClusters(
                    eligibleComponents,
                    componentBounds,
                    Mathf.Min(DetachedCoherenceMaxClusterDistance, Mathf.Max(0.025f, settings.maxProjectionDistance * 0.2f)));
                return new DetachedCoherence
                {
                    groupCount = groupCount,
                    componentByGroup = componentByGroup,
                    componentSizes = componentSizes,
                    componentGroups = componentGroups,
                    reference = reference,
                    surfaceSupport = surfaceSupport,
                    clusters = clusters,
                };
            }
        }

        /// <param name="setup">The transfer's frame-invariant part (<see cref="DetachedCoherence.Prepare"/>), shared by its frames.</param>
        private static void ApplyDetachedTransferredComponentCoherence(
            State state,
            Lazy<DetachedCoherence> setup,
            Vector3[] transferredGroupDeltas,
            string shapeName)
        {
            var asset = state.asset;
            var settings = state.settings;
            if (asset == null || settings == null || !settings.stabilizeDetachedTransferredComponents ||
                transferredGroupDeltas == null || asset.groupAdjacency == null || asset.groupRep == null ||
                asset.worldVertices == null || asset.GroupCount <= 0)
                return;

            var prepared = setup.Value;
            if (prepared == null || prepared.groupCount != Mathf.Min(asset.GroupCount, transferredGroupDeltas.Length))
                return;
            var componentByGroup = prepared.componentByGroup;
            var componentSizes = prepared.componentSizes;
            var componentGroups = prepared.componentGroups;
            var reference = prepared.reference;
            var surfaceSupport = prepared.surfaceSupport;
            var clusters = prepared.clusters;

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
            var components = donorComponents.ToArray();
            var ranks = new int[components.Length];
            for (int t = 0; t < ranks.Length; t++)
                ranks[t] = components[t] >= 0 && components[t] < componentSizes.Length ? componentSizes[components[t]] : int.MinValue;
            var surface = new MeshSnapshot
            {
                worldVertices = reference,
                worldNormals = ComputeDetachedWorldNormals(reference, triangles),
                triangles = triangles
            };

            return new DetachedSurfaceSupport
            {
                surface = surface,
                bvh = MeshSnapshot.BuildIndex(surface),
                triangleComponents = components,
                triangleRanks = ranks,
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

            // The components a receiver may follow: any but the cluster's own (a component outside the table: none).
            var outsideCluster = new bool[componentSizes.Length];
            for (int c = 0; c < outsideCluster.Length; c++) outsideCluster[c] = true;
            foreach (int c in clusterComponents)
                if (c >= 0 && c < outsideCluster.Length) outsideCluster[c] = false;
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
                // Donor triangles of a component outside the cluster, no smaller than the receiver's, of a compatible region.
                var rule = TriangleRule.All.Labelled(support.triangleComponents, outsideCluster).Ranked(support.triangleRanks, receiverSize);
                if (support.triangleRegions != null) rule = rule.CompatibleRegion(support.triangleRegions, receiverRegion, second: true);
                var hit = support.bvh.ClosestPoint(reference[g], searchDistance, rule);

                if (!hit.Found || !IsStableDetachedSurfaceSupport(reference[g], support.surface, hit))
                    continue;

                int tri = hit.TriangleOrNone * 3;
                if (tri + 2 >= support.surface.triangles.Length)
                    continue;

                int ga = support.surface.triangles[tri];
                int gb = support.surface.triangles[tri + 1];
                int gc = support.surface.triangles[tri + 2];
                if (ga < 0 || gb < 0 || gc < 0 ||
                    ga >= currentDeltas.Length || gb >= currentDeltas.Length || gc >= currentDeltas.Length)
                    continue;

                var surfaceDelta = currentDeltas[ga] * hit.Bary.x +
                                   currentDeltas[gb] * hit.Bary.y +
                                   currentDeltas[gc] * hit.Bary.z;
                float follow = DetachedCoherenceSurfaceFollowStrength *
                               Mathf.Lerp(1f, 0.95f, Smooth01(0f, searchDistance, hit.Distance));
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

        private static bool IsStableDetachedSurfaceSupport(Vector3 receiverPoint, MeshSnapshot donorSurface, MeshBvh.Hit hit)
        {
            if (donorSurface == null || !hit.Found)
                return false;

            var supportNormal = donorSurface.BaryNormal(hit.TriangleOrNone, hit.Bary);
            if (supportNormal.sqrMagnitude <= 1e-12f)
                return false;

            float side = Vector3.Dot(receiverPoint - hit.Position, supportNormal);
            float backfaceTolerance = Mathf.Max(0.005f, hit.Distance * 0.25f);
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
            MeshBvh bvhTarget,
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
            bool lowerBodyCloth = settings.preserveLowerBodyCoverage &&
                ReFitSurfaceCoverage.IsLowerBodyCloth(asset, state.assetGroupRegions, state.tubes.groups);

            Parallel.For(0, groupCount, g =>
            {
                int rep = asset.groupRep[g];
                var delta = primaryGroupDeltas != null && g < primaryGroupDeltas.Length ? primaryGroupDeltas[g] : Vector3.zero;
                var queryPoint = asset.worldVertices[rep] + delta;
                var region = state.assetGroupRegions != null ? state.assetGroupRegions[g] : BodyRegion.Unknown;
                bool useNormal = settings.filterByNormal && !lowerBodyCloth && !state.tubes.groups[g];
                var binding = SurfaceBindingSolver.BindPoint(
                    queryPoint, targetBasis, bvhTarget, range,
                    region, settings.filterByBoneRegion ? state.targetTriRegions : null,
                    asset.worldNormals[rep], cosMax, useNormal, true, state.targetTriCrossMasks);

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
            bool lowerBodyCloth = state.settings.preserveLowerBodyCoverage &&
                ReFitSurfaceCoverage.IsLowerBodyCloth(asset, state.assetGroupRegions, state.tubes.groups);

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
                    chainedTargetValid = state.chainedTargetBindings != null && g < state.chainedTargetBindings.Length && state.chainedTargetBindings[g].valid,
                    chainedTargetLocalPoint = state.chainedTargetBindings != null && g < state.chainedTargetBindings.Length && state.chainedTargetBindings[g].valid
                        ? asset.rendererWorldToLocal.MultiplyPoint3x4(state.chainedTargetBindings[g].point)
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
                if (state.tubes.groups[g])
                    point.note = "closed tube: nearest surface with configured region policy; tube face normal is not a skin-facing constraint; centerline field" +
                                 (string.IsNullOrEmpty(point.note) ? "" : "; " + point.note);
                else if (lowerBodyCloth)
                    point.note = "lower-body cloth: nearest surface with configured region policy; lining and hem normals do not reject the nearby leg" +
                                 (string.IsNullOrEmpty(point.note) ? "" : "; " + point.note);

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

        // The deltas of a transfer that moved the body by motion times a frame, per frame (a shape taken away: reversed).
        private static Vector3[] PerFrame(Vector3[] deltas, float motion)
        {
            if (motion == 1f) return deltas;
            for (int i = 0; i < deltas.Length; i++) deltas[i] /= motion;
            return deltas;
        }

        // The clothing a frame's normals turn from: the fit (or the clothing given shapes alone), without what a shape the
        // target shows adds to it.
        private static Vector3[] FrameBase(Vector3[] primary, Vector3[] frame, float shown)
        {
            if (shown <= 0f) return primary;
            var result = new Vector3[frame.Length];
            for (int i = 0; i < result.Length; i++) result[i] = (primary != null ? primary[i] : Vector3.zero) - frame[i] * shown;
            return result;
        }

        /// <summary>
        /// The fit holds the followed shapes the target shows: the refit shape gives back what their clothing shapes show of
        /// them, so the refit and the shapes at the body's weights are the fit again (and the refit alone, the clothing without them).
        /// </summary>
        private static void GiveBackShown(State state)
        {
            if (state.primaryLocalDeltas == null || !state.shapes.Any(shape => shape.shown > 0f)) return;
            var primary = (Vector3[])state.primaryLocalDeltas.Clone();
            var raw = (Vector3[])state.primaryRawLocalDeltas.Clone();
            foreach (var shape in state.shapes)
            {
                if (shape.shown <= 0f) continue;
                var frame = shape.frames[0];
                for (int i = 0; i < primary.Length; i++)
                {
                    primary[i] -= frame.localDeltas[i] * shape.shown;
                    raw[i] -= frame.rawLocalDeltas[i] * shape.shown;
                }
            }
            state.primaryLocalDeltas = primary;
            state.primaryRawLocalDeltas = raw;
            if (state.settings.recalculateNormalDeltas)
                (state.primaryNormalDeltas, state.primaryTangentDeltas) = SurfaceDeltas(state.asset, primary, null);
        }

        /// <summary>
        /// Clothing given shapes alone, made with followed shapes the target shows (or the request says): with no refit shape to
        /// give them back, its rest does. Each such shape's clothing frame takes it away, so the rest becomes the clothing
        /// without them and, at the weights it was made with, the clothing is as authored.
        /// </summary>
        private static void GiveBackShownToRest(State state)
        {
            if (state.wantMesh || !state.shapes.Any(shape => shape.shown > 0f)) return;
            var rest = new Vector3[state.asset.localVertices.Length];
            foreach (var shape in state.shapes)
            {
                if (shape.shown <= 0f) continue;
                var frame = shape.frames[0];
                for (int i = 0; i < rest.Length; i++) rest[i] -= frame.localDeltas[i] * shape.shown;
            }
            state.restLocalDeltas = rest;
            (state.restNormalDeltas, state.restTangentDeltas) = SurfaceDeltas(state.asset, rest, null);
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
            var inverseSkin = rendererLocalDeltas ? null : asset.InverseSkinMatrices();
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
                    result[i] = inverseSkin[i].MultiplyVector(dWorld);
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
                newMesh.AddBlendShapeFrame(comp.primaryShapeName, 100f, state.primaryLocalDeltas, state.primaryNormalDeltas, state.primaryTangentDeltas);
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
                var transferredShapes = new List<ReFitGeneratedTransferredShapeMetadata>();
                for (int s = 0; s < state.shapes.Count; s++)
                {
                    var shape = state.shapes[s];
                    string desired = settings.prefixTransferredShapes
                        ? $"{settings.blendshapeName}_{shape.sourceName}"
                        : shape.sourceName;
                    var name = UniqueShapeName(newMesh, desired);
                    // Same frame weights as the body shape: the mirrored slider value then interpolates identically.
                    foreach (var frame in shape.frames)
                    {
                        newMesh.AddBlendShapeFrame(name, frame.weight, frame.localDeltas, frame.normalDeltas, frame.tangentDeltas);
                        transferredShapes.Add(new ReFitGeneratedTransferredShapeMetadata
                        {
                            frameIdentity = frame.identity,
                            generatedName = name,
                            sourceName = shape.sourceName,
                            localDeltas = frame.localDeltas != null
                                ? (Vector3[])frame.localDeltas.Clone()
                                : null
                        });
                    }
                    comp.secondarySourceShapeNames[s] = shape.sourceName;
                    comp.secondaryShapeNames[s] = name;
                    comp.secondaryMirrorWeights[s] = shape.mirrorWeight;
                    comp.debugSecondaryRawLocalDeltas[s] = shape.frames[shape.frames.Count - 1].rawLocalDeltas;
                }
                if (comp.generatedMetadata != null)
                    comp.generatedMetadata.transferredShapes = transferredShapes.ToArray();
            }

            if (state.replace)
            {
                newMesh.boneWeights = state.newWeights;
                newMesh.bindposes = state.newBindposes;
            }
            if (state.restLocalDeltas != null) MoveRest(newMesh, state);
            ReportReducedInfluences(state.request?.assetRenderer != null ? state.request.assetRenderer.sharedMesh : null, newMesh, comp.report);

            comp.mesh = newMesh;
            comp.success = !comp.report.HasErrors;
        }

        /// <summary>
        /// ReFit skins with the four largest influences of each vertex and writes four when it writes skin weights (a
        /// replaced armature, or a staged pose baked into the rest): says so when the clothing had more.
        /// </summary>
        internal static void ReportReducedInfluences(Mesh original, Mesh refitted, ReFitReport report)
        {
            if (original == null || refitted == null || report == null) return;
            int before = MeshSnapshot.MaxBonesPerVertex(original);
            if (before <= 4) return;
            int after = MeshSnapshot.MaxBonesPerVertex(refitted);
            if (after >= before) return;
            report.Warn("skin-influences-reduced",
                $"'{original.name}' has up to {before} bone influences per vertex; the refitted mesh keeps the {after} largest of each " +
                "(as Unity and VRChat render with four by default).");
        }

        // The clothing's rest moved by GiveBackShownToRest: its positions, and its normals and tangents turned with them.
        private static void MoveRest(Mesh mesh, State state)
        {
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] += state.restLocalDeltas[i];
            mesh.vertices = vertices;
            var normals = mesh.normals;
            if (normals.Length == vertices.Length)
            {
                for (int i = 0; i < normals.Length; i++) normals[i] += state.restNormalDeltas[i];
                mesh.normals = normals;
            }
            var tangents = mesh.tangents;
            if (state.restTangentDeltas != null && tangents.Length == vertices.Length)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    var t = state.restTangentDeltas[i];
                    tangents[i] = new Vector4(tangents[i].x + t.x, tangents[i].y + t.y, tangents[i].z + t.z, tangents[i].w);
                }
                mesh.tangents = tangents;
            }
            mesh.RecalculateBounds();
        }

        // ------------------------------------------------------------------
        // Regions
        // ------------------------------------------------------------------

        /// <summary>
        /// How much of each body vertex's skinning is the legs' (0 the torso, arms and feet, 1 the thighs and shins, in between
        /// across the hips and ankles), from its weights. The feet and the bones under them are not the legs: a shoe's sole or a
        /// sock keeps no leg room over them (the foot floated in the shoe, the sole sank below the floor). Null for a body
        /// without skin weights.
        /// </summary>
        internal static float[] LegShares(MeshSnapshot body, Dictionary<Transform, BodyRegion> boneRegions, Dictionary<HumanBodyBones, Transform> humanMap)
        {
            var weights = body.boneWeights;
            if (weights == null || weights.Length != body.worldVertices.Length || body.bones == null) return null;
            var feet = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }
                .Select(bone => humanMap != null && humanMap.TryGetValue(bone, out var t) ? t : null).Where(t => t != null).ToArray();
            var leg = new bool[body.bones.Length];
            for (int k = 0; k < leg.Length; k++)
                leg[k] = body.bones[k] != null && boneRegions.TryGetValue(body.bones[k], out var r) && (r == BodyRegion.LeftLeg || r == BodyRegion.RightLeg) &&
                         !feet.Any(body.bones[k].IsChildOf);
            float Leg(int bone, float weight) => bone >= 0 && bone < leg.Length && leg[bone] ? weight : 0f;
            var shares = new float[weights.Length];
            for (int v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                shares[v] = Mathf.Clamp01(Leg(w.boneIndex0, w.weight0) + Leg(w.boneIndex1, w.weight1) + Leg(w.boneIndex2, w.weight2) + Leg(w.boneIndex3, w.weight3));
            }
            return shares;
        }

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

        // A region holding this share of a triangle's skinning makes the triangle part of it (a transition band).
        private const float TriangleRegionShare = 0.25f;

        /// <summary>Every region holding at least <see cref="TriangleRegionShare"/> of each triangle's skin weight.</summary>
        private static int[] TriangleRegionMasks(MeshSnapshot body, Dictionary<Transform, BodyRegion> boneRegions)
        {
            int triCount = body.triangles.Length / 3;
            var masks = new int[triCount];
            if (body.rigid || body.boneWeights == null || body.boneWeights.Length != body.localVertices.Length) return masks;
            var perBone = new BodyRegion[body.bones != null ? body.bones.Length : 0];
            for (int k = 0; k < perBone.Length; k++)
                perBone[k] = body.bones[k] != null && boneRegions.TryGetValue(body.bones[k], out var r) ? r : BodyRegion.Unknown;
            Parallel.For(0, triCount, t =>
            {
                var shares = new float[6];
                float total = 0f;
                for (int corner = 0; corner < 3; corner++)
                {
                    var w = body.boneWeights[body.triangles[t * 3 + corner]];
                    AddRegionShare(shares, ref total, perBone, w.boneIndex0, w.weight0);
                    AddRegionShare(shares, ref total, perBone, w.boneIndex1, w.weight1);
                    AddRegionShare(shares, ref total, perBone, w.boneIndex2, w.weight2);
                    AddRegionShare(shares, ref total, perBone, w.boneIndex3, w.weight3);
                }
                int mask = 0;
                if (total > 1e-6f)
                    for (int region = 0; region < shares.Length; region++)
                        if (shares[region] >= TriangleRegionShare * total) mask |= SurfaceBindingSolver.RegionMask((BodyRegion)region);
                masks[t] = mask;
            });
            return masks;
        }

        /// <summary>
        /// Region masks for matching across bodies: a triangle the torso holds also counts for every limb. Two bases can
        /// split the same skin differently (a buttock weighted to the hips on one and to the thighs on the other), so a
        /// limb's clothing may land on torso skin of the other body, never on another limb.
        /// </summary>
        private static int[] LimbsAcceptTorso(int[] masks)
        {
            int torso = SurfaceBindingSolver.RegionMask(BodyRegion.Torso);
            int limbs = SurfaceBindingSolver.RegionMask(BodyRegion.LeftArm) | SurfaceBindingSolver.RegionMask(BodyRegion.RightArm) |
                        SurfaceBindingSolver.RegionMask(BodyRegion.LeftLeg) | SurfaceBindingSolver.RegionMask(BodyRegion.RightLeg);
            var result = new int[masks.Length];
            for (int t = 0; t < masks.Length; t++) result[t] = (masks[t] & torso) != 0 ? masks[t] | limbs : masks[t];
            return result;
        }

        private static void AddRegionShare(float[] shares, ref float total, BodyRegion[] perBone, int index, float weight)
        {
            if (weight <= 0f || index < 0 || index >= perBone.Length || perBone[index] == BodyRegion.Unknown) return;
            shares[(int)perBone[index]] += weight;
            total += weight;
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
        /// Per-vertex normal and tangent deltas so the shape lights as authored at full weight: each authored normal (and
        /// tangent) turns as the surface around it turns, from the welded smooth normal before the frame to the one after it.
        /// Hard edges and custom normals keep their look, and a vertex whose surface does not turn gets no delta (the
        /// shapes stay sparse). <paramref name="baseDeltas"/> is what lies under the frame already (the refit, for stacked
        /// shapes), may be null: the authored normal is first turned by it.
        /// </summary>
        internal static (Vector3[] normals, Vector3[] tangents) SurfaceDeltas(MeshSnapshot asset, Vector3[] frameDeltas, Vector3[] baseDeltas,
            WeldedNormalsCache cache = null)
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
            // The normals of the rest and of the fit are the same for every frame: computed once (the same sums, the same bits).
            var weldedRest = cache != null ? cache.Rest() : null;
            var weldedBefore = baseDeltas == null ? weldedRest ?? WeldedNormals(before, asset)
                : cache?.Fitted(baseDeltas, before) ?? WeldedNormals(before, asset);
            var weldedAfter = WeldedNormals(after, asset);
            weldedRest ??= baseDeltas == null ? weldedBefore : WeldedNormals(asset.localVertices, asset);
            var tangentsIn = asset.baseTangents;
            var normals = new Vector3[n];
            var tangents = tangentsIn != null ? new Vector3[n] : null;
            for (int i = 0; i < n; i++)
            {
                if (weldedBefore[i] == weldedAfter[i]) continue;
                var under = baseDeltas == null ? Quaternion.identity : Quaternion.FromToRotation(weldedRest[i], weldedBefore[i]);
                var turn = Quaternion.FromToRotation(weldedBefore[i], weldedAfter[i]);
                var normal = under * asset.baseNormals[i];
                normals[i] = turn * normal - normal;
                if (tangents == null) continue;
                var tangent = under * new Vector3(tangentsIn[i].x, tangentsIn[i].y, tangentsIn[i].z);
                tangents[i] = turn * tangent - tangent;
            }
            return (normals, tangents);
        }

        /// <summary>
        /// The clothing's welded normals at rest and as fitted (for <see cref="SurfaceDeltas"/>), kept for the frames of one
        /// transfer: every frame turns its normals from them. Thread safe.
        /// </summary>
        internal sealed class WeldedNormalsCache
        {
            private readonly MeshSnapshot asset;
            private readonly Vector3[] fit;
            private Vector3[] rest, fitted;

            /// <param name="fit">The fit the frames add to (their base deltas when they show no shape), or null.</param>
            public WeldedNormalsCache(MeshSnapshot asset, Vector3[] fit)
            {
                this.asset = asset;
                this.fit = fit;
            }

            public Vector3[] Rest()
            {
                lock (this) return rest ??= WeldedNormals(asset.localVertices, asset);
            }

            /// <summary>The normals at <paramref name="positions"/> (rest plus <paramref name="baseDeltas"/>), kept when those are the fit; else null.</summary>
            public Vector3[] Fitted(Vector3[] baseDeltas, Vector3[] positions)
            {
                if (!ReferenceEquals(baseDeltas, fit)) return null;
                lock (this) return fitted ??= WeldedNormals(positions, asset);
            }
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
