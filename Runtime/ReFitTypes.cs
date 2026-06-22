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
        public const int DefaultPrimarySmoothingIterations = 3;
        public const float DefaultPrimarySmoothingStrength = 0.6f;
        public const int DefaultTransferredBlendshapeSmoothingIterations = 0;
        public const float DefaultTransferredBlendshapeSmoothingStrength = 0f;

        /// <summary>Asset vertices farther than this from the body surface are not deformed at all.</summary>
        public float maxProjectionDistance = 0.25f;
        /// <summary>Asset vertices closer than this to the body surface get the full deformation; between this and <see cref="maxProjectionDistance"/> the deformation fades out smoothly.</summary>
        public float falloffStartDistance = 0.06f;
        /// <summary>Laplacian smoothing iterations applied to the primary mesh-to-mesh deformation field.</summary>
        public int primarySmoothingIterations = DefaultPrimarySmoothingIterations;
        /// <summary>Strength of each primary mesh-to-mesh smoothing iteration (0..1).</summary>
        [Range(0f, 1f)] public float primarySmoothingStrength = DefaultPrimarySmoothingStrength;
        /// <summary>Laplacian smoothing iterations applied to transferred body blendshape deformation fields.</summary>
        public int transferredBlendshapeSmoothingIterations = DefaultTransferredBlendshapeSmoothingIterations;
        /// <summary>Strength of each transferred blendshape smoothing iteration (0..1).</summary>
        [Range(0f, 1f)] public float transferredBlendshapeSmoothingStrength = DefaultTransferredBlendshapeSmoothingStrength;
        /// <summary>Preserve small detached accessory components when a transferred body blendshape only affects part of the component.</summary>
        public bool stabilizeDetachedTransferredComponents = true;
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
        /// <summary>Capture per-projection diagnostics for editor debug gizmos. Intended for debug runs only.</summary>
        public bool captureProjectionDebug = false;
        /// <summary>Maximum number of projection groups captured for debug visualization. 0 or less captures all groups.</summary>
        public int maxProjectionDebugGroups = 5000;
        /// <summary>Preserve clothing/body signed clearance after mesh refit and transferred body blendshapes.</summary>
        public bool enableClearanceCorrection = true;
        /// <summary>Small body clearance kept even when the adaptive target clearance is compressed by muscle expansion.</summary>
        public float clearanceMinimumSafetyDistance = 0.003f;
        /// <summary>Source clearance multiplier retained on strongly expanded body areas; lower values make clothing fit tighter there.</summary>
        [Range(0f, 1f)] public float clearanceTightnessFactor = 0.5f;
        /// <summary>Body expansion distance where adaptive clearance compression starts.</summary>
        public float clearanceExpansionStart = 0.01f;
        /// <summary>Body expansion distance where adaptive clearance compression reaches <see cref="clearanceTightnessFactor"/>.</summary>
        public float clearanceExpansionFull = 0.06f;
        /// <summary>Maximum outward correction per pass, in meters.</summary>
        public float clearanceMaxOutwardCorrection = 0.04f;
        /// <summary>Maximum inward correction per pass, in meters. Keep below outward correction to avoid shrinkwrap.</summary>
        public float clearanceMaxInwardCorrection = 0.015f;
        /// <summary>Scale applied to outward correction before clamping.</summary>
        [Range(0f, 1f)] public float clearanceOutwardStrength = 1f;
        /// <summary>Scale applied to inward correction before clamping.</summary>
        [Range(0f, 1f)] public float clearanceInwardStrength = 0.65f;
        /// <summary>Smoothing iterations applied only to the clearance correction field.</summary>
        public int clearanceSmoothingIterations = 2;
        /// <summary>Strength of each clearance correction smoothing iteration.</summary>
        [Range(0f, 1f)] public float clearanceSmoothingStrength = 0.5f;
        /// <summary>Extra edge/triangle surface samples used after smoothing to catch curved-surface clipping between vertices.</summary>
        public int clearanceSurfaceGuardIterations = 1;
        /// <summary>Strength of each surface guard iteration.</summary>
        [Range(0f, 1f)] public float clearanceSurfaceGuardStrength = 0.45f;
        /// <summary>Maximum per-iteration correction from edge/triangle surface samples, in meters.</summary>
        public float clearanceMaxSurfaceGuardCorrection = 0.003f;
        /// <summary>Penetration depth required before edge/triangle surface samples are corrected, in meters.</summary>
        public float clearanceSurfaceGuardTriggerDistance = 0.0015f;
        /// <summary>Number of checked samples per triangle edge for the surface guard.</summary>
        public int clearanceSurfaceGuardEdgeSamples = 1;
        /// <summary>Maximum total clearance correction added to the primary refit field for one welded group.</summary>
        public float clearanceMaxPrimaryTotalCorrection = 0.06f;
        /// <summary>Maximum total clearance correction added to one transferred blendshape field for one welded group.</summary>
        public float clearanceMaxTransferredTotalCorrection = 0.045f;
        /// <summary>Scale applied to inward tightening when correcting transferred blendshapes.</summary>
        [Range(0f, 1f)] public float clearanceTransferredInwardScale = 1f;
        /// <summary>Scale applied to transferred clearance correction on open garment boundary groups.</summary>
        [Range(0f, 1f)] public float clearanceOpenBoundaryCorrectionScale = 0.08f;
        /// <summary>Scale applied to transferred clearance correction when the body binding used a relaxed or low-normal match.</summary>
        [Range(0f, 1f)] public float clearanceLowConfidenceCorrectionScale = 0.35f;
        /// <summary>Scale applied to upper-body garment hem following and inward tightening on lower-body classified groups.</summary>
        [Range(0f, 1f)] public float upperBodyGarmentHemFollowScale = 0.25f;
        /// <summary>Propagate clearance corrections from corrected garment polygons to nearby disconnected garment islands.</summary>
        public bool clearancePropagateDisconnectedIslands = true;
        /// <summary>Strength of topology-independent garment-to-garment correction propagation.</summary>
        [Range(0f, 1f)] public float clearanceIslandPropagationStrength = 0.6f;
        /// <summary>Maximum distance, in meters, for disconnected garment islands to inherit nearby garment corrections.</summary>
        public float clearanceIslandPropagationSearchDistance = 0.08f;
        /// <summary>Maximum extra correction added by disconnected island propagation, in meters.</summary>
        public float clearanceMaxIslandPropagationCorrection = 0.025f;
        /// <summary>Minimum donor correction magnitude, in meters, before a garment polygon can propagate its correction.</summary>
        public float clearanceIslandPropagationMinDonorCorrection = 0.001f;

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
        /// <summary>Diagnostic name of the referenced bone at computation time.</summary>
        public string name;
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

    /// <summary>Non-deforming tail helper for a rebuilt leaf bone, stored local to the rebuilt bone.</summary>
    [Serializable]
    public class ReFitLeafTailHint
    {
        public int boneIndex = -1;
        public string name;
        public Vector3 localPosition;
        public string source;
    }

    /// <summary>How a vertex/group skin weight was selected during armature replacement.</summary>
    public enum ReFitWeightDecision
    {
        None,
        Projected,
        Original,
        Blended,
        ExtraPreserved,
        Fallback
    }

    /// <summary>Debug outputs from weight transfer, aligned to asset groups / vertices.</summary>
    public class ReFitWeightTransferDebugInfo
    {
        public BoneWeight[] projectedByGroup;
        public bool[] projectedValidByGroup;
        public BoneWeight[] originalByVertex;
        public bool[] originalValidByVertex;
        public BoneWeight[] finalByVertex;
        public ReFitWeightDecision[] decisionsByVertex;
    }

    /// <summary>Aggregate stats for the clearance correction pass.</summary>
    [Serializable]
    public class ReFitClearanceCorrectionStats
    {
        public int eligibleGroups;
        public int outwardGroups;
        public int inwardGroups;
        public float maxOutwardCorrection;
        public float maxInwardCorrection;
        public float maxPenetrationBefore;
        public float maxPenetrationAfter;
        public int safetyGuardGroups;
        public float maxSafetyGuardCorrection;
        public int surfaceGuardSamples;
        public int surfaceGuardGroups;
        public float maxSurfaceGuardCorrection;
        public float maxClearanceLossBefore;
        public float maxExpansion;
        public int cappedGroups;
        public float maxCappedCorrection;
        public int islandPropagationCandidateGroups;
        public int islandPropagationRejectedGroups;
        public int propagatedIslandGroups;
        public int islandPropagationCandidateComponents;
        public int islandPropagationPropagatedComponents;
        public int islandPropagationRejectedNoSupportGroups;
        public int islandPropagationRejectedUnstableGroups;
        public int islandPropagationRejectedInsufficientSupportGroups;
        public int islandPropagationRejectedWeakDonorGroups;
        public int islandPropagationRejectedWeightGroups;
        public int islandPropagationRejectedBudgetGroups;
        public float maxPropagatedIslandCorrection;
        public float maxIslandPropagationSupportDistance;
        public ReFitIslandPropagationDebugData islandPropagationDebug;

        public bool HasCorrections => outwardGroups > 0 || inwardGroups > 0 || surfaceGuardGroups > 0 ||
                                      cappedGroups > 0 || propagatedIslandGroups > 0 ||
                                      islandPropagationCandidateGroups > 0 || islandPropagationRejectedGroups > 0;

        public void Add(ReFitClearanceCorrectionStats other)
        {
            if (other == null) return;
            eligibleGroups += other.eligibleGroups;
            outwardGroups += other.outwardGroups;
            inwardGroups += other.inwardGroups;
            maxOutwardCorrection = Mathf.Max(maxOutwardCorrection, other.maxOutwardCorrection);
            maxInwardCorrection = Mathf.Max(maxInwardCorrection, other.maxInwardCorrection);
            maxPenetrationBefore = Mathf.Max(maxPenetrationBefore, other.maxPenetrationBefore);
            maxPenetrationAfter = Mathf.Max(maxPenetrationAfter, other.maxPenetrationAfter);
            safetyGuardGroups += other.safetyGuardGroups;
            maxSafetyGuardCorrection = Mathf.Max(maxSafetyGuardCorrection, other.maxSafetyGuardCorrection);
            surfaceGuardSamples += other.surfaceGuardSamples;
            surfaceGuardGroups += other.surfaceGuardGroups;
            maxSurfaceGuardCorrection = Mathf.Max(maxSurfaceGuardCorrection, other.maxSurfaceGuardCorrection);
            maxClearanceLossBefore = Mathf.Max(maxClearanceLossBefore, other.maxClearanceLossBefore);
            maxExpansion = Mathf.Max(maxExpansion, other.maxExpansion);
            cappedGroups += other.cappedGroups;
            maxCappedCorrection = Mathf.Max(maxCappedCorrection, other.maxCappedCorrection);
            islandPropagationCandidateGroups += other.islandPropagationCandidateGroups;
            islandPropagationRejectedGroups += other.islandPropagationRejectedGroups;
            propagatedIslandGroups += other.propagatedIslandGroups;
            islandPropagationCandidateComponents += other.islandPropagationCandidateComponents;
            islandPropagationPropagatedComponents += other.islandPropagationPropagatedComponents;
            islandPropagationRejectedNoSupportGroups += other.islandPropagationRejectedNoSupportGroups;
            islandPropagationRejectedUnstableGroups += other.islandPropagationRejectedUnstableGroups;
            islandPropagationRejectedInsufficientSupportGroups += other.islandPropagationRejectedInsufficientSupportGroups;
            islandPropagationRejectedWeakDonorGroups += other.islandPropagationRejectedWeakDonorGroups;
            islandPropagationRejectedWeightGroups += other.islandPropagationRejectedWeightGroups;
            islandPropagationRejectedBudgetGroups += other.islandPropagationRejectedBudgetGroups;
            maxPropagatedIslandCorrection = Mathf.Max(maxPropagatedIslandCorrection, other.maxPropagatedIslandCorrection);
            maxIslandPropagationSupportDistance = Mathf.Max(maxIslandPropagationSupportDistance, other.maxIslandPropagationSupportDistance);
            if (other.islandPropagationDebug != null)
                islandPropagationDebug = other.islandPropagationDebug;
        }

        public string Summary(string label)
        {
            return $"{label}: eligible={eligibleGroups}, outward={outwardGroups}, inward={inwardGroups}, " +
                   $"maxOut={maxOutwardCorrection * 1000f:0.###}mm, maxIn={maxInwardCorrection * 1000f:0.###}mm, " +
                   $"guarded={safetyGuardGroups}, maxGuard={maxSafetyGuardCorrection * 1000f:0.###}mm, " +
                   $"surfaceSamples={surfaceGuardSamples}, surfaceGroups={surfaceGuardGroups}, maxSurfaceGuard={maxSurfaceGuardCorrection * 1000f:0.###}mm, " +
                   $"capped={cappedGroups}, maxCapped={maxCappedCorrection * 1000f:0.###}mm, " +
                   $"islandCandidates={islandPropagationCandidateGroups}, islandRejected={islandPropagationRejectedGroups}, " +
                   $"islandPropagated={propagatedIslandGroups}, islandComponents={islandPropagationCandidateComponents}/{islandPropagationPropagatedComponents}, " +
                   $"islandRejects(noSupport={islandPropagationRejectedNoSupportGroups}, unstable={islandPropagationRejectedUnstableGroups}, insufficientSupport={islandPropagationRejectedInsufficientSupportGroups}, weakDonor={islandPropagationRejectedWeakDonorGroups}, weight={islandPropagationRejectedWeightGroups}, budget={islandPropagationRejectedBudgetGroups}), " +
                   $"maxIslandPropagated={maxPropagatedIslandCorrection * 1000f:0.###}mm, " +
                   $"maxIslandSupportDistance={maxIslandPropagationSupportDistance * 1000f:0.###}mm, " +
                   $"maxPenetrationBefore={maxPenetrationBefore * 1000f:0.###}mm, maxPenetrationAfter={maxPenetrationAfter * 1000f:0.###}mm, " +
                   $"maxClearanceLossBefore={maxClearanceLossBefore * 1000f:0.###}mm, maxExpansion={maxExpansion * 1000f:0.###}mm.";
        }
    }

    /// <summary>Group-level state of topology-independent garment island propagation.</summary>
    public enum ReFitIslandPropagationStatus
    {
        None = 0,
        Candidate = 1,
        Applied = 2,
        RejectedNoSupport = 3,
        RejectedUnstableSupport = 4,
        RejectedWeakDonor = 5,
        RejectedInsufficientComponentSupport = 6,
        RejectedWeight = 7,
        RejectedBudget = 8
    }

    /// <summary>Per-group diagnostics for disconnected garment island propagation.</summary>
    [Serializable]
    public class ReFitIslandPropagationDebugData
    {
        public ReFitIslandPropagationDebugPoint[] groups;
    }

    /// <summary>One propagation decision, stored by welded group index.</summary>
    [Serializable]
    public class ReFitIslandPropagationDebugPoint
    {
        public int groupIndex;
        public int componentIndex = -1;
        public int componentSize;
        public int supportComponentIndex = -1;
        public int supportGroupIndex = -1;
        public ReFitIslandPropagationStatus status;
        public string reason;
        public float supportDistance;
        public float receiverWeight;
        public float currentCorrectionMagnitude;
        public float targetCorrectionMagnitude;
        public float appliedCorrectionMagnitude;
    }

    /// <summary>Per-group projection and weight-transfer diagnostics for debug scene gizmos.</summary>
    [Serializable]
    public class ReFitProjectionDebugData
    {
        public ReFitProjectionDebugPoint[] points;
    }

    /// <summary>One projection diagnostic line, stored in renderer-local coordinates for debug snapshots.</summary>
    [Serializable]
    public class ReFitProjectionDebugPoint
    {
        public int groupIndex;
        public int vertexIndex;
        public Vector3 assetLocalPoint;
        public Vector3 sourceHitLocalPoint;
        public Vector3 targetHitLocalPoint;
        public int sourceTriangle = -1;
        public int targetTriangle = -1;
        public Vector3 sourceBarycentric;
        public Vector3 targetBarycentric;
        public float sourceDistance;
        public float targetDistance;
        public float falloff;
        public float normalDot;
        public int weldedVertexCount = 1;
        public BodyRegion assetRegion = BodyRegion.Unknown;
        public BodyRegion sourceHitRegion = BodyRegion.Unknown;
        public BodyRegion targetHitRegion = BodyRegion.Unknown;
        public bool sourceUsedRelaxedFallback;
        public bool targetUsedRelaxedFallback;
        public bool sourceValid;
        public bool targetValid;
        public ReFitWeightDecision weightDecision = ReFitWeightDecision.None;
        public string projectedWeights;
        public string originalWeights;
        public string finalWeights;
        public string note;
    }

    /// <summary>Per-snapshot welded vertex group diagnostics for Scene view mesh-edge overlays.</summary>
    [Serializable]
    public class ReFitWeldedGroupDebugData
    {
        public ReFitWeldedGroupDebugPoint[] groups;
    }

    /// <summary>One welded vertex group marker, stored in renderer-local coordinates.</summary>
    [Serializable]
    public class ReFitWeldedGroupDebugPoint
    {
        public int groupIndex;
        public int representativeVertexIndex;
        public int vertexCount;
        public Vector3 localPoint;
        public int islandPropagationComponentIndex = -1;
        public int islandPropagationComponentSize;
        public int islandPropagationSupportComponentIndex = -1;
        public int islandPropagationSupportGroupIndex = -1;
        public ReFitIslandPropagationStatus islandPropagationStatus;
        public string islandPropagationReason;
        public float islandPropagationSupportDistance;
        public float islandPropagationReceiverWeight;
        public float islandPropagationCurrentCorrectionMagnitude;
        public float islandPropagationTargetCorrectionMagnitude;
        public float islandPropagationAppliedCorrectionMagnitude;
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
        /// <summary>Non-deforming leaf-tail helpers to create under rebuilt leaf bones for debug/armature visualization.</summary>
        public ReFitLeafTailHint[] leafTailHints;
        /// <summary>Optional per-projection diagnostics captured for editor debug gizmos.</summary>
        public ReFitProjectionDebugData projectionDebug;
        /// <summary>Aggregate stats for the clearance correction pass, when enabled.</summary>
        public ReFitClearanceCorrectionStats clearanceCorrectionStats;
        /// <summary>Debug-only island propagation decisions from the primary clearance correction pass.</summary>
        public ReFitIslandPropagationDebugData primaryIslandPropagationDebug;
        /// <summary>Debug-only island propagation decisions from the latest transferred-shape clearance correction pass.</summary>
        public ReFitIslandPropagationDebugData transferredIslandPropagationDebug;
        /// <summary>Debug-only primary field before clearance correction.</summary>
        public Vector3[] debugPrimaryRawLocalDeltas;
        /// <summary>Debug-only transferred fields before clearance correction, aligned with <see cref="secondaryShapeNames"/>.</summary>
        public Vector3[][] debugSecondaryRawLocalDeltas;
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
