using System;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Shared fitting policy for the wizard and API clients. Does not change unrelated settings.</summary>
    public static class ReFitSettingsPresets
    {
        public static void ApplyTightness(ReFitSettings settings, float value)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            float clearanceTightnessPreset = Mathf.Clamp01(value);
            settings.clearanceTightnessFactor = LerpPreset(clearanceTightnessPreset, 0.85f, 0.5f, 0.08f);
            settings.clearanceMinimumSafetyDistance = LerpPreset(clearanceTightnessPreset, 0.006f, 0.003f, 0.002f);
            settings.clearanceMaxOutwardCorrection = LerpPreset(clearanceTightnessPreset, 0.04f, 0.04f, 0.12f);
            settings.clearanceMaxSurfaceGuardCorrection = LerpPreset(clearanceTightnessPreset, 0.001f, 0.003f, 0.08f);
            settings.clearanceSurfaceGuardTriggerDistance = LerpPreset(clearanceTightnessPreset, 0.003f, 0.0015f, 0.00025f);
            settings.clearanceMaxInwardCorrection = LerpPreset(clearanceTightnessPreset, 0.005f, 0.015f, 0.18f);
            settings.clearanceInwardStrength = LerpPreset(clearanceTightnessPreset, 0.2f, 0.65f, 0.82f);
            settings.clearanceExpansionStart = LerpPreset(clearanceTightnessPreset, 0.035f, 0.01f, 0.002f);
            settings.clearanceExpansionFull = LerpPreset(clearanceTightnessPreset, 0.12f, 0.06f, 0.025f);
            settings.clearanceSmoothingIterations = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 3f, 2f, 1f));
            settings.clearanceSmoothingStrength = LerpPreset(clearanceTightnessPreset, 0.65f, 0.5f, 0.35f);
            settings.clearanceSurfaceGuardIterations = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 0f, 1f, 6f));
            settings.clearanceSurfaceGuardStrength = LerpPreset(clearanceTightnessPreset, 0.25f, 0.45f, 1f);
            settings.clearanceSurfaceGuardEdgeSamples = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 1f, 1f, 3f));
            settings.clearanceMaxPrimaryTotalCorrection = LerpPreset(clearanceTightnessPreset, 0.025f, 0.06f, 0.06f);
            settings.clearanceMaxTransferredTotalCorrection = LerpPreset(clearanceTightnessPreset, 0.015f, 0.035f, 0.045f);
            settings.clearanceTransferredInwardScale = LerpPreset(clearanceTightnessPreset, 0.45f, 0.75f, 0.92f);
            settings.clearanceOpenBoundaryCorrectionScale = LerpPreset(clearanceTightnessPreset, 0.18f, 0.1f, 0.06f);
            settings.clearanceLowConfidenceCorrectionScale = LerpPreset(clearanceTightnessPreset, 0.55f, 0.4f, 0.28f);
            settings.upperBodyGarmentHemFollowScale = LerpPreset(clearanceTightnessPreset, 0.45f, 0.3f, 0.18f);
            settings.clearancePropagateDisconnectedIslands = true;
            settings.clearanceIslandPropagationStrength = LerpPreset(clearanceTightnessPreset, 0.25f, 0.6f, 0.85f);
            settings.clearanceIslandPropagationSearchDistance = LerpPreset(clearanceTightnessPreset, 0.05f, 0.08f, 0.12f);
            settings.clearanceMaxIslandPropagationCorrection = LerpPreset(clearanceTightnessPreset, 0.008f, 0.025f, 0.045f);
            settings.clearanceIslandPropagationMinDonorCorrection = 0.001f;
            settings.clearanceOutwardStrength = 1f;
        }

        private static float SmoothPreset(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        private static float LerpPreset(float value, float loose, float middle, float tight)
        {
            value = Mathf.Clamp01(value);
            if (value <= 0.5f)
                return Mathf.Lerp(loose, middle, SmoothPreset(value * 2f));
            return Mathf.Lerp(middle, tight, SmoothPreset((value - 0.5f) * 2f));
        }

    }
}
