using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Coarse body region used to reject implausible surface bindings (left leg vs right leg, etc.).</summary>
    public enum BodyRegion
    {
        Unknown = -1,
        Torso = 0,
        Head = 1,
        LeftArm = 2,
        RightArm = 3,
        LeftLeg = 4,
        RightLeg = 5
    }

    /// <summary>
    /// Maps avatar skeletons: humanoid bones via the Animator definition (with a name-based fallback),
    /// arbitrary bone name matching between hierarchies, and classification of bones into coarse body regions.
    /// </summary>
    public static class HumanoidBoneMapper
    {
        /// <summary>Returns the humanoid bone map of an avatar (HumanBodyBones -> Transform). Uses the Animator when available, otherwise a name-based fallback.</summary>
        public static Dictionary<HumanBodyBones, Transform> GetHumanoidMap(GameObject avatarRoot, ReFitReport report)
        {
            var map = new Dictionary<HumanBodyBones, Transform>();
            if (avatarRoot == null) return map;

            var animator = FindHumanoidAnimator(avatarRoot);
            if (animator != null)
            {
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    Transform t = null;
                    try { t = animator.GetBoneTransform(b); } catch { }
                    if (t != null) map[b] = t;
                }
                if (map.Count > 0) return map;
            }

            report?.Warn("no-humanoid-rig",
                $"'{avatarRoot.name}' has no humanoid Animator avatar. Falling back to bone-name matching; results may be less reliable.");
            FallbackNameMap(avatarRoot.transform, map);
            return map;
        }

        /// <summary>Finds the humanoid Animator of an avatar root, if any.</summary>
        public static Animator FindHumanoidAnimator(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;
            foreach (var animator in avatarRoot.GetComponentsInChildren<Animator>(true))
            {
                if (animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
                    return animator;
            }
            return null;
        }

        /// <summary>
        /// Builds a normalized-name -> Transform index of every transform under <paramref name="root"/>.
        /// Ambiguous names keep the first occurrence.
        /// </summary>
        public static Dictionary<string, Transform> BuildNameIndex(Transform root)
        {
            var index = new Dictionary<string, Transform>();
            if (root == null) return index;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var key = ReFitUtility.NormalizeName(t.name);
                if (key.Length == 0) continue;
                if (!index.ContainsKey(key)) index[key] = t;
            }
            return index;
        }

        /// <summary>
        /// Matches the bones of <paramref name="bones"/> into the hierarchy below <paramref name="otherRoot"/> by normalized name,
        /// with a humanoid-name fallback for common aliases such as "Left elbow" vs "forearm.L".
        /// Unmatched bones map to null.
        /// </summary>
        public static Dictionary<Transform, Transform> MatchBonesByName(IEnumerable<Transform> bones, Transform otherRoot)
        {
            var index = BuildNameIndex(otherRoot);
            var otherHumanMap = new Dictionary<HumanBodyBones, Transform>();
            FallbackNameMap(otherRoot, otherHumanMap);

            var result = new Dictionary<Transform, Transform>();
            foreach (var bone in bones)
            {
                if (bone == null || result.ContainsKey(bone)) continue;
                var key = ReFitUtility.NormalizeName(bone.name);
                index.TryGetValue(key, out var match);
                if (match == null &&
                    TryInferHumanBone(key, out var human) &&
                    otherHumanMap.TryGetValue(human, out var humanMatch))
                {
                    match = humanMatch;
                }
                result[bone] = match;
            }
            return result;
        }

        /// <summary>
        /// Classifies every transform under <paramref name="avatarRoot"/> into a <see cref="BodyRegion"/>:
        /// a bone inherits the region of its nearest humanoid ancestor.
        /// </summary>
        public static Dictionary<Transform, BodyRegion> ClassifyBones(GameObject avatarRoot, Dictionary<HumanBodyBones, Transform> humanMap)
        {
            var regions = new Dictionary<Transform, BodyRegion>();
            if (avatarRoot == null) return regions;

            var humanRegionOf = new Dictionary<Transform, BodyRegion>();
            foreach (var kv in humanMap)
            {
                if (kv.Value == null) continue;
                // First (most specific) human bone wins if several map to one transform.
                if (!humanRegionOf.ContainsKey(kv.Value)) humanRegionOf[kv.Value] = RegionOf(kv.Key);
            }

            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                var cur = t;
                var region = BodyRegion.Unknown;
                while (cur != null)
                {
                    if (humanRegionOf.TryGetValue(cur, out var r)) { region = r; break; }
                    if (cur == avatarRoot.transform) break;
                    cur = cur.parent;
                }
                regions[t] = region;
            }
            return regions;
        }

        /// <summary>Coarse region of a humanoid bone.</summary>
        public static BodyRegion RegionOf(HumanBodyBones bone)
        {
            var name = bone.ToString();
            bool left = name.StartsWith("Left");
            bool right = name.StartsWith("Right");
            if (name.Contains("Eye") || name == "Head" || name == "Jaw") return BodyRegion.Head;
            if (name.Contains("Shoulder") || name.Contains("Arm") || name.Contains("Hand") ||
                name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") ||
                name.Contains("Ring") || name.Contains("Little"))
                return left ? BodyRegion.LeftArm : right ? BodyRegion.RightArm : BodyRegion.Torso;
            if (name.Contains("Leg") || name.Contains("Foot") || name.Contains("Toes"))
                return left ? BodyRegion.LeftLeg : right ? BodyRegion.RightLeg : BodyRegion.Torso;
            return BodyRegion.Torso; // Hips, Spine, Chest, UpperChest, Neck
        }

        /// <summary>
        /// Whether a binding between two regions is plausible. Unknown and Torso are compatible with everything;
        /// left/right limb pairs, arm/leg pairs and head/limb pairs are rejected.
        /// </summary>
        public static bool RegionsCompatible(BodyRegion a, BodyRegion b)
        {
            if (a == BodyRegion.Unknown || b == BodyRegion.Unknown) return true;
            if (a == b) return true;
            if (a == BodyRegion.Torso || b == BodyRegion.Torso) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // Name-based humanoid fallback (used only for non-humanoid rigs)
        // ------------------------------------------------------------------

        private static readonly (HumanBodyBones bone, string[] patterns)[] FallbackPatterns =
        {
            (HumanBodyBones.Hips, new[] { "hips", "hip", "pelvis" }),
            (HumanBodyBones.Spine, new[] { "spine" }),
            (HumanBodyBones.Chest, new[] { "chest" }),
            (HumanBodyBones.UpperChest, new[] { "upperchest", "chestup", "chest2" }),
            (HumanBodyBones.Neck, new[] { "neck" }),
            (HumanBodyBones.Head, new[] { "head" }),
            (HumanBodyBones.LeftUpperLeg, new[] { "leftupperleg", "upperlegl", "leftleg", "thighl", "lthigh" }),
            (HumanBodyBones.RightUpperLeg, new[] { "rightupperleg", "upperlegr", "rightleg", "thighr", "rthigh" }),
            (HumanBodyBones.LeftLowerLeg, new[] { "leftlowerleg", "lowerlegl", "leftknee", "shinl", "calfl" }),
            (HumanBodyBones.RightLowerLeg, new[] { "rightlowerleg", "lowerlegr", "rightknee", "shinr", "calfr" }),
            (HumanBodyBones.LeftFoot, new[] { "leftfoot", "footl", "lfoot", "leftankle" }),
            (HumanBodyBones.RightFoot, new[] { "rightfoot", "footr", "rfoot", "rightankle" }),
            (HumanBodyBones.LeftShoulder, new[] { "leftshoulder", "shoulderl", "lshoulder", "leftclavicle" }),
            (HumanBodyBones.RightShoulder, new[] { "rightshoulder", "shoulderr", "rshoulder", "rightclavicle" }),
            (HumanBodyBones.LeftUpperArm, new[] { "leftupperarm", "upperarml", "leftarm", "larm" }),
            (HumanBodyBones.RightUpperArm, new[] { "rightupperarm", "upperarmr", "rightarm", "rarm" }),
            (HumanBodyBones.LeftLowerArm, new[] { "leftlowerarm", "lowerarml", "leftelbow", "forearml", "lefthandelbow" }),
            (HumanBodyBones.RightLowerArm, new[] { "rightlowerarm", "lowerarmr", "rightelbow", "forearmr" }),
            (HumanBodyBones.LeftHand, new[] { "lefthand", "handl", "lhand", "leftwrist" }),
            (HumanBodyBones.RightHand, new[] { "righthand", "handr", "rhand", "rightwrist" }),
        };

        private static void FallbackNameMap(Transform root, Dictionary<HumanBodyBones, Transform> map)
        {
            var index = BuildNameIndex(root);
            foreach (var (bone, patterns) in FallbackPatterns)
            {
                foreach (var p in patterns)
                {
                    if (index.TryGetValue(p, out var t)) { map[bone] = t; break; }
                }
            }
        }

        private static bool TryInferHumanBone(string normalizedName, out HumanBodyBones humanBone)
        {
            foreach (var (bone, patterns) in FallbackPatterns)
            {
                foreach (var p in patterns)
                {
                    if (normalizedName == p)
                    {
                        humanBone = bone;
                        return true;
                    }
                }
            }

            humanBone = HumanBodyBones.LastBone;
            return false;
        }
    }
}
