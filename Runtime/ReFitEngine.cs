using System;
using System.Collections;
using System.Collections.Generic;
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
    public class ReFitEngine
    {
        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Runs the full computation synchronously.</summary>
        public ReFitComputation Run(ReFitRequest request, ReFitProgress progress = null)
        {
            var state = Prepare(request, progress);
            if (state.failed) return state.comp;
            try
            {
                ComputeGeometry(state);
                progress?.Invoke(0.85f, "Baking the mesh");
                Bake(state);
            }
            catch (Exception e)
            {
                state.comp.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
            }
            progress?.Invoke(1f, "Done");
            return state.comp;
        }

        /// <summary>
        /// Runs the computation as an editor coroutine: yield it from any coroutine runner. The heavy geometry
        /// is computed on a background thread so the editor stays responsive. <paramref name="onComplete"/> is
        /// invoked on the main thread with the result.
        /// </summary>
        public IEnumerator RunCoroutine(ReFitRequest request, ReFitProgress progress, Action<ReFitComputation> onComplete)
        {
            State state;
            try
            {
                state = Prepare(request, progress);
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
                Bake(state);
            }
            catch (Exception e)
            {
                state.comp.report.Error("refit-exception", $"Unexpected error while baking: {e.Message}");
            }
            progress?.Invoke(1f, "Done");
            onComplete?.Invoke(state.comp);
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
            // background results
            public Vector3[] localDeltas;
            public Vector3[] normalDeltas;
        }

        private class State
        {
            public ReFitRequest request;
            public ReFitSettings settings;
            public ReFitComputation comp = new ReFitComputation();
            public bool failed;
            public bool wantMesh;
            public bool replace;
            public bool sourceIsTarget;

            public MeshSnapshot asset;
            public MeshSnapshot sourceBody;   // null when not wantMesh; == targetBasis when source == target
            public MeshSnapshot targetBasis;
            public List<ShapeTask> shapes = new List<ShapeTask>();

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
            public Vector3[] primaryLocalDeltas;
            public Vector3[] primaryNormalDeltas;
            public BoneWeight[] newWeights;
            public ReFitWeightTransferDebugInfo weightDebug;

            // background progress
            public volatile string backgroundLabel = "Computing...";
            public float backgroundProgress;
            public Exception backgroundError;

            public ReFitReport Report => comp.report;
        }

        // ------------------------------------------------------------------
        // Phase 1: Prepare (main thread — all scene/Unity-object access)
        // ------------------------------------------------------------------

        private State Prepare(ReFitRequest request, ReFitProgress progress)
        {
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
                state.asset = MeshSnapshot.Capture(stage.assetRenderer, true, null, report);
                state.targetBasis = MeshSnapshot.Capture(stage.targetBody, false,
                    BuildZeroBlendShapeOverrides(stage.targetBody), report);
                if (state.wantMesh)
                    state.sourceBody = stage.sourceBody == stage.targetBody
                        ? state.targetBasis
                        : MeshSnapshot.Capture(stage.sourceBody, false,
                            BuildZeroBlendShapeOverrides(stage.sourceBody), report);

                // Per-shape mesh-local frame deltas (main thread: Mesh API)
                int vertexCount = state.targetBasis.localVertices.Length;
                var bodyMesh = stage.targetBody.sharedMesh;
                foreach (var shape in state.shapes)
                {
                    shape.frameDeltas = new Vector3[vertexCount];
                    int frame = bodyMesh.GetBlendShapeFrameCount(shape.shapeIndex) - 1;
                    bodyMesh.GetBlendShapeFrameVertices(shape.shapeIndex, frame, shape.frameDeltas, null, null);
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
            } // stage disposed: everything below works on captured arrays only

            return state;
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

            // Spatial indices
            SetBackgroundProgress(state, 0.05f, "Building spatial indices");
            var bvhTarget = SurfaceBvh.Build(targetBasis);
            var bvhSource = state.wantMesh && state.sourceBody != targetBasis ? SurfaceBvh.Build(state.sourceBody) : bvhTarget;

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

            // ---- Mesh deformation field --------------------------------------------------
            Vector3[] primaryGroupDeltas = null;
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
                DeltaField.Smooth(primaryGroupDeltas, asset.groupAdjacency, settings.smoothingIterations, settings.smoothingStrength);

                state.primaryLocalDeltas = ToLocalDeltas(state, primaryGroupDeltas, true);
                if (settings.recalculateNormalDeltas)
                    state.primaryNormalDeltas = NormalDeltas(asset, state.primaryLocalDeltas, null);
            }

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
                        worldShapeDelta[i] = targetBasis.skinMatrices[i].MultiplyVector(shape.frameDeltas[i]));

                    var groupDeltas = new Vector3[groupCount];
                    Parallel.For(0, groupCount, g =>
                    {
                        if (!targetBindings[g].valid) { groupDeltas[g] = Vector3.zero; return; }
                        int t = targetBindings[g].triangle * 3;
                        var bary = targetBindings[g].bary;
                        var d = worldShapeDelta[targetBasis.triangles[t]] * bary.x
                              + worldShapeDelta[targetBasis.triangles[t + 1]] * bary.y
                              + worldShapeDelta[targetBasis.triangles[t + 2]] * bary.z;
                        groupDeltas[g] = d * falloff[g];
                    });
                    DeltaField.Smooth(groupDeltas, asset.groupAdjacency, settings.smoothingIterations, settings.smoothingStrength);

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
                    ? WeightTransfer.Transfer(asset, targetBasis, targetBindings, state.bodyBoneToNew,
                        state.assetBoneToNew, state.assetBoneIsExtra, state.newBoneRegions, state.assetGroupRegions,
                        settings, state.Report, out state.weightDebug)
                    : RemapAllOriginal(asset, state.assetBoneToNew);
            }

            if (settings.captureProjectionDebug)
                state.comp.projectionDebug = BuildProjectionDebugData(state, bindings, targetBindings, falloff);

            SetBackgroundProgress(state, 1f, "Finishing");
        }

        private static void SetBackgroundProgress(State state, float t, string label)
        {
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
            var w2l = asset.rendererWorldToLocal;
            bool replace = state.replace;
            Parallel.For(0, vertexCount, i =>
            {
                var dWorld = groupDeltas[asset.groupOfVertex[i]];
                if (replace)
                {
                    result[i] = isPositionFrame
                        // New bindposes are captured in the staged pose: mesh space == staged renderer space.
                        ? w2l.MultiplyPoint3x4(asset.worldVertices[i] + dWorld) - asset.localVertices[i]
                        : w2l.MultiplyVector(dWorld);
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
            newMesh.name = state.asset.mesh.name.Replace("(Clone)", "") + "_ReFit";

            if (state.primaryLocalDeltas != null)
            {
                comp.primaryShapeName = UniqueShapeName(newMesh, settings.blendshapeName);
                newMesh.AddBlendShapeFrame(comp.primaryShapeName, 100f, state.primaryLocalDeltas, state.primaryNormalDeltas, null);
            }

            if (state.shapes.Count > 0)
            {
                comp.secondaryShapeNames = new string[state.shapes.Count];
                comp.secondaryMirrorWeights = new float[state.shapes.Count];
                for (int s = 0; s < state.shapes.Count; s++)
                {
                    var shape = state.shapes[s];
                    string desired = settings.prefixTransferredShapes
                        ? $"{settings.blendshapeName}_{shape.sourceName}"
                        : shape.sourceName;
                    var name = UniqueShapeName(newMesh, desired);
                    newMesh.AddBlendShapeFrame(name, 100f, shape.localDeltas, shape.normalDeltas, null);
                    comp.secondaryShapeNames[s] = name;
                    comp.secondaryMirrorWeights[s] = shape.mirrorWeight;
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
        // Bone plan (armature replacement) — main thread only
        // ------------------------------------------------------------------

        private static bool BuildBonePlan(NormalizedStage stage, MeshSnapshot asset, MeshSnapshot targetBody,
            ReFitComputation comp, Dictionary<Transform, BodyRegion> sourceRegions,
            Dictionary<Transform, BodyRegion> targetRegions, ReFitReport report,
            out Matrix4x4[] bindposes, out int[] bodyBoneToNewOut, out int[] assetBoneToNewOut,
            out bool[] assetBoneIsExtraOut, out BodyRegion[] newBoneRegionsOut)
        {
            bindposes = null;
            bodyBoneToNewOut = null;
            assetBoneToNewOut = null;
            assetBoneIsExtraOut = null;
            newBoneRegionsOut = null;

            var targetRoot = stage.targetRoot.transform;
            var targetNameIndex = HumanoidBoneMapper.BuildNameIndex(targetRoot, stage.targetExcludedAssetRoot);
            var targetHumanIndex = HumanoidBoneMapper.BuildHumanoidBoneIndex(targetRoot, stage.targetHumanMap, stage.targetExcludedAssetRoot);
            var sourceHumanIndex = HumanoidBoneMapper.BuildHumanoidBoneIndex(stage.sourceRoot.transform, stage.sourceHumanMap);

            var stageBones = new List<Transform>();
            var refs = new List<ReFitBoneRef>();
            var newBoneRegions = new List<BodyRegion>();
            var indexOf = new Dictionary<Transform, int>();
            var keptSet = new HashSet<Transform>();
            var originalAssetBonesByNew = new Dictionary<int, List<Transform>>();
            var assetBoneSet = new HashSet<Transform>();
            if (asset.bones != null)
                foreach (var bone in asset.bones)
                    if (bone != null) assetBoneSet.Add(bone);

            int AddTargetBone(Transform t)
            {
                if (indexOf.TryGetValue(t, out int idx)) return idx;
                idx = stageBones.Count;
                stageBones.Add(t);
                indexOf[t] = idx;
                refs.Add(new ReFitBoneRef { origin = ReFitBoneOrigin.Target, name = t.name, path = ReFitUtility.IndexPath(t, targetRoot) });
                newBoneRegions.Add(RegionOf(t, targetRegions));
                return idx;
            }

            void AddOriginalAssetBone(int newIndex, Transform assetBone)
            {
                if (newIndex < 0 || assetBone == null) return;
                if (!originalAssetBonesByNew.TryGetValue(newIndex, out var list))
                {
                    list = new List<Transform>();
                    originalAssetBonesByNew[newIndex] = list;
                }
                if (!list.Contains(assetBone))
                    list.Add(assetBone);
            }

            // Reverse human map of the source (bone transform -> human bone) for chain resolution.
            var sourceHumanOf = new Dictionary<Transform, HumanBodyBones>();
            foreach (var kv in sourceHumanIndex)
                if (kv.Value != null && !sourceHumanOf.ContainsKey(kv.Value)) sourceHumanOf[kv.Value] = kv.Key;

            Transform ResolveToTarget(Transform assetOrSourceBone)
            {
                if (assetOrSourceBone == null) return null;
                if (IsArmatureContainerBone(assetOrSourceBone))
                    return ResolveNearestDescendant(assetOrSourceBone);
                var direct = ResolveDirect(assetOrSourceBone);
                if (direct != null) return direct;

                if (HumanoidBoneMapper.TryInferHumanoidBone(assetOrSourceBone, out var explicitHuman) &&
                    explicitHuman == HumanBodyBones.UpperChest &&
                    !targetHumanIndex.ContainsKey(HumanBodyBones.UpperChest))
                    return null;

                var chainTarget = ResolveInsertedChainBone(assetOrSourceBone);
                if (chainTarget != null) return chainTarget;

                return null;
            }

            Transform ResolveDirect(Transform assetOrSourceBone)
            {
                if (assetOrSourceBone == null) return null;
                if (IsArmatureContainerBone(assetOrSourceBone)) return null;
                if (targetNameIndex.TryGetValue(ReFitUtility.NormalizeName(assetOrSourceBone.name), out var byName))
                    return byName;
                if (HumanoidBoneMapper.TryInferHumanoidBone(assetOrSourceBone, out var namedHuman) &&
                    HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, namedHuman, out var namedTarget))
                    return namedTarget;

                if (!stage.assetOnSourceAvatar && stage.assetBoneToSource.TryGetValue(assetOrSourceBone, out var mapped) &&
                    mapped != null)
                {
                    if (targetNameIndex.TryGetValue(ReFitUtility.NormalizeName(mapped.name), out var mappedByName))
                        return mappedByName;
                    if (sourceHumanOf.TryGetValue(mapped, out var human) &&
                        HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, human, out var tgt))
                        return tgt;
                    if (HumanoidBoneMapper.TryInferHumanoidBone(mapped, out human) &&
                        HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, human, out tgt))
                        return tgt;
                }
                return null;
            }

            Transform ResolveInsertedChainBone(Transform bone)
            {
                var ancestor = ResolveNearestAncestor(bone);
                if (ancestor == null) return null;

                var descendant = ResolveNearestDescendant(bone);
                if (descendant == null) return null;

                var da = Vector3.SqrMagnitude(bone.position - ancestor.position);
                var dd = Vector3.SqrMagnitude(bone.position - descendant.position);
                return dd < da ? descendant : ancestor;
            }

            Transform ResolveNearestAncestor(Transform bone)
            {
                var cur = bone.parent;
                while (cur != null)
                {
                    var resolved = ResolveDirect(cur);
                    if (resolved != null) return resolved;
                    if (cur == stage.sourceRoot.transform || cur == stage.assetStageRoot) break;
                    cur = cur.parent;
                }
                return null;
            }

            Transform ResolveNearestDescendant(Transform bone)
            {
                var queue = new Queue<Transform>();
                for (int i = 0; i < bone.childCount; i++)
                    queue.Enqueue(bone.GetChild(i));

                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    var resolved = ResolveDirect(cur);
                    if (resolved != null) return resolved;
                    for (int i = 0; i < cur.childCount; i++)
                        queue.Enqueue(cur.GetChild(i));
                }
                return null;
            }

            bool IsArmatureContainerBone(Transform bone)
            {
                if (bone == null) return false;
                var key = ReFitUtility.NormalizeName(bone.name);
                return key == "armature" || key == "skeleton" || key == "rig";
            }

            // 2) resolve every asset bone
            int assetBoneCount = asset.bones != null ? asset.bones.Length : 0;
            var assetBoneToNew = new int[assetBoneCount];
            var assetBoneIsExtra = new bool[assetBoneCount];
            int mappedCount = 0, keptCount = 0;
            var keptOriginRoot = stage.assetOnSourceAvatar ? stage.sourceRoot.transform : stage.assetStageRoot;
            var keptOrigin = stage.assetOnSourceAvatar ? ReFitBoneOrigin.SourceAvatar : ReFitBoneOrigin.Asset;

            for (int k = 0; k < assetBoneCount; k++)
            {
                var bone = asset.bones[k];
                if (bone == null) { assetBoneToNew[k] = -1; continue; }
                var target = ResolveToTarget(bone);
                if (target != null)
                {
                    assetBoneToNew[k] = AddTargetBone(target);
                    AddOriginalAssetBone(assetBoneToNew[k], bone);
                    mappedCount++;
                }
                else
                {
                    if (indexOf.TryGetValue(bone, out int existing))
                    {
                        assetBoneToNew[k] = existing;
                        assetBoneIsExtra[k] = true;
                        AddOriginalAssetBone(existing, bone);
                        continue;
                    }
                    int idx = stageBones.Count;
                    stageBones.Add(bone);
                    indexOf[bone] = idx;
                    refs.Add(new ReFitBoneRef { origin = keptOrigin, name = bone.name, path = ReFitUtility.IndexPath(bone, keptOriginRoot) });
                    newBoneRegions.Add(RegionOfKeptBone(bone, stage, sourceRegions));
                    keptSet.Add(bone);
                    assetBoneToNew[k] = idx;
                    assetBoneIsExtra[k] = true;
                    AddOriginalAssetBone(idx, bone);
                    keptCount++;
                }
            }

            if (mappedCount == 0)
            {
                report.Warn("no-target-bones",
                    "No asset bone could be matched to the target avatar's skeleton; armature replacement is not possible.");
                return false;
            }
            if (keptCount > 0)
                report.Info("kept-bones", $"{keptCount} asset bones have no target equivalent and will be preserved (physics/extra bones).");

            // 3) placements for kept subtree roots
            var placements = new List<ReFitKeptBonePlacement>();
            foreach (var bone in keptSet)
            {
                if (bone.parent != null && keptSet.Contains(bone.parent)) continue;
                var parentTarget = FindResolvedAncestor(bone, ResolveToTarget, keptOriginRoot);
                if (parentTarget == null)
                {
                    stage.targetHumanMap.TryGetValue(HumanBodyBones.Hips, out parentTarget);
                    if (parentTarget == null) parentTarget = targetRoot;
                }
                placements.Add(new ReFitKeptBonePlacement
                {
                    boneIndex = indexOf[bone],
                    targetParentPath = ReFitUtility.IndexPath(parentTarget, targetRoot),
                    localPosition = parentTarget.InverseTransformPoint(bone.position),
                    localRotation = Quaternion.Inverse(parentTarget.rotation) * bone.rotation,
                    localScale = SafeDivide(bone.lossyScale, parentTarget.lossyScale)
                });
            }

            // 4) root bone: prefer the mapped version of the asset's root, then target hips.
            comp.rootBoneIndex = -1;
            var assetRootBone = stage.assetRenderer.rootBone;
            if (assetRootBone != null && indexOf.TryGetValue(assetRootBone, out var rootIndex))
            {
                comp.rootBoneIndex = rootIndex;
            }
            else if (assetRootBone != null)
            {
                var mappedRoot = ResolveToTarget(assetRootBone);
                if (mappedRoot != null)
                    comp.rootBoneIndex = AddTargetBone(mappedRoot);
            }
            if (comp.rootBoneIndex < 0 &&
                HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, HumanBodyBones.Hips, out var hips))
                comp.rootBoneIndex = AddTargetBone(hips);
            if (comp.rootBoneIndex < 0 && stageBones.Count > 0)
                comp.rootBoneIndex = 0;

            // 5) target body weights are projected onto the hoodie-represented bone set. Target bones that
            // do not have a hoodie equivalent are remapped to the nearest represented ancestor, so the output
            // armature does not grow lower legs/fingers/etc. just because the target avatar has them.
            var bodyBoneToNew = BuildBodyBoneRemap(targetBody.bones, indexOf, comp.rootBoneIndex);

            // 6) bindposes captured in the staged pose, relative to the staged asset renderer
            var rendererL2W = stage.assetRenderer.transform.localToWorldMatrix;
            bindposes = new Matrix4x4[stageBones.Count];
            for (int i = 0; i < stageBones.Count; i++)
                bindposes[i] = stageBones[i].worldToLocalMatrix * rendererL2W;

            comp.bones = refs.ToArray();
            comp.keptPlacements = placements.ToArray();
            comp.leafTailHints = BuildLeafTailHints(stageBones, originalAssetBonesByNew, assetBoneSet);
            bodyBoneToNewOut = bodyBoneToNew;
            assetBoneToNewOut = assetBoneToNew;
            assetBoneIsExtraOut = assetBoneIsExtra;
            newBoneRegionsOut = newBoneRegions.ToArray();
            return true;
        }

        private static BodyRegion RegionOf(Transform bone, Dictionary<Transform, BodyRegion> regions)
        {
            if (bone != null && regions != null && regions.TryGetValue(bone, out var region))
                return region;
            return BodyRegion.Unknown;
        }

        private static BodyRegion RegionOfKeptBone(Transform bone, NormalizedStage stage,
            Dictionary<Transform, BodyRegion> sourceRegions)
        {
            if (bone == null) return BodyRegion.Unknown;
            if (stage.assetOnSourceAvatar)
                return RegionOf(bone, sourceRegions);
            if (stage.assetBoneToSource != null && stage.assetBoneToSource.TryGetValue(bone, out var source) && source != null)
                return RegionOf(source, sourceRegions);
            return BodyRegion.Unknown;
        }

        private static ReFitLeafTailHint[] BuildLeafTailHints(List<Transform> stageBones,
            Dictionary<int, List<Transform>> originalAssetBonesByNew, HashSet<Transform> assetBoneSet)
        {
            if (stageBones == null || stageBones.Count == 0) return Array.Empty<ReFitLeafTailHint>();

            var represented = new HashSet<Transform>();
            foreach (var bone in stageBones)
                if (bone != null) represented.Add(bone);

            var hints = new List<ReFitLeafTailHint>();
            for (int i = 0; i < stageBones.Count; i++)
            {
                var bone = stageBones[i];
                if (bone == null || HasRepresentedDescendant(bone, represented)) continue;

                if (TryTargetTailHint(i, bone, represented, out var hint) ||
                    TryOriginalTailHint(i, bone, originalAssetBonesByNew, assetBoneSet, out hint))
                    hints.Add(hint);
            }

            return hints.ToArray();
        }

        private static bool HasRepresentedDescendant(Transform bone, HashSet<Transform> represented)
        {
            if (bone == null || represented == null) return false;
            var queue = new Queue<Transform>();
            for (int i = 0; i < bone.childCount; i++)
                queue.Enqueue(bone.GetChild(i));

            while (queue.Count > 0)
            {
                var child = queue.Dequeue();
                if (child != null && represented.Contains(child))
                    return true;
                if (child == null) continue;
                for (int i = 0; i < child.childCount; i++)
                    queue.Enqueue(child.GetChild(i));
            }
            return false;
        }

        private static bool TryTargetTailHint(int boneIndex, Transform targetBone, HashSet<Transform> represented,
            out ReFitLeafTailHint hint)
        {
            hint = null;
            if (targetBone == null) return false;
            var tail = FindPreferredTailChild(targetBone, represented);
            if (tail == null) return false;
            var local = targetBone.InverseTransformPoint(tail.position);
            if (local.sqrMagnitude < 1e-8f) return false;
            hint = new ReFitLeafTailHint
            {
                boneIndex = boneIndex,
                name = "__ReFitLeafTail_" + SafeObjectName(tail.name),
                localPosition = local,
                source = "target:" + tail.name
            };
            return true;
        }

        private static bool TryOriginalTailHint(int boneIndex, Transform finalBoneFrame,
            Dictionary<int, List<Transform>> originalAssetBonesByNew, HashSet<Transform> assetBoneSet,
            out ReFitLeafTailHint hint)
        {
            hint = null;
            if (finalBoneFrame == null || originalAssetBonesByNew == null ||
                !originalAssetBonesByNew.TryGetValue(boneIndex, out var originals))
                return false;

            foreach (var original in originals)
            {
                if (original == null) continue;
                var child = FindPreferredTailChild(original, assetBoneSet);
                if (child == null) continue;
                var local = finalBoneFrame.InverseTransformPoint(child.position);
                if (local.sqrMagnitude < 1e-8f) continue;
                hint = new ReFitLeafTailHint
                {
                    boneIndex = boneIndex,
                    name = "__ReFitLeafTail_" + SafeObjectName(child.name),
                    localPosition = local,
                    source = "asset-child:" + child.name
                };
                return true;
            }

            foreach (var original in originals)
            {
                if (original == null || original.parent == null) continue;
                var direction = original.position - original.parent.position;
                float parentLength = direction.magnitude;
                if (parentLength <= 1e-5f) continue;
                var inferred = original.position + direction.normalized * Mathf.Clamp(parentLength * 0.45f, 0.025f, 0.2f);
                var local = finalBoneFrame.InverseTransformPoint(inferred);
                if (local.sqrMagnitude < 1e-8f) continue;
                hint = new ReFitLeafTailHint
                {
                    boneIndex = boneIndex,
                    name = "__ReFitLeafTail_Inferred",
                    localPosition = local,
                    source = "asset-inferred:" + original.name
                };
                return true;
            }

            return false;
        }

        private static Transform FindPreferredTailChild(Transform bone, HashSet<Transform> excluded)
        {
            if (bone == null) return null;
            HumanoidBoneMapper.TryInferHumanoidBone(bone, out var boneHuman);

            var directExpected = FindDirectExpectedTailChild(bone, excluded, boneHuman);
            if (directExpected != null)
                return directExpected;

            Transform bestExpected = null;
            int bestExpectedScore = int.MinValue;

            var queue = new Queue<(Transform transform, int depth)>();
            for (int i = 0; i < bone.childCount; i++)
                queue.Enqueue((bone.GetChild(i), 1));

            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                var child = entry.transform;
                if (child == null) continue;
                if (!IsExcludedTailCandidate(child, excluded))
                {
                    int score = TailChildScore(boneHuman, bone.position, child, entry.depth);
                    if (score > bestExpectedScore)
                    {
                        bestExpected = child;
                        bestExpectedScore = score;
                    }
                }

                if (entry.depth < 4)
                    for (int i = 0; i < child.childCount; i++)
                        queue.Enqueue((child.GetChild(i), entry.depth + 1));
            }

            if (bestExpected != null && bestExpectedScore >= 1000)
                return bestExpected;

            Transform bestDirect = null;
            float bestDistance = 0f;
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (IsExcludedTailCandidate(child, excluded)) continue;
                float distance = Vector3.Distance(bone.position, child.position);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    bestDirect = child;
                }
            }

            return bestDirect ?? bestExpected;
        }

        private static int TailChildScore(HumanBodyBones parentHuman, Vector3 parentPosition, Transform child, int depth)
        {
            int score = Mathf.Max(0, 50 - depth);
            if (HumanoidBoneMapper.TryInferHumanoidBone(child, out var childHuman) &&
                IsExpectedLeafChild(parentHuman, childHuman))
                score += 2000;
            score += Mathf.RoundToInt(Vector3.Distance(parentPosition, child.position) * 100f);
            return score;
        }

        private static Transform FindDirectExpectedTailChild(Transform bone, HashSet<Transform> excluded,
            HumanBodyBones parentHuman)
        {
            Transform best = null;
            float bestDistance = -1f;
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (IsExcludedTailCandidate(child, excluded)) continue;
                if (!HumanoidBoneMapper.TryInferHumanoidBone(child, out var childHuman)) continue;
                if (!IsExpectedLeafChild(parentHuman, childHuman)) continue;

                float distance = Vector3.Distance(bone.position, child.position);
                if (distance > bestDistance)
                {
                    best = child;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool IsExpectedLeafChild(HumanBodyBones parent, HumanBodyBones child)
        {
            switch (parent)
            {
                case HumanBodyBones.LeftLowerArm: return child == HumanBodyBones.LeftHand;
                case HumanBodyBones.RightLowerArm: return child == HumanBodyBones.RightHand;
                case HumanBodyBones.LeftUpperArm: return child == HumanBodyBones.LeftLowerArm;
                case HumanBodyBones.RightUpperArm: return child == HumanBodyBones.RightLowerArm;
                case HumanBodyBones.LeftLowerLeg: return child == HumanBodyBones.LeftFoot;
                case HumanBodyBones.RightLowerLeg: return child == HumanBodyBones.RightFoot;
                case HumanBodyBones.LeftUpperLeg: return child == HumanBodyBones.LeftLowerLeg;
                case HumanBodyBones.RightUpperLeg: return child == HumanBodyBones.RightLowerLeg;
                case HumanBodyBones.LeftFoot: return child == HumanBodyBones.LeftToes;
                case HumanBodyBones.RightFoot: return child == HumanBodyBones.RightToes;
                case HumanBodyBones.Neck: return child == HumanBodyBones.Head;
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest: return child == HumanBodyBones.Neck || child == HumanBodyBones.Head;
                default: return false;
            }
        }

        private static bool IsExcludedTailCandidate(Transform candidate, HashSet<Transform> excluded)
        {
            return candidate == null || (excluded != null && excluded.Contains(candidate));
        }

        private static string SafeObjectName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Tail";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value.Replace('/', '_').Replace('\\', '_');
        }

        private static int[] BuildBodyBoneRemap(Transform[] bodyBones, Dictionary<Transform, int> represented,
            int fallbackIndex)
        {
            var result = new int[bodyBones != null ? bodyBones.Length : 0];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = -1;
                var bone = bodyBones[i];
                while (bone != null)
                {
                    if (represented.TryGetValue(bone, out var idx))
                    {
                        result[i] = idx;
                        break;
                    }
                    bone = bone.parent;
                }
                if (result[i] < 0 && fallbackIndex >= 0)
                    result[i] = fallbackIndex;
            }
            return result;
        }

        private static Transform FindResolvedAncestor(Transform bone, Func<Transform, Transform> resolve, Transform stopAt)
        {
            var cur = bone.parent;
            while (cur != null)
            {
                var t = resolve(cur);
                if (t != null) return t;
                if (cur == stopAt) break;
                cur = cur.parent;
            }
            return null;
        }

        private static Vector3 SafeDivide(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(b.x) > 1e-8f ? a.x / b.x : 1f,
                Mathf.Abs(b.y) > 1e-8f ? a.y / b.y : 1f,
                Mathf.Abs(b.z) > 1e-8f ? a.z / b.z : 1f);
        }

        private static BoneWeight[] RemapAllOriginal(MeshSnapshot asset, int[] assetBoneToNew)
        {
            int n = asset.localVertices.Length;
            var result = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                var bw = asset.boneWeights[i];
                bw.boneIndex0 = Remap(bw.boneIndex0, assetBoneToNew);
                bw.boneIndex1 = Remap(bw.boneIndex1, assetBoneToNew);
                bw.boneIndex2 = Remap(bw.boneIndex2, assetBoneToNew);
                bw.boneIndex3 = Remap(bw.boneIndex3, assetBoneToNew);
                result[i] = bw;
            }
            return result;
        }

        private static int Remap(int idx, int[] map) => idx >= 0 && idx < map.Length && map[idx] >= 0 ? map[idx] : 0;

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
