using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Skin weights of a refitted asset on the target armature. The clothing's own weights, mapped onto the target bones,
    /// follow the change of skinning between the two bodies at the points each clothing vertex is matched to: where the
    /// target hands the hip skin over to a thigh, the clothing over it does too, and where both bodies agree the creator's
    /// weights stay exactly as authored. Vertices mostly driven by "extra" bones (skirt/physics bones, props...) keep their
    /// original weights. All indices in the produced weights refer to the new combined bone list built by the engine.
    /// </summary>
    public static class WeightTransfer
    {
        // Light smoothing of the per-group change over the clothing's own surface: neighbors matched to neighboring skin
        // must not tear apart in a pose because their matches fell on either side of a body triangle edge.
        private const int ChangeSmoothingIterations = 2;
        private const float ChangeSmoothingStrength = 0.5f;
        // Weight the change may move onto a limb the clothing vertex does not belong to before the whole change is
        // treated as a wrong match (a left-leg strap matched to right-leg skin).
        private const float IncompatibleChangeTolerance = 0.05f;
        // Smaller changes are rounding; the vertex reports its original weights.
        private const float ChangeEpsilon = 0.01f;
        // Clothing on the skin follows the skin's change fully; clothing standing this far off it (a pouch, a hood, a
        // skirt panel) keeps the weights its creator gave it, so it moves as designed instead of being dragged by the skin.
        private const float SkinContactFull = 0.01f;
        private const float SkinContactNone = 0.04f;

        /// <param name="asset">Asset snapshot (original weights / groups).</param>
        /// <param name="sourceBody">Body the asset was made for, at rest.</param>
        /// <param name="sourceBindings">Per-group match on <paramref name="sourceBody"/>.</param>
        /// <param name="sourceBoneToNew">Maps a source body bone index to the new bone list (-1 when unrepresented).</param>
        /// <param name="targetBody">Target body snapshot.</param>
        /// <param name="targetBindings">Per-group match on the target body: the same anatomical point as the source match.</param>
        /// <param name="targetBoneToNew">Maps a target body bone index to the new bone list.</param>
        /// <param name="groupConfidence">0..1 per group (distance falloff), further reduced for clothing standing off the skin.</param>
        /// <param name="assetBoneToNew">Maps an asset bone index to the new bone list (-1 when unresolvable).</param>
        /// <param name="assetBoneIsExtra">True for asset bones kept as-is (no target equivalent).</param>
        public static BoneWeight[] Transfer(
            MeshSnapshot asset,
            MeshSnapshot sourceBody, SurfaceBinding[] sourceBindings, int[] sourceBoneToNew,
            MeshSnapshot targetBody, SurfaceBinding[] targetBindings, int[] targetBoneToNew,
            float[] groupConfidence, int[] assetBoneToNew, bool[] assetBoneIsExtra,
            BodyRegion[] newBoneRegions, BodyRegion[] assetGroupRegions,
            ReFitSettings settings, ReFitReport report, out ReFitWeightTransferDebugInfo debug)
        {
            int vertexCount = asset.localVertices.Length;
            int groupCount = asset.GroupCount;
            int boneCount = newBoneRegions != null ? newBoneRegions.Length : 0;
            var result = new BoneWeight[vertexCount];
            debug = new ReFitWeightTransferDebugInfo
            {
                projectedByGroup = new BoneWeight[groupCount],
                projectedValidByGroup = new bool[groupCount],
                originalByVertex = new BoneWeight[vertexCount],
                originalValidByVertex = new bool[vertexCount],
                finalByVertex = result,
                decisionsByVertex = new ReFitWeightDecision[vertexCount]
            };

            // The target body's weights at each match: the debug view, and the weights of vertices without mapped ones.
            var projected = new BoneWeight[groupCount];
            var projectedOk = new bool[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                projectedOk[g] = TryProjectGroup(targetBody, targetBindings[g], targetBoneToNew, out projected[g]);
                debug.projectedByGroup[g] = projected[g];
                debug.projectedValidByGroup[g] = projectedOk[g];
            }

            var change = BuildChange(asset, sourceBody, sourceBindings, sourceBoneToNew, targetBody, targetBindings,
                targetBoneToNew, groupConfidence, boneCount, out var hasChange);

            int kept = 0, adjusted = 0, unchanged = 0, rejected = 0, projectedCount = 0, fallback = 0;
            bool hasOriginal = !asset.rigid && asset.boneWeights != null && asset.boneWeights.Length == vertexCount;
            var dense = new float[boneCount];
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

                if (hasOriginal && settings.keepExtraBoneVertices && originalOk &&
                    ExtraFraction(asset.boneWeights[i], assetBoneIsExtra) >= settings.extraBoneWeightThreshold)
                {
                    result[i] = original;
                    debug.decisionsByVertex[i] = ReFitWeightDecision.ExtraPreserved;
                    kept++;
                    continue;
                }

                if (originalOk)
                {
                    if (!hasChange[g])
                    {
                        result[i] = original;
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                        unchanged++;
                        continue;
                    }
                    var region = DominantRegion(original, newBoneRegions);
                    if (region == BodyRegion.Unknown && assetGroupRegions != null && g < assetGroupRegions.Length)
                        region = assetGroupRegions[g];
                    int offset = g * boneCount;
                    float incompatible = 0f, moved = 0f;
                    for (int b = 0; b < boneCount; b++)
                    {
                        float c = change[offset + b];
                        if (c > 0f && !LimbCompatible(region, newBoneRegions[b])) incompatible += c;
                        moved += Mathf.Abs(c);
                    }
                    if (incompatible > IncompatibleChangeTolerance)
                    {
                        // The bodies' change here would move this clothing part onto another limb: a wrong match.
                        result[i] = original;
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                        rejected++;
                        continue;
                    }
                    if (moved * 0.5f < ChangeEpsilon)
                    {
                        result[i] = original;
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                        unchanged++;
                        continue;
                    }
                    Array.Clear(dense, 0, boneCount);
                    AddDense(dense, original.boneIndex0, original.weight0);
                    AddDense(dense, original.boneIndex1, original.weight1);
                    AddDense(dense, original.boneIndex2, original.weight2);
                    AddDense(dense, original.boneIndex3, original.weight3);
                    for (int b = 0; b < boneCount; b++)
                    {
                        float c = change[offset + b];
                        if (c > 0f && !LimbCompatible(region, newBoneRegions[b])) continue;
                        dense[b] = Mathf.Max(0f, dense[b] + c);
                    }
                    if (TopFour(dense, out result[i]))
                    {
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Blended;
                        adjusted++;
                    }
                    else
                    {
                        result[i] = original;
                        debug.decisionsByVertex[i] = ReFitWeightDecision.Original;
                        unchanged++;
                    }
                    continue;
                }

                if (projectedOk[g])
                {
                    result[i] = projected[g];
                    debug.decisionsByVertex[i] = ReFitWeightDecision.Projected;
                    projectedCount++;
                    continue;
                }

                result[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
                debug.decisionsByVertex[i] = ReFitWeightDecision.Fallback;
                fallback++;
            }

            report?.Info("weights-transferred",
                $"Skin weights: {adjusted} follow the bodies' change of skinning, {unchanged} kept from mapped clothing weights where the bodies agree, " +
                $"{rejected} kept because the change would cross onto another limb, {projectedCount} projected from the target body (no mapped clothing weight), " +
                $"{kept} kept on preserved bones, {fallback} fallback.");
            return result;
        }

        /// <summary>
        /// Target minus source skinning (in the new bone list) at each group's matches, scaled by its confidence and
        /// lightly smoothed over the clothing surface. Groups without both matches have no change.
        /// </summary>
        private static float[] BuildChange(MeshSnapshot asset, MeshSnapshot sourceBody, SurfaceBinding[] sourceBindings,
            int[] sourceBoneToNew, MeshSnapshot targetBody, SurfaceBinding[] targetBindings, int[] targetBoneToNew,
            float[] groupConfidence, int boneCount, out bool[] hasChange)
        {
            int groupCount = asset.GroupCount;
            var change = new float[groupCount * boneCount];
            var has = new bool[groupCount];
            bool sourceSkinned = sourceBody != null && !sourceBody.rigid && sourceBody.boneWeights != null && sourceBody.boneWeights.Length > 0;
            bool targetSkinned = targetBody != null && !targetBody.rigid && targetBody.boneWeights != null && targetBody.boneWeights.Length > 0;
            if (boneCount > 0 && sourceSkinned && targetSkinned && sourceBindings != null && targetBindings != null)
                Parallel.For(0, groupCount, g =>
                {
                    float confidence = groupConfidence != null && g < groupConfidence.Length ? groupConfidence[g] : 1f;
                    if (!sourceBindings[g].valid || !targetBindings[g].valid) return;
                    confidence *= 1f - Mathf.Clamp01((sourceBindings[g].distance - SkinContactFull) / (SkinContactNone - SkinContactFull));
                    if (confidence <= 0f) return;
                    int offset = g * boneCount;
                    AccumulateSkin(change, offset, boneCount, targetBody, targetBindings[g], targetBoneToNew, confidence);
                    AccumulateSkin(change, offset, boneCount, sourceBody, sourceBindings[g], sourceBoneToNew, -confidence);
                    has[g] = true;
                });

            if (boneCount > 0)
            {
                var buffer = new float[change.Length];
                for (int iteration = 0; iteration < ChangeSmoothingIterations; iteration++)
                {
                    Parallel.For(0, groupCount, g =>
                    {
                        int offset = g * boneCount;
                        if (!has[g]) return;
                        int neighbors = 0;
                        foreach (int n in asset.groupAdjacency[g]) if (has[n]) neighbors++;
                        for (int b = 0; b < boneCount; b++)
                        {
                            float own = change[offset + b];
                            if (neighbors == 0) { buffer[offset + b] = own; continue; }
                            float sum = 0f;
                            foreach (int n in asset.groupAdjacency[g]) if (has[n]) sum += change[n * boneCount + b];
                            buffer[offset + b] = Mathf.Lerp(own, sum / neighbors, ChangeSmoothingStrength);
                        }
                    });
                    var swap = change; change = buffer; buffer = swap;
                }
            }
            hasChange = has;
            return change;
        }

        private static void AccumulateSkin(float[] change, int offset, int boneCount, MeshSnapshot body, SurfaceBinding binding,
            int[] boneToNew, float scale)
        {
            int t = binding.triangle * 3;
            for (int corner = 0; corner < 3; corner++)
            {
                float bary = corner == 0 ? binding.bary.x : corner == 1 ? binding.bary.y : binding.bary.z;
                if (bary <= 0f) continue;
                var w = body.boneWeights[body.triangles[t + corner]];
                AccumulateBone(change, offset, boneCount, w.boneIndex0, w.weight0 * bary * scale, boneToNew);
                AccumulateBone(change, offset, boneCount, w.boneIndex1, w.weight1 * bary * scale, boneToNew);
                AccumulateBone(change, offset, boneCount, w.boneIndex2, w.weight2 * bary * scale, boneToNew);
                AccumulateBone(change, offset, boneCount, w.boneIndex3, w.weight3 * bary * scale, boneToNew);
            }
        }

        private static void AccumulateBone(float[] change, int offset, int boneCount, int bodyBone, float weight, int[] boneToNew)
        {
            if (weight == 0f || boneToNew == null || bodyBone < 0 || bodyBone >= boneToNew.Length) return;
            int index = boneToNew[bodyBone];
            if (index < 0 || index >= boneCount) return;
            change[offset + index] += weight;
        }

        // Torso cloth may hand weight to any limb it borders (hips to thighs, chest to shoulders); limb cloth only to its
        // own limb or the torso, never to the other side or another limb.
        private static bool LimbCompatible(BodyRegion own, BodyRegion bone)
        {
            if (own == BodyRegion.Unknown || own == BodyRegion.Torso) return true;
            if (bone == BodyRegion.Unknown || bone == BodyRegion.Torso) return true;
            return own == bone;
        }

        private static void AddDense(float[] dense, int index, float weight)
        {
            if (weight <= 0f || index < 0 || index >= dense.Length) return;
            dense[index] += weight;
        }

        private static bool TopFour(float[] dense, out BoneWeight bw)
        {
            var acc = new Dictionary<int, float>(8);
            for (int b = 0; b < dense.Length; b++)
                if (dense[b] > 1e-5f) acc[b] = dense[b];
            return NormalizeTop4(acc, out bw);
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

        private static bool TryProjectGroup(MeshSnapshot body, SurfaceBinding binding, int[] bodyBoneToNew, out BoneWeight bw)
        {
            bw = default;
            if (!binding.valid || body == null || body.rigid || body.boneWeights == null || body.boneWeights.Length == 0) return false;

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

        private static bool NormalizeTop4(Dictionary<int, float> acc, out BoneWeight bw)
        {
            bw = new BoneWeight();
            if (acc == null || acc.Count == 0) return false;

            var top = new List<KeyValuePair<int, float>>(acc);
            // Equal weights keep the lower bone index first, independent of dictionary order.
            top.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : x.Key.CompareTo(y.Key));
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
    }
}
