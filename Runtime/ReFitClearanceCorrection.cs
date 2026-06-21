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
                expansions[g] = expansion;
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
