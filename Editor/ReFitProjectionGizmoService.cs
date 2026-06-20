using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Scene view projection diagnostics for ReFit debug snapshots.</summary>
    [InitializeOnLoad]
    internal static class ReFitProjectionGizmoService
    {
        private const string EnabledKey = "Orbiters.ReFit.ProjectionGizmos";
        private const float HoverDistancePixels = 8f;

        static ReFitProjectionGizmoService()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(EnabledKey, false); }
            set
            {
                EditorPrefs.SetBool(EnabledKey, value);
                SceneView.RepaintAll();
            }
        }

        private static void OnSceneGui(SceneView view)
        {
            if (!Enabled) return;
            var components = Resources.FindObjectsOfTypeAll<ReFitProjectionDebugComponent>();
            if (components == null || components.Length == 0) return;

            var previousZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;

            ReFitProjectionDebugPoint hovered = null;
            Vector3 hoveredPosition = Vector3.zero;
            float bestHoverDistance = HoverDistancePixels;

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

                    Handles.color = new Color(0.15f, 0.55f, 1f, 0.65f);
                    Handles.DrawLine(asset, source);
                    Handles.color = DecisionColor(point);
                    Handles.DrawLine(source, target);

                    UpdateHovered(point, asset, source, ref hovered, ref hoveredPosition, ref bestHoverDistance);
                    UpdateHovered(point, source, target, ref hovered, ref hoveredPosition, ref bestHoverDistance);
                }
            }

            if (hovered != null)
            {
                Handles.color = Color.white;
                Handles.Label(hoveredPosition, BuildLabel(hovered), EditorStyles.helpBox);
            }

            Handles.zTest = previousZTest;
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
                $"source tri {point.sourceTriangle}, bary {Format(point.sourceBarycentric)}, dist {point.sourceDistance:0.####}m, region {point.sourceHitRegion}, relaxed {point.sourceUsedRelaxedFallback}\n" +
                $"target tri {point.targetTriangle}, bary {Format(point.targetBarycentric)}, dist {point.targetDistance:0.####}m, region {point.targetHitRegion}, relaxed {point.targetUsedRelaxedFallback}\n" +
                $"asset region {point.assetRegion}, normal dot {point.normalDot:0.###}, falloff {point.falloff:0.###}, weights {point.weightDecision}\n" +
                $"projected: {Empty(point.projectedWeights)}\n" +
                $"original: {Empty(point.originalWeights)}\n" +
                $"final: {Empty(point.finalWeights)}" +
                (string.IsNullOrEmpty(point.note) ? string.Empty : "\n" + point.note);
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
