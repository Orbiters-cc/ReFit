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
            renderer.sharedMesh = comp.mesh;
            debug?.Capture("01_generated_mesh_assigned", renderer);

            // --- armature replacement ------------------------------------------------------
            if (comp.armatureReplaced)
            {
                if (!ApplyBonePlan(request, comp, targetInstance, assetInstanceRoot, renderer, report))
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
            debug?.Capture(comp.armatureReplaced ? "02_armature_replaced" : "02_armature_kept", renderer);

            // --- enable the generated shapes -------------------------------------------------
            if (!string.IsNullOrEmpty(comp.primaryShapeName))
            {
                int idx = comp.mesh.GetBlendShapeIndex(comp.primaryShapeName);
                if (idx >= 0) renderer.SetBlendShapeWeight(idx, 100f);
                debug?.Capture(idx >= 0 ? "03_primary_blendshape_enabled" : "03_primary_blendshape_missing", renderer);
            }
            if (comp.secondaryShapeNames != null)
            {
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
                    debug?.Capture("04_transferred_blendshapes_enabled", renderer);
            }
            debug?.Capture("05_final_result", renderer);

            Selection.activeGameObject = renderer.gameObject;
            EditorGUIUtility.PingObject(renderer.gameObject);
            return renderer;
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
            var sourceBones = ResolveBoneBlueprints(request, comp, targetInstance, assetInstanceRoot, out var missing);
            if (missing > 0)
                report.Warn("bones-missing", $"{missing}/{sourceBones.Length} bones could not be resolved on the target; affected vertices may not deform correctly.");

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
            RemoveReplacedAssetArmature(request, targetInstance, assetInstanceRoot, renderer, oldBones, oldRootBone, bones, newArmatureRoot, report);
            newArmatureRoot.name = UniqueChildName(assetInstanceRoot, "Armature", newArmatureRoot);
            report.Info("armature-rebuilt", $"Built a new clothing armature with {bones.Length - missing}/{bones.Length} resolved bone(s).");
            return missing == 0;
        }

        private static Transform[] ResolveBoneBlueprints(ReFitRequest request, ReFitComputation comp,
            GameObject targetInstance, Transform assetInstanceRoot, out int missing)
        {
            missing = 0;
            var sources = new Transform[comp.bones != null ? comp.bones.Length : 0];
            for (int i = 0; i < sources.Length; i++)
            {
                var boneRef = comp.bones[i];
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
                if (sources[i] == null) missing++;
            }
            return sources;
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

                SetParentPreservingWorld(proxy, parent, source.position, source.rotation, source.lossyScale);
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
