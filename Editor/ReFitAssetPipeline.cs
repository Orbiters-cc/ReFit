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
        public static SkinnedMeshRenderer ApplyToScene(ReFitRequest request, ReFitComputation comp, ReFitReport report)
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

            // --- enable the generated shapes -------------------------------------------------
            if (!string.IsNullOrEmpty(comp.primaryShapeName))
            {
                int idx = comp.mesh.GetBlendShapeIndex(comp.primaryShapeName);
                if (idx >= 0) renderer.SetBlendShapeWeight(idx, 100f);
            }
            if (comp.secondaryShapeNames != null)
            {
                for (int s = 0; s < comp.secondaryShapeNames.Length; s++)
                {
                    int idx = comp.mesh.GetBlendShapeIndex(comp.secondaryShapeNames[s]);
                    if (idx < 0) continue;
                    float weight = comp.secondaryMirrorWeights != null && s < comp.secondaryMirrorWeights.Length
                        ? comp.secondaryMirrorWeights[s]
                        : 100f;
                    renderer.SetBlendShapeWeight(idx, weight);
                }
            }

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
            var realRoot = PoseNormalizer.FindCommonRoot(request.assetRenderer);
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
            // 1) Duplicate kept source-avatar bone subtrees (unparented for now).
            //    Duplicates carry their components (e.g. physbones) along.
            var duplicated = new List<(int[] srcPath, Transform dupRoot)>();
            if (comp.keptPlacements != null && request.sourceAvatar != null)
            {
                foreach (var placement in comp.keptPlacements)
                {
                    var boneRef = comp.bones[placement.boneIndex];
                    if (boneRef.origin != ReFitBoneOrigin.SourceAvatar) continue;
                    var src = ReFitUtility.ResolvePath(request.sourceAvatar.transform, boneRef.path);
                    if (src == null) { report.Warn("kept-bone-missing", "Could not resolve a preserved bone subtree on the source avatar."); continue; }
                    var dup = Object.Instantiate(src.gameObject);
                    dup.name = src.name;
                    Undo.RegisterCreatedObjectUndo(dup, "ReFit kept bones");
                    duplicated.Add((boneRef.path, dup.transform));
                }
            }

            // 2) Resolve the FULL bone array before any reparenting (index paths shift once subtrees move).
            var bones = new Transform[comp.bones.Length];
            int missing = 0;
            for (int i = 0; i < comp.bones.Length; i++)
            {
                var boneRef = comp.bones[i];
                switch (boneRef.origin)
                {
                    case ReFitBoneOrigin.Target:
                        bones[i] = ReFitUtility.ResolvePath(targetInstance.transform, boneRef.path);
                        break;
                    case ReFitBoneOrigin.Asset:
                        bones[i] = ReFitUtility.ResolvePath(assetInstanceRoot, boneRef.path);
                        break;
                    case ReFitBoneOrigin.SourceAvatar:
                        bones[i] = FindInDuplicates(boneRef.path, duplicated);
                        break;
                }
                if (bones[i] == null) missing++;
            }
            if (missing > 0)
                report.Warn("bones-missing", $"{missing}/{bones.Length} bones could not be resolved on the target; affected vertices may not deform correctly.");

            // 3) Place kept subtree roots under their target parents with the staged relative transform.
            if (comp.keptPlacements != null)
            {
                foreach (var placement in comp.keptPlacements)
                {
                    var subtreeRoot = bones[placement.boneIndex];
                    if (subtreeRoot == null) continue;
                    var parent = ReFitUtility.ResolvePath(targetInstance.transform, placement.targetParentPath);
                    if (parent == null)
                    {
                        report.Warn("kept-parent-missing", $"Could not resolve the target parent of preserved bone '{subtreeRoot.name}'.");
                        continue;
                    }
                    if (!MakeRestructurable(subtreeRoot, report)) continue;
                    Undo.SetTransformParent(subtreeRoot, parent, "ReFit kept bones");
                    subtreeRoot.localPosition = placement.localPosition;
                    subtreeRoot.localRotation = placement.localRotation;
                    subtreeRoot.localScale = placement.localScale;
                }
            }

            // 4) Assign the new skinning.
            renderer.bones = bones;
            if (comp.rootBoneIndex >= 0 && comp.rootBoneIndex < bones.Length && bones[comp.rootBoneIndex] != null)
                renderer.rootBone = bones[comp.rootBoneIndex];
            // Mesh space changed (staged-pose bindposes): the authored local bounds are no longer reliable.
            renderer.updateWhenOffscreen = true;
            return missing == 0;
        }

        private static Transform FindInDuplicates(int[] path, List<(int[] srcPath, Transform dupRoot)> duplicated)
        {
            foreach (var (srcPath, dupRoot) in duplicated)
            {
                if (path.Length < srcPath.Length) continue;
                bool prefix = true;
                for (int i = 0; i < srcPath.Length; i++)
                    if (path[i] != srcPath[i]) { prefix = false; break; }
                if (!prefix) continue;
                var rel = new int[path.Length - srcPath.Length];
                System.Array.Copy(path, srcPath.Length, rel, 0, rel.Length);
                var t = ReFitUtility.ResolvePath(dupRoot, rel);
                if (t != null) return t;
            }
            return null;
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
