using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Armature;
using UnityEngine;

namespace Orbiters.ReFit
{
    public partial class ReFitEngine
    {
        // ------------------------------------------------------------------
        // Bone plan (armature replacement) — main thread only
        // ------------------------------------------------------------------

        // The humanoid segments the source skeleton is carried onto the target's by: each joint onto its counterpart, the
        // segment to its child turned onto the target's (never the bones' own axes: exporters roll bones differently).
        private static readonly (HumanBodyBones bone, HumanBodyBones child)[] RetargetSegments =
        {
            (HumanBodyBones.Hips, HumanBodyBones.Spine), (HumanBodyBones.Spine, HumanBodyBones.Chest), (HumanBodyBones.Chest, HumanBodyBones.Neck),
            (HumanBodyBones.UpperChest, HumanBodyBones.Neck), (HumanBodyBones.Neck, HumanBodyBones.Head), (HumanBodyBones.Head, HumanBodyBones.LastBone),
            (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm), (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
            (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand), (HumanBodyBones.LeftHand, HumanBodyBones.LastBone),
            (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm), (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand), (HumanBodyBones.RightHand, HumanBodyBones.LastBone),
            (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
            (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes), (HumanBodyBones.LeftToes, HumanBodyBones.LastBone),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
            (HumanBodyBones.RightFoot, HumanBodyBones.RightToes), (HumanBodyBones.RightToes, HumanBodyBones.LastBone),
        };

        /// <summary>
        /// Per bone of the source body: the transform carrying it onto the target skeleton. A humanoid joint goes onto its
        /// counterpart, turned so its segment lies along the target's and scaled by their length (half to double); a joint
        /// without a child segment turns and scales with its parent; other bones follow their nearest humanoid ancestor.
        /// Identity where the skeletons coincide. Null without humanoid joints in common. Main thread (reads transforms).
        /// </summary>
        private static Matrix4x4[] SkeletonRetarget(NormalizedStage stage, SkinnedMeshRenderer sourceBody)
        {
            if (stage?.sourceHumanMap == null || stage.targetHumanMap == null || sourceBody == null || sourceBody.bones == null) return null;
            var byJoint = new Dictionary<Transform, Matrix4x4>();
            var turnOf = new Dictionary<HumanBodyBones, (Quaternion turn, float scale)>();
            foreach (var (role, childRole) in RetargetSegments)
            {
                if (!stage.sourceHumanMap.TryGetValue(role, out var source) || source == null ||
                    !stage.targetHumanMap.TryGetValue(role, out var target) || target == null) continue;
                var turn = Quaternion.identity;
                float scale = 1f;
                if (childRole != HumanBodyBones.LastBone &&
                    stage.sourceHumanMap.TryGetValue(childRole, out var sourceChild) && sourceChild != null &&
                    stage.targetHumanMap.TryGetValue(childRole, out var targetChild) && targetChild != null)
                {
                    var from = sourceChild.position - source.position;
                    var to = targetChild.position - target.position;
                    if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                    {
                        turn = Quaternion.FromToRotation(from, to);
                        scale = Mathf.Clamp(to.magnitude / from.magnitude, .5f, 2f);
                    }
                }
                else
                {
                    // A hand, a head, toes: as its parent segment.
                    for (var parent = source.parent; parent != null; parent = parent.parent)
                    {
                        HumanBodyBones parentRole = HumanBodyBones.LastBone;
                        foreach (var pair in stage.sourceHumanMap) if (pair.Value == parent) { parentRole = pair.Key; break; }
                        if (parentRole != HumanBodyBones.LastBone && turnOf.TryGetValue(parentRole, out var inherited)) { (turn, scale) = inherited; break; }
                    }
                }
                turnOf[role] = (turn, scale);
                byJoint[source] = Matrix4x4.Translate(target.position) * Matrix4x4.Rotate(turn) * Matrix4x4.Scale(Vector3.one * scale) *
                                  Matrix4x4.Translate(-source.position);
            }
            if (byJoint.Count == 0) return null;
            var bones = sourceBody.bones;
            var result = new Matrix4x4[bones.Length];
            for (int k = 0; k < bones.Length; k++)
            {
                result[k] = Matrix4x4.identity;
                for (var bone = bones[k]; bone != null; bone = bone.parent)
                    if (byJoint.TryGetValue(bone, out var carried)) { result[k] = carried; break; }
            }
            return result;
        }

        // A clothing bone stands on its target bone within this distance and turn.
        private const float SameSkeletonDistance = 0.002f, SameSkeletonDegrees = 2f;

        /// <summary>
        /// The clothing's armature already is the target's skeleton, as the clothing is worn: every bone its mesh is weighted
        /// to stands on the target bone of the same name (or humanoid role), turned the same way, except bones of the clothing
        /// only (skirt or strap chains) under such a bone. Replacing it would copy those bones into a second armature, and
        /// restructure a prefab, or an outfit a linker (VRCFury, Modular Avatar) merges onto the avatar, for nothing: other
        /// parts of the outfit share the armature and the linker links it.
        /// </summary>
        private static bool ArmatureMatchesTarget(NormalizedStage stage, MeshSnapshot asset, out int matched)
        {
            matched = 0;
            if (asset.bones == null || asset.boneWeights == null || stage.targetRoot == null) return false;
            var weighted = new bool[asset.bones.Length];
            foreach (var w in asset.boneWeights)
            {
                if (w.weight0 > 0f && w.boneIndex0 < weighted.Length) weighted[w.boneIndex0] = true;
                if (w.weight1 > 0f && w.boneIndex1 < weighted.Length) weighted[w.boneIndex1] = true;
                if (w.weight2 > 0f && w.boneIndex2 < weighted.Length) weighted[w.boneIndex2] = true;
                if (w.weight3 > 0f && w.boneIndex3 < weighted.Length) weighted[w.boneIndex3] = true;
            }
            var targetRoot = stage.targetRoot.transform;
            var byName = HumanoidBoneMapper.BuildNameIndex(targetRoot, stage.targetExcludedAssetRoot);
            var byRole = HumanoidBoneMapper.BuildHumanoidBoneIndex(targetRoot, stage.targetHumanMap, stage.targetExcludedAssetRoot);
            var counterpart = new Dictionary<Transform, Transform>();
            Transform Counterpart(Transform bone)
            {
                if (bone == null) return null;
                if (counterpart.TryGetValue(bone, out var known)) return known;
                Transform found = null;
                if (byName.TryGetValue(BoneNames.Normalize(bone.name), out var named)) found = named;
                else if (BoneNames.TryInferHumanoid(bone.name, out var role) && HumanoidBoneMapper.TryGetHumanoidEquivalent(byRole, role, out var byHuman))
                    found = byHuman;
                counterpart[bone] = found;
                return found;
            }
            for (int k = 0; k < weighted.Length; k++)
            {
                var bone = asset.bones[k];
                if (!weighted[k] || bone == null) continue;
                var target = Counterpart(bone);
                if (target == null)
                {
                    // A bone of the clothing only: it stays with the clothing under a bone that matches.
                    var parent = bone.parent;
                    while (parent != null && Counterpart(parent) == null) parent = parent.parent;
                    if (parent == null) return false;
                    continue;
                }
                if ((bone.position - target.position).magnitude > SameSkeletonDistance ||
                    Quaternion.Angle(bone.rotation, target.rotation) > SameSkeletonDegrees)
                    return false;
                matched++;
            }
            return matched > 0;
        }

        private static bool BuildBonePlan(NormalizedStage stage, MeshSnapshot asset, MeshSnapshot targetBody,
            MeshSnapshot sourceBody, ReFitComputation comp, Dictionary<Transform, BodyRegion> sourceRegions,
            Dictionary<Transform, BodyRegion> targetRegions, ReFitReport report,
            out Matrix4x4[] bindposes, out int[] bodyBoneToNewOut, out int[] sourceBoneToNewOut, out int[] assetBoneToNewOut,
            out bool[] assetBoneIsExtraOut, out BodyRegion[] newBoneRegionsOut)
        {
            bindposes = null;
            bodyBoneToNewOut = null;
            sourceBoneToNewOut = null;
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

                if (BoneNames.TryInferHumanoid(assetOrSourceBone.name, out var explicitHuman) &&
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
                if (targetNameIndex.TryGetValue(BoneNames.Normalize(assetOrSourceBone.name), out var byName))
                    return byName;
                if (BoneNames.TryInferHumanoid(assetOrSourceBone.name, out var namedHuman) &&
                    HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, namedHuman, out var namedTarget))
                    return namedTarget;

                if (!stage.assetOnSourceAvatar && stage.assetBoneToSource.TryGetValue(assetOrSourceBone, out var mapped) &&
                    mapped != null)
                {
                    if (targetNameIndex.TryGetValue(BoneNames.Normalize(mapped.name), out var mappedByName))
                        return mappedByName;
                    if (sourceHumanOf.TryGetValue(mapped, out var human) &&
                        HumanoidBoneMapper.TryGetHumanoidEquivalent(targetHumanIndex, human, out var tgt))
                        return tgt;
                    if (BoneNames.TryInferHumanoid(mapped.name, out human) &&
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

            bool IsArmatureContainerBone(Transform bone) => bone != null && BoneNames.IsArmatureContainer(bone.name);

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
            // Only bones the clothing is weighted to count: an armature exported whole lists the base's fluff, tail and ear
            // bones too, and the body's weights on them would make the clothing swing with their physics.
            var weighted = new HashSet<int>();
            if (asset.boneWeights != null)
                foreach (var w in asset.boneWeights)
                {
                    if (w.weight0 > 0f) weighted.Add(w.boneIndex0);
                    if (w.weight1 > 0f) weighted.Add(w.boneIndex1);
                    if (w.weight2 > 0f) weighted.Add(w.boneIndex2);
                    if (w.weight3 > 0f) weighted.Add(w.boneIndex3);
                }
            var represented = new Dictionary<Transform, int>();
            foreach (var pair in indexOf)
            {
                bool clothing = false;
                for (int k = 0; k < assetBoneToNew.Length && !clothing; k++)
                    clothing = assetBoneToNew[k] == pair.Value && weighted.Contains(k);
                if (clothing || pair.Value == comp.rootBoneIndex) represented[pair.Key] = pair.Value;
            }
            var bodyBoneToNew = BuildBodyBoneRemap(targetBody.bones, represented, comp.rootBoneIndex);
            // The source body's bones through their target equivalents (by name or humanoid role), so both bodies'
            // weights compare in the new bone list. A source bone without one counts as its nearest resolved ancestor.
            var sourceBones = sourceBody != null && sourceBody.bones != null ? sourceBody.bones : Array.Empty<Transform>();
            var sourceAsTarget = new Transform[sourceBones.Length];
            for (int i = 0; i < sourceBones.Length; i++)
                for (var bone = sourceBones[i]; bone != null && sourceAsTarget[i] == null; bone = bone.parent)
                {
                    sourceAsTarget[i] = ResolveDirect(bone);
                    if (bone == stage.sourceRoot.transform) break;
                }
            var sourceBoneToNew = BuildBodyBoneRemap(sourceAsTarget, represented, comp.rootBoneIndex);

            // 6) bindposes captured in the staged pose, relative to the staged asset renderer
            var rendererL2W = stage.assetRenderer.transform.localToWorldMatrix;
            bindposes = new Matrix4x4[stageBones.Count];
            for (int i = 0; i < stageBones.Count; i++)
                bindposes[i] = stageBones[i].worldToLocalMatrix * rendererL2W;

            comp.bones = refs.ToArray();
            comp.assetBoneToNewBoneIndices = assetBoneToNew;
            comp.keptPlacements = placements.ToArray();
            comp.leafTailHints = BuildLeafTailHints(stageBones, originalAssetBonesByNew, assetBoneSet);
            bodyBoneToNewOut = bodyBoneToNew;
            sourceBoneToNewOut = sourceBoneToNew;
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
            BoneNames.TryInferHumanoid(bone.name, out var boneHuman);

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
            if (BoneNames.TryInferHumanoid(child.name, out var childHuman) &&
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
                if (!BoneNames.TryInferHumanoid(child.name, out var childHuman)) continue;
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
    }
}
