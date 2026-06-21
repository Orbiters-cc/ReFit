using System;
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

        public sealed class Profile
        {
            public bool[] eligible;
            public float[] sourceClearance;
            public Vector3[] sourceBodyPoint;
            public int eligibleGroups;
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
            ReFitSettings settings)
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
                    amount = Mathf.Min(
                        error * Mathf.Clamp01(settings.clearanceOutwardStrength),
                        Mathf.Max(0f, settings.clearanceMaxOutwardCorrection));
                }
                else if (error < -MeaningfulCorrection)
                {
                    amount = -Mathf.Min(
                        -error * Mathf.Clamp01(settings.clearanceInwardStrength),
                        Mathf.Max(0f, settings.clearanceMaxInwardCorrection));
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
                stats);

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
                    g >= measured.Length || !measured[g])
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
                    mutableGroupDeltas[g] += normal * amount;
                    stats.safetyGuardGroups++;
                    stats.outwardGroups++;
                    stats.maxSafetyGuardCorrection = Mathf.Max(stats.maxSafetyGuardCorrection, amount);
                    stats.maxOutwardCorrection = Mathf.Max(stats.maxOutwardCorrection, amount);
                    finalClearance = currentClearance + amount;
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
            ReFitClearanceCorrectionStats stats)
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

            var bodySurface = BuildBodySurfaceSnapshot(targetBody, bodyWorldShapeDeltas);
            if (bodySurface == null || bodySurface.triangles == null || bodySurface.worldVertices == null ||
                bodySurface.worldVertices.Length == 0)
                return;

            var bvh = SurfaceBvh.Build(bodySurface);
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

                    var a = groupWorld[ga];
                    var b = groupWorld[gb];
                    var c = groupWorld[gc];

                    violatingSamples += AccumulateTriangleSurfaceGuardSamples(
                        a, b, c, ga, gb, gc,
                        profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                        corrections, weights, stats);
                }

                if (violatingSamples == 0)
                    break;

                stats.surfaceGuardSamples += violatingSamples;
                bool appliedAny = false;
                for (int g = 0; g < groupCount; g++)
                {
                    if (weights[g] <= 1e-6f)
                        continue;

                    var correction = corrections[g] / weights[g];
                    float magnitude = correction.magnitude;
                    if (magnitude <= MeaningfulCorrection)
                        continue;

                    if (magnitude > maxGuard)
                        correction *= maxGuard / magnitude;

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

            float remainingPenetration = MeasureWorstSurfacePenetration(
                asset, profile, falloff, expansions, settings, groupWorld, bodySurface, bvh, queryRange, safety);
            if (remainingPenetration > MeaningfulCorrection)
                stats.maxPenetrationAfter = Mathf.Max(stats.maxPenetrationAfter, remainingPenetration);
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
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            int violations = 0;
            violations += AccumulateEdgeSurfaceGuardSamples(a, b, ga, gb,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                corrections, weights, stats);
            violations += AccumulateEdgeSurfaceGuardSamples(b, c, gb, gc,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                corrections, weights, stats);
            violations += AccumulateEdgeSurfaceGuardSamples(c, a, gc, ga,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength,
                corrections, weights, stats);

            violations += AccumulateSurfaceGuardSample((a + b + c) / 3f, ga, 1f / 3f, gb, 1f / 3f, gc, 1f / 3f,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, corrections, weights, stats) ? 1 : 0;

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
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            int samples = Mathf.Clamp(settings != null ? settings.clearanceSurfaceGuardEdgeSamples : 1, 1, 3);
            int violations = 0;
            if (samples >= 2)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 1f / 3f), ga, 2f / 3f, gb, 1f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, corrections, weights, stats) ? 1 : 0;
            }

            if (samples == 1 || samples >= 3)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 0.5f), ga, 0.5f, gb, 0.5f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, corrections, weights, stats) ? 1 : 0;
            }

            if (samples >= 2)
            {
                violations += AccumulateSurfaceGuardSample(Vector3.Lerp(a, b, 2f / 3f), ga, 1f / 3f, gb, 2f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety, triggerDistance, strength, corrections, weights, stats) ? 1 : 0;
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
            Vector3[] corrections,
            float[] weights,
            ReFitClearanceCorrectionStats stats)
        {
            float ew0 = EligibleSampleWeight(profile, falloff, expansions, settings, g0, w0);
            float ew1 = EligibleSampleWeight(profile, falloff, expansions, settings, g1, w1);
            float ew2 = EligibleSampleWeight(profile, falloff, expansions, settings, g2, w2);
            float totalWeight = ew0 + ew1 + ew2;
            if (totalWeight <= 1e-6f)
                return false;

            var hit = bvh.ClosestPoint(point, queryRange, null);
            if (!hit.found)
                return false;

            var normal = bodySurface.BaryNormal(hit.triangle, hit.bary);
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

                var a = groupWorld[ga];
                var b = groupWorld[gb];
                var c = groupWorld[gc];
                worst = Mathf.Max(worst, MeasureTriangleSurfacePenetration(
                    a, b, c, ga, gb, gc, profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            }

            return worst;
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
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            float worst = 0f;
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(a, b, ga, gb,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(b, c, gb, gc,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, MeasureEdgeSurfacePenetration(c, a, gc, ga,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            worst = Mathf.Max(worst, SurfacePenetrationAt((a + b + c) / 3f, ga, 1f / 3f, gb, 1f / 3f, gc, 1f / 3f,
                profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
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
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            }

            if (samples == 1 || samples >= 3)
            {
                worst = Mathf.Max(worst, SurfacePenetrationAt(Vector3.Lerp(a, b, 0.5f), ga, 0.5f, gb, 0.5f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
            }

            if (samples >= 2)
            {
                worst = Mathf.Max(worst, SurfacePenetrationAt(Vector3.Lerp(a, b, 2f / 3f), ga, 1f / 3f, gb, 2f / 3f, -1, 0f,
                    profile, falloff, expansions, settings, bodySurface, bvh, queryRange, safety));
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
            MeshSnapshot bodySurface,
            SurfaceBvh bvh,
            float queryRange,
            float safety)
        {
            if (EligibleSampleWeight(profile, falloff, expansions, settings, g0, w0) +
                EligibleSampleWeight(profile, falloff, expansions, settings, g1, w1) +
                EligibleSampleWeight(profile, falloff, expansions, settings, g2, w2) <= 1e-6f)
                return 0f;

            var hit = bvh.ClosestPoint(point, queryRange, null);
            if (!hit.found)
                return 0f;

            var normal = bodySurface.BaryNormal(hit.triangle, hit.bary);
            float clearance = Vector3.Dot(point - hit.position, normal);
            return clearance < 0f ? -clearance : 0f;
        }

        private static float EligibleSampleWeight(
            Profile profile,
            float[] falloff,
            float[] expansions,
            ReFitSettings settings,
            int group,
            float weight)
        {
            if (group < 0 || weight <= 0f || profile == null || profile.eligible == null ||
                group >= profile.eligible.Length || !profile.eligible[group])
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
