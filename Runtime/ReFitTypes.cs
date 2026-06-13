using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>What kind of re-fit to perform.</summary>
    public enum ReFitMode
    {
        /// <summary>Deform the asset from source avatar A's body surface to target avatar B's body surface.</summary>
        MeshToMesh,
        /// <summary>Transfer a blendshape of the target avatar's body onto the asset (asset already fits the target).</summary>
        Blendshape,
        /// <summary>Both: fit the asset from A to B, and additionally transfer a blendshape of B onto the asset.</summary>
        MeshAndBlendshape
    }

    /// <summary>How the asset's offset from the body surface is preserved during projection.</summary>
    public enum OffsetMode
    {
        /// <summary>The asset vertex follows the translation of its closest body surface point (ShapeKeyTransfer-style). Robust default.</summary>
        Translate,
        /// <summary>The asset vertex offset is additionally rotated from the source surface normal to the target surface normal. Better for strongly rotated surfaces, more sensitive to noisy normals.</summary>
        RotateWithNormal
    }

    /// <summary>Tuning options for a re-fit operation. Defaults are sensible for ~1.5m humanoid avatars (units: meters).</summary>
    [Serializable]
    public class ReFitSettings
    {
        /// <summary>Asset vertices farther than this from the body surface are not deformed at all.</summary>
        public float maxProjectionDistance = 0.25f;
        /// <summary>Asset vertices closer than this to the body surface get the full deformation; between this and <see cref="maxProjectionDistance"/> the deformation fades out smoothly.</summary>
        public float falloffStartDistance = 0.06f;
        /// <summary>Laplacian smoothing iterations applied to the computed deformation field.</summary>
        public int smoothingIterations = 3;
        /// <summary>Strength of each smoothing iteration (0..1).</summary>
        [Range(0f, 1f)] public float smoothingStrength = 0.6f;
        /// <summary>Reject body surface candidates whose normal disagrees with the asset vertex normal by more than <see cref="maxNormalAngle"/> degrees.</summary>
        public bool filterByNormal = true;
        /// <summary>Maximum angle (degrees) between asset vertex normal and body face normal for a binding to be accepted.</summary>
        public float maxNormalAngle = 80f;
        /// <summary>Reject bindings between incompatible body regions (e.g. left leg vs right leg, arm vs leg) using humanoid bone weights.</summary>
        public bool filterByBoneRegion = true;
        /// <summary>Project the target body's skin weights onto the asset (only used when the armature is replaced).</summary>
        public bool transferWeights = true;
        /// <summary>Replace the asset's bones with the target avatar's bones (MeshToMesh / MeshAndBlendshape modes).</summary>
        public bool replaceArmature = true;
        /// <summary>Vertices mostly weighted to bones that have no equivalent on the target (skirt/physics bones, buttons...) keep their original weights, and those bones are preserved.</summary>
        public bool keepExtraBoneVertices = true;
        /// <summary>Fraction of skin weight on unmapped bones above which a vertex keeps its original weights.</summary>
        [Range(0f, 1f)] public float extraBoneWeightThreshold = 0.4f;
        /// <summary>Name of the generated blendshape carrying the mesh deformation.</summary>
        public string blendshapeName = "refit";
        /// <summary>
        /// When true (default), transferred blendshapes are named "&lt;blendshapeName&gt;_&lt;shape&gt;".
        /// When false they keep the exact body shape name, so animations/links driving the body shape name
        /// can drive the asset too (used by the MCB integration).
        /// </summary>
        public bool prefixTransferredShapes = true;
        /// <summary>How the asset-to-body offset is preserved.</summary>
        public OffsetMode offsetMode = OffsetMode.Translate;
        /// <summary>Also bake per-vertex normal deltas into the generated blendshape for correct lighting at full weight.</summary>
        public bool recalculateNormalDeltas = true;
        /// <summary>Try to save a standalone prefab of the result when the armature is kept (editor pipeline).</summary>
        public bool savePrefab = true;
        /// <summary>Normalized landmark mismatch above which a proportion warning is emitted (never blocks).</summary>
        public float proportionWarningThreshold = 0.05f;

        /// <summary>Creates a deep copy of these settings.</summary>
        public ReFitSettings Clone() => (ReFitSettings)MemberwiseClone();
    }

    /// <summary>
    /// Describes a complete re-fit operation. Fill it and pass it to the editor facade
    /// (<c>Orbiters.ReFit.Editor.ReFitService.Execute</c>) or to <see cref="ReFitEngine.Run"/> directly.
    /// </summary>
    public class ReFitRequest
    {
        /// <summary>The clothing / accessory mesh to re-fit. Required. May be a scene object or part of a prefab asset.</summary>
        public SkinnedMeshRenderer assetRenderer;
        /// <summary>Avatar the asset was originally made for (model A). Scene object or prefab. Optional for <see cref="ReFitMode.Blendshape"/> (the target is then used as source).</summary>
        public GameObject sourceAvatar;
        /// <summary>Avatar the asset should fit (model B). Required. Scene object or prefab.</summary>
        public GameObject targetAvatar;
        /// <summary>Optional override of the source avatar's main body renderer (auto-detected otherwise).</summary>
        public SkinnedMeshRenderer sourceBodyRenderer;
        /// <summary>Optional override of the target avatar's main body renderer (auto-detected otherwise).</summary>
        public SkinnedMeshRenderer targetBodyRenderer;
        /// <summary>Name of the blendshape on the target body to transfer (Blendshape / MeshAndBlendshape modes).</summary>
        public string targetBlendshape;
        /// <summary>
        /// Multiple blendshapes to transfer in one pass (bindings are computed once and reused — much faster
        /// than running once per shape). When set, takes precedence over <see cref="targetBlendshape"/>.
        /// Names not found on the target body are skipped with a warning.
        /// </summary>
        public List<string> targetBlendshapes;
        /// <summary>What to do.</summary>
        public ReFitMode mode = ReFitMode.MeshToMesh;
        /// <summary>Tuning options.</summary>
        public ReFitSettings settings = new ReFitSettings();
    }

    /// <summary>Severity of a <see cref="ReFitMessage"/>.</summary>
    public enum ReFitSeverity { Info, Warning, Error }

    /// <summary>A single diagnostic emitted during a re-fit.</summary>
    public class ReFitMessage
    {
        public ReFitSeverity severity;
        /// <summary>Stable machine-readable identifier (e.g. "proportions-mismatch").</summary>
        public string code;
        /// <summary>Human readable explanation.</summary>
        public string text;
        public override string ToString() => $"[{severity}] {code}: {text}";
    }

    /// <summary>Collected diagnostics of a re-fit operation.</summary>
    public class ReFitReport
    {
        public readonly List<ReFitMessage> messages = new List<ReFitMessage>();
        /// <summary>Normalized landmark mismatch between source and target (0 = identical proportions). NaN when not evaluated.</summary>
        public float proportionScore = float.NaN;

        public bool HasErrors
        {
            get { foreach (var m in messages) if (m.severity == ReFitSeverity.Error) return true; return false; }
        }
        public bool HasWarnings
        {
            get { foreach (var m in messages) if (m.severity == ReFitSeverity.Warning) return true; return false; }
        }

        public void Info(string code, string text) => Add(ReFitSeverity.Info, code, text);
        public void Warn(string code, string text) => Add(ReFitSeverity.Warning, code, text);
        public void Error(string code, string text) => Add(ReFitSeverity.Error, code, text);
        public void Add(ReFitSeverity severity, string code, string text)
        {
            messages.Add(new ReFitMessage { severity = severity, code = code, text = text });
            switch (severity)
            {
                case ReFitSeverity.Error: Debug.LogError($"[ReFit] {code}: {text}"); break;
                case ReFitSeverity.Warning: Debug.LogWarning($"[ReFit] {code}: {text}"); break;
                default: Debug.Log($"[ReFit] {code}: {text}"); break;
            }
        }
    }

    /// <summary>Progress callback: <c>t</c> in [0,1], <c>label</c> describes the current phase.</summary>
    public delegate void ReFitProgress(float t, string label);

    /// <summary>Where a kept (non-replaced) bone lives.</summary>
    public enum ReFitBoneOrigin
    {
        /// <summary>Resolved on the target avatar hierarchy.</summary>
        Target,
        /// <summary>Kept from the asset's own hierarchy.</summary>
        Asset,
        /// <summary>Kept from the source avatar hierarchy (must be duplicated onto the target by the applier).</summary>
        SourceAvatar
    }

    /// <summary>Reference to a bone of the produced skinning, expressed as a child-index path so it can be resolved on clones / fresh prefab instances.</summary>
    public class ReFitBoneRef
    {
        public ReFitBoneOrigin origin;
        /// <summary>Child-index path relative to the origin root (target avatar root, asset root or source avatar root).</summary>
        public int[] path;
    }

    /// <summary>Placement of the root of a kept bone subtree, relative to a bone of the target avatar.</summary>
    public class ReFitKeptBonePlacement
    {
        /// <summary>Index into <see cref="ReFitComputation.bones"/> of the kept subtree root.</summary>
        public int boneIndex;
        /// <summary>Child-index path of the new parent, relative to the target avatar root.</summary>
        public int[] targetParentPath;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    /// <summary>
    /// Pure result of the geometry computation (no assets saved, no scene modified).
    /// The editor layer (<c>ReFitAssetPipeline</c>) turns this into saved assets and scene changes.
    /// </summary>
    public class ReFitComputation
    {
        public bool success;
        public ReFitReport report = new ReFitReport();
        /// <summary>New mesh instance (not saved to disk) with the generated blendshape(s), and new bindposes/weights when the armature is replaced.</summary>
        public Mesh mesh;
        /// <summary>The mesh the scene renderer used before the re-fit was applied (for reverting). Set by the editor pipeline.</summary>
        public Mesh appliedOriginalMesh;
        /// <summary>Name of the generated main "refit" blendshape, null when not generated.</summary>
        public string primaryShapeName;
        /// <summary>Names of the generated transferred blendshapes (one per requested target shape), null/empty when none.</summary>
        public string[] secondaryShapeNames;
        /// <summary>For each transferred shape, the weight it currently has on the target body (used to mirror it on the asset).</summary>
        public float[] secondaryMirrorWeights;
        /// <summary>True when <see cref="bones"/>/<see cref="rootBoneIndex"/> describe a new skinning that must be applied to the renderer.</summary>
        public bool armatureReplaced;
        /// <summary>New bone list (aligned with the mesh bindposes), null when the armature is kept.</summary>
        public ReFitBoneRef[] bones;
        /// <summary>Kept bone subtree roots that must be (re)attached under target bones.</summary>
        public ReFitKeptBonePlacement[] keptPlacements;
        /// <summary>Index into <see cref="bones"/> to use as the renderer root bone, -1 if unavailable.</summary>
        public int rootBoneIndex = -1;
        /// <summary>Child-index path of the asset renderer inside the asset root hierarchy.</summary>
        public int[] assetRendererPath;
    }

    /// <summary>Small helpers shared across the package.</summary>
    public static class ReFitUtility
    {
        /// <summary>Builds the child-index path of <paramref name="t"/> relative to <paramref name="root"/>; null if not a descendant.</summary>
        public static int[] IndexPath(Transform t, Transform root)
        {
            if (t == null || root == null) return null;
            var stack = new List<int>();
            var cur = t;
            while (cur != null && cur != root)
            {
                if (cur.parent == null) return null;
                stack.Add(cur.GetSiblingIndex());
                cur = cur.parent;
            }
            if (cur != root) return null;
            stack.Reverse();
            return stack.ToArray();
        }

        /// <summary>Resolves a child-index path produced by <see cref="IndexPath"/> on another (structurally identical) hierarchy.</summary>
        public static Transform ResolvePath(Transform root, int[] path)
        {
            if (root == null || path == null) return null;
            var cur = root;
            foreach (var i in path)
            {
                if (i < 0 || i >= cur.childCount) return null;
                cur = cur.GetChild(i);
            }
            return cur;
        }

        /// <summary>Lower-cases and strips separators so "Upper_Leg.L" and "UpperLegL" compare equal.</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == ' ' || c == '_' || c == '.' || c == '-' || c == ':') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
