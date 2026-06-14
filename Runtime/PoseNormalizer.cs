using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// A temporary, hidden copy of the source avatar, target avatar and asset, all driven into the same
    /// neutral humanoid pose, scale-matched and hip-aligned so their surfaces can be compared in world space.
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

    /// <summary>Builds <see cref="NormalizedStage"/> instances: cloning, neutral posing, scale matching and asset armature posing.</summary>
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
                    : HumanoidBoneMapper.GetHumanoidMap(stage.targetRoot, report);

                ApplyNeutralPose(stage.sourceRoot, report);
                if (!stage.sourceIsTarget) ApplyNeutralPose(stage.targetRoot, report);

                if (!stage.sourceIsTarget) ScaleAndAlign(stage, report);

                PoseAsset(stage, report);
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

            // Asset: either part of the source avatar hierarchy, or a standalone hierarchy.
            var carrier = stage.sourceIsTarget ? request.targetAvatar : request.sourceAvatar;
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
                stage.assetInTargetSpace = !stage.sourceIsTarget && request.targetAvatar != null &&
                                           request.assetRenderer.transform.IsChildOf(request.targetAvatar.transform);

                var realAssetRoot = FindCommonRoot(request.assetRenderer);
                stage.realAssetObject = realAssetRoot.gameObject;
                stage.assetRendererPath = ReFitUtility.IndexPath(request.assetRenderer.transform, realAssetRoot);
                var assetClone = Clone(realAssetRoot.gameObject, stage.stagingRoot.transform);
                stage.assetStageRoot = assetClone.transform;
                var t = ReFitUtility.ResolvePath(assetClone.transform, stage.assetRendererPath);
                stage.assetRenderer = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (stage.assetRenderer == null)
                    report.Error("asset-clone-failed", "Could not locate the asset renderer inside the staged asset clone.");
            }

            stage.sourceBody = ResolveBodyRenderer(stage.sourceRoot, request.sourceBodyRenderer,
                stage.sourceIsTarget ? request.targetAvatar : request.sourceAvatar, stage.assetRenderer, report, "source");
            stage.targetBody = stage.sourceIsTarget && request.targetBodyRenderer == null && request.sourceBodyRenderer == null
                ? stage.sourceBody
                : ResolveBodyRenderer(stage.targetRoot, request.targetBodyRenderer, request.targetAvatar, stage.assetRenderer, report, "target");
            if (stage.sourceIsTarget && stage.sourceBody == null) stage.sourceBody = stage.targetBody;
            if (stage.sourceIsTarget && stage.targetBody == null) stage.targetBody = stage.sourceBody;
        }

        private static GameObject Clone(GameObject original, Transform parent)
        {
            var clone = UnityEngine.Object.Instantiate(original);
            clone.name = original.name;
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.transform.SetParent(parent, true);
            clone.SetActive(true);

            // Make the clone inert: nothing should animate or react while we pose it.
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
            {
                try { if (!(behaviour is Transform)) behaviour.enabled = false; }
                catch { /* some behaviours refuse; harmless */ }
            }
            return clone;
        }

        /// <summary>Smallest ancestor of the renderer containing the renderer, its bones and root bone (the "asset root").</summary>
        public static Transform FindCommonRoot(SkinnedMeshRenderer smr)
        {
            var t = smr.transform;
            while (t.parent != null && !ContainsAllBones(t, smr)) t = t.parent;
            return t;
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

        /// <summary>
        /// Picks an avatar's main body renderer: explicit override (resolved into the clone), else a renderer named
        /// "Body", else the skinned renderer with the most vertices (excluding the asset itself).
        /// </summary>
        private static SkinnedMeshRenderer ResolveBodyRenderer(GameObject cloneRoot, SkinnedMeshRenderer overrideRenderer,
            GameObject realRoot, SkinnedMeshRenderer stagedAsset, ReFitReport report, string label)
        {
            if (cloneRoot == null) return null;

            if (overrideRenderer != null && realRoot != null && overrideRenderer.transform.IsChildOf(realRoot.transform))
            {
                var path = ReFitUtility.IndexPath(overrideRenderer.transform, realRoot.transform);
                var t = ReFitUtility.ResolvePath(cloneRoot.transform, path);
                var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr != null) return smr;
                report.Warn("body-override-failed", $"Could not resolve the {label} body override in the staged clone; auto-detecting instead.");
            }

            SkinnedMeshRenderer best = null;
            int bestVerts = -1;
            foreach (var smr in cloneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == stagedAsset || smr.sharedMesh == null) continue;
                if (string.Equals(smr.name, "Body", StringComparison.OrdinalIgnoreCase)) return smr;
                if (smr.sharedMesh.vertexCount > bestVerts) { bestVerts = smr.sharedMesh.vertexCount; best = smr; }
            }
            if (best == null)
                report.Error("no-body-renderer", $"No skinned body renderer found on the {label} avatar.");
            else
                report.Info("body-autodetect", $"Using '{best.name}' as the {label} body renderer.");
            return best;
        }

        // ------------------------------------------------------------------
        // Posing
        // ------------------------------------------------------------------

        /// <summary>Drives a staged avatar into the muscle-neutral humanoid pose, facing identity rotation.</summary>
        private static void ApplyNeutralPose(GameObject cloneRoot, ReFitReport report)
        {
            var animator = HumanoidBoneMapper.FindHumanoidAnimator(cloneRoot);
            if (animator == null)
            {
                report.Warn("no-neutral-pose",
                    $"'{cloneRoot.name}' is not a humanoid avatar; assuming it is already posed consistently with the other model.");
                return;
            }

            try
            {
                var handler = new HumanPoseHandler(animator.avatar, animator.transform);
                var pose = new HumanPose();
                handler.GetHumanPose(ref pose);
                if (pose.muscles != null)
                    for (int i = 0; i < pose.muscles.Length; i++) pose.muscles[i] = 0f;
                pose.bodyRotation = Quaternion.identity;
                handler.SetHumanPose(ref pose);
                handler.Dispose();
            }
            catch (Exception e)
            {
                report.Warn("neutral-pose-failed", $"Could not apply the neutral pose to '{cloneRoot.name}': {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Scale & alignment
        // ------------------------------------------------------------------

        private static void ScaleAndAlign(NormalizedStage stage, ReFitReport report)
        {
            // 1) Scale: prefer humanoid landmarks, fall back to the body meshes' world height so that
            //    non-humanoid rigs (and FBX imported at a different unit scale) are still matched.
            float ms = Measure(stage.sourceHumanMap);
            float mt = Measure(stage.targetHumanMap);
            bool usedBounds = false;
            if (ms <= 1e-5f || mt <= 1e-5f)
            {
                var sb = BakedWorldBounds(stage.sourceBody);
                var tb = BakedWorldBounds(stage.targetBody);
                ms = sb.HasValue ? sb.Value.size.y : -1f;
                mt = tb.HasValue ? tb.Value.size.y : -1f;
                usedBounds = true;
            }

            if (ms > 1e-5f && mt > 1e-5f)
            {
                stage.appliedScale = mt / ms;
                if (Mathf.Abs(stage.appliedScale - 1f) > 1e-3f)
                {
                    stage.sourceRoot.transform.localScale *= stage.appliedScale;
                    // Source-space standalone assets keep their authored pose, but they still need the same
                    // global source-side scale so their current scene fit remains aligned with source A.
                    if (stage.assetStageRoot != null && !stage.assetInTargetSpace)
                        stage.assetStageRoot.localScale *= stage.appliedScale;
                    report.Info("scale-matched",
                        $"Scaled the source side by x{stage.appliedScale:0.###} to match the target size" +
                        (usedBounds ? " (measured from the body meshes)." : "."));
                }
            }
            else
            {
                report.Warn("scale-unmeasured", "Could not measure either avatar; skipping scale matching.");
            }

            // 2) Alignment: prefer hips, fall back to the body meshes' centers (recomputed after scaling).
            if (stage.sourceHumanMap.TryGetValue(HumanBodyBones.Hips, out var srcHips) && srcHips != null &&
                stage.targetHumanMap.TryGetValue(HumanBodyBones.Hips, out var tgtHips) && tgtHips != null)
            {
                var delta = tgtHips.position - srcHips.position;
                stage.sourceRoot.transform.position += delta;
                if (stage.assetStageRoot != null && !stage.assetInTargetSpace)
                    stage.assetStageRoot.position += delta;
            }
            else
            {
                var sb = BakedWorldBounds(stage.sourceBody);
                var tb = BakedWorldBounds(stage.targetBody);
                if (sb.HasValue && tb.HasValue)
                {
                    var delta = tb.Value.center - sb.Value.center;
                    stage.sourceRoot.transform.position += delta;
                    if (stage.assetStageRoot != null && !stage.assetInTargetSpace)
                        stage.assetStageRoot.position += delta;
                }
            }
        }

        /// <summary>Characteristic length usable on both maps: armspan first, hips-to-head second.</summary>
        private static float Measure(Dictionary<HumanBodyBones, Transform> map)
        {
            if (TryGet(map, HumanBodyBones.LeftHand, out var lh) && TryGet(map, HumanBodyBones.RightHand, out var rh))
                return Vector3.Distance(lh.position, rh.position);
            if (TryGet(map, HumanBodyBones.Hips, out var hips) && TryGet(map, HumanBodyBones.Head, out var head))
                return Vector3.Distance(hips.position, head.position);
            return -1f;
        }

        /// <summary>World-space AABB of a skinned renderer in its current pose, baked deterministically.</summary>
        private static Bounds? BakedWorldBounds(SkinnedMeshRenderer smr)
        {
            if (smr == null || smr.sharedMesh == null) return null;
            var snap = MeshSnapshot.Capture(smr, false, null, null);
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
            if (stage.assetStageRoot == null) return;

            var assetTransforms = stage.assetStageRoot.GetComponentsInChildren<Transform>(true);
            stage.assetBoneToSource = HumanoidBoneMapper.MatchBonesByName(assetTransforms, stage.sourceRoot.transform);

            if (stage.assetInTargetSpace)
            {
                // The asset already sits in the target's space; the source body has been scaled/aligned to that
                // same space, so they overlap. Re-posing the asset onto the source would misalign it.
                report.Info("asset-target-space", "The asset already fits the target; binding it in place.");
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
                    $"Only {boneMatched}/{boneCount} asset bones matched the source skeleton by name. The fit may be unreliable; keeping the asset's current scene pose.");
            }
            else if (boneMatched > 0 && !stage.assetInTargetSpace)
            {
                report.Info("armature-mapped",
                    $"Mapped the asset armature to the source avatar ({boneMatched}/{boneCount} skinned bones matched); keeping the asset's current scene pose.");
            }
        }
    }
}
