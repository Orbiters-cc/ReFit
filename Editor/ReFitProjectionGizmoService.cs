using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Orbiters.XRayGizmos.Editor;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Scene view projection diagnostics for ReFit debug snapshots.</summary>
    [InitializeOnLoad]
    internal static class ReFitProjectionGizmoService
    {
        private const string EnabledKey = "Orbiters.ReFit.ProjectionGizmos";
        private const string WeldedGroupsEnabledKey = "Orbiters.ReFit.WeldedGroupGizmos";
        private const float HoverDistancePixels = 8f;
        private const float SingleWeldedGroupMarkerScale = 0.014f;
        private const float MultiWeldedGroupMarkerScale = 0.022f;

        static ReFitProjectionGizmoService()
        {
            XRayExternalGizmoRegistry.Register(
                "orbiters.refit.projection-rays",
                "ReFit projection rays",
                () => Enabled,
                value => Enabled = value,
                "Asset/source/target projection diagnostics");
            XRayExternalGizmoRegistry.Register(
                "orbiters.refit.welded-groups",
                "ReFit welded vertex groups",
                () => WeldedGroupsEnabled,
                value => WeldedGroupsEnabled = value,
                "Welded vertex group markers on ReFit debug mesh-edge snapshots");
            SceneView.duringSceneGui += OnSceneGui;
        }

        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(EnabledKey, false); }
            set
            {
                EditorPrefs.SetBool(EnabledKey, value);
                XRayExternalGizmoRegistry.NotifyChanged();
                SceneView.RepaintAll();
            }
        }

        public static bool WeldedGroupsEnabled
        {
            get { return EditorPrefs.GetBool(WeldedGroupsEnabledKey, true); }
            set
            {
                EditorPrefs.SetBool(WeldedGroupsEnabledKey, value);
                XRayExternalGizmoRegistry.NotifyChanged();
                SceneView.RepaintAll();
            }
        }

        private static void OnSceneGui(SceneView view)
        {
            bool drawProjectionRays = Enabled;
            bool drawWeldedGroups = WeldedGroupsEnabled && XRayMeshEdgeService.Enabled;
            if (!drawProjectionRays && !drawWeldedGroups) return;

            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;

            ReFitProjectionDebugPoint hovered = null;
            Vector3 hoveredPosition = Vector3.zero;
            float bestHoverDistance = HoverDistancePixels;
            ReFitWeldedGroupDebugPoint hoveredGroup = null;
            Vector3 hoveredGroupPosition = Vector3.zero;
            float bestGroupHoverDistance = HoverDistancePixels;

            if (drawProjectionRays)
                DrawProjectionRays(ref hovered, ref hoveredPosition, ref bestHoverDistance);

            if (drawWeldedGroups)
                DrawWeldedGroups(ref hoveredGroup, ref hoveredGroupPosition, ref bestGroupHoverDistance);

            if (hovered != null)
            {
                Handles.color = Color.white;
                Handles.Label(hoveredPosition, BuildLabel(hovered), EditorStyles.helpBox);
            }
            else if (hoveredGroup != null)
            {
                Handles.color = Color.white;
                Handles.Label(hoveredGroupPosition, BuildWeldedGroupLabel(hoveredGroup), EditorStyles.helpBox);
            }

            Handles.zTest = previousZTest;
        }

        private static void DrawProjectionRays(
            ref ReFitProjectionDebugPoint hovered,
            ref Vector3 hoveredPosition,
            ref float bestHoverDistance)
        {
            var components = Resources.FindObjectsOfTypeAll<ReFitProjectionDebugComponent>();
            if (components == null || components.Length == 0) return;

            foreach (var component in components)
            {
                if (component == null || !component.visible || component.data == null || component.data.points == null)
                    continue;
                if (EditorUtility.IsPersistent(component) || component.gameObject == null || !component.gameObject.scene.IsValid())
                    continue;

                var renderer = component.targetRenderer != null
                    ? component.targetRenderer
                    : component.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) continue;

                var localToWorld = renderer.transform.localToWorldMatrix;
                foreach (var point in component.data.points)
                {
                    if (point == null) continue;
                    var asset = localToWorld.MultiplyPoint3x4(point.assetLocalPoint);
                    var source = localToWorld.MultiplyPoint3x4(point.sourceHitLocalPoint);
                    var target = localToWorld.MultiplyPoint3x4(point.targetHitLocalPoint);

                    Handles.color = new Color(0.95f, 0.55f, 0.12f, 0.65f);
                    Handles.DrawLine(asset, source);
                    Handles.color = DecisionColor(point);
                    Handles.DrawLine(source, target);
                    DrawPoint(asset, new Color(1f, 0.75f, 0.2f, 0.85f), 0.0075f);
                    DrawPoint(source, new Color(0.1f, 0.65f, 1f, 0.85f), 0.006f);
                    DrawPoint(target, DecisionColor(point), 0.006f);

                    UpdateHovered(point, asset, source, ref hovered, ref hoveredPosition, ref bestHoverDistance);
                    UpdateHovered(point, source, target, ref hovered, ref hoveredPosition, ref bestHoverDistance);
                }
            }
        }

        private static void DrawWeldedGroups(
            ref ReFitWeldedGroupDebugPoint hoveredGroup,
            ref Vector3 hoveredGroupPosition,
            ref float bestHoverDistance)
        {
            var activeRendererIds = new HashSet<int>();
            foreach (var renderer in XRayMeshEdgeService.ActiveRenderers)
                if (renderer != null)
                    activeRendererIds.Add(renderer.GetInstanceID());
            if (activeRendererIds.Count == 0)
                return;

            var components = Resources.FindObjectsOfTypeAll<ReFitWeldedGroupDebugComponent>();
            if (components == null || components.Length == 0) return;

            foreach (var component in components)
            {
                if (component == null || !component.visible || component.data == null || component.data.groups == null)
                    continue;
                if (EditorUtility.IsPersistent(component) || component.gameObject == null || !component.gameObject.scene.IsValid())
                    continue;

                var renderer = component.targetRenderer != null
                    ? component.targetRenderer
                    : component.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null || !activeRendererIds.Contains(renderer.GetInstanceID()))
                    continue;

                var localToWorld = renderer.transform.localToWorldMatrix;
                foreach (var group in component.data.groups)
                {
                    if (group == null) continue;
                    var position = localToWorld.MultiplyPoint3x4(group.localPoint);
                    DrawWeldedGroupPoint(position, group);
                    UpdateHovered(group, position, ref hoveredGroup, ref hoveredGroupPosition, ref bestHoverDistance);
                }
            }
        }

        private static void DrawPoint(Vector3 position, Color color, float scale)
        {
            Handles.color = color;
            float size = HandleUtility.GetHandleSize(position) * scale;
            Handles.SphereHandleCap(0, position, Quaternion.identity, size, EventType.Repaint);
        }

        private static void DrawWeldedGroupPoint(Vector3 position, ReFitWeldedGroupDebugPoint group)
        {
            var color = WeldedGroupColor(group);
            Handles.color = color;
            float scale = group != null && group.vertexCount > 1
                ? MultiWeldedGroupMarkerScale
                : SingleWeldedGroupMarkerScale;
            float size = HandleUtility.GetHandleSize(position) * scale;
            Handles.CubeHandleCap(0, position, Quaternion.identity, size, EventType.Repaint);
        }

        private static void UpdateHovered(ReFitProjectionDebugPoint point, Vector3 a, Vector3 b,
            ref ReFitProjectionDebugPoint hovered, ref Vector3 hoveredPosition, ref float bestHoverDistance)
        {
            float distance = HandleUtility.DistanceToLine(a, b);
            if (distance > bestHoverDistance) return;
            bestHoverDistance = distance;
            hovered = point;
            hoveredPosition = (a + b) * 0.5f;
        }

        private static void UpdateHovered(ReFitWeldedGroupDebugPoint group, Vector3 position,
            ref ReFitWeldedGroupDebugPoint hovered, ref Vector3 hoveredPosition, ref float bestHoverDistance)
        {
            if (Event.current == null)
                return;

            float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(position), Event.current.mousePosition);
            if (distance > bestHoverDistance) return;
            bestHoverDistance = distance;
            hovered = group;
            hoveredPosition = position;
        }

        private static Color WeldedGroupColor(ReFitWeldedGroupDebugPoint group)
        {
            if (group != null && group.vertexCount > 1)
                return new Color(0.1f, 1f, 0.95f, 0.9f);

            int hash = group != null ? group.groupIndex * 1103515245 + 12345 : 0;
            float hue = Mathf.Abs(hash % 997) / 997f;
            var color = Color.HSVToRGB(hue, 0.45f, 1f);
            color.a = 0.72f;
            return color;
        }

        private static Color DecisionColor(ReFitProjectionDebugPoint point)
        {
            if (point == null || !point.sourceValid || !point.targetValid)
                return new Color(1f, 0.15f, 0.15f, 0.85f);
            if (point.sourceUsedRelaxedFallback || point.targetUsedRelaxedFallback)
                return new Color(1f, 0.85f, 0.1f, 0.85f);

            switch (point.weightDecision)
            {
                case ReFitWeightDecision.Projected:
                    return new Color(0.2f, 1f, 0.35f, 0.8f);
                case ReFitWeightDecision.Blended:
                    return new Color(0.1f, 0.95f, 1f, 0.85f);
                case ReFitWeightDecision.Original:
                case ReFitWeightDecision.ExtraPreserved:
                    return new Color(0.45f, 0.45f, 1f, 0.8f);
                case ReFitWeightDecision.Fallback:
                    return new Color(1f, 0.2f, 0.2f, 0.9f);
                default:
                    return new Color(0.85f, 0.85f, 0.85f, 0.6f);
            }
        }

        private static string BuildLabel(ReFitProjectionDebugPoint point)
        {
            return
                $"group {point.groupIndex}, vertex {point.vertexIndex}\n" +
                $"welded vertices {Mathf.Max(1, point.weldedVertexCount)}\n" +
                $"source tri {point.sourceTriangle}, bary {Format(point.sourceBarycentric)}, dist {point.sourceDistance:0.####}m, region {point.sourceHitRegion}, relaxed {point.sourceUsedRelaxedFallback}\n" +
                $"target tri {point.targetTriangle}, bary {Format(point.targetBarycentric)}, dist {point.targetDistance:0.####}m, region {point.targetHitRegion}, relaxed {point.targetUsedRelaxedFallback}\n" +
                $"asset region {point.assetRegion}, normal dot {point.normalDot:0.###}, falloff {point.falloff:0.###}, weights {point.weightDecision}\n" +
                $"projected: {Empty(point.projectedWeights)}\n" +
                $"original: {Empty(point.originalWeights)}\n" +
                $"final: {Empty(point.finalWeights)}" +
                (string.IsNullOrEmpty(point.note) ? string.Empty : "\n" + point.note);
        }

        private static string BuildWeldedGroupLabel(ReFitWeldedGroupDebugPoint group)
        {
            return
                $"welded group {group.groupIndex}\n" +
                $"representative vertex {group.representativeVertexIndex}\n" +
                $"vertices {Mathf.Max(1, group.vertexCount)}";
        }

        private static string Empty(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }
    }
}
