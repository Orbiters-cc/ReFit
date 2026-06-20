using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Projects the target body's skin weights onto the asset through its surface bindings, while letting
    /// vertices that are mostly driven by "extra" bones (skirt/physics bones, props...) keep their original weights.
    /// All indices in the produced weights refer to the new combined bone list built by the engine.
    /// </summary>
    public static class WeightTransfer
    {
        /// <param name="asset">Asset snapshot (original weights / groups).</param>
        /// <param name="body">Target body snapshot (weights to project).</param>
        /// <param name="targetBindings">Per-group binding onto the target body surface.</param>
        /// <param name="bodyBoneToNew">Maps a target body bone index to the new bone list (always valid).</param>
        /// <param name="assetBoneToNew">Maps an asset bone index to the new bone list (-1 when unresolvable).</param>
        /// <param name="assetBoneIsExtra">True for asset bones kept as-is (no target equivalent).</param>
        public static BoneWeight[] Transfer(
            MeshSnapshot asset, MeshSnapshot body, SurfaceBinding[] targetBindings,
            int[] bodyBoneToNew, int[] assetBoneToNew, bool[] assetBoneIsExtra,
            BodyRegion[] newBoneRegions, BodyRegion[] assetGroupRegions,
            ReFitSettings settings, ReFitReport report, out ReFitWeightTransferDebugInfo debug)
        {
            int vertexCount = asset.localVertices.Length;
            var result = new BoneWeight[vertexCount];
            debug = new ReFitWeightTransferDebugInfo
            {
                projectedByGroup = new BoneWeight[asset.GroupCount],
                projectedValidByGroup = new bool[asset.GroupCount],
                originalByVertex = new BoneWeight[vertexCount],
                originalValidByVertex = new bool[vertexCount],
                finalByVertex = result,
                decisionsByVertex = new ReFitWeightDecision[vertexCount]
            };

            // Projected weights are identical for every member of a welding group: compute once per group.
            var projected = new BoneWeight[asset.GroupCount];
            var projectedOk = new bool[asset.GroupCount];
            for (int g = 0; g < asset.GroupCount; g++)
            {
                projectedOk[g] = TryProjectGroup(g, body, targetBindings, bodyBoneToNew, out projected[g]);
                debug.projectedByGroup[g] = projected[g];
                debug.projectedValidByGroup[g] = projectedOk[g];
            }

            int kept = 0, projectedCount = 0, blended = 0, originalCount = 0, rejected = 0, fallback = 0;
            bool hasOriginal = !asset.rigid && asset.boneWeights != null && asset.boneWeights.Length == vertexCount;

            for (int i = 0; i < vertexCount; i++)
            {
                int g = asset.groupOfVertex[i];
                BoneWeight original = default;
                bool originalOk = hasOriginal && TryRemapOriginal(asset.boneWeights[i], assetBoneToNew, out original);
                if (originalOk)
                {
                    debug.originalByVertex[i] = original;
                    debug.originalValidByVertex[i] = true;
                }

                float extraFraction = 0f;
                if (hasOriginal && settings.keepExtraBoneVertices)
                    extraFraction = ExtraFraction(asset.boneWeights[i], assetBoneIsExtra);

                if (hasOriginal && settings.keepExtraBoneVertices && extraFraction >= settings.extraBoneWeightThreshold)
                {
                    if (originalOk)
                    {
                        result[i] = original;
                        debug.decisionsByVertex[i] = ReFitWeightDecision.ExtraPreserved;
                        kept++;
                        continue;
                    }
                }

                if (projectedOk[g] && originalOk)
                {
                    var originalRegion = DominantRegion(original, newBoneRegions);
                    if (originalRegion == BodyRegion.Unknown && assetGroupRegions != null && g < assetGroupRegions.Length)
                        originalRegion = assetGroupRegions[g];
                    var projectedRegion = DominantRegion(projected[g], newBoneRegions);
                    if (projectedRegion == BodyRegion.Unknown)
                        projectedRegion = targetBindings[g].hitRegion;
                    if (WeightRegionsCompatible(originalRegion, projectedRegion))
                    {
                        float share = ProjectionBlendShare(originalRegion, projectedRegion, targetBindings[g]);
                        result[i] = BlendWeights(original, projected[g], 1f - share, share);
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Blended;
                        blended++;
                        continue;
                    }

                    result[i] = original;
                    debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                    rejected++;
                    continue;
                }

                if (projectedOk[g])
                {
                    result[i] = projected[g];
                    debug.decisionsByVertex[i] = ReFitWeightDecision.Projected;
                    projectedCount++;
                    continue;
                }

                // Last resorts: original weights remapped, then full weight on bone 0.
                if (originalOk)
                {
                    result[i] = original;
                    debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                    originalCount++;
                    continue;
                }

                result[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                debug.decisionsByVertex[i] = ReFitWeightDecision.Fallback;
                fallback++;
            }

            report?.Info("weights-transferred",
                $"Skin weights: {projectedCount} projected from the target body, {blended} blended with mapped clothing weights, " +
                $"{rejected} incompatible projections rejected, {originalCount} kept from mapped clothing weights, {kept} kept on preserved bones, {fallback} fallback.");
            return result;
        }

        private static float ExtraFraction(BoneWeight bw, bool[] isExtra)
        {
            float extra = 0f, total = 0f;
            Accumulate(bw.boneIndex0, bw.weight0, isExtra, ref extra, ref total);
            Accumulate(bw.boneIndex1, bw.weight1, isExtra, ref extra, ref total);
            Accumulate(bw.boneIndex2, bw.weight2, isExtra, ref extra, ref total);
            Accumulate(bw.boneIndex3, bw.weight3, isExtra, ref extra, ref total);
            return total > 1e-6f ? extra / total : 0f;
        }

        private static void Accumulate(int index, float w, bool[] isExtra, ref float extra, ref float total)
        {
            if (w <= 0f) return;
            total += w;
            if (index >= 0 && index < isExtra.Length && isExtra[index]) extra += w;
        }

        private static bool TryProjectGroup(int g, MeshSnapshot body, SurfaceBinding[] bindings, int[] bodyBoneToNew, out BoneWeight bw)
        {
            bw = default;
            var binding = bindings[g];
            if (!binding.valid || body.rigid || body.boneWeights == null || body.boneWeights.Length == 0) return false;

            var acc = new Dictionary<int, float>(8);
            int t = binding.triangle * 3;
            AccumulateCorner(acc, body, body.triangles[t], binding.bary.x, bodyBoneToNew);
            AccumulateCorner(acc, body, body.triangles[t + 1], binding.bary.y, bodyBoneToNew);
            AccumulateCorner(acc, body, body.triangles[t + 2], binding.bary.z, bodyBoneToNew);
            if (acc.Count == 0) return false;

            return NormalizeTop4(acc, out bw);
        }

        private static void AccumulateCorner(Dictionary<int, float> acc, MeshSnapshot body, int vertex, float baryWeight, int[] bodyBoneToNew)
        {
            if (baryWeight <= 0f) return;
            var bw = body.boneWeights[vertex];
            AddWeight(acc, bw.boneIndex0, bw.weight0 * baryWeight, bodyBoneToNew);
            AddWeight(acc, bw.boneIndex1, bw.weight1 * baryWeight, bodyBoneToNew);
            AddWeight(acc, bw.boneIndex2, bw.weight2 * baryWeight, bodyBoneToNew);
            AddWeight(acc, bw.boneIndex3, bw.weight3 * baryWeight, bodyBoneToNew);
        }

        private static void AddWeight(Dictionary<int, float> acc, int bodyBone, float w, int[] bodyBoneToNew)
        {
            if (w <= 0f || bodyBone < 0 || bodyBone >= bodyBoneToNew.Length) return;
            int idx = bodyBoneToNew[bodyBone];
            if (idx < 0) return;
            acc.TryGetValue(idx, out var cur);
            acc[idx] = cur + w;
        }

        private static bool TryRemapOriginal(BoneWeight original, int[] assetBoneToNew, out BoneWeight bw)
        {
            var acc = new Dictionary<int, float>(4);
            CollectRemapped(original.boneIndex0, original.weight0, assetBoneToNew, acc);
            CollectRemapped(original.boneIndex1, original.weight1, assetBoneToNew, acc);
            CollectRemapped(original.boneIndex2, original.weight2, assetBoneToNew, acc);
            CollectRemapped(original.boneIndex3, original.weight3, assetBoneToNew, acc);
            return NormalizeTop4(acc, out bw);
        }

        private static void CollectRemapped(int assetBone, float w, int[] assetBoneToNew, Dictionary<int, float> acc)
        {
            if (w <= 0f || assetBone < 0 || assetBone >= assetBoneToNew.Length) return;
            int idx = assetBoneToNew[assetBone];
            if (idx < 0) return;
            acc.TryGetValue(idx, out var current);
            acc[idx] = current + w;
        }

        private static BoneWeight BlendWeights(BoneWeight a, BoneWeight b, float aWeight, float bWeight)
        {
            var acc = new Dictionary<int, float>(8);
            AddRemappedWeight(acc, a.boneIndex0, a.weight0 * aWeight);
            AddRemappedWeight(acc, a.boneIndex1, a.weight1 * aWeight);
            AddRemappedWeight(acc, a.boneIndex2, a.weight2 * aWeight);
            AddRemappedWeight(acc, a.boneIndex3, a.weight3 * aWeight);
            AddRemappedWeight(acc, b.boneIndex0, b.weight0 * bWeight);
            AddRemappedWeight(acc, b.boneIndex1, b.weight1 * bWeight);
            AddRemappedWeight(acc, b.boneIndex2, b.weight2 * bWeight);
            AddRemappedWeight(acc, b.boneIndex3, b.weight3 * bWeight);
            NormalizeTop4(acc, out var bw);
            return bw;
        }

        private static void AddRemappedWeight(Dictionary<int, float> acc, int index, float weight)
        {
            if (weight <= 0f || index < 0) return;
            acc.TryGetValue(index, out var current);
            acc[index] = current + weight;
        }

        private static bool NormalizeTop4(Dictionary<int, float> acc, out BoneWeight bw)
        {
            bw = new BoneWeight();
            if (acc == null || acc.Count == 0) return false;

            var top = new List<KeyValuePair<int, float>>(acc);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            int n = Mathf.Min(4, top.Count);
            float total = 0f;
            for (int k = 0; k < n; k++) total += top[k].Value;
            if (total <= 1e-8f) return false;

            if (n > 0) { bw.boneIndex0 = top[0].Key; bw.weight0 = top[0].Value / total; }
            if (n > 1) { bw.boneIndex1 = top[1].Key; bw.weight1 = top[1].Value / total; }
            if (n > 2) { bw.boneIndex2 = top[2].Key; bw.weight2 = top[2].Value / total; }
            if (n > 3) { bw.boneIndex3 = top[3].Key; bw.weight3 = top[3].Value / total; }
            return true;
        }

        private static BodyRegion DominantRegion(BoneWeight weight, BodyRegion[] boneRegions)
        {
            var region = BodyRegion.Unknown;
            float best = 0f;
            PickRegion(weight.boneIndex0, weight.weight0, boneRegions, ref best, ref region);
            PickRegion(weight.boneIndex1, weight.weight1, boneRegions, ref best, ref region);
            PickRegion(weight.boneIndex2, weight.weight2, boneRegions, ref best, ref region);
            PickRegion(weight.boneIndex3, weight.weight3, boneRegions, ref best, ref region);
            return region;
        }

        private static void PickRegion(int index, float weight, BodyRegion[] boneRegions, ref float best, ref BodyRegion region)
        {
            if (weight <= best || boneRegions == null || index < 0 || index >= boneRegions.Length)
                return;
            if (boneRegions[index] == BodyRegion.Unknown)
                return;
            best = weight;
            region = boneRegions[index];
        }

        private static bool WeightRegionsCompatible(BodyRegion original, BodyRegion projected)
        {
            if (original == BodyRegion.Unknown || projected == BodyRegion.Unknown) return true;
            if (original == projected) return true;
            if (original == BodyRegion.Torso || projected == BodyRegion.Torso) return true;
            if (IsLeg(original) && IsLeg(projected)) return true;
            return false;
        }

        private static float ProjectionBlendShare(BodyRegion original, BodyRegion projected, SurfaceBinding binding)
        {
            float share = original == BodyRegion.Unknown ? 0.55f : 0.25f;
            if (original == BodyRegion.Torso && projected == BodyRegion.Torso)
                share = 0.5f;
            if (binding.usedRelaxedFallback)
                share = Mathf.Min(share, 0.1f);
            return share;
        }

        private static bool IsLeg(BodyRegion region)
        {
            return region == BodyRegion.LeftLeg || region == BodyRegion.RightLeg;
        }
    }
}
