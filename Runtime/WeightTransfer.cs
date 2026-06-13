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
            ReFitSettings settings, ReFitReport report)
        {
            int vertexCount = asset.localVertices.Length;
            var result = new BoneWeight[vertexCount];

            // Projected weights are identical for every member of a welding group: compute once per group.
            var projected = new BoneWeight[asset.GroupCount];
            var projectedOk = new bool[asset.GroupCount];
            for (int g = 0; g < asset.GroupCount; g++)
                projectedOk[g] = TryProjectGroup(g, body, targetBindings, bodyBoneToNew, out projected[g]);

            int kept = 0, projectedCount = 0, fallback = 0;
            bool hasOriginal = !asset.rigid && asset.boneWeights != null && asset.boneWeights.Length == vertexCount;

            for (int i = 0; i < vertexCount; i++)
            {
                int g = asset.groupOfVertex[i];

                float extraFraction = 0f;
                if (hasOriginal && settings.keepExtraBoneVertices)
                    extraFraction = ExtraFraction(asset.boneWeights[i], assetBoneIsExtra);

                if (hasOriginal && settings.keepExtraBoneVertices && extraFraction >= settings.extraBoneWeightThreshold)
                {
                    if (TryRemapOriginal(asset.boneWeights[i], assetBoneToNew, out result[i])) { kept++; continue; }
                }

                if (projectedOk[g]) { result[i] = projected[g]; projectedCount++; continue; }

                // Last resorts: original weights remapped, then full weight on bone 0.
                if (hasOriginal && TryRemapOriginal(asset.boneWeights[i], assetBoneToNew, out result[i])) { fallback++; continue; }
                result[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                fallback++;
            }

            report?.Info("weights-transferred",
                $"Skin weights: {projectedCount} projected from the target body, {kept} kept on preserved bones, {fallback} fallback.");
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

            // top 4
            var top = new List<KeyValuePair<int, float>>(acc);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            int n = Mathf.Min(4, top.Count);
            float total = 0f;
            for (int k = 0; k < n; k++) total += top[k].Value;
            if (total <= 1e-8f) return false;

            bw = new BoneWeight();
            if (n > 0) { bw.boneIndex0 = top[0].Key; bw.weight0 = top[0].Value / total; }
            if (n > 1) { bw.boneIndex1 = top[1].Key; bw.weight1 = top[1].Value / total; }
            if (n > 2) { bw.boneIndex2 = top[2].Key; bw.weight2 = top[2].Value / total; }
            if (n > 3) { bw.boneIndex3 = top[3].Key; bw.weight3 = top[3].Value / total; }
            return true;
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
            bw = new BoneWeight();
            Span<(int idx, float w)> entries = stackalloc (int, float)[4];
            int n = 0;
            float total = 0f;
            CollectRemapped(original.boneIndex0, original.weight0, assetBoneToNew, entries, ref n, ref total);
            CollectRemapped(original.boneIndex1, original.weight1, assetBoneToNew, entries, ref n, ref total);
            CollectRemapped(original.boneIndex2, original.weight2, assetBoneToNew, entries, ref n, ref total);
            CollectRemapped(original.boneIndex3, original.weight3, assetBoneToNew, entries, ref n, ref total);
            if (n == 0 || total <= 1e-8f) return false;
            if (n > 0) { bw.boneIndex0 = entries[0].idx; bw.weight0 = entries[0].w / total; }
            if (n > 1) { bw.boneIndex1 = entries[1].idx; bw.weight1 = entries[1].w / total; }
            if (n > 2) { bw.boneIndex2 = entries[2].idx; bw.weight2 = entries[2].w / total; }
            if (n > 3) { bw.boneIndex3 = entries[3].idx; bw.weight3 = entries[3].w / total; }
            return true;
        }

        private static void CollectRemapped(int assetBone, float w, int[] assetBoneToNew, Span<(int idx, float w)> entries, ref int n, ref float total)
        {
            if (w <= 0f || assetBone < 0 || assetBone >= assetBoneToNew.Length || n >= 4) return;
            int idx = assetBoneToNew[assetBone];
            if (idx < 0) return;
            entries[n++] = (idx, w);
            total += w;
        }
    }
}
