using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Compares humanoid landmark positions between the staged (scale-matched, hip-aligned) source and target
    /// avatars and emits a warning when proportions differ noticeably. This never blocks the operation: bases with
    /// intentionally relocated bones (e.g. for nicer deformations) are a supported workflow — the surface
    /// projection compensates for the difference.
    /// </summary>
    public static class ProportionChecker
    {
        private static readonly HumanBodyBones[] Landmarks =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
        };

        /// <summary>
        /// Returns the normalized mismatch score (0 = identical landmark layout) and records it (plus an optional
        /// warning) in the report. Returns NaN when the avatars cannot be compared.
        /// </summary>
        public static float Check(NormalizedStage stage, ReFitSettings settings, ReFitReport report)
        {
            if (stage == null || stage.sourceIsTarget) return float.NaN;

            float reference = ReferenceLength(stage.targetHumanMap);
            if (reference <= 1e-5f)
            {
                report.Warn("proportions-unmeasured", "Could not measure the avatars; skipping the proportion check.");
                return float.NaN;
            }

            float sum = 0f;
            int count = 0;
            var worst = new List<(string name, float d)>();
            foreach (var bone in Landmarks)
            {
                if (!TryGet(stage.sourceHumanMap, bone, out var s) || !TryGet(stage.targetHumanMap, bone, out var t)) continue;
                float d = Vector3.Distance(s.position, t.position);
                sum += d;
                count++;
                worst.Add((bone.ToString(), d));
            }

            if (count < 4)
            {
                report.Warn("proportions-unmeasured", "Too few comparable humanoid landmarks; skipping the proportion check.");
                return float.NaN;
            }

            float score = (sum / count) / reference;
            report.proportionScore = score;

            if (score > settings.proportionWarningThreshold)
            {
                worst.Sort((a, b) => b.d.CompareTo(a.d));
                var top = worst.GetRange(0, Mathf.Min(3, worst.Count));
                var details = string.Join(", ", top.ConvertAll(w => $"{w.name} ({w.d * 100f:0.#}cm)"));
                report.Warn("proportions-mismatch",
                    $"The avatars' proportions differ (score {score:0.###}, largest offsets: {details}). " +
                    "You can still proceed — if the meshes are similar but the bones were intentionally moved " +
                    "(e.g. for better deformations), the surface projection will compensate. " +
                    "If the bodies themselves are very different, expect to tweak the result.");
            }
            else
            {
                report.Info("proportions-ok", $"Avatar proportions match well (score {score:0.###}).");
            }
            return score;
        }

        private static float ReferenceLength(Dictionary<HumanBodyBones, Transform> map)
        {
            if (TryGet(map, HumanBodyBones.LeftHand, out var lh) && TryGet(map, HumanBodyBones.RightHand, out var rh))
                return Vector3.Distance(lh.position, rh.position);
            if (TryGet(map, HumanBodyBones.Hips, out var hips) && TryGet(map, HumanBodyBones.Head, out var head))
                return Vector3.Distance(hips.position, head.position) * 2f;
            return -1f;
        }

        private static bool TryGet(Dictionary<HumanBodyBones, Transform> map, HumanBodyBones b, out Transform t)
        {
            t = null;
            return map != null && map.TryGetValue(b, out t) && t != null;
        }
    }
}
