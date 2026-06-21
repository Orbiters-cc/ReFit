using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Settings for the optional post-refit garment relaxation pass.</summary>
    public class ReFitGravitySettings
    {
        public float maxBodyDistance = 0.35f;
        public float minimumClearance = 0.045f;
        public float anchorForwardDelta = 0.025f;
        public float propagationReach = 0.55f;
        public float propagationStrength = 0.7f;
        public float maxExtraPush = 0.045f;
        public float bakedStrengthMultiplier = 3f;
        public int propagationIterations = 48;
        public int scalarSmoothingIterations = 3;
        public float scalarSmoothingStrength = 0.35f;
        public float minimumMeaningfulDelta = 0.003f;
        public bool filterByBodyRegion = true;
        public bool filterByNormal = true;
        public float maxNormalAngle = 88f;
        public float frontDotStart = 0.08f;
        public float frontDotFull = 0.35f;
        public float centerHalfWidthStart = 0.45f;
        public float centerHalfWidthEnd = 0.85f;
        public float effectHeightStart = 0.08f;
        public float effectHeightFull = 0.22f;
        public float effectHeightFadeStart = 0.5f;
        public float effectHeightEnd = 0.66f;
        public float anchorHeightStart = 0.55f;
        public float anchorHeightEnd = 0.9f;
    }

    /// <summary>Result of the body-clothing detector used before offering the gravity preview.</summary>
    public class ReFitGravityCandidate
    {
        public bool isCandidate;
        public float score;
        public string[] reasons;
    }

    /// <summary>One additive gravity blendshape frame, paired with the shape it was derived from.</summary>
    public class ReFitGravityFrame
    {
        public string sourceShapeName;
        public string gravityShapeName;
        public Vector3[] localDeltas;
        public float maxDelta;
        public int affectedVertices;
    }

    /// <summary>Preview data for the optional post-refit garment relaxation pass.</summary>
    public class ReFitGravityPreview
    {
        public ReFitGravityCandidate candidate;
        public ReFitGravityFrame[] frames;
        public Vector3[] previewLocalVertices;
        public Matrix4x4[] previewSkinMatrices;
        public int[] triangles;
        public string bodyRendererName;
    }

    /// <summary>
    /// Computes an optional additive "gravity" blendshape that lets broad clothing keep clearance below
    /// strong upper-body expansion. This is an editor-time bake helper; it adds no runtime physics components.
    /// </summary>
    public static class ReFitGravityRelaxation
    {
        private static readonly string[] ClothingTokens =
        {
            "hoodie", "shirt", "tshirt", "tee", "sweater", "jacket", "coat", "top",
            "tank", "vest", "dress", "robe", "tunic", "cloth", "clothing", "outfit",
            "pants", "shorts", "skirt", "suit", "uniform", "bodyclothing"
        };

        private static readonly string[] ExclusionTokens =
        {
            "body", "skin", "face", "head", "hair", "fur", "tail", "ear", "horn",
            "eye", "teeth", "tongue", "prop", "weapon", "glasses", "collar", "ring",
            "bracelet", "necklace", "shoe", "boot", "sock", "glove"
        };

        public static ReFitGravitySettings DefaultSettings => new ReFitGravitySettings();

        public static ReFitGravityCandidate DetectCandidate(SkinnedMeshRenderer renderer, GameObject targetAvatar)
        {
            var reasons = new List<string>();
            var candidate = new ReFitGravityCandidate { reasons = new string[0] };
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (renderer == null || mesh == null)
                return candidate;

            float score = 0f;
            string searchable = SearchableText(renderer);
            bool hasClothingToken = ContainsAny(searchable, ClothingTokens);
            if (ContainsAny(searchable, ExclusionTokens) && !hasClothingToken)
            {
                candidate.score = -1f;
                candidate.reasons = new[] { "excluded by name/material token" };
                return candidate;
            }

            if (hasClothingToken)
            {
                score += 0.35f;
                reasons.Add("clothing-like name or material");
            }

            var bounds = renderer.localBounds;
            if (bounds.size.y >= 0.35f && bounds.size.x >= 0.25f)
            {
                score += 0.2f;
                reasons.Add("broad body coverage");
            }

            if (mesh.vertexCount >= 24)
            {
                score += 0.1f;
                reasons.Add("mesh has enough surface area");
            }

            var regionCoverage = RegionCoverage(renderer, targetAvatar);
            if (regionCoverage.torsoArmShare >= 0.55f)
            {
                score += 0.25f;
                reasons.Add("skin weights mostly follow torso/arms");
            }
            if (regionCoverage.legHeadShare > 0.65f)
            {
                score -= 0.35f;
                reasons.Add("mostly non-torso regions");
            }

            if (mesh.blendShapeCount > 0)
            {
                score += 0.1f;
                reasons.Add("has generated blendshapes to pair with gravity");
            }

            candidate.score = score;
            candidate.isCandidate = score >= 0.45f;
            candidate.reasons = reasons.ToArray();
            return candidate;
        }

        public static ReFitGravityPreview GeneratePreview(
            SkinnedMeshRenderer renderer,
            SkinnedMeshRenderer bodyRenderer,
            GameObject targetAvatar,
            IList<string> sourceShapeNames,
            ReFitGravitySettings settings,
            ReFitReport report = null)
        {
            settings = settings ?? DefaultSettings;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (renderer == null || mesh == null || bodyRenderer == null || bodyRenderer.sharedMesh == null)
                return null;

            var candidate = DetectCandidate(renderer, targetAvatar);
            if (!candidate.isCandidate)
                return new ReFitGravityPreview { candidate = candidate, frames = new ReFitGravityFrame[0] };

            var names = BuildSourceShapeList(mesh, sourceShapeNames);
            if (names.Count == 0)
                return new ReFitGravityPreview { candidate = candidate, frames = new ReFitGravityFrame[0] };

            var body = MeshSnapshot.Capture(bodyRenderer, false, null, report);
            var bodyBvh = SurfaceBvh.Build(body);
            var bodyVertexRegions = BuildBodyVertexRegions(body, targetAvatar);
            var bodyTriRegions = BuildBodyTriangleRegions(body, bodyVertexRegions);
            var frames = new List<ReFitGravityFrame>();
            MeshSnapshot previewSnapshot = null;

            for (int i = 0; i < names.Count; i++)
            {
                var baseOverrides = BuildShapeOverrides(mesh, renderer, names[i], 0f);
                var activeOverrides = BuildShapeOverrides(mesh, renderer, names[i], 1f);
                var baseAsset = MeshSnapshot.Capture(renderer, true, baseOverrides, report);
                var asset = MeshSnapshot.Capture(renderer, true, activeOverrides, report);
                if (previewSnapshot == null)
                    previewSnapshot = asset;

                var frame = ComputeFrame(names[i], baseAsset, asset, body, bodyBvh, bodyVertexRegions, bodyTriRegions, targetAvatar, settings);
                if (frame != null && frame.maxDelta >= settings.minimumMeaningfulDelta && frame.affectedVertices > 0)
                    frames.Add(frame);
            }

            if (previewSnapshot == null)
                previewSnapshot = MeshSnapshot.Capture(renderer, true, null, report);

            return new ReFitGravityPreview
            {
                candidate = candidate,
                frames = frames.ToArray(),
                previewLocalVertices = previewSnapshot.localVertices,
                previewSkinMatrices = previewSnapshot.skinMatrices,
                triangles = previewSnapshot.triangles,
                bodyRendererName = bodyRenderer.name
            };
        }

        private static ReFitGravityFrame ComputeFrame(
            string sourceShapeName,
            MeshSnapshot baseAsset,
            MeshSnapshot asset,
            MeshSnapshot body,
            SurfaceBvh bodyBvh,
            BodyRegion[] bodyVertexRegions,
            BodyRegion[] bodyTriRegions,
            GameObject targetAvatar,
            ReFitGravitySettings settings)
        {
            int groupCount = asset.GroupCount;
            if (groupCount == 0)
                return null;

            var assetRegions = BuildAssetGroupRegions(asset, targetAvatar);
            var bindings = new SurfaceBinding[groupCount];
            var clearances = new float[groupCount];
            var heights = new float[groupCount];
            var effectMask = new float[groupCount];
            var eligible = new bool[groupCount];
            var desired = new float[groupCount];
            var up = targetAvatar != null ? targetAvatar.transform.up : Vector3.up;
            var forward = StableForwardDirection(baseAsset, asset, targetAvatar, up);
            var right = StableRightDirection(targetAvatar, up, forward);
            float cosMax = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
            var heightRange = GroupHeightRange(asset, up);
            var bodyBounds = BoundsOf(body.worldVertices);
            var torsoBounds = BodyTorsoBounds(body, bodyVertexRegions, bodyBounds);
            var bodyCenter = torsoBounds.center;
            float bodyHalfWidth = BodyTorsoHalfExtent(body, bodyCenter, right, bodyVertexRegions);
            float bodyFrontDepth = BodyTorsoForwardDepth(body, bodyCenter, forward, bodyVertexRegions);

            Parallel.For(0, groupCount, g =>
            {
                int vertex = asset.groupRep[g];
                var region = assetRegions != null ? assetRegions[g] : BodyRegion.Unknown;
                var binding = SurfaceBindingSolver.BindPoint(
                    asset.worldVertices[vertex],
                    body,
                    bodyBvh,
                    settings.maxBodyDistance,
                    region,
                    settings.filterByBodyRegion ? bodyTriRegions : null,
                    asset.worldNormals[vertex],
                    cosMax,
                    settings.filterByNormal);

                bindings[g] = binding;
                heights[g] = Vector3.Dot(asset.worldVertices[vertex], up);
                if (!binding.valid || !CanRelaxRegion(region, binding.hitRegion))
                    return;

                float height01 = NormalizeHeight(heights[g], heightRange);
                float frontWeight = FrontWeight(asset.worldVertices[vertex], bodyCenter, forward, bodyFrontDepth, settings);
                float centerWeight = CenterWeight(asset.worldVertices[vertex], bodyCenter, right, bodyHalfWidth, settings);
                float spatialMask = frontWeight * centerWeight;
                float heightWeight = EffectHeightWeight(height01, settings);
                float anchorWeight = AnchorHeightWeight(height01, settings);
                if (spatialMask <= 0.0001f || (heightWeight <= 0.0001f && anchorWeight <= 0.0001f))
                    return;

                clearances[g] = Vector3.Dot(asset.worldVertices[vertex] - binding.point, forward);
                effectMask[g] = spatialMask * heightWeight;
                eligible[g] = true;

                float activeForwardDelta = Vector3.Dot(asset.worldVertices[vertex] - baseAsset.worldVertices[vertex], forward);
                float anchorMask = anchorWeight * spatialMask;
                if (activeForwardDelta >= settings.anchorForwardDelta && anchorMask > 0.0001f)
                    desired[g] = activeForwardDelta * anchorMask;
            });

            PropagateDesiredClearance(asset, eligible, heights, desired, settings);
            SmoothScalar(desired, eligible, asset.groupAdjacency, settings.scalarSmoothingIterations, settings.scalarSmoothingStrength);

            var amounts = new float[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                if (!eligible[g] || desired[g] <= 0f || effectMask[g] <= 0f)
                    continue;

                float targetClearance = Mathf.Max(settings.minimumClearance, desired[g] * settings.propagationStrength);
                float extra = Mathf.Clamp(targetClearance - clearances[g], 0f, settings.maxExtraPush);
                if (extra <= 0.0005f)
                    continue;
                amounts[g] = extra * effectMask[g] * Mathf.Max(0f, settings.bakedStrengthMultiplier);
            }
            var amountEligible = new bool[groupCount];
            for (int g = 0; g < groupCount; g++)
                amountEligible[g] = eligible[g] && effectMask[g] > 0.0001f;
            SmoothScalar(amounts, amountEligible, asset.groupAdjacency, settings.scalarSmoothingIterations, settings.scalarSmoothingStrength);

            var groupWorldDeltas = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++)
                if (amounts[g] > 0.0005f)
                    groupWorldDeltas[g] = forward * amounts[g];

            return BuildFrame(sourceShapeName, asset, groupWorldDeltas);
        }

        private static void PropagateDesiredClearance(
            MeshSnapshot asset,
            bool[] eligible,
            float[] heights,
            float[] desired,
            ReFitGravitySettings settings)
        {
            var next = new float[desired.Length];
            for (int it = 0; it < settings.propagationIterations; it++)
            {
                bool changed = false;
                Array.Copy(desired, next, desired.Length);
                for (int g = 0; g < desired.Length; g++)
                {
                    if (!eligible[g])
                        continue;

                    float best = next[g];
                    var p = asset.worldVertices[asset.groupRep[g]];
                    foreach (var nb in asset.groupAdjacency[g])
                    {
                        if (!eligible[nb] || desired[nb] <= 0f)
                            continue;

                        float verticalDelta = heights[nb] - heights[g];
                        if (verticalDelta < -0.04f)
                            continue;

                        var q = asset.worldVertices[asset.groupRep[nb]];
                        float edge = Vector3.Distance(p, q);
                        float verticalBias = verticalDelta >= 0f ? 1f : 0.72f;
                        float candidate = desired[nb] * Mathf.Exp(-edge / Mathf.Max(settings.propagationReach, 0.001f)) * verticalBias;
                        if (candidate > best + 0.0001f)
                            best = candidate;
                    }

                    if (best > next[g] + 0.0001f)
                    {
                        next[g] = best;
                        changed = true;
                    }
                }

                Array.Copy(next, desired, desired.Length);
                if (!changed)
                    break;
            }
        }

        private static void SmoothScalar(float[] values, bool[] eligible, List<int>[] adjacency, int iterations, float strength)
        {
            if (values == null || eligible == null || adjacency == null || iterations <= 0 || strength <= 0f)
                return;

            var buffer = new float[values.Length];
            for (int it = 0; it < iterations; it++)
            {
                for (int g = 0; g < values.Length; g++)
                {
                    if (!eligible[g])
                    {
                        buffer[g] = 0f;
                        continue;
                    }

                    float sum = values[g];
                    int count = 1;
                    foreach (var nb in adjacency[g])
                    {
                        if (!eligible[nb])
                            continue;
                        sum += values[nb];
                        count++;
                    }

                    float avg = sum / Mathf.Max(1, count);
                    buffer[g] = Mathf.Lerp(values[g], avg, strength);
                }

                Array.Copy(buffer, values, values.Length);
            }
        }

        private static ReFitGravityFrame BuildFrame(string sourceShapeName, MeshSnapshot asset, Vector3[] groupWorldDeltas)
        {
            int vertexCount = asset.localVertices.Length;
            var localDeltas = new Vector3[vertexCount];
            float maxDelta = 0f;
            int affected = 0;

            Parallel.For(0, vertexCount, i =>
            {
                var worldDelta = groupWorldDeltas[asset.groupOfVertex[i]];
                if (worldDelta.sqrMagnitude <= 1e-12f)
                    return;

                Matrix4x4 inverse = asset.rigid ? asset.rendererWorldToLocal : asset.skinMatrices[i].inverse;
                localDeltas[i] = inverse.MultiplyVector(worldDelta);
            });

            for (int i = 0; i < localDeltas.Length; i++)
            {
                float magnitude = localDeltas[i].magnitude;
                if (magnitude <= 0.0001f)
                    continue;
                affected++;
                if (magnitude > maxDelta)
                    maxDelta = magnitude;
            }

            return new ReFitGravityFrame
            {
                sourceShapeName = sourceShapeName,
                gravityShapeName = sourceShapeName + "-gravity",
                localDeltas = localDeltas,
                maxDelta = maxDelta,
                affectedVertices = affected
            };
        }

        private static Dictionary<int, float> BuildShapeOverrides(Mesh mesh, SkinnedMeshRenderer renderer, string activeShapeName, float activeWeight01)
        {
            var overrides = new Dictionary<int, float>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float weight = renderer.GetBlendShapeWeight(i) / 100f;
                if (name.EndsWith("-gravity", StringComparison.OrdinalIgnoreCase))
                    weight = 0f;
                if (name == activeShapeName)
                    weight = Mathf.Clamp01(activeWeight01);
                overrides[i] = weight;
            }
            return overrides;
        }

        private static List<string> BuildSourceShapeList(Mesh mesh, IList<string> requested)
        {
            var names = new List<string>();
            if (requested != null)
            {
                for (int i = 0; i < requested.Count; i++)
                {
                    var name = requested[i];
                    if (string.IsNullOrEmpty(name) || name.EndsWith("-gravity", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (mesh.GetBlendShapeIndex(name) >= 0 && !names.Contains(name))
                        names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    var name = mesh.GetBlendShapeName(i);
                    if (!string.IsNullOrEmpty(name) && !name.EndsWith("-gravity", StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }

            return names;
        }

        private static BodyRegion[] BuildAssetGroupRegions(MeshSnapshot asset, GameObject targetAvatar)
        {
            var perBone = BuildBoneRegions(asset.bones, targetAvatar);
            var perVertex = VertexRegions(asset, perBone);
            var perGroup = new BodyRegion[asset.GroupCount];
            for (int g = 0; g < perGroup.Length; g++)
                perGroup[g] = perVertex[asset.groupRep[g]];
            return perGroup;
        }

        private static BodyRegion[] BuildBodyVertexRegions(MeshSnapshot body, GameObject targetAvatar)
        {
            var perBone = BuildBoneRegions(body.bones, targetAvatar);
            return VertexRegions(body, perBone);
        }

        private static BodyRegion[] BuildBodyTriangleRegions(MeshSnapshot body, BodyRegion[] perVertex)
        {
            int triCount = body.triangles.Length / 3;
            var perTri = new BodyRegion[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var a = perVertex[body.triangles[t * 3]];
                var b = perVertex[body.triangles[t * 3 + 1]];
                var c = perVertex[body.triangles[t * 3 + 2]];
                perTri[t] = a == b || a == c ? a : (b == c ? b : a);
            }
            return perTri;
        }

        private static BodyRegion[] BuildBoneRegions(Transform[] bones, GameObject targetAvatar)
        {
            var result = new BodyRegion[bones != null ? bones.Length : 0];
            for (int i = 0; i < result.Length; i++)
                result[i] = BodyRegion.Unknown;

            Dictionary<Transform, BodyRegion> targetRegions = null;
            if (targetAvatar != null)
                targetRegions = HumanoidBoneMapper.ClassifyBones(
                    targetAvatar,
                    HumanoidBoneMapper.GetHumanoidMap(targetAvatar, null));

            for (int i = 0; i < result.Length; i++)
            {
                var bone = bones[i];
                if (bone == null)
                    continue;

                if (targetRegions != null && targetRegions.TryGetValue(bone, out var region))
                {
                    result[i] = region;
                    continue;
                }

                if (HumanoidBoneMapper.TryInferHumanoidBone(bone, out var human))
                    result[i] = HumanoidBoneMapper.RegionOf(human);
            }
            return result;
        }

        private static BodyRegion[] VertexRegions(MeshSnapshot snapshot, BodyRegion[] perBone)
        {
            int count = snapshot.localVertices.Length;
            var regions = new BodyRegion[count];
            bool hasWeights = !snapshot.rigid && snapshot.boneWeights != null && snapshot.boneWeights.Length == count;
            for (int i = 0; i < count; i++)
            {
                regions[i] = BodyRegion.Unknown;
                if (!hasWeights)
                    continue;

                var bw = snapshot.boneWeights[i];
                float best = 0f;
                PickRegion(bw.boneIndex0, bw.weight0, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex1, bw.weight1, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex2, bw.weight2, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex3, bw.weight3, perBone, ref best, ref regions[i]);
            }
            return regions;
        }

        private static void PickRegion(int boneIndex, float weight, BodyRegion[] perBone, ref float best, ref BodyRegion region)
        {
            if (weight <= best || boneIndex < 0 || boneIndex >= perBone.Length)
                return;
            if (perBone[boneIndex] == BodyRegion.Unknown)
                return;
            best = weight;
            region = perBone[boneIndex];
        }

        private static bool CanRelaxRegion(BodyRegion assetRegion, BodyRegion hitRegion)
        {
            bool assetTorso = assetRegion == BodyRegion.Torso || assetRegion == BodyRegion.Unknown;
            bool hitTorso = hitRegion == BodyRegion.Torso || hitRegion == BodyRegion.Unknown;
            bool excluded =
                assetRegion == BodyRegion.Head || assetRegion == BodyRegion.LeftArm ||
                assetRegion == BodyRegion.RightArm || assetRegion == BodyRegion.LeftLeg ||
                assetRegion == BodyRegion.RightLeg || hitRegion == BodyRegion.Head ||
                hitRegion == BodyRegion.LeftArm || hitRegion == BodyRegion.RightArm ||
                hitRegion == BodyRegion.LeftLeg || hitRegion == BodyRegion.RightLeg;
            return assetTorso && hitTorso && !excluded;
        }

        private static (float min, float max) GroupHeightRange(MeshSnapshot asset, Vector3 up)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            for (int i = 0; i < asset.groupRep.Length; i++)
            {
                float h = Vector3.Dot(asset.worldVertices[asset.groupRep[i]], up);
                if (h < min) min = h;
                if (h > max) max = h;
            }
            if (float.IsNaN(min) || float.IsInfinity(min) || float.IsNaN(max) || float.IsInfinity(max))
                return (0f, 0f);
            return (min, max);
        }

        private static float NormalizeHeight(float height, (float min, float max) range)
        {
            return range.max <= range.min + 1e-5f ? 0.5f : Mathf.Clamp01((height - range.min) / (range.max - range.min));
        }

        private static float EffectHeightWeight(float height01, ReFitGravitySettings settings)
        {
            float rise = Smooth01(settings.effectHeightStart, settings.effectHeightFull, height01);
            float fall = 1f - Smooth01(settings.effectHeightFadeStart, settings.effectHeightEnd, height01);
            return Mathf.Clamp01(rise * fall);
        }

        private static float AnchorHeightWeight(float height01, ReFitGravitySettings settings)
        {
            float rise = Smooth01(settings.anchorHeightStart, settings.anchorHeightStart + 0.08f, height01);
            float fall = 1f - Smooth01(settings.anchorHeightEnd - 0.08f, settings.anchorHeightEnd, height01);
            return Mathf.Clamp01(rise * fall);
        }

        private static float FrontWeight(Vector3 point, Vector3 center, Vector3 forward, float frontDepth, ReFitGravitySettings settings)
        {
            float depth = Vector3.Dot(point - center, forward);
            float normalized = depth / Mathf.Max(frontDepth, settings.minimumClearance, 0.001f);
            return Smooth01(settings.frontDotStart, settings.frontDotFull, normalized);
        }

        private static float CenterWeight(Vector3 point, Vector3 center, Vector3 right, float halfWidth, ReFitGravitySettings settings)
        {
            float lateral = Mathf.Abs(Vector3.Dot(point - center, right)) / Mathf.Max(halfWidth, 0.001f);
            return 1f - Smooth01(settings.centerHalfWidthStart, settings.centerHalfWidthEnd, lateral);
        }

        private static float Smooth01(float start, float end, float value)
        {
            if (Mathf.Abs(end - start) <= 1e-6f)
                return value >= end ? 1f : 0f;
            float t = Mathf.Clamp01((value - start) / (end - start));
            return t * t * (3f - 2f * t);
        }

        private static Bounds BoundsOf(Vector3[] points)
        {
            if (points == null || points.Length == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++)
                bounds.Encapsulate(points[i]);
            return bounds;
        }

        private static Bounds BodyTorsoBounds(MeshSnapshot body, BodyRegion[] vertexRegions, Bounds fallback)
        {
            if (body == null || body.worldVertices == null ||
                vertexRegions == null || vertexRegions.Length != body.worldVertices.Length)
                return fallback;

            bool hasTorso = false;
            var bounds = new Bounds();
            for (int i = 0; i < body.worldVertices.Length; i++)
            {
                if (vertexRegions[i] != BodyRegion.Torso)
                    continue;

                if (!hasTorso)
                {
                    bounds = new Bounds(body.worldVertices[i], Vector3.zero);
                    hasTorso = true;
                }
                else
                {
                    bounds.Encapsulate(body.worldVertices[i]);
                }
            }

            return hasTorso ? bounds : fallback;
        }

        private static float BodyTorsoHalfExtent(MeshSnapshot body, Vector3 center, Vector3 axis, BodyRegion[] vertexRegions)
        {
            float max = 0f;
            int torsoCount = 0;
            if (vertexRegions != null && vertexRegions.Length == body.worldVertices.Length)
            {
                for (int i = 0; i < body.worldVertices.Length; i++)
                {
                    if (vertexRegions[i] != BodyRegion.Torso)
                        continue;
                    max = Mathf.Max(max, Mathf.Abs(Vector3.Dot(body.worldVertices[i] - center, axis)));
                    torsoCount++;
                }
            }

            if (torsoCount > 0)
                return Mathf.Max(max, 0.001f);

            for (int i = 0; i < body.worldVertices.Length; i++)
                max = Mathf.Max(max, Mathf.Abs(Vector3.Dot(body.worldVertices[i] - center, axis)));
            return Mathf.Max(max, 0.001f);
        }

        private static float BodyTorsoForwardDepth(MeshSnapshot body, Vector3 center, Vector3 forward, BodyRegion[] vertexRegions)
        {
            float max = 0f;
            int torsoCount = 0;
            if (vertexRegions != null && vertexRegions.Length == body.worldVertices.Length)
            {
                for (int i = 0; i < body.worldVertices.Length; i++)
                {
                    if (vertexRegions[i] != BodyRegion.Torso)
                        continue;
                    max = Mathf.Max(max, Vector3.Dot(body.worldVertices[i] - center, forward));
                    torsoCount++;
                }
            }

            if (torsoCount > 0)
                return Mathf.Max(max, 0.001f);

            for (int i = 0; i < body.worldVertices.Length; i++)
                max = Mathf.Max(max, Vector3.Dot(body.worldVertices[i] - center, forward));
            return Mathf.Max(max, 0.001f);
        }

        private static Vector3 StableForwardDirection(MeshSnapshot baseAsset, MeshSnapshot asset, GameObject targetAvatar, Vector3 up)
        {
            var fallback = targetAvatar != null ? targetAvatar.transform.forward : Vector3.forward;
            fallback -= up * Vector3.Dot(fallback, up);
            fallback = fallback.sqrMagnitude > 1e-8f ? fallback.normalized : Vector3.forward;

            if (baseAsset == null || baseAsset.worldVertices == null || asset == null ||
                asset.worldVertices == null || baseAsset.worldVertices.Length != asset.worldVertices.Length)
                return fallback;

            var range = GroupHeightRange(asset, up);
            var sum = Vector3.zero;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                int vertex = asset.groupRep[g];
                float height01 = NormalizeHeight(Vector3.Dot(asset.worldVertices[vertex], up), range);
                if (height01 < 0.5f || height01 > 0.9f)
                    continue;

                var delta = asset.worldVertices[vertex] - baseAsset.worldVertices[vertex];
                delta -= up * Vector3.Dot(delta, up);
                if (delta.sqrMagnitude <= 1e-6f)
                    continue;
                if (Vector3.Dot(delta, fallback) <= 0f)
                    continue;
                sum += delta;
            }

            if (sum.sqrMagnitude <= 1e-8f)
                return fallback;
            sum -= up * Vector3.Dot(sum, up);
            if (sum.sqrMagnitude <= 1e-8f)
                return fallback;
            var forward = sum.normalized;
            return Vector3.Dot(forward, fallback) < 0f ? -forward : forward;
        }

        private static Vector3 StableRightDirection(GameObject targetAvatar, Vector3 up, Vector3 forward)
        {
            var right = targetAvatar != null ? targetAvatar.transform.right : Vector3.right;
            right -= up * Vector3.Dot(right, up);
            right -= forward * Vector3.Dot(right, forward);
            if (right.sqrMagnitude > 1e-8f)
                return right.normalized;

            right = Vector3.Cross(up, forward);
            return right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
        }

        private static (float torsoArmShare, float legHeadShare) RegionCoverage(SkinnedMeshRenderer renderer, GameObject targetAvatar)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0)
                return (0f, 0f);

            var perBone = BuildBoneRegions(renderer.bones, targetAvatar);
            var weights = mesh.boneWeights;
            if (weights == null || weights.Length != mesh.vertexCount || perBone.Length == 0)
                return (0f, 0f);

            int torsoArm = 0;
            int legHead = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                var region = DominantRegion(weights[i], perBone);
                if (region == BodyRegion.Torso || region == BodyRegion.LeftArm || region == BodyRegion.RightArm)
                    torsoArm++;
                if (region == BodyRegion.LeftLeg || region == BodyRegion.RightLeg || region == BodyRegion.Head)
                    legHead++;
            }

            float count = Mathf.Max(1, weights.Length);
            return (torsoArm / count, legHead / count);
        }

        private static BodyRegion DominantRegion(BoneWeight weight, BodyRegion[] perBone)
        {
            var region = BodyRegion.Unknown;
            float best = 0f;
            PickRegion(weight.boneIndex0, weight.weight0, perBone, ref best, ref region);
            PickRegion(weight.boneIndex1, weight.weight1, perBone, ref best, ref region);
            PickRegion(weight.boneIndex2, weight.weight2, perBone, ref best, ref region);
            PickRegion(weight.boneIndex3, weight.weight3, perBone, ref best, ref region);
            return region;
        }

        private static string SearchableText(SkinnedMeshRenderer renderer)
        {
            var text = renderer.name + " " + renderer.gameObject.name + " ";
            var mesh = renderer.sharedMesh;
            if (mesh != null)
                text += mesh.name + " ";
            var materials = renderer.sharedMaterials;
            if (materials != null)
            {
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null)
                        text += materials[i].name + " ";
            }
            return ReFitUtility.NormalizeName(text);
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            if (string.IsNullOrEmpty(haystack))
                return false;
            for (int i = 0; i < needles.Length; i++)
            {
                var key = ReFitUtility.NormalizeName(needles[i]);
                if (key.Length > 0 && haystack.Contains(key))
                    return true;
            }
            return false;
        }
    }
}
