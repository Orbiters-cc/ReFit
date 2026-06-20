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
        public static Dictionary<HumanBodyBones, Transform> GetHumanoidMap(GameObject avatarRoot, ReFitReport report,
            Transform excludedRoot = null)
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
                    if (t != null && !IsExcluded(t, excludedRoot)) map[b] = t;
                }
                if (map.Count > 0) return map;
            }

            report?.Warn("no-humanoid-rig",
                $"'{avatarRoot.name}' has no humanoid Animator avatar. Falling back to bone-name matching; results may be less reliable.");
            FallbackNameMap(avatarRoot.transform, map, excludedRoot);
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
        /// Ambiguous names keep the first occurrence. Humanoid aliases are also indexed, so equivalent
        /// names like "Left arm", "upper_arm.L" and "LeftUpperArm" resolve to the same transform.
        /// </summary>
        public static Dictionary<string, Transform> BuildNameIndex(Transform root, Transform excludedRoot = null)
        {
            var index = BuildExactNameIndex(root, excludedRoot);
            var humanIndex = BuildHumanoidBoneIndex(root, null, excludedRoot);
            foreach (var kv in humanIndex)
                AddHumanAliases(index, kv.Key, kv.Value);
            return index;
        }

        /// <summary>Builds a humanoid-bone -> Transform index using Animator data when supplied and name aliases otherwise.</summary>
        public static Dictionary<HumanBodyBones, Transform> BuildHumanoidBoneIndex(Transform root,
            Dictionary<HumanBodyBones, Transform> seed = null, Transform excludedRoot = null)
        {
            var index = new Dictionary<HumanBodyBones, Transform>();
            if (seed != null)
            {
                foreach (var kv in seed)
                    if (kv.Value != null && !IsExcluded(kv.Value, excludedRoot) && !index.ContainsKey(kv.Key)) index[kv.Key] = kv.Value;
            }

            if (root == null) return index;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsExcluded(t, excludedRoot)) continue;
                if (TryInferHumanoidBone(t, out var bone) && !index.ContainsKey(bone))
                    index[bone] = t;
            }
            return index;
        }

        /// <summary>Infers the humanoid bone represented by a transform name, if the name is recognizable.</summary>
        public static bool TryInferHumanoidBone(Transform transform, out HumanBodyBones bone)
        {
            bone = HumanBodyBones.LastBone;
            return transform != null && TryInferHumanoidBone(transform.name, out bone);
        }

        /// <summary>Infers the humanoid bone represented by a name, if the name is recognizable.</summary>
        public static bool TryInferHumanoidBone(string name, out HumanBodyBones bone)
        {
            bone = HumanBodyBones.LastBone;
            var key = ReFitUtility.NormalizeName(name);
            if (key.Length == 0) return false;
            foreach (var entry in FallbackPatterns)
            {
                foreach (var pattern in entry.patterns)
                {
                    if (key == pattern)
                    {
                        bone = entry.bone;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Finds a target transform for a humanoid bone, falling back to closely related bones when absent.</summary>
        public static bool TryGetHumanoidEquivalent(Dictionary<HumanBodyBones, Transform> index,
            HumanBodyBones bone, out Transform transform)
        {
            transform = null;
            if (index == null || bone == HumanBodyBones.LastBone) return false;
            if (index.TryGetValue(bone, out transform) && transform != null) return true;

            foreach (var fallback in RelatedBones(bone))
                if (index.TryGetValue(fallback, out transform) && transform != null)
                    return true;

            transform = null;
            return false;
        }

        private static Dictionary<string, Transform> BuildExactNameIndex(Transform root, Transform excludedRoot = null)
        {
            var index = new Dictionary<string, Transform>();
            if (root == null) return index;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsExcluded(t, excludedRoot)) continue;
                var key = ReFitUtility.NormalizeName(t.name);
                if (key.Length == 0) continue;
                if (!index.ContainsKey(key)) index[key] = t;
            }
            return index;
        }

        /// <summary>
        /// Matches the bones of <paramref name="bones"/> into the hierarchy below <paramref name="otherRoot"/> by normalized name.
        /// Unmatched bones map to null.
        /// </summary>
        public static Dictionary<Transform, Transform> MatchBonesByName(IEnumerable<Transform> bones, Transform otherRoot)
        {
            var index = BuildNameIndex(otherRoot);
            var humanIndex = BuildHumanoidBoneIndex(otherRoot);
            var result = new Dictionary<Transform, Transform>();
            foreach (var bone in bones)
            {
                if (bone == null || result.ContainsKey(bone)) continue;
                if (!index.TryGetValue(ReFitUtility.NormalizeName(bone.name), out var match) &&
                    TryInferHumanoidBone(bone, out var human) &&
                    TryGetHumanoidEquivalent(humanIndex, human, out var humanMatch))
                    match = humanMatch;
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
            (HumanBodyBones.Spine, new[] { "spine", "spine1", "spine01", "waist" }),
            (HumanBodyBones.Chest, new[] { "chest", "torso", "ribcage", "spine2", "spine02" }),
            (HumanBodyBones.UpperChest, new[] { "upperchest", "chestup", "upchest", "chestupper", "spine3", "spine03" }),
            (HumanBodyBones.Neck, new[] { "neck" }),
            (HumanBodyBones.Head, new[] { "head" }),
            (HumanBodyBones.Jaw, new[] { "jaw", "mandible" }),
            (HumanBodyBones.LeftUpperLeg, new[] { "leftupperleg", "upperlegl", "lupperleg", "leftleg", "thighl", "lthigh", "legl" }),
            (HumanBodyBones.RightUpperLeg, new[] { "rightupperleg", "upperlegr", "rupperleg", "rightleg", "thighr", "rthigh", "legr" }),
            (HumanBodyBones.LeftLowerLeg, new[] { "leftlowerleg", "lowerlegl", "llowerleg", "leftknee", "kneel", "shinl", "calfl" }),
            (HumanBodyBones.RightLowerLeg, new[] { "rightlowerleg", "lowerlegr", "rlowerleg", "rightknee", "kneer", "shinr", "calfr" }),
            (HumanBodyBones.LeftFoot, new[] { "leftfoot", "footl", "lfoot", "leftankle", "anklel" }),
            (HumanBodyBones.RightFoot, new[] { "rightfoot", "footr", "rfoot", "rightankle", "ankler" }),
            (HumanBodyBones.LeftToes, new[] { "lefttoes", "toesl", "ltoes", "lefttoe", "toel" }),
            (HumanBodyBones.RightToes, new[] { "righttoes", "toesr", "rtoes", "righttoe", "toer" }),
            (HumanBodyBones.LeftShoulder, new[] { "leftshoulder", "shoulderl", "lshoulder", "leftclavicle", "claviclel", "lclavicle" }),
            (HumanBodyBones.RightShoulder, new[] { "rightshoulder", "shoulderr", "rshoulder", "rightclavicle", "clavicler", "rclavicle" }),
            (HumanBodyBones.LeftUpperArm, new[] { "leftupperarm", "upperarml", "lupperarm", "leftarm", "larm", "arml" }),
            (HumanBodyBones.RightUpperArm, new[] { "rightupperarm", "upperarmr", "rupperarm", "rightarm", "rarm", "armr" }),
            (HumanBodyBones.LeftLowerArm, new[] { "leftlowerarm", "lowerarml", "llowerarm", "leftelbow", "elbowl", "forearml", "lforearm" }),
            (HumanBodyBones.RightLowerArm, new[] { "rightlowerarm", "lowerarmr", "rlowerarm", "rightelbow", "elbowr", "forearmr", "rforearm" }),
            (HumanBodyBones.LeftHand, new[] { "lefthand", "handl", "lhand", "leftwrist", "wristl" }),
            (HumanBodyBones.RightHand, new[] { "righthand", "handr", "rhand", "rightwrist", "wristr" }),
        };

        private static void AddHumanAliases(Dictionary<string, Transform> index, HumanBodyBones bone, Transform t)
        {
            foreach (var entry in FallbackPatterns)
            {
                if (entry.bone != bone) continue;
                foreach (var pattern in entry.patterns)
                    if (!index.ContainsKey(pattern)) index[pattern] = t;
                return;
            }
        }

        private static IEnumerable<HumanBodyBones> RelatedBones(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.UpperChest:
                    break;
                case HumanBodyBones.Chest:
                    yield return HumanBodyBones.UpperChest;
                    yield return HumanBodyBones.Spine;
                    break;
                case HumanBodyBones.Neck:
                    yield return HumanBodyBones.Head;
                    yield return HumanBodyBones.UpperChest;
                    yield return HumanBodyBones.Chest;
                    break;
                case HumanBodyBones.LeftShoulder:
                    yield return HumanBodyBones.LeftUpperArm;
                    yield return HumanBodyBones.UpperChest;
                    yield return HumanBodyBones.Chest;
                    break;
                case HumanBodyBones.RightShoulder:
                    yield return HumanBodyBones.RightUpperArm;
                    yield return HumanBodyBones.UpperChest;
                    yield return HumanBodyBones.Chest;
                    break;
                case HumanBodyBones.LeftToes:
                    yield return HumanBodyBones.LeftFoot;
                    break;
                case HumanBodyBones.RightToes:
                    yield return HumanBodyBones.RightFoot;
                    break;
            }
        }

        private static void FallbackNameMap(Transform root, Dictionary<HumanBodyBones, Transform> map, Transform excludedRoot = null)
        {
            var index = BuildExactNameIndex(root, excludedRoot);
            foreach (var (bone, patterns) in FallbackPatterns)
            {
                foreach (var p in patterns)
                {
                    if (index.TryGetValue(p, out var t)) { map[bone] = t; break; }
                }
            }
        }

        private static bool IsExcluded(Transform transform, Transform excludedRoot)
        {
            return transform != null && excludedRoot != null && transform.IsChildOf(excludedRoot);
        }
    }
}
