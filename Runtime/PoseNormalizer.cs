using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// A temporary, hidden copy of the source avatar, target avatar and asset in their authored/imported pose,
    /// scale-matched and root-aligned so their surfaces can be compared in world space.
    /// Dispose to destroy the staged clones.
    /// </summary>
    public class NormalizedStage : IDisposable
    {
        public GameObject stagingRoot;
        /// <summary>Clone of the source avatar (same object as <see cref="targetRoot"/> when source == target).</summary>
        public GameObject sourceRoot;
        /// <summary>Clone of the target avatar.</summary>
        public GameObject targetRoot;
        /// <summary>Clone of the asset renderer.</summary>
        public SkinnedMeshRenderer assetRenderer;
        public SkinnedMeshRenderer sourceBody;
        public SkinnedMeshRenderer targetBody;
        public Dictionary<HumanBodyBones, Transform> sourceHumanMap;
        public Dictionary<HumanBodyBones, Transform> targetHumanMap;
        /// <summary>Root transform of the staged standalone asset (null when the asset lives on the source avatar).</summary>
        public Transform assetStageRoot;
        /// <summary>Nested target-space asset copy inside <see cref="targetRoot"/> that must be ignored for target skeleton/body indexing.</summary>
        public Transform targetExcludedAssetRoot;
        /// <summary>True when the asset renderer is part of the source avatar hierarchy (shares its armature).</summary>
        public bool assetOnSourceAvatar;
        /// <summary>
        /// True when the asset already lives in the TARGET avatar's space (it is parented under the target, and
        /// the source is a separate reference). Such assets must NOT be scaled/posed onto the source — they are
        /// already at the target's scale and position; the source body is brought to target space for comparison.
        /// </summary>
        public bool assetInTargetSpace;
        /// <summary>True when source and target are the same avatar (Blendshape mode without a distinct source).</summary>
        public bool sourceIsTarget;
        /// <summary>Name-based map of staged asset bones to staged source bones (standalone assets only; values may be null).</summary>
        public Dictionary<Transform, Transform> assetBoneToSource = new Dictionary<Transform, Transform>();
        /// <summary>Uniform scale applied to the source side so it matches the target size.</summary>
        public float appliedScale = 1f;
        /// <summary>Always false: ReFit preserves authored pose during staging. Kept for diagnostics compatibility.</summary>
        public bool sourceNeutralPoseApplied;
        /// <summary>Always false: ReFit preserves authored pose during staging. Kept for diagnostics compatibility.</summary>
        public bool targetNeutralPoseApplied;
        /// <summary>The real object the asset paths are relative to (asset hierarchy root, or the avatar carrying it).</summary>
        public GameObject realAssetObject;
        /// <summary>Child-index path of the asset renderer inside <see cref="realAssetObject"/>.</summary>
        public int[] assetRendererPath;

        public void Dispose()
        {
            if (stagingRoot != null) UnityEngine.Object.DestroyImmediate(stagingRoot);
            stagingRoot = null;
        }
    }

    /// <summary>Builds <see cref="NormalizedStage"/> instances: cloning, scale matching and asset armature posing.</summary>
    public static class PoseNormalizer
    {
        /// <summary>
        /// Creates the normalized stage for a request. Returns null (with errors in <paramref name="report"/>) when the
        /// request cannot be staged at all. Warnings never abort.
        /// </summary>
        public static NormalizedStage CreateStage(ReFitRequest request, ReFitReport report)
        {
            if (request == null) { report.Error("bad-request", "Request is null."); return null; }
            if (request.assetRenderer == null) { report.Error("missing-asset", "No asset renderer set."); return null; }
            if (request.assetRenderer.sharedMesh == null) { report.Error("missing-asset-mesh", "The asset renderer has no mesh."); return null; }
            if (request.targetAvatar == null) { report.Error("missing-target", "No target avatar set."); return null; }

            var stage = new NormalizedStage();
            stage.stagingRoot = new GameObject("__ReFit_Staging__") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                BuildClones(request, stage, report);
                if (stage.assetRenderer == null || stage.targetBody == null || stage.sourceBody == null)
                {
                    report.Error("staging-failed", "Could not resolve the staged renderers (see previous messages).");
                    stage.Dispose();
                    return null;
                }

                stage.sourceHumanMap = HumanoidBoneMapper.GetHumanoidMap(stage.sourceRoot, report);
                stage.targetHumanMap = stage.sourceIsTarget
                    ? stage.sourceHumanMap
                    : HumanoidBoneMapper.GetHumanoidMap(stage.targetRoot, report, stage.targetExcludedAssetRoot);

                if (!stage.sourceIsTarget) ScaleAndAlign(stage, report);

                PoseAsset(stage, report);
                if (!stage.assetOnSourceAvatar && stage.assetRenderer != null)
                    BakeCurrentSkinPoseAsDefault(stage.assetRenderer, report);
                return stage;
            }
            catch (Exception e)
            {
                report.Error("staging-exception", $"Unexpected error while staging: {e.Message}\n{e.StackTrace}");
                stage.Dispose();
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Cloning
        // ------------------------------------------------------------------

        private static void BuildClones(ReFitRequest request, NormalizedStage stage, ReFitReport report)
        {
            stage.targetRoot = Clone(request.targetAvatar, stage.stagingRoot.transform);
            stage.sourceIsTarget = request.sourceAvatar == null || request.sourceAvatar == request.targetAvatar;
            stage.sourceRoot = stage.sourceIsTarget ? stage.targetRoot : Clone(request.sourceAvatar, stage.stagingRoot.transform);

            bool targetSpaceAsset = ShouldStageAssetAsTargetSpace(request, stage);

            // Asset: either part of the source avatar hierarchy, or a standalone hierarchy.
            var carrier = targetSpaceAsset ? null : (stage.sourceIsTarget ? request.targetAvatar : request.sourceAvatar);
            if (carrier != null && request.assetRenderer.transform.IsChildOf(carrier.transform))
            {
                stage.assetOnSourceAvatar = true;
                stage.realAssetObject = carrier;
                stage.assetRendererPath = ReFitUtility.IndexPath(request.assetRenderer.transform, carrier.transform);
                var t = ReFitUtility.ResolvePath(stage.sourceRoot.transform, stage.assetRendererPath);
                stage.assetRenderer = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (stage.assetRenderer == null)
                    report.Error("asset-clone-failed", "Could not locate the asset renderer inside the staged avatar clone.");
            }
            else
            {
                // The asset already lives in the target avatar's space when it is parented under the target and
                // the source is a distinct reference (e.g. "fit a blendshape on my avatar", or the MCB module).
                stage.assetInTargetSpace = targetSpaceAsset && request.targetAvatar != null &&
                                           request.assetRenderer.transform.IsChildOf(request.targetAvatar.transform);

                var realAssetRoot = FindAssetObjectRoot(request.assetRenderer, request.sourceAvatar, request.targetAvatar);
                stage.realAssetObject = realAssetRoot.gameObject;
                stage.assetRendererPath = ReFitUtility.IndexPath(request.assetRenderer.transform, realAssetRoot);
                var assetClone = Clone(realAssetRoot.gameObject, stage.stagingRoot.transform);
                stage.assetStageRoot = assetClone.transform;
                var t = ReFitUtility.ResolvePath(assetClone.transform, stage.assetRendererPath);
                stage.assetRenderer = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (stage.assetRenderer == null)
                    report.Error("asset-clone-failed", "Could not locate the asset renderer inside the staged asset clone.");
                else
                {
                    MarkTargetNestedAssetCopy(request, stage, realAssetRoot, report);
                    RebindExternalBones(request, stage, realAssetRoot, assetClone.transform, report);
                }
            }

            var sourceExcludedRoot = stage.sourceIsTarget ? stage.targetExcludedAssetRoot : null;
            stage.sourceBody = ResolveBodyRenderer(stage.sourceRoot, request.sourceBodyRenderer,
                stage.sourceIsTarget ? request.targetAvatar : request.sourceAvatar, stage.assetRenderer, report, "source", sourceExcludedRoot);
            stage.targetBody = stage.sourceIsTarget && request.targetBodyRenderer == null && request.sourceBodyRenderer == null
                ? stage.sourceBody
                : ResolveBodyRenderer(stage.targetRoot, request.targetBodyRenderer, request.targetAvatar, stage.assetRenderer, report, "target", stage.targetExcludedAssetRoot);
            if (stage.sourceIsTarget && stage.sourceBody == null) stage.sourceBody = stage.targetBody;
            if (stage.sourceIsTarget && stage.targetBody == null) stage.targetBody = stage.sourceBody;
        }

        private static bool ShouldStageAssetAsTargetSpace(ReFitRequest request, NormalizedStage stage)
        {
            if (request == null || stage == null || request.assetRenderer == null || request.targetAvatar == null)
                return false;
            if (!request.assetRenderer.transform.IsChildOf(request.targetAvatar.transform))
                return false;
            if (request.assetRenderer == request.sourceBodyRenderer || request.assetRenderer == request.targetBodyRenderer)
                return false;

            if (request.mode == ReFitMode.Blendshape && stage.sourceIsTarget)
                return true;

            if (request.mode != ReFitMode.MeshAndBlendshape || stage.sourceIsTarget)
                return false;

            var settings = request.settings;
            if (settings != null && !settings.replaceArmature)
                return true;

            var metadata = request.assetRenderer.GetComponent<ReFitGeneratedAssetMetadata>();
            return metadata != null && metadata.data != null;
        }

        private static GameObject Clone(GameObject original, Transform parent)
        {
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name;
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetParent(parent, true);
            clone.SetActive(true);

            // Make the clone inert: nothing should animate or react while it is staged.
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
            {
                try { behaviour.enabled = false; }
                catch { /* some behaviours refuse; harmless */ }
            }
            return clone;
        }

        /// <summary>
        /// Converts the renderer's current skinned pose into a temporary mesh rest pose.
        /// This is used for scene-authored accessory poses, e.g. a T-pose hoodie whose arm bones were rotated
        /// in the scene to match an A-pose avatar before running ReFit.
        /// </summary>
        public static bool BakeCurrentSkinPoseAsDefault(SkinnedMeshRenderer renderer, ReFitReport report = null)
        {
            if (renderer == null || renderer.sharedMesh == null)
            {
                return false;
            }

            var sourceMesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = sourceMesh.bindposes;
            var weights = sourceMesh.boneWeights;
            int vertexCount = sourceMesh.vertexCount;
            if (bones == null || bones.Length == 0 ||
                bindposes == null || bindposes.Length != bones.Length ||
                weights == null || weights.Length != vertexCount)
            {
                return false;
            }

            var meshLocalToRendererLocal = new Matrix4x4[bones.Length];
            var normalLocalToRendererLocal = new Matrix4x4[bones.Length];
            var rendererWorldToLocal = renderer.transform.worldToLocalMatrix;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null)
                {
                    meshLocalToRendererLocal[i] = Matrix4x4.identity;
                    normalLocalToRendererLocal[i] = Matrix4x4.identity;
                    continue;
                }

                var m = rendererWorldToLocal * bones[i].localToWorldMatrix * bindposes[i];
                meshLocalToRendererLocal[i] = m;
                normalLocalToRendererLocal[i] = m.inverse.transpose;
            }

            var bakedMesh = UnityEngine.Object.Instantiate(sourceMesh);
            bakedMesh.name = sourceMesh.name.Replace("(Clone)", "") + "_ScenePoseDefault";

            var sourceVertices = sourceMesh.vertices;
            var bakedVertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                bakedVertices[i] = SkinPoint(sourceVertices[i], weights[i], meshLocalToRendererLocal);
            bakedMesh.vertices = bakedVertices;

            var sourceNormals = sourceMesh.normals;
            if (sourceNormals != null && sourceNormals.Length == vertexCount)
            {
                var bakedNormals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                    bakedNormals[i] = SkinDirection(sourceNormals[i], weights[i], normalLocalToRendererLocal, true);
                bakedMesh.normals = bakedNormals;
            }
            else
            {
                bakedMesh.RecalculateNormals();
            }

            var sourceTangents = sourceMesh.tangents;
            if (sourceTangents != null && sourceTangents.Length == vertexCount)
            {
                var bakedTangents = new Vector4[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    var tangent = new Vector3(sourceTangents[i].x, sourceTangents[i].y, sourceTangents[i].z);
                    tangent = SkinDirection(tangent, weights[i], meshLocalToRendererLocal, true);
                    bakedTangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, sourceTangents[i].w);
                }
                bakedMesh.tangents = bakedTangents;
            }

            BakeBlendShapesIntoCurrentPose(sourceMesh, bakedMesh, weights, meshLocalToRendererLocal, normalLocalToRendererLocal);

            var currentBindposes = new Matrix4x4[bones.Length];
            var rendererLocalToWorld = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < bones.Length; i++)
                currentBindposes[i] = bones[i] != null
                    ? bones[i].worldToLocalMatrix * rendererLocalToWorld
                    : Matrix4x4.identity;

            bakedMesh.bindposes = currentBindposes;
            bakedMesh.boneWeights = weights;
            bakedMesh.RecalculateBounds();
            renderer.sharedMesh = bakedMesh;
            report?.Info("asset-scene-pose-default",
                $"Baked the current scene pose of '{renderer.name}' into a temporary mesh rest pose.");
            return true;
        }

        private static void BakeBlendShapesIntoCurrentPose(
            Mesh sourceMesh,
            Mesh bakedMesh,
            BoneWeight[] weights,
            Matrix4x4[] meshLocalToRendererLocal,
            Matrix4x4[] normalLocalToRendererLocal)
        {
            int vertexCount = sourceMesh.vertexCount;
            int shapeCount = sourceMesh.blendShapeCount;
            if (shapeCount == 0)
            {
                return;
            }

            bakedMesh.ClearBlendShapes();
            var sourceDeltaVertices = new Vector3[vertexCount];
            var sourceDeltaNormals = new Vector3[vertexCount];
            var sourceDeltaTangents = new Vector3[vertexCount];
            var bakedDeltaVertices = new Vector3[vertexCount];
            var bakedDeltaNormals = new Vector3[vertexCount];
            var bakedDeltaTangents = new Vector3[vertexCount];

            for (int s = 0; s < shapeCount; s++)
            {
                string shapeName = sourceMesh.GetBlendShapeName(s);
                int frameCount = sourceMesh.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frameCount; f++)
                {
                    System.Array.Clear(sourceDeltaVertices, 0, sourceDeltaVertices.Length);
                    System.Array.Clear(sourceDeltaNormals, 0, sourceDeltaNormals.Length);
                    System.Array.Clear(sourceDeltaTangents, 0, sourceDeltaTangents.Length);
                    sourceMesh.GetBlendShapeFrameVertices(s, f, sourceDeltaVertices, sourceDeltaNormals, sourceDeltaTangents);

                    for (int i = 0; i < vertexCount; i++)
                    {
                        bakedDeltaVertices[i] = SkinDirection(sourceDeltaVertices[i], weights[i], meshLocalToRendererLocal, false);
                        bakedDeltaNormals[i] = SkinDirection(sourceDeltaNormals[i], weights[i], normalLocalToRendererLocal, false);
                        bakedDeltaTangents[i] = SkinDirection(sourceDeltaTangents[i], weights[i], meshLocalToRendererLocal, false);
                    }

                    bakedMesh.AddBlendShapeFrame(
                        shapeName,
                        sourceMesh.GetBlendShapeFrameWeight(s, f),
                        bakedDeltaVertices,
                        bakedDeltaNormals,
                        bakedDeltaTangents);
                }
            }
        }

        private static Vector3 SkinPoint(Vector3 point, BoneWeight weight, Matrix4x4[] matrices)
        {
            Vector3 result = Vector3.zero;
            float total = 0f;
            AccumulatePoint(ref result, ref total, point, weight.boneIndex0, weight.weight0, matrices);
            AccumulatePoint(ref result, ref total, point, weight.boneIndex1, weight.weight1, matrices);
            AccumulatePoint(ref result, ref total, point, weight.boneIndex2, weight.weight2, matrices);
            AccumulatePoint(ref result, ref total, point, weight.boneIndex3, weight.weight3, matrices);
            if (total <= 1e-6f) return point;
            return Mathf.Abs(total - 1f) > 1e-4f ? result / total : result;
        }

        private static Vector3 SkinDirection(Vector3 direction, BoneWeight weight, Matrix4x4[] matrices, bool normalize)
        {
            Vector3 result = Vector3.zero;
            float total = 0f;
            AccumulateDirection(ref result, ref total, direction, weight.boneIndex0, weight.weight0, matrices);
            AccumulateDirection(ref result, ref total, direction, weight.boneIndex1, weight.weight1, matrices);
            AccumulateDirection(ref result, ref total, direction, weight.boneIndex2, weight.weight2, matrices);
            AccumulateDirection(ref result, ref total, direction, weight.boneIndex3, weight.weight3, matrices);
            if (total <= 1e-6f) result = direction;
            else if (Mathf.Abs(total - 1f) > 1e-4f) result /= total;
            return normalize && result.sqrMagnitude > 1e-12f ? result.normalized : result;
        }

        private static void AccumulatePoint(ref Vector3 result, ref float total, Vector3 point, int index, float weight, Matrix4x4[] matrices)
        {
            if (weight <= 0f || index < 0 || index >= matrices.Length) return;
            result += matrices[index].MultiplyPoint3x4(point) * weight;
            total += weight;
        }

        private static void AccumulateDirection(ref Vector3 result, ref float total, Vector3 direction, int index, float weight, Matrix4x4[] matrices)
        {
            if (weight <= 0f || index < 0 || index >= matrices.Length) return;
            result += matrices[index].MultiplyVector(direction) * weight;
            total += weight;
        }

        private static void MarkTargetNestedAssetCopy(ReFitRequest request, NormalizedStage stage,
            Transform realAssetRoot, ReFitReport report)
        {
            if (request?.targetAvatar == null || stage?.targetRoot == null || realAssetRoot == null)
                return;
            if (!stage.assetInTargetSpace || !realAssetRoot.IsChildOf(request.targetAvatar.transform))
                return;

            var path = ReFitUtility.IndexPath(realAssetRoot, request.targetAvatar.transform);
            var targetCopy = ReFitUtility.ResolvePath(stage.targetRoot.transform, path);
            if (targetCopy == null || targetCopy == stage.targetRoot.transform)
                return;

            stage.targetExcludedAssetRoot = targetCopy;
            report.Info("target-asset-copy-excluded",
                $"Keeping the target-space asset copy '{realAssetRoot.name}' in the staged target for stable paths, but excluding it from target skeleton/body indexing.");
        }

        /// <summary>Smallest ancestor of the renderer containing the renderer, its bones and root bone (the "asset root").</summary>
        public static Transform FindCommonRoot(SkinnedMeshRenderer smr)
        {
            var t = smr.transform;
            while (t.parent != null && !ContainsAllBones(t, smr)) t = t.parent;
            return t;
        }

        /// <summary>
        /// Root of the asset object hierarchy, not necessarily the root containing its current skin bones.
        /// Re-fitted assets may already reference target-avatar bones, so using skin bones for root discovery can
        /// accidentally promote the asset root to the whole avatar.
        /// </summary>
        public static Transform FindAssetObjectRoot(SkinnedMeshRenderer smr, GameObject sourceAvatar, GameObject targetAvatar)
        {
            if (smr == null) return null;
            var t = smr.transform;

            var sourceRoot = sourceAvatar != null ? sourceAvatar.transform : null;
            var targetRoot = targetAvatar != null ? targetAvatar.transform : null;
            var boundary = targetRoot != null && t.IsChildOf(targetRoot) ? targetRoot :
                sourceRoot != null && t.IsChildOf(sourceRoot) ? sourceRoot : null;

            if (boundary != null)
            {
                while (t.parent != null && t.parent != boundary)
                    t = t.parent;
                return t;
            }

            return FindCommonRoot(smr);
        }

        private static bool ContainsAllBones(Transform candidate, SkinnedMeshRenderer smr)
        {
            if (!smr.transform.IsChildOf(candidate)) return false;
            if (smr.rootBone != null && !smr.rootBone.IsChildOf(candidate)) return false;
            var bones = smr.bones;
            if (bones != null)
            {
                foreach (var b in bones)
                    if (b != null && !b.IsChildOf(candidate)) return false;
            }
            return true;
        }

        private static void RebindExternalBones(ReFitRequest request, NormalizedStage stage, Transform realAssetRoot,
            Transform assetCloneRoot, ReFitReport report)
        {
            if (request?.assetRenderer == null || stage?.assetRenderer == null) return;
            var originalBones = request.assetRenderer.bones;
            if (originalBones == null || originalBones.Length == 0) return;

            var rebound = new Transform[originalBones.Length];
            int mapped = 0;
            for (int i = 0; i < originalBones.Length; i++)
            {
                rebound[i] = ResolveEquivalent(originalBones[i], request, stage, realAssetRoot, assetCloneRoot);
                if (rebound[i] != null) mapped++;
            }
            stage.assetRenderer.bones = rebound;
            stage.assetRenderer.rootBone = ResolveEquivalent(request.assetRenderer.rootBone, request, stage, realAssetRoot, assetCloneRoot);
            if (mapped > 0)
                report.Info("asset-bones-rebound", $"Resolved {mapped}/{originalBones.Length} staged asset bones onto cloned asset/avatar hierarchies.");
        }

        private static Transform ResolveEquivalent(Transform original, ReFitRequest request, NormalizedStage stage,
            Transform realAssetRoot, Transform assetCloneRoot)
        {
            if (original == null) return null;
            if (realAssetRoot != null && original.IsChildOf(realAssetRoot))
            {
                var path = ReFitUtility.IndexPath(original, realAssetRoot);
                return ReFitUtility.ResolvePath(assetCloneRoot, path);
            }
            if (request.targetAvatar != null && original.IsChildOf(request.targetAvatar.transform))
            {
                var path = ReFitUtility.IndexPath(original, request.targetAvatar.transform);
                return ReFitUtility.ResolvePath(stage.targetRoot.transform, path);
            }
            if (request.sourceAvatar != null && original.IsChildOf(request.sourceAvatar.transform))
            {
                var path = ReFitUtility.IndexPath(original, request.sourceAvatar.transform);
                return ReFitUtility.ResolvePath(stage.sourceRoot.transform, path);
            }
            return original;
        }

        /// <summary>
        /// Picks an avatar's main body renderer: explicit override (resolved into the clone), else a renderer named
        /// "Body", else the skinned renderer with the most vertices (excluding the asset itself).
        /// </summary>
        private static SkinnedMeshRenderer ResolveBodyRenderer(GameObject cloneRoot, SkinnedMeshRenderer overrideRenderer,
            GameObject realRoot, SkinnedMeshRenderer stagedAsset, ReFitReport report, string label, Transform excludedRoot)
        {
            if (cloneRoot == null) return null;

            if (overrideRenderer != null && realRoot != null && overrideRenderer.transform.IsChildOf(realRoot.transform))
            {
                var path = ReFitUtility.IndexPath(overrideRenderer.transform, realRoot.transform);
                var t = ReFitUtility.ResolvePath(cloneRoot.transform, path);
                var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr != null && !IsExcluded(smr.transform, excludedRoot)) return smr;
                report.Warn("body-override-failed", $"Could not resolve the {label} body override in the staged clone; auto-detecting instead.");
            }

            SkinnedMeshRenderer best = null;
            int bestVerts = -1;
            foreach (var smr in cloneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == stagedAsset || smr.sharedMesh == null) continue;
                if (IsExcluded(smr.transform, excludedRoot)) continue;
                if (string.Equals(smr.name, "Body", StringComparison.OrdinalIgnoreCase)) return smr;
                if (smr.sharedMesh.vertexCount > bestVerts) { bestVerts = smr.sharedMesh.vertexCount; best = smr; }
            }
            if (best == null)
                report.Error("no-body-renderer", $"No skinned body renderer found on the {label} avatar.");
            else
                report.Info("body-autodetect", $"Using '{best.name}' as the {label} body renderer.");
            return best;
        }

        private static bool IsExcluded(Transform transform, Transform excludedRoot)
        {
            return transform != null && excludedRoot != null && transform.IsChildOf(excludedRoot);
        }

        // ------------------------------------------------------------------
        // Scale & alignment
        // ------------------------------------------------------------------

        private static void ScaleAndAlign(NormalizedStage stage, ReFitReport report)
        {
            var sourceBounds = BakedWorldBounds(stage.sourceBody);
            var targetBounds = BakedWorldBounds(stage.targetBody);
            var measure = SelectScaleMeasure(stage, sourceBounds, targetBounds, report);

            if (measure.valid)
            {
                stage.appliedScale = measure.target / measure.source;
                if (Mathf.Abs(stage.appliedScale - 1f) > 1e-3f)
                {
                    stage.sourceRoot.transform.localScale *= stage.appliedScale;
                    // Only scale the asset with the source when the asset belongs to the source's space.
                    // A target-space asset (already fitting the target) keeps its own scale.
                    if (stage.assetStageRoot != null && !stage.assetInTargetSpace)
                        stage.assetStageRoot.localScale *= stage.appliedScale;
                    report.Info("scale-matched",
                        $"Scaled the source side by x{stage.appliedScale:0.###} to match the target size (measured from {measure.label}).");
                }
            }
            else
            {
                report.Warn("scale-unmeasured", "Could not measure either avatar; skipping scale matching.");
            }

            // 2) Alignment: align the source side by avatar root/pivot, not by body-surface center. The surface
            // center can be an intentional shape difference that the primary refit must preserve.
            var rootOffset = stage.targetRoot.transform.position - stage.sourceRoot.transform.position;
            if (rootOffset.sqrMagnitude > 1e-12f)
            {
                stage.sourceRoot.transform.position += rootOffset;
                if (stage.assetStageRoot != null && !stage.assetInTargetSpace)
                    stage.assetStageRoot.position += rootOffset;
            }
        }

        private static ScaleMeasure SelectScaleMeasure(
            NormalizedStage stage,
            Bounds? sourceBounds,
            Bounds? targetBounds,
            ReFitReport report)
        {
            var bounds = MakeScaleMeasure(
                sourceBounds.HasValue ? sourceBounds.Value.size.y : -1f,
                targetBounds.HasValue ? targetBounds.Value.size.y : -1f,
                "body mesh bounds");
            var torso = MakeScaleMeasure(
                MeasureTorso(stage.sourceHumanMap),
                MeasureTorso(stage.targetHumanMap),
                "hips/head landmarks");
            var armspan = MakeScaleMeasure(
                MeasureArmspan(stage.sourceHumanMap),
                MeasureArmspan(stage.targetHumanMap),
                "hand-span landmarks");

            if (bounds.valid)
            {
                WarnAboutRejectedScaleOutlier(bounds, torso, report);
                WarnAboutRejectedScaleOutlier(bounds, armspan, report);
                return bounds;
            }

            if (torso.valid)
            {
                WarnAboutRejectedScaleOutlier(torso, armspan, report);
                return torso;
            }

            if (armspan.valid)
            {
                report.Warn("scale-armspan-authored-pose",
                    "Using hand-span scale as a last resort in the authored pose. " +
                    "This can be wrong when one rig is in A-pose and the other is in T-pose.");
                return armspan;
            }

            return new ScaleMeasure();
        }

        private static ScaleMeasure MakeScaleMeasure(float source, float target, string label)
        {
            return new ScaleMeasure
            {
                valid = source > 1e-5f && target > 1e-5f,
                source = source,
                target = target,
                label = label
            };
        }

        private static void WarnAboutRejectedScaleOutlier(ScaleMeasure selected, ScaleMeasure rejected, ReFitReport report)
        {
            if (!selected.valid || !rejected.valid) return;

            float selectedScale = selected.target / selected.source;
            float rejectedScale = rejected.target / rejected.source;
            float disagreement = Mathf.Abs((rejectedScale / Mathf.Max(selectedScale, 1e-5f)) - 1f);
            if (disagreement <= 0.15f) return;

            report.Info("scale-outlier-ignored",
                $"Ignoring {rejected.label} scale x{rejectedScale:0.###} because {selected.label} scale x{selectedScale:0.###} is the active surface reference.");
        }

        private static float MeasureTorso(Dictionary<HumanBodyBones, Transform> map)
        {
            if (TryGet(map, HumanBodyBones.Hips, out var hips) && TryGet(map, HumanBodyBones.Head, out var head))
                return Vector3.Distance(hips.position, head.position);
            return -1f;
        }

        private static float MeasureArmspan(Dictionary<HumanBodyBones, Transform> map)
        {
            if (TryGet(map, HumanBodyBones.LeftHand, out var lh) && TryGet(map, HumanBodyBones.RightHand, out var rh))
                return Vector3.Distance(lh.position, rh.position);
            return -1f;
        }

        private struct ScaleMeasure
        {
            public bool valid;
            public float source;
            public float target;
            public string label;
        }

        /// <summary>World-space AABB of a skinned renderer in its current pose, baked deterministically.</summary>
        private static Bounds? BakedWorldBounds(SkinnedMeshRenderer smr)
        {
            if (smr == null || smr.sharedMesh == null) return null;
            var snap = MeshSnapshot.Capture(smr, false, ZeroBlendShapeOverrides(smr), null);
            var verts = snap.worldVertices;
            if (verts == null || verts.Length == 0) return null;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < verts.Length; i++)
            {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }

            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        private static Dictionary<int, float> ZeroBlendShapeOverrides(SkinnedMeshRenderer smr)
        {
            var mesh = smr != null ? smr.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0)
                return null;

            var overrides = new Dictionary<int, float>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++)
                overrides[i] = 0f;
            return overrides;
        }

        private static bool TryGet(Dictionary<HumanBodyBones, Transform> map, HumanBodyBones b, out Transform t)
        {
            t = null;
            return map != null && map.TryGetValue(b, out t) && t != null;
        }

        // ------------------------------------------------------------------
        // Asset posing
        // ------------------------------------------------------------------

        private static void PoseAsset(NormalizedStage stage, ReFitReport report)
        {
            if (stage.assetOnSourceAvatar || stage.assetRenderer == null) return; // shares the source armature, already posed

            var assetTransforms = stage.assetStageRoot != null
                ? stage.assetStageRoot.GetComponentsInChildren<Transform>(true)
                : Array.Empty<Transform>();
            stage.assetBoneToSource = HumanoidBoneMapper.MatchBonesByName(assetTransforms, stage.sourceRoot.transform);

            if (stage.assetInTargetSpace)
            {
                // The asset already sits in the target's space; the source body has been scaled/aligned to that
                // same space, so they overlap. Keep the current transforms, but still build the source-bone map
                // above so region filtering and weight projection can classify the clothing vertices.
                report.Info("asset-target-space",
                    $"The asset already fits the target; binding it in place after matching {CountMappedSkinBones(stage)}/{CountSkinBones(stage)} skinned bone(s) to the source.");
                return;
            }

            // Apply parent-first so children read already-updated parents.
            int applied = 0;
            foreach (var t in assetTransforms) // GetComponentsInChildren is depth-first, parents before children
            {
                if (t == stage.assetStageRoot) continue;
                if (stage.assetBoneToSource.TryGetValue(t, out var src) && src != null)
                {
                    t.position = src.position;
                    t.rotation = src.rotation;
                    applied++;
                }
            }

            // How much of the actual skinning skeleton did we match?
            var bones = stage.assetRenderer.bones;
            int boneCount = 0, boneMatched = 0;
            if (bones != null)
            {
                foreach (var b in bones)
                {
                    if (b == null) continue;
                    boneCount++;
                    if (stage.assetBoneToSource.TryGetValue(b, out var m) && m != null) boneMatched++;
                }
            }

            if (boneCount > 0 && boneMatched == 0)
            {
                report.Warn("armature-match-none",
                    "No asset bone matched the source avatar's skeleton by name. The asset is assumed to already overlap the source body in world space.");
            }
            else if (boneCount > 0 && boneMatched < boneCount / 2)
            {
                report.Warn("armature-match-weak",
                    $"Only {boneMatched}/{boneCount} asset bones matched the source skeleton by name. The fit may be unreliable.");
            }
            else if (applied > 0)
            {
                report.Info("armature-matched", $"Posed the asset onto the source avatar ({boneMatched}/{boneCount} skinned bones matched).");
            }
        }

        private static int CountSkinBones(NormalizedStage stage)
        {
            int count = 0;
            var bones = stage?.assetRenderer != null ? stage.assetRenderer.bones : null;
            if (bones == null) return 0;
            foreach (var bone in bones)
                if (bone != null)
                    count++;
            return count;
        }

        private static int CountMappedSkinBones(NormalizedStage stage)
        {
            int count = 0;
            var bones = stage?.assetRenderer != null ? stage.assetRenderer.bones : null;
            if (bones == null || stage.assetBoneToSource == null) return 0;
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                if (stage.assetBoneToSource.TryGetValue(bone, out var source) && source != null)
                    count++;
            }
            return count;
        }
    }
}
