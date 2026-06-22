using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Final outcome of a re-fit, including saved asset paths and the scene renderer that received the result.</summary>
    public class ReFitResult
    {
        public bool success;
        public ReFitReport report = new ReFitReport();
        /// <summary>The generated mesh asset.</summary>
        public Mesh mesh;
        /// <summary>Project path of the saved mesh (.asset).</summary>
        public string meshAssetPath;
        /// <summary>Project path of the saved prefab, when one could be produced (armature not replaced).</summary>
        public string prefabAssetPath;
        /// <summary>The scene renderer now using the re-fitted mesh.</summary>
        public SkinnedMeshRenderer sceneRenderer;
        /// <summary>The mesh the renderer used before the re-fit (assign it back to revert).</summary>
        public Mesh originalMesh;
        /// <summary>Name of the generated primary refit blendshape.</summary>
        public string primaryShapeName;
        /// <summary>Names of the generated transferred blendshapes.</summary>
        public string[] secondaryShapeNames;
        /// <summary>Gravity blendshape names added after the optional preview is confirmed.</summary>
        public string[] gravityShapeNames;
        /// <summary>Default scene/prefab weight used when the optional gravity blendshapes were applied.</summary>
        public float gravityDefaultWeight;

        public string[] GeneratedShapeNames()
        {
            var names = new List<string>();
            if (!string.IsNullOrEmpty(primaryShapeName))
                names.Add(primaryShapeName);
            if (secondaryShapeNames != null)
            {
                for (int i = 0; i < secondaryShapeNames.Length; i++)
                    if (!string.IsNullOrEmpty(secondaryShapeNames[i]) && !names.Contains(secondaryShapeNames[i]))
                        names.Add(secondaryShapeNames[i]);
            }
            return names.ToArray();
        }
    }

    /// <summary>
    /// Turns a <see cref="ReFitComputation"/> into saved assets and scene changes: saves the mesh,
    /// applies it to (or instantiates) the asset, rebinds the armature to the target avatar and saves a prefab
    /// when possible. All scene changes are undoable.
    /// </summary>
    public static class ReFitAssetPipeline
    {
        /// <summary>Root folder for generated assets.</summary>
        public const string OutputRoot = "Assets/ReFit";

        /// <summary>Saves the mesh under <see cref="OutputRoot"/>/<paramref name="subfolder"/> and returns its path.</summary>
        public static string SaveMesh(Mesh mesh, string subfolder, ReFitReport report)
        {
            var folder = EnsureFolder(string.IsNullOrEmpty(subfolder) ? OutputRoot : $"{OutputRoot}/{Sanitize(subfolder)}");
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(mesh.name)}.asset");
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            report?.Info("mesh-saved", $"Saved the re-fitted mesh to '{path}'.");
            return path;
        }

        /// <summary>
        /// Applies the computation to the scene: resolves (or instantiates) the real target avatar and asset,
        /// swaps the mesh, performs the armature replacement and enables the generated blendshapes.
        /// </summary>
        public static SkinnedMeshRenderer ApplyToScene(ReFitRequest request, ReFitComputation comp, ReFitReport report,
            ReFitDebugSession debug = null)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("ReFit");

            // --- real target instance ------------------------------------------------------
            GameObject targetInstance = request.targetAvatar;
            if (EditorUtility.IsPersistent(targetInstance))
            {
                targetInstance = (GameObject)PrefabUtility.InstantiatePrefab(request.targetAvatar);
                if (targetInstance == null)
                {
                    report.Error("target-instantiate-failed", "Could not instantiate the target avatar prefab into the scene.");
                    return null;
                }
                Undo.RegisterCreatedObjectUndo(targetInstance, "ReFit target avatar");
                report.Info("target-instantiated", $"Added '{targetInstance.name}' to the scene (the target avatar was a project file).");
            }

            // --- real asset instance -------------------------------------------------------
            var renderer = ResolveAssetInstance(request, comp, targetInstance, report, out var assetInstanceRoot);
            if (renderer == null) return null;

            Undo.RecordObject(renderer, "ReFit");
            comp.appliedOriginalMesh = renderer.sharedMesh;
            CaptureGeneratedMeshPreviewWithOriginalSkinning(debug, renderer, comp);
            renderer.sharedMesh = comp.mesh;
            if (!comp.armatureReplaced)
                debug?.Capture("02_generated_mesh_assigned", renderer, comp.projectionDebug);

            // --- armature replacement ------------------------------------------------------
            bool armatureApplied = false;
            if (comp.armatureReplaced)
            {
                armatureApplied = ApplyBonePlan(request, comp, targetInstance, assetInstanceRoot, renderer, report);
                if (!armatureApplied)
                    report.Warn("bone-apply-incomplete", "The armature replacement could not be fully applied; check the messages above.");
                else if (!renderer.transform.IsChildOf(targetInstance.transform))
                {
                    // The mesh now follows the target's bones; keep the object tidy under the target avatar.
                    var container = renderer.transform;
                    while (container.parent != null &&
                           !container.parent.IsChildOf(targetInstance.transform) && container.parent != targetInstance.transform &&
                           container.parent.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 1)
                        container = container.parent;
                    if (MakeRestructurable(container, report))
                        Undo.SetTransformParent(container, targetInstance.transform, "ReFit parent asset");
                }
            }
            debug?.Capture(armatureApplied ? "03_armature_replaced" :
                (comp.armatureReplaced ? "03_armature_replace_failed" : "03_armature_kept"), renderer);

            // --- enable the generated shapes -------------------------------------------------
            SetGeneratedBlendShapeWeights(renderer, comp, 0f, false);
            if (!string.IsNullOrEmpty(comp.primaryShapeName))
            {
                CaptureGeneratedDeltaOverride(debug, renderer, comp, "04_primary_raw_blendshape_enabled",
                    comp.debugPrimaryRawLocalDeltas, null, true, false);

                int idx = comp.mesh.GetBlendShapeIndex(comp.primaryShapeName);
                if (idx >= 0) renderer.SetBlendShapeWeight(idx, 100f);
                debug?.Capture(idx >= 0 ? "05_primary_clearance_corrected" : "05_primary_blendshape_missing",
                    renderer, null, comp.primaryIslandPropagationDebug);
            }
            if (comp.secondaryShapeNames != null)
            {
                CaptureGeneratedDeltaOverride(debug, renderer, comp, "06_transferred_raw_blendshapes_enabled",
                    null, comp.debugSecondaryRawLocalDeltas, true, true);

                bool hadSecondaryShape = false;
                for (int s = 0; s < comp.secondaryShapeNames.Length; s++)
                {
                    int idx = comp.mesh.GetBlendShapeIndex(comp.secondaryShapeNames[s]);
                    if (idx < 0) continue;
                    hadSecondaryShape = true;
                    float weight = comp.secondaryMirrorWeights != null && s < comp.secondaryMirrorWeights.Length
                        ? comp.secondaryMirrorWeights[s]
                        : 100f;
                    renderer.SetBlendShapeWeight(idx, weight);
                }
                if (hadSecondaryShape)
                    debug?.Capture("07_transferred_clearance_corrected", renderer, null,
                        comp.transferredIslandPropagationDebug);
            }
            debug?.Capture("08_final_result", renderer, comp.projectionDebug,
                comp.transferredIslandPropagationDebug ?? comp.primaryIslandPropagationDebug);

            Selection.activeGameObject = renderer.gameObject;
            EditorGUIUtility.PingObject(renderer.gameObject);
            return renderer;
        }

        private static void CaptureGeneratedDeltaOverride(
            ReFitDebugSession debug,
            SkinnedMeshRenderer renderer,
            ReFitComputation comp,
            string label,
            Vector3[] primaryOverride,
            Vector3[][] secondaryOverrides,
            bool primaryEnabled,
            bool secondariesEnabled)
        {
            if (debug == null || renderer == null || comp == null || comp.mesh == null)
                return;
            if (primaryOverride == null && (secondaryOverrides == null || secondaryOverrides.Length == 0))
                return;

            var originalMesh = renderer.sharedMesh;
            var previewMesh = BuildGeneratedDeltaOverrideMesh(comp, primaryOverride, secondaryOverrides);
            if (previewMesh == null)
                return;

            try
            {
                renderer.sharedMesh = previewMesh;
                SetGeneratedBlendShapeWeights(renderer, comp, primaryEnabled ? 100f : 0f, secondariesEnabled);
                debug.Capture(label, renderer, comp.projectionDebug);
            }
            finally
            {
                renderer.sharedMesh = originalMesh;
                SetGeneratedBlendShapeWeights(renderer, comp, originalMesh == comp.mesh && primaryEnabled ? 100f : 0f, false);
                Object.DestroyImmediate(previewMesh);
            }
        }

        private static Mesh BuildGeneratedDeltaOverrideMesh(
            ReFitComputation comp,
            Vector3[] primaryOverride,
            Vector3[][] secondaryOverrides)
        {
            var source = comp != null ? comp.mesh : null;
            if (source == null)
                return null;

            var mesh = Object.Instantiate(source);
            mesh.name = source.name.Replace("(Clone)", "") + "_DebugDeltaOverride";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.ClearBlendShapes();

            int vertexCount = source.vertexCount;
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];

            for (int s = 0; s < source.blendShapeCount; s++)
            {
                string shapeName = source.GetBlendShapeName(s);
                var overrideDeltas = OverrideForShape(comp, shapeName, primaryOverride, secondaryOverrides);
                if (overrideDeltas != null && overrideDeltas.Length == vertexCount)
                {
                    mesh.AddBlendShapeFrame(shapeName, 100f, overrideDeltas, null, null);
                    continue;
                }

                int frames = source.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frames; f++)
                {
                    source.GetBlendShapeFrameVertices(s, f, deltaVertices, deltaNormals, deltaTangents);
                    mesh.AddBlendShapeFrame(shapeName, source.GetBlendShapeFrameWeight(s, f),
                        deltaVertices, deltaNormals, deltaTangents);
                }
            }

            return mesh;
        }

        private static Vector3[] OverrideForShape(
            ReFitComputation comp,
            string shapeName,
            Vector3[] primaryOverride,
            Vector3[][] secondaryOverrides)
        {
            if (!string.IsNullOrEmpty(comp.primaryShapeName) && shapeName == comp.primaryShapeName)
                return primaryOverride;

            if (comp.secondaryShapeNames == null || secondaryOverrides == null)
                return null;

            int count = Mathf.Min(comp.secondaryShapeNames.Length, secondaryOverrides.Length);
            for (int i = 0; i < count; i++)
                if (shapeName == comp.secondaryShapeNames[i])
                    return secondaryOverrides[i];

            return null;
        }

        private static void SetGeneratedBlendShapeWeights(
            SkinnedMeshRenderer renderer,
            ReFitComputation comp,
            float primaryWeight,
            bool secondariesEnabled)
        {
            if (renderer == null || renderer.sharedMesh == null || comp == null)
                return;

            SetBlendShapeWeight(renderer, comp.primaryShapeName, primaryWeight);
            if (comp.secondaryShapeNames == null)
                return;

            for (int s = 0; s < comp.secondaryShapeNames.Length; s++)
            {
                float weight = secondariesEnabled && comp.secondaryMirrorWeights != null && s < comp.secondaryMirrorWeights.Length
                    ? comp.secondaryMirrorWeights[s]
                    : 0f;
                SetBlendShapeWeight(renderer, comp.secondaryShapeNames[s], weight);
            }
        }

        private static void SetBlendShapeWeight(SkinnedMeshRenderer renderer, string shapeName, float weight)
        {
            if (renderer == null || renderer.sharedMesh == null || string.IsNullOrEmpty(shapeName))
                return;

            int index = renderer.sharedMesh.GetBlendShapeIndex(shapeName);
            if (index >= 0)
                renderer.SetBlendShapeWeight(index, weight);
        }

        private static void CaptureGeneratedMeshPreviewWithOriginalSkinning(ReFitDebugSession debug,
            SkinnedMeshRenderer renderer, ReFitComputation comp)
        {
            if (debug == null || renderer == null || comp == null || comp.mesh == null)
                return;
            if (!comp.armatureReplaced)
                return;

            var originalMesh = renderer.sharedMesh;
            if (originalMesh == null || originalMesh.vertexCount != comp.mesh.vertexCount)
                return;

            var previewMesh = Object.Instantiate(comp.mesh);
            previewMesh.name = comp.mesh.name.Replace("(Clone)", "") + "_OriginalSkinningPreview";
            previewMesh.hideFlags = HideFlags.HideAndDontSave;
            previewMesh.bindposes = originalMesh.bindposes;
            previewMesh.boneWeights = originalMesh.boneWeights;
            previewMesh.RecalculateBounds();

            try
            {
                renderer.sharedMesh = previewMesh;
                debug.Capture("02_generated_mesh_original_skinning", renderer, comp.projectionDebug);
            }
            finally
            {
                renderer.sharedMesh = originalMesh;
                Object.DestroyImmediate(previewMesh);
            }
        }

        /// <summary>
        /// Saves the asset hierarchy as a prefab (variant when it came from a prefab). Only meaningful when the
        /// armature was NOT replaced — a replaced armature references the target avatar's scene bones, which a
        /// standalone prefab cannot capture. Returns the path or null.
        /// </summary>
        public static string TrySavePrefab(ReFitComputation comp, SkinnedMeshRenderer sceneRenderer, string subfolder, ReFitReport report)
        {
            if (comp.armatureReplaced || sceneRenderer == null) return null;
            try
            {
                var root = PoseNormalizer.FindCommonRoot(sceneRenderer);
                if (root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 8) return null; // probably a whole avatar; don't prefab that
                var folder = EnsureFolder(string.IsNullOrEmpty(subfolder) ? OutputRoot : $"{OutputRoot}/{Sanitize(subfolder)}");
                var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(root.name)}_ReFit.prefab");
                var prefab = PrefabUtility.SaveAsPrefabAsset(root.gameObject, path, out bool ok);
                if (!ok || prefab == null) return null;
                report.Info("prefab-saved", $"Saved a re-fitted prefab to '{path}'.");
                return path;
            }
            catch (System.Exception e)
            {
                report.Info("prefab-skipped", $"No standalone prefab was saved ({e.Message}). The scene object and mesh asset are the deliverables.");
                return null;
            }
        }

        /// <summary>
        /// Repairs stale duplicate transform-only armature branches left on a scene asset before a new ReFit run.
        /// This is intentionally conservative: branches used by the renderer or another renderer are not deleted.
        /// </summary>
        public static void RepairSceneAssetArmature(ReFitRequest request, ReFitReport report)
        {
            var renderer = request != null ? request.assetRenderer : null;
            if (renderer == null || EditorUtility.IsPersistent(renderer)) return;

            var assetRoot = PoseNormalizer.FindAssetObjectRoot(renderer, request.sourceAvatar, request.targetAvatar);
            if (assetRoot == null || IsAvatarRoot(assetRoot, request, request.targetAvatar)) return;

            RemoveTransientDebugObjects(assetRoot, report);
            int removedDuplicateBranches = RemoveDuplicateSiblingBranches(assetRoot, renderer, report);
            int removedUnusedArmatures = RemoveUnusedArmatureContainers(assetRoot, null, renderer, null, report);
            int removed = removedDuplicateBranches + removedUnusedArmatures;
            if (removed > 0)
                report.Info("stale-armature-repaired",
                    $"Removed {removed} stale local armature branch(es) from '{HierarchyPath(assetRoot)}' before running ReFit.");

            ValidateExistingInputArmature(assetRoot, renderer, report);
        }

        // ------------------------------------------------------------------
        // Asset instance resolution
        // ------------------------------------------------------------------

        private static SkinnedMeshRenderer ResolveAssetInstance(ReFitRequest request, ReFitComputation comp,
            GameObject targetInstance, ReFitReport report, out Transform assetInstanceRoot)
        {
            assetInstanceRoot = null;

            var carrier = request.sourceAvatar == null || request.sourceAvatar == request.targetAvatar
                ? request.targetAvatar
                : request.sourceAvatar;
            bool onAvatar = carrier != null && request.assetRenderer.transform.IsChildOf(carrier.transform);

            if (onAvatar)
            {
                if (carrier == request.targetAvatar && targetInstance != request.targetAvatar)
                {
                    // Target was a prefab we just instantiated; the asset lives inside it.
                    var t = ReFitUtility.ResolvePath(targetInstance.transform, comp.assetRendererPath);
                    var found = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                    if (found == null) { report.Error("asset-resolve-failed", "Could not locate the asset inside the instantiated target avatar."); return null; }
                    assetInstanceRoot = targetInstance.transform;
                    return found;
                }
                if (EditorUtility.IsPersistent(request.assetRenderer))
                {
                    report.Error("asset-in-prefab-source",
                        "The asset lives inside a source avatar prefab file. Drop that avatar into the scene first, then run ReFit on the scene instance.");
                    return null;
                }
                assetInstanceRoot = carrier.transform;
                return request.assetRenderer;
            }

            // Standalone asset hierarchy
            var realRoot = PoseNormalizer.FindAssetObjectRoot(request.assetRenderer, request.sourceAvatar, request.targetAvatar);
            if (EditorUtility.IsPersistent(request.assetRenderer))
            {
                var instance = PrefabUtility.InstantiatePrefab(realRoot.gameObject) as GameObject;
                if (instance == null) instance = Object.Instantiate(realRoot.gameObject);
                instance.name = realRoot.name;
                Undo.RegisterCreatedObjectUndo(instance, "ReFit asset");
                var t = ReFitUtility.ResolvePath(instance.transform, comp.assetRendererPath);
                var found = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (found == null) { report.Error("asset-resolve-failed", "Could not locate the renderer inside the instantiated asset."); return null; }
                assetInstanceRoot = instance.transform;
                return found;
            }

            assetInstanceRoot = realRoot;
            return request.assetRenderer;
        }

        // ------------------------------------------------------------------
        // Bone plan application
        // ------------------------------------------------------------------

        private static bool ApplyBonePlan(ReFitRequest request, ReFitComputation comp, GameObject targetInstance,
            Transform assetInstanceRoot, SkinnedMeshRenderer renderer, ReFitReport report)
        {
            var oldBones = renderer.bones;
            var oldRootBone = renderer.rootBone;

            // Resolve the source transforms first. The renderer will not bind to these transforms directly:
            // they are used as blueprints for a fresh clothing-owned armature.
            var sourceBones = ResolveBoneBlueprints(request, comp, targetInstance, assetInstanceRoot, report, out var missing);
            if (missing > 0)
            {
                report.Error("bones-missing",
                    $"{missing}/{sourceBones.Length} bone blueprint(s) could not be resolved; armature replacement was aborted instead of creating null skin bones.");
                return false;
            }

            if (!MakeRestructurable(assetInstanceRoot, report)) return false;

            var newArmatureRoot = CreateMaterializedArmature(assetInstanceRoot);
            var bones = MaterializeBonePlan(comp, sourceBones, targetInstance, assetInstanceRoot, newArmatureRoot, report);

            // Assign the new skinning.
            renderer.bones = bones;
            if (comp.rootBoneIndex >= 0 && comp.rootBoneIndex < bones.Length && bones[comp.rootBoneIndex] != null)
                renderer.rootBone = bones[comp.rootBoneIndex];
            else
            {
                foreach (var bone in bones)
                {
                    if (bone == null) continue;
                    renderer.rootBone = bone;
                    break;
                }
            }
            // Mesh space changed (staged-pose bindposes): the authored local bounds are no longer reliable.
            renderer.updateWhenOffscreen = true;
            CreateLeafTailHelpers(comp, bones, report);
            RefreshMeshBindposes(renderer, bones, report);
            ValidateMaterializedArmature(sourceBones, bones, renderer, report);
            RemoveTransientDebugObjects(assetInstanceRoot, report);
            RemoveReplacedAssetArmature(request, targetInstance, assetInstanceRoot, renderer, oldBones, oldRootBone, bones, newArmatureRoot, report);
            RemoveUnusedArmatureContainers(assetInstanceRoot, newArmatureRoot, renderer, bones, report);
            newArmatureRoot.name = UniqueChildName(assetInstanceRoot, "Armature", newArmatureRoot);
            ValidateFinalArmatureHierarchy(assetInstanceRoot, renderer, newArmatureRoot, bones, report);
            report.Info("armature-rebuilt", $"Built a new clothing armature with {bones.Length - missing}/{bones.Length} resolved bone(s).");
            return missing == 0;
        }

        private static void CreateLeafTailHelpers(ReFitComputation comp, Transform[] bones, ReFitReport report)
        {
            if (comp?.leafTailHints == null || bones == null) return;

            int created = 0;
            foreach (var hint in comp.leafTailHints)
            {
                if (hint == null || hint.boneIndex < 0 || hint.boneIndex >= bones.Length) continue;
                var parent = bones[hint.boneIndex];
                if (parent == null || hint.localPosition.sqrMagnitude < 1e-8f) continue;
                var existing = parent.Find(hint.name);
                if (existing != null)
                    Undo.DestroyObjectImmediate(existing.gameObject);

                var helper = new GameObject(string.IsNullOrEmpty(hint.name) ? "__ReFitLeafTail" : hint.name).transform;
                Undo.RegisterCreatedObjectUndo(helper.gameObject, "ReFit leaf tail helper");
                helper.SetParent(parent, false);
                helper.localPosition = hint.localPosition;
                helper.localRotation = Quaternion.identity;
                helper.localScale = Vector3.one;
                created++;
            }

            if (created > 0)
                report.Info("leaf-tail-helpers-created",
                    $"Created {created} non-deforming leaf-tail helper(s) under rebuilt clothing bones.");
        }

        private static Transform[] ResolveBoneBlueprints(ReFitRequest request, ReFitComputation comp,
            GameObject targetInstance, Transform assetInstanceRoot, ReFitReport report, out int missing)
        {
            missing = 0;
            var sources = new Transform[comp.bones != null ? comp.bones.Length : 0];
            var missingDetails = new List<string>();
            for (int i = 0; i < sources.Length; i++)
            {
                var boneRef = comp.bones[i];
                if (boneRef == null)
                {
                    missing++;
                    if (missingDetails.Count < 16)
                        missingDetails.Add($"{i}: <null bone ref>");
                    continue;
                }

                switch (boneRef.origin)
                {
                    case ReFitBoneOrigin.Target:
                        sources[i] = targetInstance != null ? ReFitUtility.ResolvePath(targetInstance.transform, boneRef.path) : null;
                        break;
                    case ReFitBoneOrigin.Asset:
                        sources[i] = ReFitUtility.ResolvePath(assetInstanceRoot, boneRef.path);
                        break;
                    case ReFitBoneOrigin.SourceAvatar:
                        sources[i] = request?.sourceAvatar != null ? ReFitUtility.ResolvePath(request.sourceAvatar.transform, boneRef.path) : null;
                        break;
                }
                if (sources[i] == null)
                {
                    missing++;
                    if (missingDetails.Count < 16)
                    {
                        var name = string.IsNullOrEmpty(boneRef.name) ? "<unnamed>" : boneRef.name;
                        missingDetails.Add($"{i}: name='{name}', origin={boneRef.origin}, path={FormatPath(boneRef.path)}");
                    }
                }
            }
            if (missingDetails.Count > 0)
            {
                var suffix = missing > missingDetails.Count ? $" (+{missing - missingDetails.Count} more)" : string.Empty;
                report.Warn("bones-missing-detail", "Missing bone blueprint refs: " + string.Join("; ", missingDetails.ToArray()) + suffix + ".");
            }
            return sources;
        }

        private static string FormatPath(int[] path)
        {
            return path == null || path.Length == 0 ? "<root>" : string.Join("/", path);
        }

        private static Transform CreateMaterializedArmature(Transform assetInstanceRoot)
        {
            var root = new GameObject("__ReFit_NewArmature").transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, "ReFit rebuilt armature");
            root.SetParent(assetInstanceRoot, false);
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;
            root.localScale = Vector3.one;
            return root;
        }

        private static Transform[] MaterializeBonePlan(ReFitComputation comp, Transform[] sourceBones,
            GameObject targetInstance, Transform assetInstanceRoot, Transform newArmatureRoot, ReFitReport report)
        {
            var bones = new Transform[sourceBones != null ? sourceBones.Length : 0];
            var sourceToNew = new Dictionary<Transform, Transform>();
            var placements = new MaterializedBonePlacement[bones.Length];

            for (int i = 0; i < bones.Length; i++)
            {
                var source = sourceBones[i];
                if (source == null) continue;
                var proxy = new GameObject(source.name).transform;
                proxy.SetParent(newArmatureRoot, false);
                bones[i] = proxy;
                if (!sourceToNew.ContainsKey(source)) sourceToNew[source] = proxy;
            }

            var placementParents = BuildPlacementParentMap(comp, sourceBones, sourceToNew, targetInstance, report);
            for (int i = 0; i < bones.Length; i++)
            {
                var source = sourceBones[i];
                var proxy = bones[i];
                if (source == null || proxy == null) continue;

                var parent = FindMaterializedParent(source, sourceToNew);
                if (parent == null && placementParents.TryGetValue(i, out var placedParent))
                    parent = placedParent;
                if (parent == null)
                    parent = newArmatureRoot;

                SetWorldTransform(proxy, newArmatureRoot, source.position, source.rotation, source.lossyScale);
                placements[i] = new MaterializedBonePlacement(proxy, parent, source.lossyScale);
            }

            var order = new List<int>();
            for (int i = 0; i < placements.Length; i++)
                if (placements[i].proxy != null)
                    order.Add(i);
            order.Sort((a, b) => DesiredParentDepth(placements[a].parent, newArmatureRoot)
                .CompareTo(DesiredParentDepth(placements[b].parent, newArmatureRoot)));

            foreach (var i in order)
            {
                var placement = placements[i];
                ReparentPreservingWorld(placement.proxy, placement.parent, placement.lossyScale);
            }

            return bones;
        }

        private static Dictionary<int, Transform> BuildPlacementParentMap(ReFitComputation comp, Transform[] sourceBones,
            Dictionary<Transform, Transform> sourceToNew, GameObject targetInstance, ReFitReport report)
        {
            var result = new Dictionary<int, Transform>();
            if (comp.keptPlacements == null || targetInstance == null) return result;

            foreach (var placement in comp.keptPlacements)
            {
                var targetParent = ReFitUtility.ResolvePath(targetInstance.transform, placement.targetParentPath);
                if (targetParent == null)
                {
                    report.Warn("kept-parent-missing", "Could not resolve the target parent of a preserved clothing bone.");
                    continue;
                }
                if (TryFindMaterializedAncestor(targetParent, sourceToNew, out var proxyParent))
                    result[placement.boneIndex] = proxyParent;
            }
            return result;
        }

        private static bool TryFindMaterializedAncestor(Transform source, Dictionary<Transform, Transform> sourceToNew,
            out Transform proxy)
        {
            while (source != null)
            {
                if (sourceToNew.TryGetValue(source, out proxy))
                    return true;
                source = source.parent;
            }
            proxy = null;
            return false;
        }

        private static Transform FindMaterializedParent(Transform source, Dictionary<Transform, Transform> sourceToNew)
        {
            var parent = source != null ? source.parent : null;
            while (parent != null)
            {
                if (sourceToNew.TryGetValue(parent, out var proxyParent))
                    return proxyParent;
                parent = parent.parent;
            }
            return null;
        }

        private static void SetParentPreservingWorld(Transform t, Transform parent, Vector3 position,
            Quaternion rotation, Vector3 lossyScale)
        {
            t.SetParent(parent, true);
            t.position = position;
            t.rotation = rotation;
            t.localScale = DivideScale(lossyScale, t.parent != null ? t.parent.lossyScale : Vector3.one);
        }

        private static void SetWorldTransform(Transform t, Transform parent, Vector3 position,
            Quaternion rotation, Vector3 lossyScale)
        {
            t.SetParent(parent, true);
            t.position = position;
            t.rotation = rotation;
            t.localScale = DivideScale(lossyScale, t.parent != null ? t.parent.lossyScale : Vector3.one);
        }

        private static void ReparentPreservingWorld(Transform t, Transform parent, Vector3 lossyScale)
        {
            var position = t.position;
            var rotation = t.rotation;
            t.SetParent(parent, true);
            t.position = position;
            t.rotation = rotation;
            t.localScale = DivideScale(lossyScale, t.parent != null ? t.parent.lossyScale : Vector3.one);
        }

        private static int DesiredParentDepth(Transform parent, Transform stopAt)
        {
            int depth = 0;
            var current = parent;
            while (current != null && current != stopAt)
            {
                depth++;
                current = current.parent;
            }
            return depth;
        }

        private static void RefreshMeshBindposes(SkinnedMeshRenderer renderer, Transform[] bones, ReFitReport report)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || bones == null || bones.Length == 0) return;

            var bindposes = new Matrix4x4[bones.Length];
            var rendererLocalToWorld = renderer.transform.localToWorldMatrix;
            for (int i = 0; i < bones.Length; i++)
                bindposes[i] = bones[i] != null
                    ? bones[i].worldToLocalMatrix * rendererLocalToWorld
                    : Matrix4x4.identity;

            mesh.bindposes = bindposes;
            EditorUtility.SetDirty(mesh);
            report.Info("armature-bindposes-refreshed",
                $"Rebuilt {bindposes.Length} bindpose(s) from the final materialized clothing armature.");
        }

        private static void ValidateMaterializedArmature(Transform[] sourceBones, Transform[] bones,
            SkinnedMeshRenderer renderer, ReFitReport report)
        {
            if (sourceBones == null || bones == null || report == null) return;

            float maxPositionDrift = 0f;
            float maxRotationDrift = 0f;
            int worstPosition = -1;
            int worstRotation = -1;
            for (int i = 0; i < bones.Length && i < sourceBones.Length; i++)
            {
                var source = sourceBones[i];
                var bone = bones[i];
                if (source == null || bone == null) continue;
                var positionDrift = Vector3.Distance(source.position, bone.position);
                if (positionDrift > maxPositionDrift)
                {
                    maxPositionDrift = positionDrift;
                    worstPosition = i;
                }

                var rotationDrift = Quaternion.Angle(source.rotation, bone.rotation);
                if (rotationDrift > maxRotationDrift)
                {
                    maxRotationDrift = rotationDrift;
                    worstRotation = i;
                }
            }

            var mesh = renderer != null ? renderer.sharedMesh : null;
            float maxBindposeDrift = 0f;
            int worstBindpose = -1;
            if (renderer != null && mesh != null && mesh.bindposes != null && mesh.bindposes.Length == bones.Length)
            {
                var rendererLocalToWorld = renderer.transform.localToWorldMatrix;
                var bindposes = mesh.bindposes;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    var expected = bones[i].worldToLocalMatrix * rendererLocalToWorld;
                    var drift = MatrixMaxAbsDelta(bindposes[i], expected);
                    if (drift > maxBindposeDrift)
                    {
                        maxBindposeDrift = drift;
                        worstBindpose = i;
                    }
                }
            }

            float maxLengthRatio = 0f;
            float maxLength = 0f;
            int worstLength = -1;
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && !boneToIndex.ContainsKey(bones[i]))
                    boneToIndex[bones[i]] = i;

            for (int i = 0; i < bones.Length && i < sourceBones.Length; i++)
            {
                var bone = bones[i];
                var source = sourceBones[i];
                if (bone == null || source == null || bone.parent == null) continue;
                if (!boneToIndex.TryGetValue(bone.parent, out var parentIndex)) continue;
                if (parentIndex < 0 || parentIndex >= sourceBones.Length || sourceBones[parentIndex] == null) continue;

                var actual = Vector3.Distance(bone.position, bone.parent.position);
                var expected = Vector3.Distance(source.position, sourceBones[parentIndex].position);
                var ratio = expected > 1e-5f ? actual / expected : actual;
                if (actual > maxLength)
                    maxLength = actual;
                if (ratio > maxLengthRatio)
                {
                    maxLengthRatio = ratio;
                    worstLength = i;
                }
            }

            report.Info("armature-validation",
                $"Materialized armature validation: maxPositionDrift={maxPositionDrift * 1000f:0.###}mm" +
                $" ({BoneLabel(bones, worstPosition)}), maxRotationDrift={maxRotationDrift:0.###}deg" +
                $" ({BoneLabel(bones, worstRotation)}), maxBindposeDrift={maxBindposeDrift:0.######}" +
                $" ({BoneLabel(bones, worstBindpose)}), maxBoneLength={maxLength:0.###}m" +
                $" ({BoneLabel(bones, worstLength)}), maxLengthRatio={maxLengthRatio:0.###}.");

            if (maxPositionDrift > 0.01f || maxRotationDrift > 1f)
                report.Error("armature-placement-invalid",
                    $"The rebuilt armature does not match its source blueprint. Worst position drift: {maxPositionDrift * 1000f:0.###}mm at {BoneLabel(bones, worstPosition)}; " +
                    $"worst rotation drift: {maxRotationDrift:0.###}deg at {BoneLabel(bones, worstRotation)}.");

            if (maxBindposeDrift > 0.0001f)
                report.Error("armature-bindpose-invalid",
                    $"The rebuilt armature bindposes do not match the final bone transforms. Worst matrix drift: {maxBindposeDrift:0.######} at {BoneLabel(bones, worstBindpose)}.");

            if (maxLengthRatio > 4f && maxLength > 0.1f)
                report.Warn("armature-length-suspicious",
                    $"A rebuilt bone span is much longer than its blueprint. Worst ratio: {maxLengthRatio:0.###} at {BoneLabel(bones, worstLength)}.");
        }

        private static float MatrixMaxAbsDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float max = 0f;
            for (int i = 0; i < 16; i++)
                max = Mathf.Max(max, Mathf.Abs(a[i] - b[i]));
            return max;
        }

        private static string BoneLabel(Transform[] bones, int index)
        {
            if (index < 0 || bones == null || index >= bones.Length || bones[index] == null)
                return "n/a";
            return $"{index}:{HierarchyPath(bones[index])}";
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "null";
            var parts = new Stack<string>();
            var current = t;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private readonly struct MaterializedBonePlacement
        {
            public readonly Transform proxy;
            public readonly Transform parent;
            public readonly Vector3 lossyScale;

            public MaterializedBonePlacement(Transform proxy, Transform parent, Vector3 lossyScale)
            {
                this.proxy = proxy;
                this.parent = parent;
                this.lossyScale = lossyScale;
            }
        }

        private static void RemoveReplacedAssetArmature(ReFitRequest request, GameObject targetInstance,
            Transform assetInstanceRoot, SkinnedMeshRenderer renderer, Transform[] oldBones, Transform oldRootBone,
            Transform[] newBones, Transform newArmatureRoot, ReFitReport report)
        {
            if (assetInstanceRoot == null || renderer == null) return;
            if (IsAvatarRoot(assetInstanceRoot, request, targetInstance)) return;

            var protectedPath = new HashSet<Transform>();
            var protectedSelf = new HashSet<Transform>();
            ProtectTransform(protectedPath, protectedSelf, newArmatureRoot, assetInstanceRoot);
            ProtectTransform(protectedPath, protectedSelf, renderer.transform, assetInstanceRoot);
            if (newBones != null)
                foreach (var bone in newBones)
                    if (bone != null && bone.IsChildOf(assetInstanceRoot))
                        ProtectTransform(protectedPath, protectedSelf, bone, assetInstanceRoot);
            foreach (var usedRenderer in assetInstanceRoot.GetComponentsInChildren<Renderer>(true))
                ProtectTransform(protectedPath, protectedSelf, usedRenderer.transform, assetInstanceRoot);
            foreach (var usedRenderer in assetInstanceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (usedRenderer == renderer) continue;
                ProtectTransform(protectedPath, protectedSelf, usedRenderer.rootBone, assetInstanceRoot);
                var usedBones = usedRenderer.bones;
                if (usedBones == null) continue;
                foreach (var bone in usedBones)
                    ProtectTransform(protectedPath, protectedSelf, bone, assetInstanceRoot);
            }

            var candidates = new HashSet<Transform>();
            AddOldAssetBone(candidates, oldRootBone, assetInstanceRoot);
            if (oldBones != null)
                foreach (var bone in oldBones)
                    AddOldAssetBone(candidates, bone, assetInstanceRoot);
            AddUnusedLocalArmatureCandidates(candidates, assetInstanceRoot, protectedSelf);
            if (candidates.Count == 0) return;

            foreach (var keep in protectedSelf)
                candidates.Remove(keep);
            if (candidates.Count == 0) return;

            var roots = new List<Transform>();
            foreach (var candidate in candidates)
            {
                if (candidate == null || !candidate.IsChildOf(assetInstanceRoot) || protectedSelf.Contains(candidate))
                    continue;

                var root = PromoteDeletionRoot(candidate, assetInstanceRoot, protectedPath, protectedSelf);
                if (root == null || protectedSelf.Contains(root) || HasProtectedDescendant(root, protectedPath))
                    continue;

                bool hasAncestor = false;
                for (int i = roots.Count - 1; i >= 0; i--)
                {
                    if (root.IsChildOf(roots[i])) { hasAncestor = true; break; }
                    if (roots[i].IsChildOf(root)) roots.RemoveAt(i);
                }
                if (!hasAncestor) roots.Add(root);
            }

            int removed = 0;
            foreach (var root in roots)
            {
                if (root == null) continue;
                Undo.DestroyObjectImmediate(root.gameObject);
                removed++;
            }

            removed += CollapseUnusedArmatureContainers(candidates, assetInstanceRoot, protectedSelf, report);

            if (removed > 0)
                report.Info("old-armature-removed", $"Removed {removed} stale asset armature root(s) after replacing the renderer bones.");
        }

        private static int RemoveDuplicateSiblingBranches(Transform assetRoot, SkinnedMeshRenderer renderer, ReFitReport report)
        {
            if (assetRoot == null || renderer == null) return 0;

            var rendererBones = BuildRendererBoneSet(renderer);
            var otherRendererBones = BuildOtherRendererBoneSet(assetRoot, renderer);
            int removed = 0;

            foreach (var parent in assetRoot.GetComponentsInChildren<Transform>(true))
            {
                var groups = new Dictionary<string, List<Transform>>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    var key = SemanticDuplicateKey(child);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new List<Transform>();
                        groups[key] = group;
                    }
                    group.Add(child);
                }

                foreach (var entry in groups)
                {
                    var group = entry.Value;
                    if (group.Count < 2) continue;

                    var keep = ChooseDuplicateBranchToKeep(group, renderer.rootBone, rendererBones, otherRendererBones);
                    foreach (var branch in group)
                    {
                        if (branch == keep) continue;
                        bool usedByRenderer = ContainsAny(branch, rendererBones);
                        bool usedByOtherRenderer = ContainsAny(branch, otherRendererBones);
                        if (usedByRenderer || usedByOtherRenderer)
                        {
                            report.Error("duplicate-armature-branch-used",
                                $"Duplicate '{entry.Key}' branches under '{HierarchyPath(parent)}' are still referenced by skinning; " +
                                $"kept '{HierarchyPath(keep)}' but cannot safely remove '{HierarchyPath(branch)}'.");
                            continue;
                        }

                        if (!CanDeleteTransformTree(branch))
                        {
                            report.Error("duplicate-armature-branch-has-renderer",
                                $"Duplicate branch '{HierarchyPath(branch)}' contains renderable content and is not referenced by skinning. " +
                                "ReFit will not delete renderers as part of armature cleanup.");
                            continue;
                        }

                        Undo.DestroyObjectImmediate(branch.gameObject);
                        removed++;
                    }
                }
            }

            return removed;
        }

        private static int RemoveUnusedArmatureContainers(Transform assetRoot, Transform keepArmatureRoot,
            SkinnedMeshRenderer renderer, Transform[] rendererBones, ReFitReport report)
        {
            if (assetRoot == null || renderer == null) return 0;

            int removed = 0;
            for (int guard = 0; guard < 6; guard++)
            {
                var protectedTransforms = new HashSet<Transform>();
                ProtectTransform(protectedTransforms, keepArmatureRoot);
                ProtectTransform(protectedTransforms, renderer.transform);

                if (rendererBones != null)
                {
                    foreach (var bone in rendererBones)
                        ProtectTransform(protectedTransforms, bone);
                }
                else
                {
                    ProtectTransform(protectedTransforms, renderer.rootBone);
                    var currentBones = renderer.bones;
                    if (currentBones != null)
                        foreach (var bone in currentBones)
                            ProtectTransform(protectedTransforms, bone);
                }

                foreach (var usedRenderer in assetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (usedRenderer == renderer) continue;
                    ProtectTransform(protectedTransforms, usedRenderer.transform);
                    ProtectTransform(protectedTransforms, usedRenderer.rootBone);
                    var usedBones = usedRenderer.bones;
                    if (usedBones == null) continue;
                    foreach (var bone in usedBones)
                        ProtectTransform(protectedTransforms, bone);
                }

                var roots = new List<Transform>();
                for (int i = 0; i < assetRoot.childCount; i++)
                {
                    var child = assetRoot.GetChild(i);
                    if (child == keepArmatureRoot || IsProtectedOrContainsProtected(child, protectedTransforms))
                        continue;
                    if (!IsArmatureContainerTransform(child) && !ContainsHumanoidBoneName(child))
                        continue;
                    if (IsTreeUsedBySkinning(child, assetRoot, renderer, rendererBones))
                        continue;
                    if (!CanDeleteTransformTree(child))
                    {
                        report.Warn("unused-armature-has-renderer",
                            $"Unused armature-like object '{HierarchyPath(child)}' contains renderable content and was left in place.");
                        continue;
                    }
                    roots.Add(child);
                }

                if (roots.Count == 0) break;
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    Undo.DestroyObjectImmediate(root.gameObject);
                    removed++;
                }
            }
            if (removed > 0)
                report.Info("unused-armature-containers-removed",
                    $"Removed {removed} unused local armature container(s) from '{HierarchyPath(assetRoot)}'.");
            return removed;
        }

        private static int RemoveTransientDebugObjects(Transform assetRoot, ReFitReport report)
        {
            if (assetRoot == null) return 0;

            var roots = new List<Transform>();
            foreach (var transform in assetRoot.GetComponentsInChildren<Transform>(true))
            {
                if (transform == assetRoot || !IsTransientDebugObject(transform)) continue;
                bool hasAncestor = false;
                for (int i = roots.Count - 1; i >= 0; i--)
                {
                    if (transform.IsChildOf(roots[i])) { hasAncestor = true; break; }
                    if (roots[i].IsChildOf(transform)) roots.RemoveAt(i);
                }
                if (!hasAncestor) roots.Add(transform);
            }

            int removed = 0;
            foreach (var root in roots)
            {
                if (root == null) continue;
                Undo.DestroyObjectImmediate(root.gameObject);
                removed++;
            }

            if (removed > 0)
                report.Info("transient-debug-objects-removed",
                    $"Removed {removed} transient debug object(s) from '{HierarchyPath(assetRoot)}' before armature cleanup.");
            return removed;
        }

        private static void ValidateExistingInputArmature(Transform assetRoot, SkinnedMeshRenderer renderer, ReFitReport report)
        {
            var duplicates = CollectDuplicateSemanticSiblings(assetRoot);
            if (duplicates.Count > 0)
                report.Error("input-armature-duplicates",
                    $"The input asset still has duplicate humanoid armature branches after preflight repair: {string.Join("; ", duplicates.ToArray())}.");
        }

        private static void ValidateFinalArmatureHierarchy(Transform assetRoot, SkinnedMeshRenderer renderer,
            Transform armatureRoot, Transform[] bones, ReFitReport report)
        {
            if (assetRoot == null || renderer == null || armatureRoot == null || report == null) return;

            RemoveTransientDebugObjects(assetRoot, report);
            RemoveUnusedArmatureContainers(assetRoot, armatureRoot, renderer, bones, report);

            if (renderer.rootBone == null)
            {
                report.Error("armature-root-missing", "The rebuilt renderer has no root bone.");
                return;
            }

            if (!renderer.rootBone.IsChildOf(armatureRoot))
                report.Error("armature-root-outside-rebuilt",
                    $"Renderer root bone '{HierarchyPath(renderer.rootBone)}' is not inside rebuilt armature '{HierarchyPath(armatureRoot)}'.");

            int outsideBones = 0;
            if (bones != null)
            {
                foreach (var bone in bones)
                    if (bone != null && !bone.IsChildOf(armatureRoot))
                        outsideBones++;
            }
            if (outsideBones > 0)
                report.Error("armature-bones-outside-rebuilt",
                    $"{outsideBones} renderer bone(s) are outside rebuilt armature '{HierarchyPath(armatureRoot)}'.");

            var skinnedBranches = new HashSet<Transform>();
            if (bones != null)
            {
                foreach (var bone in bones)
                {
                    var branch = DirectChildUnder(bone, armatureRoot);
                    if (branch != null) skinnedBranches.Add(branch);
                }
            }
            var rootBranch = DirectChildUnder(renderer.rootBone, armatureRoot);
            if (rootBranch != null) skinnedBranches.Add(rootBranch);

            if (skinnedBranches.Count != 1)
                report.Error("armature-multiple-root-branches",
                    $"Rebuilt armature '{HierarchyPath(armatureRoot)}' has {skinnedBranches.Count} skinned root branch(es): {BranchList(skinnedBranches)}.");

            var duplicateFinalBranches = CollectDuplicateSemanticSiblings(armatureRoot);
            if (duplicateFinalBranches.Count > 0)
                report.Error("armature-duplicate-branches",
                    $"Rebuilt armature has duplicate humanoid sibling branches: {string.Join("; ", duplicateFinalBranches.ToArray())}.");

            var extraContainers = new List<string>();
            for (int i = 0; i < assetRoot.childCount; i++)
            {
                var child = assetRoot.GetChild(i);
                if (child == armatureRoot) continue;
                if (IsArmatureContainerTransform(child) || ContainsHumanoidBoneName(child))
                {
                    if (!IsTreeUsedBySkinning(child, assetRoot, renderer, bones) && CanDeleteTransformTree(child))
                    {
                        var removedPath = HierarchyPath(child);
                        Undo.DestroyObjectImmediate(child.gameObject);
                        report.Info("final-unused-armature-removed",
                            $"Removed unused armature-like object '{removedPath}' during final hierarchy validation.");
                        continue;
                    }
                    extraContainers.Add(HierarchyPath(child));
                }
            }
            if (extraContainers.Count > 0)
                report.Error("armature-parallel-containers",
                    $"Asset root still has parallel armature-like object(s) after replacement: {string.Join("; ", extraContainers.ToArray())}.");

            report.Info("armature-hierarchy-validation",
                $"Final hierarchy validation: armature='{HierarchyPath(armatureRoot)}', rootBone='{HierarchyPath(renderer.rootBone)}', " +
                $"skinnedRootBranches={skinnedBranches.Count}, duplicateSiblingBranches={duplicateFinalBranches.Count}, parallelArmatures={extraContainers.Count}.");
        }

        private static HashSet<Transform> BuildRendererBoneSet(SkinnedMeshRenderer renderer)
        {
            var result = new HashSet<Transform>();
            if (renderer == null) return result;
            if (renderer.rootBone != null) result.Add(renderer.rootBone);
            var bones = renderer.bones;
            if (bones == null) return result;
            foreach (var bone in bones)
                if (bone != null) result.Add(bone);
            return result;
        }

        private static HashSet<Transform> BuildOtherRendererBoneSet(Transform assetRoot, SkinnedMeshRenderer renderer)
        {
            var result = new HashSet<Transform>();
            if (assetRoot == null) return result;
            foreach (var other in assetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (other == null || other == renderer) continue;
                if (other.rootBone != null) result.Add(other.rootBone);
                var bones = other.bones;
                if (bones == null) continue;
                foreach (var bone in bones)
                    if (bone != null) result.Add(bone);
            }
            return result;
        }

        private static string SemanticDuplicateKey(Transform transform)
        {
            if (transform == null) return null;
            var key = ReFitUtility.NormalizeName(transform.name);
            if (string.IsNullOrEmpty(key)) return null;
            if (IsArmatureContainerTransform(transform)) return key;
            return HumanoidBoneMapper.TryInferHumanoidBone(transform, out _) ? key : null;
        }

        private static Transform ChooseDuplicateBranchToKeep(List<Transform> branches, Transform rootBone,
            HashSet<Transform> rendererBones, HashSet<Transform> otherRendererBones)
        {
            Transform best = null;
            int bestScore = int.MinValue;
            foreach (var branch in branches)
            {
                if (branch == null) continue;
                int score = 0;
                if (rootBone != null && rootBone.IsChildOf(branch)) score += 100000;
                score += CountContained(branch, rendererBones) * 100;
                score += CountContained(branch, otherRendererBones) * 10;
                score -= branch.GetSiblingIndex();
                if (best == null || score > bestScore)
                {
                    best = branch;
                    bestScore = score;
                }
            }
            return best;
        }

        private static int CountContained(Transform root, HashSet<Transform> transforms)
        {
            if (root == null || transforms == null) return 0;
            int count = 0;
            foreach (var transform in transforms)
                if (transform != null && transform.IsChildOf(root))
                    count++;
            return count;
        }

        private static bool ContainsAny(Transform root, HashSet<Transform> transforms)
        {
            return CountContained(root, transforms) > 0;
        }

        private static bool IsTreeUsedBySkinning(Transform root, Transform assetRoot,
            SkinnedMeshRenderer primaryRenderer, Transform[] primaryBones)
        {
            if (root == null) return false;
            if (RendererUsesTree(primaryRenderer, primaryBones, root)) return true;

            if (assetRoot == null) return false;
            foreach (var renderer in assetRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer == primaryRenderer) continue;
                if (RendererUsesTree(renderer, renderer.bones, root)) return true;
            }
            return false;
        }

        private static bool RendererUsesTree(SkinnedMeshRenderer renderer, Transform[] bones, Transform root)
        {
            if (renderer == null || root == null) return false;
            if (renderer.transform != null && renderer.transform.IsChildOf(root)) return true;
            if (renderer.rootBone != null && renderer.rootBone.IsChildOf(root)) return true;

            var activeBones = bones ?? renderer.bones;
            if (activeBones == null) return false;
            foreach (var bone in activeBones)
                if (bone != null && bone.IsChildOf(root))
                    return true;
            return false;
        }

        private static bool CanDeleteTransformTree(Transform root)
        {
            if (root == null) return false;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var components = transform.GetComponents<Component>();
                foreach (var component in components)
                    if (component is Renderer || component is MeshFilter)
                        return false;
            }
            return true;
        }

        private static void ProtectTransform(HashSet<Transform> protectedTransforms, Transform transform)
        {
            if (protectedTransforms == null || transform == null) return;
            protectedTransforms.Add(transform);
        }

        private static void ProtectTransformAndDescendants(HashSet<Transform> protectedTransforms, Transform transform)
        {
            if (protectedTransforms == null || transform == null) return;
            foreach (var child in transform.GetComponentsInChildren<Transform>(true))
                protectedTransforms.Add(child);
        }

        private static bool IsProtectedOrContainsProtected(Transform root, HashSet<Transform> protectedTransforms)
        {
            if (root == null || protectedTransforms == null) return false;
            foreach (var transform in protectedTransforms)
                if (transform != null && transform.IsChildOf(root))
                    return true;
            return false;
        }

        private static bool IsArmatureContainerTransform(Transform transform)
        {
            if (transform == null) return false;
            var key = ReFitUtility.NormalizeName(transform.name);
            return key == "armature" || key == "skeleton" || key == "rig" || key.StartsWith("armature");
        }

        private static bool IsTransientDebugObject(Transform transform)
        {
            if (transform == null) return false;
            return transform.name.StartsWith("__XRayGizmos_", System.StringComparison.Ordinal);
        }

        private static List<string> CollectDuplicateSemanticSiblings(Transform root)
        {
            var result = new List<string>();
            if (root == null) return result;
            foreach (var parent in root.GetComponentsInChildren<Transform>(true))
            {
                var counts = new Dictionary<string, int>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    var key = SemanticDuplicateKey(child);
                    if (string.IsNullOrEmpty(key)) continue;
                    counts.TryGetValue(key, out var count);
                    counts[key] = count + 1;
                }

                foreach (var entry in counts)
                    if (entry.Value > 1)
                        result.Add($"{HierarchyPath(parent)} has {entry.Value} '{entry.Key}' children");
            }
            return result;
        }

        private static Transform DirectChildUnder(Transform transform, Transform ancestor)
        {
            if (transform == null || ancestor == null || !transform.IsChildOf(ancestor)) return null;
            var current = transform;
            while (current.parent != null && current.parent != ancestor)
                current = current.parent;
            return current.parent == ancestor ? current : null;
        }

        private static string BranchList(HashSet<Transform> branches)
        {
            if (branches == null || branches.Count == 0) return "none";
            var parts = new List<string>();
            foreach (var branch in branches)
                if (branch != null) parts.Add(HierarchyPath(branch));
            return string.Join(", ", parts.ToArray());
        }

        private static bool IsAvatarRoot(Transform root, ReFitRequest request, GameObject targetInstance)
        {
            if (root == null) return true;
            if (targetInstance != null && root == targetInstance.transform) return true;
            if (request?.targetAvatar != null && root == request.targetAvatar.transform) return true;
            if (request?.sourceAvatar != null && root == request.sourceAvatar.transform) return true;
            return false;
        }

        private static void AddOldAssetBone(HashSet<Transform> candidates, Transform bone, Transform assetRoot)
        {
            if (bone == null || assetRoot == null || !bone.IsChildOf(assetRoot)) return;
            candidates.Add(bone);
        }

        private static void AddUnusedLocalArmatureCandidates(HashSet<Transform> candidates, Transform assetRoot,
            HashSet<Transform> protectedSelf)
        {
            if (assetRoot == null) return;
            foreach (var t in assetRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == assetRoot || protectedSelf.Contains(t)) continue;
                if (LooksLikeArmatureTransform(t) || ContainsHumanoidBoneName(t))
                    candidates.Add(t);
            }
        }

        private static void ProtectTransform(HashSet<Transform> protectedPath, HashSet<Transform> protectedSelf,
            Transform t, Transform stopAt)
        {
            if (t == null || stopAt == null || !t.IsChildOf(stopAt)) return;
            protectedSelf.Add(t);
            ProtectPath(protectedPath, t, stopAt);
        }

        private static void ProtectPath(HashSet<Transform> protectedPath, Transform t, Transform stopAt)
        {
            while (t != null)
            {
                protectedPath.Add(t);
                if (t == stopAt) break;
                t = t.parent;
            }
        }

        private static Transform PromoteDeletionRoot(Transform candidate, Transform assetRoot,
            HashSet<Transform> protectedPath, HashSet<Transform> protectedSelf)
        {
            var root = candidate;
            while (root.parent != null && root.parent != assetRoot && root.parent.IsChildOf(assetRoot) &&
                   !protectedSelf.Contains(root.parent) && !HasProtectedDescendant(root.parent, protectedPath) &&
                   HasOnlyTransform(root.parent))
            {
                root = root.parent;
            }
            return root;
        }

        private static int CollapseUnusedArmatureContainers(HashSet<Transform> candidates, Transform assetRoot,
            HashSet<Transform> protectedSelf, ReFitReport report)
        {
            if (candidates == null || candidates.Count == 0 || assetRoot == null) return 0;

            var ordered = new List<Transform>(candidates);
            ordered.Sort((a, b) => Depth(b).CompareTo(Depth(a)));

            int removed = 0;
            foreach (var candidate in ordered)
            {
                if (candidate == null || candidate == assetRoot || !candidate.IsChildOf(assetRoot)) continue;
                if (protectedSelf.Contains(candidate) || !HasOnlyTransform(candidate)) continue;
                if (!LooksLikeArmatureTransform(candidate) && !ContainsHumanoidBoneName(candidate)) continue;
                var parent = candidate.parent;
                if (parent == null) continue;
                if (!MakeRestructurable(candidate, report)) continue;

                for (int i = candidate.childCount - 1; i >= 0; i--)
                {
                    var child = candidate.GetChild(i);
                    var position = child.position;
                    var rotation = child.rotation;
                    var scale = child.lossyScale;
                    Undo.SetTransformParent(child, parent, "ReFit remove stale armature");
                    child.position = position;
                    child.rotation = rotation;
                    child.localScale = DivideScale(scale, child.parent != null ? child.parent.lossyScale : Vector3.one);
                }

                Undo.DestroyObjectImmediate(candidate.gameObject);
                removed++;
            }
            return removed;
        }

        private static int Depth(Transform transform)
        {
            int depth = 0;
            while (transform != null)
            {
                depth++;
                transform = transform.parent;
            }
            return depth;
        }

        private static Vector3 DivideScale(Vector3 value, Vector3 by)
        {
            return new Vector3(
                Mathf.Abs(by.x) > 1e-6f ? value.x / by.x : value.x,
                Mathf.Abs(by.y) > 1e-6f ? value.y / by.y : value.y,
                Mathf.Abs(by.z) > 1e-6f ? value.z / by.z : value.z);
        }

        private static string UniqueChildName(Transform parent, string desired, Transform self)
        {
            if (parent == null) return desired;
            bool available = true;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == self) continue;
                if (child.name == desired) { available = false; break; }
            }
            if (available) return desired;

            int suffix = 1;
            while (true)
            {
                var candidate = $"{desired} {suffix}";
                available = true;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child == self) continue;
                    if (child.name == candidate) { available = false; break; }
                }
                if (available) return candidate;
                suffix++;
            }
        }

        private static bool LooksLikeArmatureTransform(Transform t)
        {
            if (t == null) return false;
            var key = ReFitUtility.NormalizeName(t.name);
            return key == "armature" || key == "skeleton" || key == "rig" ||
                   HumanoidBoneMapper.TryInferHumanoidBone(t, out _);
        }

        private static bool ContainsHumanoidBoneName(Transform root)
        {
            if (root == null) return false;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child != root && HumanoidBoneMapper.TryInferHumanoidBone(child, out _))
                    return true;
            return false;
        }

        private static bool HasProtectedDescendant(Transform root, HashSet<Transform> protectedSet)
        {
            foreach (var protectedTransform in protectedSet)
                if (protectedTransform != null && protectedTransform != root && protectedTransform.IsChildOf(root))
                    return true;
            return false;
        }

        private static bool HasOnlyTransform(Transform t)
        {
            var components = t.GetComponents<Component>();
            foreach (var component in components)
                if (component != null && !(component is Transform))
                    return false;
            return true;
        }

        /// <summary>
        /// Unity forbids restructuring prefab instances; unpack (outermost first) until <paramref name="t"/> can be
        /// reparented. Returns false when it could not be made restructurable.
        /// </summary>
        private static bool MakeRestructurable(Transform t, ReFitReport report)
        {
            for (int guard = 0; guard < 6; guard++)
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(t)) return true;
                // Reparenting the instance ROOT itself is allowed without unpacking.
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(t);
                if (root == t.gameObject) return true;
                if (root == null) return true;
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                report.Info("prefab-unpacked", $"Unpacked the prefab instance '{root.name}' to allow moving '{t.name}'.");
            }
            report.Warn("prefab-unpack-failed", $"Could not unpack the prefab instance containing '{t.name}'.");
            return false;
        }

        // ------------------------------------------------------------------
        // Folders
        // ------------------------------------------------------------------

        private static string EnsureFolder(string path)
        {
            var parts = path.Split('/');
            var current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
            return current;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ReFit";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
