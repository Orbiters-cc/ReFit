using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Preserves clothing/body signed clearance after ReFit deformations, with adaptive compression on expanded body areas.
    /// This is baked into generated mesh/blendshape deltas and adds no runtime avatar components.
    /// </summary>
    public static class ReFitClearanceCorrection
    {
        private const float SourceAboveEpsilon = 0.0005f;
        private const float MeaningfulCorrection = 0.0001f;
        private const float TrustedBindingNormalDot = 0.35f;

        public sealed class Profile
        {
            public bool[] eligible;
            public float[] sourceClearance;
            public Vector3[] sourceBodyPoint;
            public int eligibleGroups;
        }

        public sealed class Context
        {
            /// <summary>Optional immutable index of the unshaped target body for reuse across transferred shapes.</summary>
            public SurfaceBvh targetBodyIndex;
            public bool transferredBlendshape;
            public BodyRegion[] assetGroupRegions;
            public BodyRegion[] targetTriangleRegions;
            public Vector3[] referenceNormals;
            public bool[] bindingRelaxed;
            public float[] bindingNormalDots;
            public float[] openBoundaryWeights;
            public float[] upperBodyHemWeights;
        }

        private sealed class IslandPropagationResult
        {
            public bool[] propagatedGroups;
            public int propagatedCount;
            public bool HasPropagated => propagatedCount > 0 && propagatedGroups != null;
        }

        private struct IslandSupportSample
        {
            public int receiverGroup;
            public int supportComponent;
            public int supportGroup;
            public Vector3 targetCorrection;
            public float targetMagnitude;
            public float currentMagnitude;
            public float supportDistance;
            public float distanceWeight;
            public float receiverWeight;
            public float Weight => Mathf.Max(0f, distanceWeight * receiverWeight);
        }

        private struct IslandTargetCorrectionField
        {
            public Vector3[] corrections;
            public float[] receiverWeights;
            public int[] supportComponents;
            public int[] supportGroups;
            public float[] supportDistances;
            public float averageWeight;
            public float averageMagnitude;
            public float maxSupportDistance;
            public int dominantSupportComponent;
            public int dominantSupportGroup;
        }

        public static Profile BuildProfile(
            MeshSnapshot asset,
            MeshSnapshot sourceBody,
            SurfaceBinding[] sourceBindings,
            ReFitSettings settings)
        {
            if (asset == null || sourceBody == null || sourceBindings == null || settings == null ||
                !settings.enableClearanceCorrection || asset.GroupCount == 0)
                return null;

            int groupCount = asset.GroupCount;
            var profile = new Profile
            {
                eligible = new bool[groupCount],
                sourceClearance = new float[groupCount],
                sourceBodyPoint = new Vector3[groupCount]
            };

            for (int g = 0; g < groupCount; g++)
            {
                if (g >= sourceBindings.Length || !sourceBindings[g].valid)
                    continue;

                int vertex = asset.groupRep[g];
                var binding = sourceBindings[g];
                var normal = sourceBody.BaryNormal(binding.triangle, binding.bary);
                float clearance = Vector3.Dot(asset.worldVertices[vertex] - binding.point, normal);
                if (clearance <= SourceAboveEpsilon)
                    continue;

                profile.eligible[g] = true;
                profile.sourceClearance[g] = clearance;
                profile.sourceBodyPoint[g] = binding.point;
                profile.eligibleGroups++;
            }

            return profile.eligibleGroups > 0 ? profile : null;
        }

        public static ReFitClearanceCorrectionStats Apply(
            MeshSnapshot asset,
            MeshSnapshot targetBody,
            SurfaceBinding[] targetBindings,
            Profile profile,
            Vector3[] baseGroupDeltas,
            Vector3[] mutableGroupDeltas,
            Vector3[] bodyWorldShapeDeltas,
            float[] falloff,
            ReFitSettings settings,
            Context context)
        {
            var stats = new ReFitClearanceCorrectionStats();
            if (asset == null || targetBody == null || targetBindings == null || profile == null ||
                mutableGroupDeltas == null || settings == null || !settings.enableClearanceCorrection)
                return stats;

            int groupCount = Mathf.Min(asset.GroupCount, Mathf.Min(targetBindings.Length, mutableGroupDeltas.Length));
            if (groupCount == 0)
                return stats;

            var corrections = new Vector3[groupCount];
            var active = new bool[groupCount];
            var signedAmounts = new float[groupCount];
            var penetrations = new float[groupCount];
            var clearanceLosses = new float[groupCount];
            var expansions = new float[groupCount];
            var measured = new bool[groupCount];
            var normals = new Vector3[groupCount];
            var bodyPoints = new Vector3[groupCount];
            var startingGroupDeltas = (Vector3[])mutableGroupDeltas.Clone();

            Parallel.For(0, groupCount, g =>
            {
                if (g >= profile.eligible.Length || !profile.eligible[g] || !targetBindings[g].valid)
                    return;

                int vertex = asset.groupRep[g];
                var binding = targetBindings[g];
                var normal = targetBody.BaryNormal(binding.triangle, binding.bary);
                var bodyShapeDelta = SampleBodyDelta(targetBody, bodyWorldShapeDeltas, binding);
                var bodyPoint = binding.point + bodyShapeDelta;
                var assetPoint = asset.worldVertices[vertex] + DeltaAt(baseGroupDeltas, g) + mutableGroupDeltas[g];

                float currentClearance = Vector3.Dot(assetPoint - bodyPoint, normal);
                float primaryExpansion = Vector3.Dot(binding.point - profile.sourceBodyPoint[g], normal);
                float shapeExpansion = Vector3.Dot(bodyShapeDelta, normal);
                float expansion = Mathf.Max(0f, primaryExpansion + shapeExpansion);
                float desiredClearance = DesiredClearance(profile.sourceClearance[g], expansion, settings);
                float error = desiredClearance - currentClearance;
                float amount;
                measured[g] = true;
                normals[g] = normal;
                bodyPoints[g] = bodyPoint;
                expansions[g] = expansion;

                if (error > MeaningfulCorrection)
                {
                    float correctionScale = DesiredCorrectionScale(settings, context, g);
                    if (correctionScale <= MeaningfulCorrection)
                        return;

                    amount = Mathf.Min(
                        error * Mathf.Clamp01(settings.clearanceOutwardStrength) * correctionScale,
                        Mathf.Max(0f, settings.clearanceMaxOutwardCorrection) * correctionScale);
                }
                else if (error < -MeaningfulCorrection)
                {
                    float inwardScale = InwardScale(settings, context, g);
                    if (inwardScale <= MeaningfulCorrection)
                        return;

                    amount = -Mathf.Min(
                        -error * Mathf.Clamp01(settings.clearanceInwardStrength) * inwardScale,
                        Mathf.Max(0f, settings.clearanceMaxInwardCorrection) * inwardScale);
                }
                else
                {
                    return;
                }

                float weight = falloff != null && g < falloff.Length ? Mathf.Clamp01(falloff[g]) : 1f;
                amount *= weight;
                if (Mathf.Abs(amount) <= MeaningfulCorrection)
                    return;

                corrections[g] = normal * amount;
                signedAmounts[g] = amount;
                active[g] = true;
                if (currentClearance < 0f)
                    penetrations[g] = -currentClearance;
                clearanceLosses[g] = Mathf.Max(0f, profile.sourceClearance[g] - currentClearance);
            });

            SmoothMasked(
                corrections,
                active,
                asset.groupAdjacency,
                settings.clearanceSmoothingIterations,
                settings.clearanceSmoothingStrength);

            for (int g = 0; g < groupCount; g++)
            {
                if (!profile.eligible[g])
                    continue;
                stats.eligibleGroups++;
                stats.maxPenetrationBefore = Mathf.Max(stats.maxPenetrationBefore, penetrations[g]);
                stats.maxClearanceLossBefore = Mathf.Max(stats.maxClearanceLossBefore, clearanceLosses[g]);
                stats.maxExpansion = Mathf.Max(stats.maxExpansion, expansions[g]);
                if (!active[g])
                    continue;

                float amount = signedAmounts[g];
                if (amount > 0f)
                {
                    stats.outwardGroups++;
                    stats.maxOutwardCorrection = Mathf.Max(stats.maxOutwardCorrection, corrections[g].magnitude);
                }
                else
                {
                    stats.inwardGroups++;
                    stats.maxInwardCorrection = Mathf.Max(stats.maxInwardCorrection, corrections[g].magnitude);
                }
                mutableGroupDeltas[g] += corrections[g];
            }

            ApplyPostSmoothSafetyGuard(
                asset,
                profile,
                baseGroupDeltas,
                mutableGroupDeltas,
                measured,
                normals,
                bodyPoints,
                settings,
                context,
                startingGroupDeltas,
                null,
                stats);

            // The deformed body stays immutable throughout this pass and island propagation.
            var bodySurface = BuildBodySurfaceSnapshot(targetBody, bodyWorldShapeDeltas);
            var bodyBvh = bodySurface != null && bodySurface.worldVertices != null &&
                bodySurface.worldVertices.Length > 0 && bodySurface.triangles != null
                ? (context?.targetBodyIndex != null && ReferenceEquals(bodySurface, targetBody)
                    ? context.targetBodyIndex : SurfaceBvh.Build(bodySurface)) : null;
            ApplySurfaceSafetyGuard(
                asset,
                targetBody,
                profile,
                baseGroupDeltas,
                mutableGroupDeltas,
                bodyWorldShapeDeltas,
                falloff,
                expansions,
                settings,
                context,
                startingGroupDeltas,
                null,
                stats, bodySurface, bodyBvh);

            ClampTotalCorrections(startingGroupDeltas, mutableGroupDeltas, settings, context, stats);

            var propagation = ApplyDisconnectedIslandPropagation(
                asset,
                profile,
                baseGroupDeltas,
                startingGroupDeltas,
                mutableGroupDeltas,
                falloff,
                expansions,
                settings,
                context,
                stats);

            if (propagation != null && propagation.HasPropagated)
            {
                ApplyPostSmoothSafetyGuard(
                    asset,
                    profile,
                    baseGroupDeltas,
                    mutableGroupDeltas,
                    measured,
                    normals,
                    bodyPoints,
                    settings,
                    context,
                    startingGroupDeltas,
                    propagation.propagatedGroups,
                    stats);

                ApplySurfaceSafetyGuard(
                    asset,
                    targetBody,
                    profile,
                    baseGroupDeltas,
                    mutableGroupDeltas,
                    bodyWorldShapeDeltas,
                    falloff,
                    expansions,
                    settings,
                    context,
                    startingGroupDeltas,
                    propagation.propagatedGroups,
                    stats, bodySurface, bodyBvh);

                ClampTotalCorrections(startingGroupDeltas, mutableGroupDeltas, settings, context, stats,
                    propagation.propagatedGroups);
            }

            stats.maxPenetrationAfter = MeasureFinalMaxPenetrationAfter(
                asset,
                targetBody,
                profile,
                baseGroupDeltas,
                mutableGroupDeltas,
                bodyWorldShapeDeltas,
                falloff,
                expansions,
                settings,
                context, bodySurface, bodyBvh);

            return stats;
        }

        private static void ApplyPostSmoothSafetyGuard(
            MeshSnapshot asset,
            Profile profile,
            Vector3[] baseGroupDeltas,
            Vector3[] mutableGroupDeltas,
            bool[] measured,
            Vector3[] normals,
            Vector3[] bodyPoints,
            ReFitSettings settings,
            Context context,
            Vector3[] startingGroupDeltas,
            bool[] allowedGroups,
            ReFitClearanceCorrectionStats stats)
        {
            if (asset == null || profile == null || mutableGroupDeltas == null || measured == null ||
                normals == null || bodyPoints == null || settings == null || stats == null)
                return;

            float safety = Mathf.Max(0f, settings.clearanceMinimumSafetyDistance);
            float maxGuard = Mathf.Max(0f, settings.clearanceMaxOutwardCorrection);
            if (safety <= 0f || maxGuard <= 0f)
                return;

            int groupCount = Mathf.Min(asset.GroupCount, mutableGroupDeltas.Length);
            for (int g = 0; g < groupCount; g++)
            {
                if (g >= profile.eligible.Length || !profile.eligible[g] ||
                    g >= measured.Length || !measured[g] ||
                    !IsAllowedGroup(allowedGroups, g))
                    continue;

                int vertex = asset.groupRep[g];
                var normal = normals[g];
                if (normal.sqrMagnitude <= 1e-12f)
                    continue;

                var assetPoint = asset.worldVertices[vertex] + DeltaAt(baseGroupDeltas, g) + mutableGroupDeltas[g];
                float currentClearance = Vector3.Dot(assetPoint - bodyPoints[g], normal);
                float finalClearance = currentClearance;

                float missing = safety - currentClearance;
                if (missing > MeaningfulCorrection)
                {
                    float amount = Mathf.Min(missing, maxGuard);
                    var add = ClampAdditionalCorrectionToBudget(
                        startingGroupDeltas,
                        mutableGroupDeltas,
                        normal * amount,
                        settings,
                        context,
                        g,
                        stats,
                        allowedGroups != null);
                    amount = add.magnitude;
                    if (amount <= MeaningfulCorrection)
                        continue;

                    mutableGroupDeltas[g] += add;
                    stats.safetyGuardGroups++;
                    stats.outwardGroups++;
                    stats.maxSafetyGuardCorrection = Mathf.Max(stats.maxSafetyGuardCorrection, amount);
                    stats.maxOutwardCorrection = Mathf.Max(stats.maxOutwardCorrection, amount);
                    finalClearance = currentClearance + Vector3.Dot(add, normal);
                }

                if (finalClearance < 0f)
                    stats.maxPenetrationAfter = Mathf.Max(stats.maxPenetrationAfter, -finalClearance);
            }
        }

        private static void ApplySurfaceSafetyGuard(
            MeshSnapshot asset,
            MeshSnapshot targetBody,
            Profile profile,
            Vector3[] baseGroupDeltas,
            Vector3[] mutableGroupDeltas,
            Vector3[] bodyWorldShapeDeltas,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            Vector3[] startingGroupDeltas,
            bool[] allowedGroups,
            ReFitClearanceCorrectionStats stats,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh)
        {
            if (asset == null || targetBody == null || profile == null || mutableGroupDeltas == null ||
                settings == null || stats == null || asset.triangles == null || asset.groupOfVertex == null)
                return;

            int iterations = Mathf.Clamp(settings.clearanceSurfaceGuardIterations, 0, 12);
            float strength = Mathf.Clamp01(settings.clearanceSurfaceGuardStrength);
            float safety = Mathf.Max(0f, settings.clearanceMinimumSafetyDistance);
            float maxGuard = Mathf.Max(0f, settings.clearanceMaxSurfaceGuardCorrection);
            float triggerDistance = Mathf.Max(0f, settings.clearanceSurfaceGuardTriggerDistance);
            if (iterations <= 0 || strength <= 0f || safety <= 0f || maxGuard <= 0f)
                return;

            if (bodySurface == null || bodySurface.triangles == null || bodySurface.worldVertices == null ||
                bodySurface.worldVertices.Length == 0)
                return;

            float queryRange = Mathf.Max(
                0.05f,
                Mathf.Max(
                    settings.maxProjectionDistance * 2f,
                    settings.clearanceMaxInwardCorrection + settings.clearanceMaxOutwardCorrection + safety + 0.02f));

            int groupCount = Mathf.Min(asset.GroupCount, mutableGroupDeltas.Length);
            var groupWorld = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                int vertex = asset.groupRep[g];
                groupWorld[g] = asset.worldVertices[vertex] + DeltaAt(baseGroupDeltas, g) + mutableGroupDeltas[g];
            }

            var corrections = new Vector3[groupCount];
            var weights = new float[groupCount];
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Array.Clear(corrections, 0, corrections.Length);
                Array.Clear(weights, 0, weights.Length);
                int violatingSamples = 0;

                for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
                {
                    int ga = GroupForVertex(asset, asset.triangles[t], groupCount);
                    int gb = GroupForVertex(asset, asset.triangles[t + 1], groupCount);
                    int gc = GroupForVertex(asset, asset.triangles[t + 2], groupCount);
                    if (ga < 0 || gb < 0 || gc < 0)
                        continue;
                    if (!AnyAllowedGroup(allowedGroups, ga, gb, gc))
                        continue;

                    var a = groupWorld[ga];
                    var b = groupWorld[gb];
                    var c = groupWorld[gc];

                    violatingSamples += AccumulateTriangleSurfaceGuardSamples(
                        a, b, c, ga, gb, gc,
                        profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                        context, allowedGroups, corrections, weights, stats);
                }

                if (violatingSamples == 0)
                    break;

                stats.surfaceGuardSamples += violatingSamples;
                bool appliedAny = false;
                for (int g = 0; g < groupCount; g++)
                {
                    if (weights[g] <= 1e-6f)
                        continue;
                    if (!IsAllowedGroup(allowedGroups, g))
                        continue;

                    var correction = corrections[g] / weights[g];
                    float magnitude = correction.magnitude;
                    if (magnitude <= MeaningfulCorrection)
                        continue;

                    if (magnitude > maxGuard)
                        correction *= maxGuard / magnitude;
                    correction = ClampAdditionalCorrectionToBudget(
                        startingGroupDeltas,
                        mutableGroupDeltas,
                        correction,
                        settings,
                        context,
                        g,
                        stats,
                        allowedGroups != null);
                    magnitude = correction.magnitude;
                    if (magnitude <= MeaningfulCorrection)
                        continue;

                    mutableGroupDeltas[g] += correction;
                    groupWorld[g] += correction;
                    appliedAny = true;
                    stats.surfaceGuardGroups++;
                    stats.outwardGroups++;
                    stats.maxSurfaceGuardCorrection = Mathf.Max(stats.maxSurfaceGuardCorrection, correction.magnitude);
                    stats.maxOutwardCorrection = Mathf.Max(stats.maxOutwardCorrection, correction.magnitude);
                }

                if (!appliedAny)
                    break;
            }

            // Final penetration is measured once after all guards, clamping and propagation in Apply.
        }

        private static IslandPropagationResult ApplyDisconnectedIslandPropagation(
            MeshSnapshot asset,
            Profile profile,
            Vector3[] baseGroupDeltas,
            Vector3[] startingGroupDeltas,
            Vector3[] mutableGroupDeltas,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            ReFitClearanceCorrectionStats stats)
        {
            if (asset == null || profile == null || startingGroupDeltas == null || mutableGroupDeltas == null ||
                settings == null || stats == null || !settings.clearancePropagateDisconnectedIslands ||
                asset.triangles == null || asset.groupOfVertex == null || asset.groupAdjacency == null)
                return null;

            float strength = Mathf.Clamp01(settings.clearanceIslandPropagationStrength);
            float searchDistance = Mathf.Max(0f, settings.clearanceIslandPropagationSearchDistance);
            float maxCorrection = Mathf.Max(0f, settings.clearanceMaxIslandPropagationCorrection);
            float donorThreshold = Mathf.Max(MeaningfulCorrection, settings.clearanceIslandPropagationMinDonorCorrection);
            if (strength <= 0f || searchDistance <= MeaningfulCorrection || maxCorrection <= MeaningfulCorrection)
                return null;

            int groupCount = Mathf.Min(asset.GroupCount, Mathf.Min(startingGroupDeltas.Length, mutableGroupDeltas.Length));
            if (groupCount <= 0)
                return null;

            var componentByGroup = BuildGroupComponents(asset, groupCount, out var componentSizes, out int largestComponentSize);
            if (componentByGroup == null || componentSizes == null || componentSizes.Length <= 1)
                return null;

            int receiverSizeLimit = Mathf.Max(64, Mathf.RoundToInt(largestComponentSize * 0.25f));
            var supportWorld = new Vector3[groupCount];
            var correctionField = new Vector3[groupCount];
            var correctionMagnitude = new float[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                int vertex = asset.groupRep[g];
                supportWorld[g] = asset.worldVertices[vertex] + DeltaAt(baseGroupDeltas, g) + startingGroupDeltas[g];
                correctionField[g] = mutableGroupDeltas[g] - startingGroupDeltas[g];
                correctionMagnitude[g] = correctionField[g].magnitude;
            }

            var donorTriangles = new List<int>();
            var donorTriangleComponents = new List<int>();
            var donorTriangleRegions = new List<BodyRegion>();
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                int ga = GroupForVertex(asset, asset.triangles[t], groupCount);
                int gb = GroupForVertex(asset, asset.triangles[t + 1], groupCount);
                int gc = GroupForVertex(asset, asset.triangles[t + 2], groupCount);
                if (ga < 0 || gb < 0 || gc < 0 || ga == gb || gb == gc || ga == gc)
                    continue;

                int component = componentByGroup[ga];
                if (componentByGroup[gb] != component || componentByGroup[gc] != component)
                    continue;

                float maxDonor = Mathf.Max(correctionMagnitude[ga],
                    Mathf.Max(correctionMagnitude[gb], correctionMagnitude[gc]));
                if (maxDonor < donorThreshold)
                    continue;

                donorTriangles.Add(ga);
                donorTriangles.Add(gb);
                donorTriangles.Add(gc);
                donorTriangleComponents.Add(component);
                donorTriangleRegions.Add(TriangleRegion(context, ga, gb, gc));
            }

            if (donorTriangles.Count < 3)
                return null;

            var donorSurface = new MeshSnapshot
            {
                worldVertices = supportWorld,
                triangles = donorTriangles.ToArray()
            };
            donorSurface.worldNormals = ComputeWorldNormals(donorSurface.worldVertices, donorSurface.triangles);
            var donorBvh = SurfaceBvh.Build(donorSurface);
            var componentGroups = BuildComponentGroupLists(componentByGroup, componentSizes);
            var debugPoints = new ReFitIslandPropagationDebugPoint[groupCount];
            var result = new IslandPropagationResult
            {
                propagatedGroups = new bool[groupCount]
            };

            for (int component = 0; component < componentGroups.Length; component++)
            {
                var groups = componentGroups[component];
                if (groups == null || groups.Count == 0 || componentSizes[component] > receiverSizeLimit)
                    continue;

                stats.islandPropagationCandidateComponents++;
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    stats.islandPropagationCandidateGroups++;
                    debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                        ReFitIslandPropagationStatus.Candidate, "candidate");
                }

                var samples = new List<IslandSupportSample>(groups.Count);
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    if (TryFindIslandSupportSample(
                            g,
                            component,
                            componentSizes,
                            supportWorld,
                            correctionField,
                            correctionMagnitude,
                            falloff,
                            expansions,
                            settings,
                            context,
                            donorSurface,
                            donorBvh,
                            donorTriangleComponents,
                            donorTriangleRegions,
                            searchDistance,
                            donorThreshold,
                            out var sample,
                            out var rejectedStatus,
                            out var rejectedReason))
                    {
                        samples.Add(sample);
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                            ReFitIslandPropagationStatus.Candidate, "supported", sample);
                    }
                    else
                    {
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                            rejectedStatus, rejectedReason);
                    }
                }

                int requiredSamples = Mathf.Min(groups.Count, Mathf.Max(2, Mathf.CeilToInt(groups.Count * 0.2f)));
                if (samples.Count < requiredSamples)
                {
                    for (int i = 0; i < groups.Count; i++)
                    {
                        int g = groups[i];
                        if (debugPoints[g] == null ||
                            debugPoints[g].status == ReFitIslandPropagationStatus.Candidate)
                        {
                            debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                                ReFitIslandPropagationStatus.RejectedInsufficientComponentSupport,
                                $"only {samples.Count}/{requiredSamples} supported groups");
                        }
                    }
                    continue;
                }

                var targetField = BuildIslandTargetCorrectionField(
                    groups,
                    samples,
                    asset.groupAdjacency,
                    groupCount);
                float componentTargetMagnitude = targetField.averageMagnitude;
                float componentWeight = targetField.averageWeight;
                if (componentTargetMagnitude < donorThreshold || componentWeight <= 1e-4f)
                {
                    var status = componentTargetMagnitude < donorThreshold
                        ? ReFitIslandPropagationStatus.RejectedWeakDonor
                        : ReFitIslandPropagationStatus.RejectedWeight;
                    var reason = componentTargetMagnitude < donorThreshold
                        ? "component donor correction is too weak"
                        : "component receiver weight is zero";
                    for (int i = 0; i < groups.Count; i++)
                    {
                        int g = groups[i];
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count, status, reason);
                    }
                    continue;
                }

                float componentInfluence = strength;
                float supportDistance = targetField.maxSupportDistance;
                int supportComponent = targetField.dominantSupportComponent;
                int supportGroup = targetField.dominantSupportGroup;
                if (componentInfluence <= 1e-4f)
                {
                    for (int i = 0; i < groups.Count; i++)
                    {
                        int g = groups[i];
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                            ReFitIslandPropagationStatus.RejectedWeight,
                            "component receiver weight is zero",
                            supportComponent, supportGroup, supportDistance, componentWeight,
                            correctionField[g].magnitude, componentTargetMagnitude, 0f);
                    }
                    continue;
                }

                var plannedAdds = new Vector3[groups.Count];
                var currentMagnitudes = new float[groups.Count];
                bool hasPlannedCorrection = false;
                float componentBudgetScale = 1f;
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    var currentCorrection = correctionField[g];
                    currentMagnitudes[i] = currentCorrection.magnitude;
                    var targetCorrection = targetField.corrections != null && i < targetField.corrections.Length
                        ? targetField.corrections[i]
                        : Vector3.zero;
                    float targetMagnitude = targetCorrection.magnitude;
                    float targetWeight = targetField.receiverWeights != null && i < targetField.receiverWeights.Length
                        ? targetField.receiverWeights[i]
                        : componentWeight;
                    int targetSupportComponent = targetField.supportComponents != null && i < targetField.supportComponents.Length
                        ? targetField.supportComponents[i]
                        : supportComponent;
                    int targetSupportGroup = targetField.supportGroups != null && i < targetField.supportGroups.Length
                        ? targetField.supportGroups[i]
                        : supportGroup;
                    float targetSupportDistance = targetField.supportDistances != null && i < targetField.supportDistances.Length
                        ? targetField.supportDistances[i]
                        : supportDistance;

                    var add = (targetCorrection - currentCorrection) * componentInfluence;
                    float magnitude = add.magnitude;
                    if (magnitude <= MeaningfulCorrection)
                    {
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                            ReFitIslandPropagationStatus.RejectedWeakDonor,
                            "support correction matches current correction",
                            targetSupportComponent, targetSupportGroup, targetSupportDistance, targetWeight,
                            currentMagnitudes[i], targetMagnitude, 0f);
                        continue;
                    }

                    if (magnitude > maxCorrection)
                    {
                        add *= maxCorrection / magnitude;
                        magnitude = maxCorrection;
                    }

                    var budgetedAdd = ClampAdditionalCorrectionToBudget(
                        startingGroupDeltas,
                        mutableGroupDeltas,
                        add,
                        settings,
                        context,
                        g,
                        null,
                        true);

                    if (budgetedAdd.sqrMagnitude <= MeaningfulCorrection * MeaningfulCorrection)
                    {
                        debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                            ReFitIslandPropagationStatus.RejectedBudget,
                            "support correction was capped by remaining budget",
                            targetSupportComponent, targetSupportGroup, targetSupportDistance, targetWeight,
                            currentMagnitudes[i], targetMagnitude, 0f);
                        continue;
                    }

                    float budgetScale = budgetedAdd.magnitude / Mathf.Max(magnitude, 1e-6f);
                    componentBudgetScale = Mathf.Min(componentBudgetScale, Mathf.Clamp01(budgetScale));
                    plannedAdds[i] = add;
                    hasPlannedCorrection = true;
                }

                if (!hasPlannedCorrection)
                    continue;

                bool componentApplied = false;
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    var add = plannedAdds[i] * componentBudgetScale;
                    if (add.sqrMagnitude <= MeaningfulCorrection * MeaningfulCorrection)
                        continue;

                    mutableGroupDeltas[g] += add;
                    stats.propagatedIslandGroups++;
                    stats.maxPropagatedIslandCorrection = Mathf.Max(stats.maxPropagatedIslandCorrection, add.magnitude);
                    stats.maxIslandPropagationSupportDistance = Mathf.Max(
                        stats.maxIslandPropagationSupportDistance,
                        supportDistance);
                    result.propagatedGroups[g] = true;
                    result.propagatedCount++;
                    componentApplied = true;

                    debugPoints[g] = CreateIslandDebugPoint(g, component, groups.Count,
                        ReFitIslandPropagationStatus.Applied,
                        "surface-follow component support",
                        targetField.supportComponents != null && i < targetField.supportComponents.Length ? targetField.supportComponents[i] : supportComponent,
                        targetField.supportGroups != null && i < targetField.supportGroups.Length ? targetField.supportGroups[i] : supportGroup,
                        targetField.supportDistances != null && i < targetField.supportDistances.Length ? targetField.supportDistances[i] : supportDistance,
                        targetField.receiverWeights != null && i < targetField.receiverWeights.Length ? targetField.receiverWeights[i] : componentWeight,
                        currentMagnitudes[i],
                        targetField.corrections != null && i < targetField.corrections.Length ? targetField.corrections[i].magnitude : componentTargetMagnitude,
                        add.magnitude);
                }

                if (componentApplied)
                    stats.islandPropagationPropagatedComponents++;
            }

            for (int g = 0; g < groupCount; g++)
            {
                var point = debugPoints[g];
                if (point == null || point.status == ReFitIslandPropagationStatus.Applied ||
                    point.status == ReFitIslandPropagationStatus.None ||
                    point.status == ReFitIslandPropagationStatus.Candidate)
                    continue;

                stats.islandPropagationRejectedGroups++;
                switch (point.status)
                {
                    case ReFitIslandPropagationStatus.RejectedNoSupport:
                        stats.islandPropagationRejectedNoSupportGroups++;
                        break;
                    case ReFitIslandPropagationStatus.RejectedUnstableSupport:
                        stats.islandPropagationRejectedUnstableGroups++;
                        break;
                    case ReFitIslandPropagationStatus.RejectedInsufficientComponentSupport:
                        stats.islandPropagationRejectedInsufficientSupportGroups++;
                        break;
                    case ReFitIslandPropagationStatus.RejectedWeakDonor:
                        stats.islandPropagationRejectedWeakDonorGroups++;
                        break;
                    case ReFitIslandPropagationStatus.RejectedWeight:
                        stats.islandPropagationRejectedWeightGroups++;
                        break;
                    case ReFitIslandPropagationStatus.RejectedBudget:
                        stats.islandPropagationRejectedBudgetGroups++;
                        break;
                }
            }

            stats.islandPropagationDebug = new ReFitIslandPropagationDebugData
            {
                groups = CompactDebugPoints(debugPoints)
            };
            return result.HasPropagated ? result : null;
        }

        private static bool TryFindIslandSupportSample(
            int group,
            int receiverComponent,
            int[] componentSizes,
            Vector3[] supportWorld,
            Vector3[] correctionField,
            float[] correctionMagnitude,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            MeshSnapshot donorSurface,
            SurfaceBvh donorBvh,
            List<int> donorTriangleComponents,
            List<BodyRegion> donorTriangleRegions,
            float searchDistance,
            float donorThreshold,
            out IslandSupportSample sample,
            out ReFitIslandPropagationStatus rejectedStatus,
            out string rejectedReason)
        {
            sample = default;
            rejectedStatus = ReFitIslandPropagationStatus.RejectedNoSupport;
            rejectedReason = "no compatible donor surface";
            var receiverRegion = GroupRegion(context, group);
            var hit = donorBvh.ClosestPoint(supportWorld[group], searchDistance, triangle =>
            {
                if (triangle < 0 || triangle >= donorTriangleComponents.Count)
                    return false;

                int donorComponent = donorTriangleComponents[triangle];
                if (donorComponent == receiverComponent)
                    return false;

                if (donorComponent >= 0 && donorComponent < componentSizes.Length &&
                    componentSizes[donorComponent] < componentSizes[receiverComponent])
                    return false;

                var donorRegion = donorTriangleRegions[triangle];
                return HumanoidBoneMapper.RegionsCompatible(receiverRegion, donorRegion);
            });
            if (!hit.found)
                return false;

            int tri = hit.triangle * 3;
            if (tri + 2 >= donorSurface.triangles.Length)
                return false;

            int ga = donorSurface.triangles[tri];
            int gb = donorSurface.triangles[tri + 1];
            int gc = donorSurface.triangles[tri + 2];
            if (!IsStableGarmentSupport(supportWorld[group], donorSurface, hit))
            {
                rejectedStatus = ReFitIslandPropagationStatus.RejectedUnstableSupport;
                rejectedReason = "receiver is behind donor support";
                return false;
            }

            var targetCorrection = correctionField[ga] * hit.bary.x +
                                   correctionField[gb] * hit.bary.y +
                                   correctionField[gc] * hit.bary.z;
            float targetMagnitude = targetCorrection.magnitude;
            if (targetMagnitude < donorThreshold)
            {
                rejectedStatus = ReFitIslandPropagationStatus.RejectedWeakDonor;
                rejectedReason = "donor correction is too weak";
                return false;
            }

            float distanceWeight = Mathf.Lerp(1f, 0.25f, Smooth01(0f, searchDistance, hit.distance));
            float receiverWeight = ReceiverGarmentSupportWeight(falloff, expansions, settings, group);
            if (distanceWeight <= 1e-4f || receiverWeight <= 1e-4f)
            {
                rejectedStatus = ReFitIslandPropagationStatus.RejectedWeight;
                rejectedReason = "receiver support weight is zero";
                return false;
            }

            sample = new IslandSupportSample
            {
                receiverGroup = group,
                supportComponent = donorTriangleComponents[hit.triangle],
                supportGroup = ga,
                targetCorrection = targetCorrection,
                targetMagnitude = targetMagnitude,
                currentMagnitude = correctionMagnitude[group],
                supportDistance = hit.distance,
                distanceWeight = distanceWeight,
                receiverWeight = receiverWeight
            };
            return true;
        }

        private static bool IsStableGarmentSupport(Vector3 receiverPoint, MeshSnapshot donorSurface, SurfaceBvh.Hit hit)
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

        private static int[] BuildGroupComponents(
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
                    if (g < 0 || g >= asset.groupAdjacency.Length || asset.groupAdjacency[g] == null)
                        continue;

                    var neighbors = asset.groupAdjacency[g];
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int n = neighbors[i];
                        if (n < 0 || n >= groupCount || components[n] >= 0)
                            continue;

                        components[n] = component;
                        queue.Enqueue(n);
                    }
                }

                sizes.Add(size);
                largestComponentSize = Mathf.Max(largestComponentSize, size);
            }

            componentSizes = sizes.ToArray();
            return components;
        }

        private static List<int>[] BuildComponentGroupLists(int[] componentByGroup, int[] componentSizes)
        {
            if (componentByGroup == null || componentSizes == null)
                return null;

            var groups = new List<int>[componentSizes.Length];
            for (int i = 0; i < groups.Length; i++)
                groups[i] = new List<int>(Mathf.Max(1, componentSizes[i]));

            for (int g = 0; g < componentByGroup.Length; g++)
            {
                int component = componentByGroup[g];
                if (component >= 0 && component < groups.Length)
                    groups[component].Add(g);
            }

            return groups;
        }

        private static IslandTargetCorrectionField BuildIslandTargetCorrectionField(
            List<int> groups,
            List<IslandSupportSample> samples,
            List<int>[] adjacency,
            int groupCount)
        {
            var field = new IslandTargetCorrectionField
            {
                corrections = new Vector3[groups != null ? groups.Count : 0],
                receiverWeights = new float[groups != null ? groups.Count : 0],
                supportComponents = new int[groups != null ? groups.Count : 0],
                supportGroups = new int[groups != null ? groups.Count : 0],
                supportDistances = new float[groups != null ? groups.Count : 0],
                averageWeight = AverageSampleWeight(samples),
                maxSupportDistance = MaxSampleSupportDistance(samples),
                dominantSupportComponent = DominantSupportComponent(samples),
                dominantSupportGroup = DominantSupportGroup(samples)
            };

            if (groups == null || groups.Count == 0)
                return field;

            for (int i = 0; i < groups.Count; i++)
            {
                field.supportComponents[i] = field.dominantSupportComponent;
                field.supportGroups[i] = field.dominantSupportGroup;
                field.supportDistances[i] = field.maxSupportDistance;
                field.receiverWeights[i] = field.averageWeight;
            }

            var fallback = WeightedAverageTargetCorrection(samples);
            var valid = new bool[groups.Count];
            var localIndex = BuildIslandLocalIndex(groups, groupCount);
            if (samples != null)
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    int local = sample.receiverGroup >= 0 && sample.receiverGroup < localIndex.Length
                        ? localIndex[sample.receiverGroup]
                        : -1;
                    if (local < 0 || local >= field.corrections.Length)
                        continue;

                    field.corrections[local] = sample.targetCorrection;
                    field.receiverWeights[local] = Mathf.Clamp01(sample.Weight);
                    field.supportComponents[local] = sample.supportComponent;
                    field.supportGroups[local] = sample.supportGroup;
                    field.supportDistances[local] = sample.supportDistance;
                    valid[local] = true;
                }
            }

            FillIslandTargetCorrectionField(groups, localIndex, adjacency, field, valid, fallback);
            SmoothIslandTargetCorrectionField(groups, localIndex, adjacency, field.corrections);

            float magnitudeTotal = 0f;
            float weightTotal = 0f;
            field.maxSupportDistance = 0f;
            for (int i = 0; i < field.corrections.Length; i++)
            {
                float weight = Mathf.Max(0.05f, field.receiverWeights[i]);
                magnitudeTotal += field.corrections[i].magnitude * weight;
                weightTotal += weight;
                field.maxSupportDistance = Mathf.Max(field.maxSupportDistance, field.supportDistances[i]);
            }

            field.averageMagnitude = weightTotal > 1e-6f
                ? magnitudeTotal / weightTotal
                : fallback.magnitude;
            return field;
        }

        private static int[] BuildIslandLocalIndex(List<int> groups, int groupCount)
        {
            int length = Mathf.Max(0, groupCount);
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                    length = Mathf.Max(length, groups[i] + 1);
            }

            var localIndex = new int[length];
            for (int i = 0; i < localIndex.Length; i++)
                localIndex[i] = -1;

            if (groups == null)
                return localIndex;

            for (int i = 0; i < groups.Count; i++)
            {
                int g = groups[i];
                if (g >= 0 && g < localIndex.Length)
                    localIndex[g] = i;
            }

            return localIndex;
        }

        private static void FillIslandTargetCorrectionField(
            List<int> groups,
            int[] localIndex,
            List<int>[] adjacency,
            IslandTargetCorrectionField field,
            bool[] valid,
            Vector3 fallback)
        {
            if (groups == null || localIndex == null || field.corrections == null || valid == null)
                return;

            var fill = new Vector3[field.corrections.Length];
            var fillWeight = new float[field.corrections.Length];
            var fillSupportComponent = new int[field.corrections.Length];
            var fillSupportGroup = new int[field.corrections.Length];
            var fillSupportDistance = new float[field.corrections.Length];
            var fillValid = new bool[field.corrections.Length];
            for (int iteration = 0; iteration < 48; iteration++)
            {
                bool changed = false;
                Array.Clear(fillValid, 0, fillValid.Length);
                for (int i = 0; i < groups.Count; i++)
                {
                    if (valid[i])
                        continue;

                    int g = groups[i];
                    if (adjacency == null || g < 0 || g >= adjacency.Length || adjacency[g] == null)
                        continue;

                    Vector3 total = Vector3.zero;
                    float weightTotal = 0f;
                    int count = 0;
                    int bestSupportComponent = field.dominantSupportComponent;
                    int bestSupportGroup = field.dominantSupportGroup;
                    float bestWeight = -1f;
                    float maxDistance = 0f;
                    var neighbors = adjacency[g];
                    for (int n = 0; n < neighbors.Count; n++)
                    {
                        int nb = neighbors[n];
                        if (nb < 0 || nb >= localIndex.Length)
                            continue;

                        int local = localIndex[nb];
                        if (local < 0 || !valid[local])
                            continue;

                        float weight = Mathf.Max(0.05f, field.receiverWeights[local]);
                        total += field.corrections[local] * weight;
                        weightTotal += weight;
                        count++;
                        maxDistance = Mathf.Max(maxDistance, field.supportDistances[local]);
                        if (weight > bestWeight)
                        {
                            bestWeight = weight;
                            bestSupportComponent = field.supportComponents[local];
                            bestSupportGroup = field.supportGroups[local];
                        }
                    }

                    if (count <= 0 || weightTotal <= 1e-6f)
                        continue;

                    fill[i] = total / weightTotal;
                    fillWeight[i] = Mathf.Clamp01(weightTotal / count);
                    fillSupportComponent[i] = bestSupportComponent;
                    fillSupportGroup[i] = bestSupportGroup;
                    fillSupportDistance[i] = maxDistance;
                    fillValid[i] = true;
                    changed = true;
                }

                if (!changed)
                    break;

                for (int i = 0; i < fillValid.Length; i++)
                {
                    if (!fillValid[i])
                        continue;

                    field.corrections[i] = fill[i];
                    field.receiverWeights[i] = fillWeight[i];
                    field.supportComponents[i] = fillSupportComponent[i];
                    field.supportGroups[i] = fillSupportGroup[i];
                    field.supportDistances[i] = fillSupportDistance[i];
                    valid[i] = true;
                }
            }

            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i])
                    continue;

                field.corrections[i] = fallback;
                field.receiverWeights[i] = field.averageWeight;
                field.supportComponents[i] = field.dominantSupportComponent;
                field.supportGroups[i] = field.dominantSupportGroup;
                field.supportDistances[i] = field.maxSupportDistance;
                valid[i] = true;
            }
        }

        private static void SmoothIslandTargetCorrectionField(
            List<int> groups,
            int[] localIndex,
            List<int>[] adjacency,
            Vector3[] corrections)
        {
            if (groups == null || localIndex == null || adjacency == null || corrections == null)
                return;

            var buffer = new Vector3[corrections.Length];
            for (int iteration = 0; iteration < 3; iteration++)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    int g = groups[i];
                    Vector3 total = corrections[i];
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
                            if (local < 0)
                                continue;

                            total += corrections[local];
                            count++;
                        }
                    }

                    buffer[i] = Vector3.Lerp(corrections[i], total / count, 0.35f);
                }

                Array.Copy(buffer, corrections, corrections.Length);
            }
        }

        private static Vector3 WeightedAverageTargetCorrection(List<IslandSupportSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return Vector3.zero;

            var total = Vector3.zero;
            float weightSum = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                float weight = samples[i].Weight;
                if (weight <= 1e-6f)
                    continue;
                total += samples[i].targetCorrection * weight;
                weightSum += weight;
            }

            if (weightSum > 1e-6f)
                return total / weightSum;

            for (int i = 0; i < samples.Count; i++)
                total += samples[i].targetCorrection;
            return total / samples.Count;
        }

        private static float AverageSampleWeight(List<IslandSupportSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return 0f;

            float total = 0f;
            for (int i = 0; i < samples.Count; i++)
                total += samples[i].Weight;
            return Mathf.Clamp01(total / samples.Count);
        }

        private static float MaxSampleSupportDistance(List<IslandSupportSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return 0f;

            float max = 0f;
            for (int i = 0; i < samples.Count; i++)
                max = Mathf.Max(max, samples[i].supportDistance);
            return max;
        }

        private static int DominantSupportComponent(List<IslandSupportSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return -1;

            var counts = new Dictionary<int, int>();
            int best = -1;
            int bestCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                int component = samples[i].supportComponent;
                counts.TryGetValue(component, out int count);
                count++;
                counts[component] = count;
                if (count > bestCount)
                {
                    best = component;
                    bestCount = count;
                }
            }

            return best;
        }

        private static int DominantSupportGroup(List<IslandSupportSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return -1;

            var counts = new Dictionary<int, int>();
            int best = -1;
            int bestCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                int group = samples[i].supportGroup;
                counts.TryGetValue(group, out int count);
                count++;
                counts[group] = count;
                if (count > bestCount)
                {
                    best = group;
                    bestCount = count;
                }
            }

            return best;
        }

        private static ReFitIslandPropagationDebugPoint CreateIslandDebugPoint(
            int group,
            int component,
            int componentSize,
            ReFitIslandPropagationStatus status,
            string reason)
        {
            return CreateIslandDebugPoint(group, component, componentSize, status, reason,
                -1, -1, 0f, 0f, 0f, 0f, 0f);
        }

        private static ReFitIslandPropagationDebugPoint CreateIslandDebugPoint(
            int group,
            int component,
            int componentSize,
            ReFitIslandPropagationStatus status,
            string reason,
            IslandSupportSample sample)
        {
            return CreateIslandDebugPoint(group, component, componentSize, status, reason,
                sample.supportComponent,
                sample.supportGroup,
                sample.supportDistance,
                sample.receiverWeight,
                sample.currentMagnitude,
                sample.targetMagnitude,
                0f);
        }

        private static ReFitIslandPropagationDebugPoint CreateIslandDebugPoint(
            int group,
            int component,
            int componentSize,
            ReFitIslandPropagationStatus status,
            string reason,
            int supportComponent,
            int supportGroup,
            float supportDistance,
            float receiverWeight,
            float currentMagnitude,
            float targetMagnitude,
            float appliedMagnitude)
        {
            return new ReFitIslandPropagationDebugPoint
            {
                groupIndex = group,
                componentIndex = component,
                componentSize = componentSize,
                supportComponentIndex = supportComponent,
                supportGroupIndex = supportGroup,
                status = status,
                reason = reason,
                supportDistance = supportDistance,
                receiverWeight = receiverWeight,
                currentCorrectionMagnitude = currentMagnitude,
                targetCorrectionMagnitude = targetMagnitude,
                appliedCorrectionMagnitude = appliedMagnitude
            };
        }

        private static ReFitIslandPropagationDebugPoint[] CompactDebugPoints(
            ReFitIslandPropagationDebugPoint[] debugPoints)
        {
            if (debugPoints == null || debugPoints.Length == 0)
                return null;

            var points = new List<ReFitIslandPropagationDebugPoint>();
            for (int i = 0; i < debugPoints.Length; i++)
                if (debugPoints[i] != null)
                    points.Add(debugPoints[i]);
            return points.Count > 0 ? points.ToArray() : null;
        }

        private static BodyRegion TriangleRegion(Context context, int ga, int gb, int gc)
        {
            var region = BodyRegion.Unknown;
            region = PickCompatibleRegion(region, GroupRegion(context, ga));
            region = PickCompatibleRegion(region, GroupRegion(context, gb));
            region = PickCompatibleRegion(region, GroupRegion(context, gc));
            return region;
        }

        private static BodyRegion PickCompatibleRegion(BodyRegion current, BodyRegion candidate)
        {
            if (candidate == BodyRegion.Unknown)
                return current;
            if (current == BodyRegion.Unknown || current == candidate)
                return candidate;
            return BodyRegion.Unknown;
        }

        private static BodyRegion GroupRegion(Context context, int group)
        {
            return context != null && context.assetGroupRegions != null &&
                   group >= 0 && group < context.assetGroupRegions.Length
                ? context.assetGroupRegions[group]
                : BodyRegion.Unknown;
        }

        private static float ReceiverGarmentSupportWeight(
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            int group)
        {
            float f = falloff != null && group >= 0 && group < falloff.Length ? Mathf.Clamp01(falloff[group]) : 1f;
            float expansion = expansions != null && group >= 0 && group < expansions.Length ? expansions[group] : 0f;
            float expansionWeight = settings != null
                ? Smooth01(settings.clearanceExpansionStart, settings.clearanceExpansionFull, expansion)
                : 1f;

            return Mathf.Clamp01(Mathf.Max(0.65f, Mathf.Max(0.35f, f) * expansionWeight));
        }

        private static int AccumulateTriangleSurfaceGuardSamples(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            int ga,
            int gb,
            int gc,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety,
            float triggerDistance,
            float strength,
            Context context,
            bool[] allowedGroups,
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            int violations = 0;
            violations += AccumulateEdgeSurfaceGuardSamples(a, b, ga, gb,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                context, allowedGroups, corrections, weights, stats);
            violations += AccumulateEdgeSurfaceGuardSamples(b, c, gb, gc,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                context, allowedGroups, corrections, weights, stats);
            violations += AccumulateEdgeSurfaceGuardSamples(c, a, gc, ga,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                context, allowedGroups, corrections, weights, stats);

            violations += AccumulateSurfaceGuardSample((a + b + c) / 3f, ga, 1f / 3f, gb, 1f / 3f, gc, 1f / 3f,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, context, allowedGroups, corrections, weights, stats) ? 1 : 0;

            return violations;
        }

        private static int AccumulateEdgeSurfaceGuardSamples(
            Vector3 a,
            Vector3 b,
            int ga,
            int gb,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety,
            float triggerDistance,
            float strength,
            Context context,
            bool[] allowedGroups,
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            int samples = Mathf.Clamp(settings != null ? settings.clearanceSurfaceGuardEdgeSamples : 1, 1, 3);
            int violations = 0;
            if (samples >= 2)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 1f / 3f), ga, 2f / 3f, gb, 1f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, context, allowedGroups, corrections, weights, stats) ? 1 : 0;
            }

            if (samples == 1 || samples >= 3)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 0.5f), ga, 0.5f, gb, 0.5f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, context, allowedGroups, corrections, weights, stats) ? 1 : 0;
            }

            if (samples >= 2)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 2f / 3f), ga, 1f / 3f, gb, 2f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, context, allowedGroups, corrections, weights, stats) ? 1 : 0;
            }

            return violations;
        }

        private static bool AccumulateSurfaceGuardSample(
            Vector3 point,
            int g0,
            float w0,
            int g1,
            float w1,
            int g2,
            float w2,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety,
            float triggerDistance,
            float strength,
            Context context,
            bool[] allowedGroups,
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            float ew0 = EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g0, w0);
            float ew1 = EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g1, w1);
            float ew2 = EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g2, w2);
            float totalWeight = ew0 + ew1 + ew2;
            if (totalWeight <= 1e-6f)
                return false;

            var referenceNormal = SampleReferenceNormal(context, g0, w0, g1, w1, g2, w2);
            var region = SampleRegion(context, g0, w0, g1, w1, g2, w2);
            var hit = ClosestCompatiblePoint(point, queryRange, bodySurface, bvh, settings, context, region, referenceNormal);
            if (!hit.found)
                return false;

            var normal = bodySurface.BaryNormal(hit.triangle, hit.bary);
            if (referenceNormal.sqrMagnitude > 1e-12f)
            {
                float normalDot = Vector3.Dot(normal, referenceNormal.normalized);
                if (settings != null && settings.filterByNormal &&
                    normalDot < Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad))
                    return false;

                if (normalDot > 0f)
                    normal = (normal + referenceNormal.normalized * 0.35f).normalized;
            }
            float clearance = Vector3.Dot(point - hit.position, normal);
            if (clearance < 0f)
                stats.maxPenetrationBefore = Mathf.Max(stats.maxPenetrationBefore, -clearance);

            if (clearance >= -triggerDistance)
                return false;

            float missing = safety - clearance;
            var correction = normal * (missing * strength);
            AddWeightedCorrection(corrections, weights, g0, ew0 / totalWeight, correction);
            AddWeightedCorrection(corrections, weights, g1, ew1 / totalWeight, correction);
            AddWeightedCorrection(corrections, weights, g2, ew2 / totalWeight, correction);
            return true;
        }

        private static void AddWeightedCorrection(Vector3[] corrections, float[] weights, int group, float weight, Vector3 correction)
        {
            if (group < 0 || group >= corrections.Length || weight <= 1e-6f)
                return;

            corrections[group] += correction * weight;
            weights[group] += weight;
        }

        private static float MeasureWorstSurfacePenetration(
            MeshSnapshot asset,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            bool[] allowedGroups,
            Vector3[] groupWorld,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            if (asset == null || asset.triangles == null || groupWorld == null)
                return 0f;

            float worst = 0f;
            int groupCount = groupWorld.Length;
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                int ga = GroupForVertex(asset, asset.triangles[t], groupCount);
                int gb = GroupForVertex(asset, asset.triangles[t + 1], groupCount);
                int gc = GroupForVertex(asset, asset.triangles[t + 2], groupCount);
                if (ga < 0 || gb < 0 || gc < 0)
                    continue;
                if (!AnyAllowedGroup(allowedGroups, ga, gb, gc))
                    continue;

                var a = groupWorld[ga];
                var b = groupWorld[gb];
                var c = groupWorld[gc];
                worst = Mathf.Max(worst, MeasureTriangleSurfacePenetration(
                    a, b, c, ga, gb, gc, profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            }

            return worst;
        }

        private static float MeasureFinalMaxPenetrationAfter(
            MeshSnapshot asset,
            MeshSnapshot targetBody,
            Profile profile,
            Vector3[] baseGroupDeltas,
            Vector3[] mutableGroupDeltas,
            Vector3[] bodyWorldShapeDeltas,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh)
        {
            if (asset == null || targetBody == null || profile == null || mutableGroupDeltas == null ||
                settings == null || asset.groupRep == null)
                return 0f;

            if (bodySurface == null || bodySurface.worldVertices == null || bodySurface.triangles == null ||
                bodySurface.worldVertices.Length == 0)
                return 0f;

            float safety = Mathf.Max(0f, settings.clearanceMinimumSafetyDistance);
            float queryRange = Mathf.Max(
                0.05f,
                Mathf.Max(
                    settings.maxProjectionDistance * 2f,
                    settings.clearanceMaxInwardCorrection + settings.clearanceMaxOutwardCorrection + safety + 0.02f));

            int groupCount = Mathf.Min(asset.GroupCount, mutableGroupDeltas.Length);
            var groupWorld = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                int vertex = asset.groupRep[g];
                groupWorld[g] = asset.worldVertices[vertex] + DeltaAt(baseGroupDeltas, g) + mutableGroupDeltas[g];
            }

            return MeasureWorstSurfacePenetration(
                asset,
                profile,
                falloff,
                expansions,
                settings,
                context,
                null,
                groupWorld,
                bodySurface,
                bvh,
                queryRange,
                safety);
        }

        private static float MeasureTriangleSurfacePenetration(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            int ga,
            int gb,
            int gc,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            bool[] allowedGroups,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            float worst = 0f;
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(a, b, ga, gb,
                profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(b, c, gb, gc,
                profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(c, a, gc, ga,
                profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, SurfacePenetrationAt((a + b + c) / 3f, ga, 1f / 3f, gb, 1f / 3f, gc, 1f / 3f,
                profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            return worst;
        }

        private static float MeasureEdgeSurfacePenetration(
            Vector3 a,
            Vector3 b,
            int ga,
            int gb,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            bool[] allowedGroups,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            int samples = Mathf.Clamp(settings != null ? settings.clearanceSurfaceGuardEdgeSamples : 1, 1, 3);
            float worst = 0f;
            if (samples >= 2)
            {
                worst = Mathf.Max(worst, SurfacePenetrationAt(Vector3.Lerp(a, b, 1f / 3f), ga, 2f / 3f, gb, 1f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            }

            if (samples == 1 || samples >= 3)
            {
                worst = Mathf.Max(worst, SurfacePenetrationAt(Vector3.Lerp(a, b, 0.5f), ga, 0.5f, gb, 0.5f, -1, 0f,
                    profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            }

            if (samples >= 2)
            {
                worst = Mathf.Max(worst, SurfacePenetrationAt(Vector3.Lerp(a, b, 2f / 3f), ga, 1f / 3f, gb, 2f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, context, allowedGroups, bodySurface, bvh, queryRange, safety));
            }

            return worst;
        }

        private static float SurfacePenetrationAt(
            Vector3 point,
            int g0,
            float w0,
            int g1,
            float w1,
            int g2,
            float w2,
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            Context context,
            bool[] allowedGroups,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            if (EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g0, w0) +
                EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g1, w1) +
                EligibleSampleWeight(profile, falloff, expansions, settings, allowedGroups, g2, w2) <= 1e-6f)
                return 0f;

            var referenceNormal = SampleReferenceNormal(context, g0, w0, g1, w1, g2, w2);
            var region = SampleRegion(context, g0, w0, g1, w1, g2, w2);
            var hit = ClosestCompatiblePoint(point, queryRange, bodySurface, bvh, settings, context, region, referenceNormal);
            if (!hit.found)
                return 0f;

            var normal = bodySurface.BaryNormal(hit.triangle, hit.bary);
            if (referenceNormal.sqrMagnitude > 1e-12f &&
                settings != null && settings.filterByNormal &&
                Vector3.Dot(normal, referenceNormal.normalized) < Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad))
                return 0f;

            float clearance = Vector3.Dot(point - hit.position, normal);
            return clearance < 0f ? -clearance : 0f;
        }

        private static float InwardScale(ReFitSettings settings, Context context, int group)
        {
            float scale = 1f;
            if (context != null && context.transferredBlendshape)
                scale *= Mathf.Clamp01(settings.clearanceTransferredInwardScale);

            scale *= DesiredCorrectionScale(settings, context, group);

            float hem = HemWeight(context, group);
            if (hem > 0f)
                scale *= Mathf.Lerp(1f, Mathf.Clamp01(settings.upperBodyGarmentHemFollowScale), hem);

            return Mathf.Clamp01(scale);
        }

        private static float DesiredCorrectionScale(ReFitSettings settings, Context context, int group)
        {
            if (settings == null || context == null || !context.transferredBlendshape)
                return 1f;

            float scale = 1f;
            float boundary = OpenBoundaryWeight(context, group);
            if (boundary > 0f)
                scale *= Mathf.Lerp(1f, Mathf.Clamp01(settings.clearanceOpenBoundaryCorrectionScale), boundary);

            if (IsLowConfidenceBinding(context, group))
                scale *= Mathf.Clamp01(settings.clearanceLowConfidenceCorrectionScale);

            return Mathf.Clamp01(scale);
        }

        private static Vector3 ClampAdditionalCorrectionToBudget(
            Vector3[] startingGroupDeltas,
            Vector3[] mutableGroupDeltas,
            Vector3 add,
            ReFitSettings settings,
            Context context,
            int group,
            ReFitClearanceCorrectionStats stats,
            bool supportPropagationBudget = false)
        {
            if (add.sqrMagnitude <= MeaningfulCorrection * MeaningfulCorrection ||
                startingGroupDeltas == null || mutableGroupDeltas == null || settings == null ||
                group < 0 || group >= startingGroupDeltas.Length || group >= mutableGroupDeltas.Length)
                return add;

            float limit = TotalCorrectionLimit(settings, context, group, supportPropagationBudget);
            if (limit <= 0f)
                return add;

            var before = mutableGroupDeltas[group] - startingGroupDeltas[group];
            var after = before + add;
            float beforeMagnitude = before.magnitude;
            float afterMagnitude = after.magnitude;
            if (afterMagnitude <= limit || afterMagnitude <= beforeMagnitude)
                return add;

            var clampedAfter = after * (limit / afterMagnitude);
            var clampedAdd = clampedAfter - before;
            if (stats != null)
            {
                stats.cappedGroups++;
                stats.maxCappedCorrection = Mathf.Max(stats.maxCappedCorrection, afterMagnitude - limit);
            }

            return clampedAdd.sqrMagnitude > MeaningfulCorrection * MeaningfulCorrection
                ? clampedAdd
                : Vector3.zero;
        }

        private static void ClampTotalCorrections(
            Vector3[] startingGroupDeltas,
            Vector3[] mutableGroupDeltas,
            ReFitSettings settings,
            Context context,
            ReFitClearanceCorrectionStats stats,
            bool[] supportPropagationGroups = null)
        {
            if (startingGroupDeltas == null || mutableGroupDeltas == null || settings == null || stats == null)
                return;

            int count = Mathf.Min(startingGroupDeltas.Length, mutableGroupDeltas.Length);
            for (int g = 0; g < count; g++)
            {
                bool supportPropagationBudget = supportPropagationGroups != null &&
                                                g < supportPropagationGroups.Length &&
                                                supportPropagationGroups[g];
                float limit = TotalCorrectionLimit(settings, context, g, supportPropagationBudget);
                if (limit <= 0f)
                    continue;

                var correction = mutableGroupDeltas[g] - startingGroupDeltas[g];
                float magnitude = correction.magnitude;
                if (magnitude <= limit)
                    continue;

                mutableGroupDeltas[g] = startingGroupDeltas[g] + correction * (limit / magnitude);
                stats.cappedGroups++;
                stats.maxCappedCorrection = Mathf.Max(stats.maxCappedCorrection, magnitude - limit);
            }
        }

        private static float TotalCorrectionLimit(
            ReFitSettings settings,
            Context context,
            int group,
            bool supportPropagationBudget = false)
        {
            float limit = context != null && context.transferredBlendshape
                ? Mathf.Max(0f, settings.clearanceMaxTransferredTotalCorrection)
                : Mathf.Max(0f, settings.clearanceMaxPrimaryTotalCorrection);

            if (!supportPropagationBudget)
            {
                float hem = HemWeight(context, group);
                if (hem > 0f)
                    limit *= Mathf.Lerp(1f, Mathf.Clamp01(settings.upperBodyGarmentHemFollowScale), hem);

                limit *= DesiredCorrectionScale(settings, context, group);
            }
            return limit;
        }

        private static float HemWeight(Context context, int group)
        {
            return context != null && context.upperBodyHemWeights != null &&
                   group >= 0 && group < context.upperBodyHemWeights.Length
                ? Mathf.Clamp01(context.upperBodyHemWeights[group])
                : 0f;
        }

        private static float OpenBoundaryWeight(Context context, int group)
        {
            return context != null && context.openBoundaryWeights != null &&
                   group >= 0 && group < context.openBoundaryWeights.Length
                ? Mathf.Clamp01(context.openBoundaryWeights[group])
                : 0f;
        }

        private static bool IsLowConfidenceBinding(Context context, int group)
        {
            if (context == null || group < 0)
                return false;

            if (context.bindingRelaxed != null && group < context.bindingRelaxed.Length && context.bindingRelaxed[group])
                return true;

            return context.bindingNormalDots != null &&
                   group < context.bindingNormalDots.Length &&
                   context.bindingNormalDots[group] < TrustedBindingNormalDot;
        }

        private static SurfaceBvh.Hit ClosestCompatiblePoint(
            Vector3 point,
            float queryRange,
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            ReFitSettings settings,
            Context context,
            BodyRegion region,
            Vector3 referenceNormal)
        {
            bool useRegion = context != null && context.targetTriangleRegions != null && region != BodyRegion.Unknown;
            bool useNormal = settings != null && settings.filterByNormal && referenceNormal.sqrMagnitude > 1e-12f;
            if (!useRegion && !useNormal)
                return bvh.ClosestPoint(point, queryRange, null);

            var normal = referenceNormal.sqrMagnitude > 1e-12f ? referenceNormal.normalized : Vector3.zero;
            float minDot = settings != null
                ? Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad)
                : -1f;

            Func<int, bool> filter = t =>
            {
                if (useRegion && (t < 0 || t >= context.targetTriangleRegions.Length ||
                                  !HumanoidBoneMapper.RegionsCompatible(region, context.targetTriangleRegions[t])))
                    return false;
                if (useNormal && Vector3.Dot(normal, bodySurface.FaceNormal(t)) < minDot)
                    return false;
                return true;
            };

            var hit = bvh.ClosestPoint(point, queryRange, filter);
            if (hit.found)
                return hit;

            if (useRegion)
            {
                Func<int, bool> regionOnly = t => t >= 0 && t < context.targetTriangleRegions.Length &&
                                                  HumanoidBoneMapper.RegionsCompatible(region, context.targetTriangleRegions[t]);
                hit = bvh.ClosestPoint(point, queryRange, regionOnly);
                if (hit.found)
                    return hit;
            }

            if (useNormal)
            {
                Func<int, bool> normalOnly = t => Vector3.Dot(normal, bodySurface.FaceNormal(t)) >= minDot;
                hit = bvh.ClosestPoint(point, queryRange, normalOnly);
            }

            return hit;
        }

        private static Vector3 SampleReferenceNormal(Context context, int g0, float w0, int g1, float w1, int g2, float w2)
        {
            if (context == null || context.referenceNormals == null)
                return Vector3.zero;

            var normal = Vector3.zero;
            AddNormal(context.referenceNormals, g0, w0, ref normal);
            AddNormal(context.referenceNormals, g1, w1, ref normal);
            AddNormal(context.referenceNormals, g2, w2, ref normal);
            return normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.zero;
        }

        private static void AddNormal(Vector3[] normals, int group, float weight, ref Vector3 normal)
        {
            if (group < 0 || group >= normals.Length || weight <= 0f)
                return;
            normal += normals[group] * weight;
        }

        private static BodyRegion SampleRegion(Context context, int g0, float w0, int g1, float w1, int g2, float w2)
        {
            if (context == null || context.assetGroupRegions == null)
                return BodyRegion.Unknown;

            BodyRegion best = BodyRegion.Unknown;
            float bestWeight = 0f;
            PickRegion(context.assetGroupRegions, g0, w0, ref best, ref bestWeight);
            PickRegion(context.assetGroupRegions, g1, w1, ref best, ref bestWeight);
            PickRegion(context.assetGroupRegions, g2, w2, ref best, ref bestWeight);
            return best;
        }

        private static void PickRegion(BodyRegion[] regions, int group, float weight, ref BodyRegion best, ref float bestWeight)
        {
            if (group < 0 || group >= regions.Length || weight <= bestWeight || regions[group] == BodyRegion.Unknown)
                return;
            best = regions[group];
            bestWeight = weight;
        }

        private static float EligibleSampleWeight(
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            bool[] allowedGroups,
            int group,
            float weight)
        {
            if (group < 0 || weight <= 0f || profile == null || profile.eligible == null ||
                group >= profile.eligible.Length || !profile.eligible[group] ||
                !IsAllowedGroup(allowedGroups, group))
                return 0f;

            float f = falloff != null && group < falloff.Length ? Mathf.Clamp01(falloff[group]) : 1f;
            float expansion = expansions != null && group < expansions.Length ? expansions[group] : 0f;
            float expansionWeight = settings != null
                ? Smooth01(settings.clearanceExpansionStart, settings.clearanceExpansionFull, expansion)
                : 1f;
            if (expansionWeight <= 1e-4f)
                return 0f;

            return weight * f * expansionWeight;
        }

        private static bool AnyAllowedGroup(bool[] allowedGroups, int g0, int g1, int g2)
        {
            return IsAllowedGroup(allowedGroups, g0) ||
                   IsAllowedGroup(allowedGroups, g1) ||
                   IsAllowedGroup(allowedGroups, g2);
        }

        private static bool IsAllowedGroup(bool[] allowedGroups, int group)
        {
            return allowedGroups == null ||
                   (group >= 0 && group < allowedGroups.Length && allowedGroups[group]);
        }

        private static int GroupForVertex(MeshSnapshot asset, int vertex, int groupCount)
        {
            if (asset == null || asset.groupOfVertex == null || vertex < 0 || vertex >= asset.groupOfVertex.Length)
                return -1;

            int group = asset.groupOfVertex[vertex];
            return group >= 0 && group < groupCount ? group : -1;
        }

        private static MeshSnapshot BuildBodySurfaceSnapshot(MeshSnapshot body, Vector3[] bodyWorldDeltas)
        {
            if (body == null || body.worldVertices == null || body.triangles == null)
                return null;

            if (bodyWorldDeltas == null || bodyWorldDeltas.Length != body.worldVertices.Length)
                return body;

            bool hasDelta = false;
            var vertices = new Vector3[body.worldVertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var delta = bodyWorldDeltas[i];
                if (!hasDelta && delta.sqrMagnitude > 1e-12f)
                    hasDelta = true;
                vertices[i] = body.worldVertices[i] + delta;
            }

            if (!hasDelta)
                return body;

            return new MeshSnapshot
            {
                worldVertices = vertices,
                worldNormals = ComputeWorldNormals(vertices, body.triangles),
                triangles = body.triangles
            };
        }

        private static Vector3[] ComputeWorldNormals(Vector3[] vertices, int[] triangles)
        {
            var normals = new Vector3[vertices.Length];
            if (triangles != null)
            {
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int ia = triangles[t];
                    int ib = triangles[t + 1];
                    int ic = triangles[t + 2];
                    if (ia < 0 || ia >= vertices.Length || ib < 0 || ib >= vertices.Length ||
                        ic < 0 || ic >= vertices.Length)
                        continue;

                    var normal = Vector3.Cross(vertices[ib] - vertices[ia], vertices[ic] - vertices[ia]);
                    normals[ia] += normal;
                    normals[ib] += normal;
                    normals[ic] += normal;
                }
            }

            for (int i = 0; i < normals.Length; i++)
                normals[i] = normals[i].sqrMagnitude > 1e-12f ? normals[i].normalized : Vector3.up;
            return normals;
        }

        private static Vector3 DeltaAt(Vector3[] deltas, int index)
        {
            return deltas != null && index >= 0 && index < deltas.Length ? deltas[index] : Vector3.zero;
        }

        private static Vector3 SampleBodyDelta(MeshSnapshot body, Vector3[] bodyWorldDeltas, SurfaceBinding binding)
        {
            if (bodyWorldDeltas == null || body == null || body.triangles == null || binding.triangle < 0)
                return Vector3.zero;
            int t = binding.triangle * 3;
            if (t + 2 >= body.triangles.Length)
                return Vector3.zero;
            return bodyWorldDeltas[body.triangles[t]] * binding.bary.x +
                   bodyWorldDeltas[body.triangles[t + 1]] * binding.bary.y +
                   bodyWorldDeltas[body.triangles[t + 2]] * binding.bary.z;
        }

        private static float DesiredClearance(float sourceClearance, float expansion, ReFitSettings settings)
        {
            float safe = Mathf.Max(0f, settings.clearanceMinimumSafetyDistance);
            float source = Mathf.Max(sourceClearance, safe);
            float tight = Mathf.Max(safe, source * Mathf.Clamp01(settings.clearanceTightnessFactor));
            float t = Smooth01(settings.clearanceExpansionStart, settings.clearanceExpansionFull, expansion);
            return Mathf.Lerp(source, tight, t);
        }

        private static float Smooth01(float start, float end, float value)
        {
            if (Mathf.Abs(end - start) <= 1e-6f)
                return value >= end ? 1f : 0f;
            float t = Mathf.Clamp01((value - start) / (end - start));
            return t * t * (3f - 2f * t);
        }

        private static void SmoothMasked(
            Vector3[] values,
            bool[] active,
            System.Collections.Generic.List<int>[] adjacency,
            int iterations,
            float strength)
        {
            if (values == null || active == null || adjacency == null || iterations <= 0 || strength <= 0f)
                return;

            var buffer = new Vector3[values.Length];
            for (int it = 0; it < iterations; it++)
            {
                Parallel.For(0, values.Length, g =>
                {
                    if (!active[g])
                    {
                        buffer[g] = Vector3.zero;
                        return;
                    }

                    var sum = values[g];
                    int count = 1;
                    foreach (var nb in adjacency[g])
                    {
                        if (!active[nb])
                            continue;
                        sum += values[nb];
                        count++;
                    }

                    buffer[g] = Vector3.Lerp(values[g], sum / count, strength);
                });
                Array.Copy(buffer, values, values.Length);
            }
        }
    }
}
