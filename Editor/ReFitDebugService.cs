using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Editor-only diagnostics for inspecting each scene mutation made by ReFit.</summary>
    public static class ReFitDebugService
    {
        private const string EnabledKey = "Orbiters.ReFit.DebugMode";
        private const string SessionRootPrefix = "__ReFit_Debug_Session_";

        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(EnabledKey, false); }
            set { EditorPrefs.SetBool(EnabledKey, value); }
        }

        public static ReFitDebugSession BeginSession(ReFitRequest request, ReFitReport report)
        {
            if (!Enabled) return null;
            return new ReFitDebugSession(request, report);
        }

        public static int FlushSceneDebugObjects()
        {
            var roots = new List<GameObject>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || EditorUtility.IsPersistent(go)) continue;
                if (!go.scene.IsValid()) continue;
                if (!go.name.StartsWith(SessionRootPrefix, StringComparison.Ordinal)) continue;
                roots.Add(go);
            }

            if (roots.Count == 0)
            {
                Debug.Log("[ReFit] No debug snapshot objects found in the open scenes.");
                return 0;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Remove ReFit debug snapshots");
            foreach (var root in roots)
                Undo.DestroyObjectImmediate(root);

            Debug.Log($"[ReFit] Removed {roots.Count} debug snapshot session(s) from the open scenes.");
            return roots.Count;
        }

        internal static string SanitizeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "ReFit";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value.Replace('/', '_').Replace('\\', '_');
        }

        internal static string SessionRootName(string suffix)
        {
            return SessionRootPrefix + suffix;
        }
    }

    public sealed class ReFitDebugSession
    {
        private const float SnapshotSpacing = 0.7f;
        private readonly ReFitRequest request;
        private readonly ReFitReport report;
        private readonly Vector3 anchor;
        private readonly GameObject root;
        private int snapshotCount;

        internal ReFitDebugSession(ReFitRequest request, ReFitReport report)
        {
            this.request = request;
            this.report = report;
            anchor = ResolveAnchor(request);

            var assetName = request != null && request.assetRenderer != null ? request.assetRenderer.name : "Asset";
            root = new GameObject(ReFitDebugService.SessionRootName(
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + ReFitDebugService.SanitizeName(assetName)));
            root.transform.position = anchor;
            Undo.RegisterCreatedObjectUndo(root, "ReFit debug session");

            Log("debug-session-started",
                $"Started debug session '{root.name}' at {Format(anchor)}. Snapshots are spaced {SnapshotSpacing:0.###}m on world -Z from that anchor.");
            LogRequest();
        }

        public GameObject Root
        {
            get { return root; }
        }

        public void Capture(string label, SkinnedMeshRenderer renderer, ReFitProjectionDebugData projectionDebug = null,
            ReFitIslandPropagationDebugData islandPropagationDebug = null)
        {
            CaptureSnapshot(label, renderer, projectionDebug, islandPropagationDebug);
        }

        public void CaptureScenePoseAsDefault(string label, SkinnedMeshRenderer renderer)
        {
            CaptureSnapshot(label, renderer, null, null);
        }

        private void CaptureSnapshot(string label, SkinnedMeshRenderer renderer,
            ReFitProjectionDebugData projectionDebug,
            ReFitIslandPropagationDebugData islandPropagationDebug)
        {
            if (renderer == null) return;
            try
            {
                var sourceRoot = PoseNormalizer.FindAssetObjectRoot(renderer,
                    request != null ? request.sourceAvatar : null,
                    request != null ? request.targetAvatar : null);
                if (sourceRoot == null) sourceRoot = renderer.transform;

                var rendererPath = ReFitUtility.IndexPath(renderer.transform, sourceRoot);
                var clone = Object.Instantiate(sourceRoot.gameObject);
                clone.name = $"{snapshotCount:00}_{ReFitDebugService.SanitizeName(label)}_{ReFitDebugService.SanitizeName(renderer.name)}";
                clone.hideFlags = HideFlags.None;
                Undo.RegisterCreatedObjectUndo(clone, "ReFit debug snapshot");

                clone.SetActive(true);
                foreach (var t in clone.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.hideFlags = HideFlags.None;
                    t.gameObject.SetActive(true);
                }

                var cloneRendererTransform = ReFitUtility.ResolvePath(clone.transform, rendererPath);
                var cloneRenderer = cloneRendererTransform != null
                    ? cloneRendererTransform.GetComponent<SkinnedMeshRenderer>()
                    : clone.GetComponentInChildren<SkinnedMeshRenderer>(true);

                var meshName = BuildSnapshotMeshName(renderer, snapshotCount, label);
                ConfigureClone(clone, renderer, cloneRenderer, meshName);
                AttachProjectionDebug(cloneRenderer, projectionDebug);
                AttachWeldedGroupDebug(cloneRenderer, islandPropagationDebug);
                clone.transform.SetParent(root.transform, true);
                PositionClone(clone, cloneRenderer != null ? (Renderer)cloneRenderer : clone.GetComponentInChildren<Renderer>(true));
                LogSnapshot(label, renderer, clone, cloneRenderer);
                snapshotCount++;
            }
            catch (Exception e)
            {
                Log("debug-snapshot-failed", $"Could not create debug snapshot '{label}': {e.Message}");
                Debug.LogException(e);
            }
        }

        private static void AttachProjectionDebug(SkinnedMeshRenderer cloneRenderer, ReFitProjectionDebugData projectionDebug)
        {
            if (cloneRenderer == null || projectionDebug == null || projectionDebug.points == null ||
                projectionDebug.points.Length == 0)
                return;

            var component = cloneRenderer.gameObject.AddComponent<ReFitProjectionDebugComponent>();
            component.targetRenderer = cloneRenderer;
            component.data = projectionDebug;
            component.visible = true;
        }

        private static void AttachWeldedGroupDebug(SkinnedMeshRenderer cloneRenderer,
            ReFitIslandPropagationDebugData islandPropagationDebug)
        {
            var data = BuildWeldedGroupDebugData(cloneRenderer, islandPropagationDebug);
            if (data == null || data.groups == null || data.groups.Length == 0)
                return;

            var component = cloneRenderer.gameObject.AddComponent<ReFitWeldedGroupDebugComponent>();
            component.targetRenderer = cloneRenderer;
            component.data = data;
            component.visible = true;
        }

        private static ReFitWeldedGroupDebugData BuildWeldedGroupDebugData(SkinnedMeshRenderer renderer,
            ReFitIslandPropagationDebugData islandPropagationDebug)
        {
            if (renderer == null || renderer.sharedMesh == null)
                return null;

            MeshSnapshot snapshot;
            try
            {
                snapshot = MeshSnapshot.Capture(renderer, true, null, null);
            }
            catch
            {
                return null;
            }

            if (snapshot == null || snapshot.groupRep == null || snapshot.groupRep.Length == 0)
                return null;

            var propagationByGroup = BuildIslandPropagationLookup(islandPropagationDebug);
            var groups = new ReFitWeldedGroupDebugPoint[snapshot.groupRep.Length];
            for (int g = 0; g < groups.Length; g++)
            {
                int vertex = snapshot.groupRep[g];
                var group = new ReFitWeldedGroupDebugPoint
                {
                    groupIndex = g,
                    representativeVertexIndex = vertex,
                    vertexCount = snapshot.groupMembers != null && g < snapshot.groupMembers.Length && snapshot.groupMembers[g] != null
                        ? snapshot.groupMembers[g].Count
                        : 1,
                    localPoint = vertex >= 0 && vertex < snapshot.localVertices.Length
                        ? snapshot.localVertices[vertex]
                        : Vector3.zero
                };
                if (propagationByGroup != null &&
                    g < propagationByGroup.Length &&
                    propagationByGroup[g] != null)
                {
                    var propagation = propagationByGroup[g];
                    group.islandPropagationComponentIndex = propagation.componentIndex;
                    group.islandPropagationComponentSize = propagation.componentSize;
                    group.islandPropagationSupportComponentIndex = propagation.supportComponentIndex;
                    group.islandPropagationSupportGroupIndex = propagation.supportGroupIndex;
                    group.islandPropagationStatus = propagation.status;
                    group.islandPropagationReason = propagation.reason;
                    group.islandPropagationSupportDistance = propagation.supportDistance;
                    group.islandPropagationReceiverWeight = propagation.receiverWeight;
                    group.islandPropagationCurrentCorrectionMagnitude = propagation.currentCorrectionMagnitude;
                    group.islandPropagationTargetCorrectionMagnitude = propagation.targetCorrectionMagnitude;
                    group.islandPropagationAppliedCorrectionMagnitude = propagation.appliedCorrectionMagnitude;
                }
                groups[g] = group;
            }

            return new ReFitWeldedGroupDebugData { groups = groups };
        }

        private static ReFitIslandPropagationDebugPoint[] BuildIslandPropagationLookup(
            ReFitIslandPropagationDebugData data)
        {
            if (data == null || data.groups == null || data.groups.Length == 0)
                return null;

            int max = -1;
            for (int i = 0; i < data.groups.Length; i++)
            {
                var point = data.groups[i];
                if (point != null)
                    max = Mathf.Max(max, point.groupIndex);
            }
            if (max < 0)
                return null;

            var lookup = new ReFitIslandPropagationDebugPoint[max + 1];
            for (int i = 0; i < data.groups.Length; i++)
            {
                var point = data.groups[i];
                if (point == null || point.groupIndex < 0 || point.groupIndex >= lookup.Length)
                    continue;
                lookup[point.groupIndex] = point;
            }
            return lookup;
        }

        public void Finish()
        {
            Log("debug-session-finished", $"Finished debug session '{root.name}' with {snapshotCount} snapshot(s).");
        }

        private void ConfigureClone(GameObject clone, SkinnedMeshRenderer sourceRenderer, SkinnedMeshRenderer cloneRenderer,
            string meshName)
        {
            var localPose = CaptureLocalPose(clone);
            foreach (var behaviour in clone.GetComponentsInChildren<Behaviour>(true))
            {
                try { behaviour.enabled = false; }
                catch { /* Some editor/third-party behaviours refuse changes. The clone is only diagnostic. */ }
            }
            RestoreLocalPose(localPose);

            var renderers = clone.GetComponentsInChildren<Renderer>(true);
            if (cloneRenderer != null)
            {
                foreach (var renderer in renderers)
                    renderer.enabled = renderer == cloneRenderer;
                FreezeRendererMesh(sourceRenderer, cloneRenderer, meshName);
                BakeCurrentSnapshotPose(cloneRenderer, meshName);
                cloneRenderer.updateWhenOffscreen = true;
            }
            else
            {
                foreach (var renderer in renderers)
                    renderer.enabled = true;
            }
        }

        private static string BuildSnapshotMeshName(SkinnedMeshRenderer renderer, int index, string label)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            var baseName = mesh != null ? mesh.name.Replace("(Clone)", "") : "Mesh";
            return $"{baseName}_Debug_{index:00}_{ReFitDebugService.SanitizeName(label)}";
        }

        private static void FreezeRendererMesh(SkinnedMeshRenderer source, SkinnedMeshRenderer clone, string meshName)
        {
            if (source == null || clone == null || source.sharedMesh == null)
                return;

            var mesh = Object.Instantiate(source.sharedMesh);
            mesh.name = string.IsNullOrEmpty(meshName)
                ? source.sharedMesh.name.Replace("(Clone)", "") + "_DebugSnapshot"
                : meshName;
            mesh.hideFlags = HideFlags.None;
            BakeSourceBlendShapesIntoBase(source, mesh);
            clone.sharedMesh = mesh;
        }

        private static void BakeSourceBlendShapesIntoBase(SkinnedMeshRenderer source, Mesh mesh)
        {
            if (source == null || source.sharedMesh == null || mesh == null)
                return;

            if (source.sharedMesh.blendShapeCount > 0)
            {
                try
                {
                    var snapshot = MeshSnapshot.Capture(source, false, null, null);
                    if (snapshot != null && snapshot.localVertices != null &&
                        snapshot.localVertices.Length == mesh.vertexCount)
                        mesh.vertices = snapshot.localVertices;
                }
                catch
                {
                    // A debug snapshot should still exist even if an unusual mesh cannot be sampled.
                }
            }

            if (mesh.blendShapeCount > 0)
                mesh.ClearBlendShapes();
            mesh.RecalculateBounds();
        }

        private static void BakeCurrentSnapshotPose(SkinnedMeshRenderer cloneRenderer, string meshName)
        {
            if (cloneRenderer == null || cloneRenderer.sharedMesh == null)
                return;

            var preBakeMesh = cloneRenderer.sharedMesh;
            if (!PoseNormalizer.BakeCurrentSkinPoseAsDefault(cloneRenderer, null))
            {
                Undo.RegisterCreatedObjectUndo(preBakeMesh, "ReFit debug mesh snapshot");
                return;
            }

            var bakedMesh = cloneRenderer.sharedMesh;
            if (bakedMesh == null)
                return;

            bakedMesh.name = string.IsNullOrEmpty(meshName)
                ? bakedMesh.name.Replace("(Clone)", "") + "_Baked"
                : meshName + "_Baked";
            bakedMesh.hideFlags = HideFlags.None;
            Undo.RegisterCreatedObjectUndo(bakedMesh, "ReFit debug baked mesh snapshot");

            if (preBakeMesh != null && preBakeMesh != bakedMesh && !AssetDatabase.Contains(preBakeMesh))
                Object.DestroyImmediate(preBakeMesh);
        }

        private static Dictionary<Transform, LocalPose> CaptureLocalPose(GameObject root)
        {
            var pose = new Dictionary<Transform, LocalPose>();
            if (root == null) return pose;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                pose[t] = new LocalPose(t.localPosition, t.localRotation, t.localScale);
            return pose;
        }

        private static void RestoreLocalPose(Dictionary<Transform, LocalPose> pose)
        {
            if (pose == null) return;
            foreach (var entry in pose)
            {
                var t = entry.Key;
                if (t == null) continue;
                t.localPosition = entry.Value.position;
                t.localRotation = entry.Value.rotation;
                t.localScale = entry.Value.scale;
            }
        }

        private void PositionClone(GameObject clone, Renderer focusRenderer)
        {
            var desiredCenter = anchor + (Vector3.back * SnapshotSpacing * (snapshotCount + 1));
            var currentCenter = focusRenderer != null ? focusRenderer.bounds.center : clone.transform.position;
            clone.transform.position += desiredCenter - currentCenter;
        }

        private void LogRequest()
        {
            if (request == null) return;
            var settings = request.settings;
            Log("debug-request",
                $"mode={request.mode}, asset='{NameOf(request.assetRenderer)}', source='{NameOf(request.sourceAvatar)}', target='{NameOf(request.targetAvatar)}', " +
                $"targetShape='{request.targetBlendshape}', replaceArmature={BoolSetting(settings, s => s.replaceArmature)}, transferWeights={BoolSetting(settings, s => s.transferWeights)}, " +
                $"captureProjectionDebug={BoolSetting(settings, s => s.captureProjectionDebug)}, " +
                $"clearanceCorrection={BoolSetting(settings, s => s.enableClearanceCorrection)}, tightening={FloatSetting(settings, s => 1f - Mathf.Clamp01(s.clearanceTightnessFactor))}, " +
                $"clearanceRetention={FloatSetting(settings, s => s.clearanceTightnessFactor)}, inwardStrength={FloatSetting(settings, s => s.clearanceInwardStrength)}, " +
                $"expansionStart={FloatSetting(settings, s => s.clearanceExpansionStart)}, expansionFull={FloatSetting(settings, s => s.clearanceExpansionFull)}, " +
                $"clearanceSmoothing={IntSetting(settings, s => s.clearanceSmoothingIterations)}x{FloatSetting(settings, s => s.clearanceSmoothingStrength)}, " +
                $"surfaceGuard={IntSetting(settings, s => s.clearanceSurfaceGuardIterations)}x{FloatSetting(settings, s => s.clearanceSurfaceGuardStrength)}, " +
                $"maxSurfaceGuard={FloatSetting(settings, s => s.clearanceMaxSurfaceGuardCorrection)}, " +
                $"surfaceGuardTrigger={FloatSetting(settings, s => s.clearanceSurfaceGuardTriggerDistance)}, " +
                $"surfaceGuardEdgeSamples={IntSetting(settings, s => s.clearanceSurfaceGuardEdgeSamples)}, " +
                $"maxPrimaryTotal={FloatSetting(settings, s => s.clearanceMaxPrimaryTotalCorrection)}, " +
                $"maxTransferredTotal={FloatSetting(settings, s => s.clearanceMaxTransferredTotalCorrection)}, " +
                $"transferredInwardScale={FloatSetting(settings, s => s.clearanceTransferredInwardScale)}, " +
                $"openBoundaryScale={FloatSetting(settings, s => s.clearanceOpenBoundaryCorrectionScale)}, " +
                $"lowConfidenceScale={FloatSetting(settings, s => s.clearanceLowConfidenceCorrectionScale)}, " +
                $"upperBodyHemFollowScale={FloatSetting(settings, s => s.upperBodyGarmentHemFollowScale)}, " +
                $"islandPropagation={BoolSetting(settings, s => s.clearancePropagateDisconnectedIslands)}x{FloatSetting(settings, s => s.clearanceIslandPropagationStrength)}, " +
                $"islandPropagationSearch={FloatSetting(settings, s => s.clearanceIslandPropagationSearchDistance)}, " +
                $"maxIslandPropagation={FloatSetting(settings, s => s.clearanceMaxIslandPropagationCorrection)}, " +
                $"islandPropagationDonorThreshold={FloatSetting(settings, s => s.clearanceIslandPropagationMinDonorCorrection)}, " +
                $"maxProjectionDistance={FloatSetting(settings, s => s.maxProjectionDistance)}, falloffStartDistance={FloatSetting(settings, s => s.falloffStartDistance)}.");
        }

        private void LogSnapshot(string label, SkinnedMeshRenderer renderer, GameObject clone, SkinnedMeshRenderer cloneRenderer)
        {
            var mesh = renderer.sharedMesh;
            var cloneMesh = cloneRenderer != null ? cloneRenderer.sharedMesh : null;
            var bones = renderer.bones;
            int nullBones = 0;
            bool rootBoneInBones = false;
            if (bones != null)
            {
                foreach (var bone in bones)
                {
                    if (bone == null) nullBones++;
                    else if (bone == renderer.rootBone) rootBoneInBones = true;
                }
            }

            string bounds = "n/a";
            try { bounds = $"center={Format(renderer.bounds.center)}, size={Format(renderer.bounds.size)}"; }
            catch { }

            Log("debug-snapshot",
                $"{label}: renderer='{HierarchyPath(renderer.transform)}', mesh='{(mesh != null ? mesh.name : "null")}', " +
                $"vertices={(mesh != null ? mesh.vertexCount : 0)}, blendShapes={(mesh != null ? mesh.blendShapeCount : 0)} [{BlendShapeSummary(renderer)}], " +
                $"bones={(bones != null ? bones.Length : 0)}, nullBones={nullBones}, rootBone='{HierarchyPath(renderer.rootBone)}', rootBoneInBones={rootBoneInBones}, bounds=({bounds}), " +
                $"copy='{clone.name}', copyRenderer='{NameOf(cloneRenderer)}', copyMesh='{(cloneMesh != null ? cloneMesh.name : "null")}', " +
                $"copyMeshId={(cloneMesh != null ? cloneMesh.GetInstanceID().ToString() : "null")}, copyBlendShapes={(cloneMesh != null ? cloneMesh.blendShapeCount : 0)}.");
        }

        private void Log(string code, string message)
        {
            if (report != null) report.Info(code, message);
            else Debug.Log("[ReFit] " + code + ": " + message);
        }

        private static Vector3 ResolveAnchor(ReFitRequest request)
        {
            if (request != null && request.targetAvatar != null)
                return request.targetAvatar.transform.position;
            if (request != null && request.assetRenderer != null)
                return request.assetRenderer.bounds.center;
            return Vector3.zero;
        }

        private static string BlendShapeSummary(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0)
                return "-";

            const int limit = 12;
            var mesh = renderer.sharedMesh;
            var parts = new List<string>();
            int count = Mathf.Min(limit, mesh.blendShapeCount);
            for (int i = 0; i < count; i++)
                parts.Add($"{mesh.GetBlendShapeName(i)}={renderer.GetBlendShapeWeight(i):0.###}");
            if (mesh.blendShapeCount > limit)
                parts.Add("+" + (mesh.blendShapeCount - limit) + " more");
            return string.Join(", ", parts);
        }

        private static string BoolSetting(ReFitSettings settings, Func<ReFitSettings, bool> getter)
        {
            return settings != null ? getter(settings).ToString() : "default";
        }

        private static string FloatSetting(ReFitSettings settings, Func<ReFitSettings, float> getter)
        {
            return settings != null ? getter(settings).ToString("0.###") : "default";
        }

        private static string IntSetting(ReFitSettings settings, Func<ReFitSettings, int> getter)
        {
            return settings != null ? getter(settings).ToString() : "default";
        }

        private static string NameOf(Object obj)
        {
            return obj != null ? obj.name : "null";
        }

        private static string HierarchyPath(Transform t)
        {
            if (t == null) return "null";
            var names = new Stack<string>();
            while (t != null)
            {
                names.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }

        private readonly struct LocalPose
        {
            public readonly Vector3 position;
            public readonly Quaternion rotation;
            public readonly Vector3 scale;

            public LocalPose(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                this.position = position;
                this.rotation = rotation;
                this.scale = scale;
            }
        }
    }
}
