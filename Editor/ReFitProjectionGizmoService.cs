using System.Collections.Generic;
using Unity.Profiling;
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
        private const string DisplaySampleBudgetKey = "Orbiters.ReFit.ProjectionGizmos.DisplaySampleBudget";
        private const float HoverDistancePixels = 8f;
        private const float SingleWeldedGroupMarkerScale = 0.014f;
        private const float MultiWeldedGroupMarkerScale = 0.022f;
        private const int ProjectionPointMarkerBudget = 2000;

        public const int MinDisplaySampleBudget = 250;
        public const int MaxDisplaySampleBudget = 100000;
        public const int DefaultDisplaySampleBudget = 3000;

        private static readonly ProfilerMarker SceneGuiMarker =
            new ProfilerMarker("ReFit.ProjectionGizmos.SceneGUI");

        private static readonly Color AssetToSourceColor = new Color(0.95f, 0.55f, 0.12f, 0.65f);
        private static readonly Color AssetPointColor = new Color(1f, 0.75f, 0.2f, 0.85f);
        private static readonly Color SourcePointColor = new Color(0.1f, 0.65f, 1f, 0.85f);
        private static readonly Color InvalidColor = new Color(1f, 0.15f, 0.15f, 0.85f);
        private static readonly Color RelaxedColor = new Color(1f, 0.85f, 0.1f, 0.85f);
        private static readonly Color ProjectedColor = new Color(0.2f, 1f, 0.35f, 0.8f);
        private static readonly Color BlendedColor = new Color(0.1f, 0.95f, 1f, 0.85f);
        private static readonly Color PreservedColor = new Color(0.45f, 0.45f, 1f, 0.8f);
        private static readonly Color FallbackColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        private static readonly Color UnknownColor = new Color(0.85f, 0.85f, 0.85f, 0.6f);
        private static readonly Color IslandAppliedColor = new Color(0.25f, 1f, 0.3f, 0.95f);
        private static readonly Color IslandCandidateColor = new Color(0.95f, 0.75f, 0.18f, 0.9f);
        private static readonly Color IslandRejectedColor = new Color(1f, 0.18f, 0.12f, 0.92f);

        private static readonly List<ProjectionSnapshot> ProjectionSnapshots = new List<ProjectionSnapshot>();
        private static readonly List<WeldedSnapshot> WeldedSnapshots = new List<WeldedSnapshot>();
        private static readonly List<ProjectionSample> ProjectionSamples = new List<ProjectionSample>();
        private static readonly List<WeldedSample> WeldedSamples = new List<WeldedSample>();
        private static readonly HashSet<int> ActiveRendererIds = new HashSet<int>();

        private static bool componentCacheDirty = true;
        private static bool sampleCacheDirty = true;
        private static int totalProjectionPointCount;
        private static int totalWeldedGroupCount;

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
            EditorApplication.hierarchyChanged += MarkComponentCacheDirty;
            AssemblyReloadEvents.beforeAssemblyReload += ClearCaches;
            EditorApplication.quitting += ClearCaches;
        }

        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(EnabledKey, false); }
            set
            {
                if (Enabled == value)
                    return;

                EditorPrefs.SetBool(EnabledKey, value);
                MarkComponentCacheDirty();
                XRayExternalGizmoRegistry.NotifyChanged();
                SceneView.RepaintAll();
            }
        }

        public static bool WeldedGroupsEnabled
        {
            get { return EditorPrefs.GetBool(WeldedGroupsEnabledKey, true); }
            set
            {
                if (WeldedGroupsEnabled == value)
                    return;

                EditorPrefs.SetBool(WeldedGroupsEnabledKey, value);
                MarkComponentCacheDirty();
                XRayExternalGizmoRegistry.NotifyChanged();
                SceneView.RepaintAll();
            }
        }

        public static int DisplaySampleBudget
        {
            get
            {
                return Mathf.Clamp(
                    EditorPrefs.GetInt(DisplaySampleBudgetKey, DefaultDisplaySampleBudget),
                    MinDisplaySampleBudget,
                    MaxDisplaySampleBudget);
            }
        }

        public static string LastStatus { get; private set; } =
            "Projection gizmos draw a sampled debug subset in the Scene view.";

        public static void SetDisplaySampleBudget(int budget)
        {
            int clamped = Mathf.Clamp(budget, MinDisplaySampleBudget, MaxDisplaySampleBudget);
            if (DisplaySampleBudget == clamped)
                return;

            EditorPrefs.SetInt(DisplaySampleBudgetKey, clamped);
            MarkSampleCacheDirty();
            SceneView.RepaintAll();
        }

        private static void OnSceneGui(SceneView view)
        {
            var evt = Event.current;
            if (evt == null || evt.type != EventType.Repaint)
                return;

            bool drawProjectionRays = Enabled;
            bool drawWeldedGroups = WeldedGroupsEnabled && XRayMeshEdgeService.Enabled;
            if (!drawProjectionRays && !drawWeldedGroups)
                return;

            using (SceneGuiMarker.Auto())
            {
                EnsureSampleCache();

                var previousZTest = Handles.zTest;
                Handles.zTest = CompareFunction.Always;
                try
                {
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
                }
                finally
                {
                    Handles.zTest = previousZTest;
                }
            }
        }

        private static void DrawProjectionRays(
            ref ReFitProjectionDebugPoint hovered,
            ref Vector3 hoveredPosition,
            ref float bestHoverDistance)
        {
            bool drawPointMarkers = ProjectionSamples.Count <= ProjectionPointMarkerBudget;
            SkinnedMeshRenderer lastRenderer = null;
            Matrix4x4 localToWorld = Matrix4x4.identity;

            for (int i = 0; i < ProjectionSamples.Count; i++)
            {
                var sample = ProjectionSamples[i];
                if (sample.Component == null || !sample.Component.visible || sample.Renderer == null || sample.Point == null)
                    continue;

                if (sample.Renderer != lastRenderer)
                {
                    lastRenderer = sample.Renderer;
                    localToWorld = lastRenderer.transform.localToWorldMatrix;
                }

                var point = sample.Point;
                var asset = localToWorld.MultiplyPoint3x4(point.assetLocalPoint);
                var source = localToWorld.MultiplyPoint3x4(point.sourceHitLocalPoint);
                var target = localToWorld.MultiplyPoint3x4(point.targetHitLocalPoint);

                Handles.color = AssetToSourceColor;
                Handles.DrawLine(asset, source);
                Handles.color = sample.TargetColor;
                Handles.DrawLine(source, target);

                if (drawPointMarkers)
                {
                    DrawPoint(asset, AssetPointColor, 0.0075f);
                    DrawPoint(source, SourcePointColor, 0.006f);
                    DrawPoint(target, sample.TargetColor, 0.006f);
                }

                UpdateHovered(point, asset, source, ref hovered, ref hoveredPosition, ref bestHoverDistance);
                UpdateHovered(point, source, target, ref hovered, ref hoveredPosition, ref bestHoverDistance);
            }
        }

        private static void DrawWeldedGroups(
            ref ReFitWeldedGroupDebugPoint hoveredGroup,
            ref Vector3 hoveredGroupPosition,
            ref float bestHoverDistance)
        {
            BuildActiveRendererSet();
            if (ActiveRendererIds.Count == 0)
                return;

            SkinnedMeshRenderer lastRenderer = null;
            Matrix4x4 localToWorld = Matrix4x4.identity;

            for (int i = 0; i < WeldedSamples.Count; i++)
            {
                var sample = WeldedSamples[i];
                if (sample.Component == null ||
                    !sample.Component.visible ||
                    sample.Renderer == null ||
                    sample.Group == null ||
                    !ActiveRendererIds.Contains(sample.Renderer.GetInstanceID()))
                {
                    continue;
                }

                if (sample.Renderer != lastRenderer)
                {
                    lastRenderer = sample.Renderer;
                    localToWorld = lastRenderer.transform.localToWorldMatrix;
                }

                var position = localToWorld.MultiplyPoint3x4(sample.Group.localPoint);
                DrawWeldedGroupPoint(position, sample);
                UpdateHovered(sample.Group, position, ref hoveredGroup, ref hoveredGroupPosition, ref bestHoverDistance);
            }
        }

        private static void DrawPoint(Vector3 position, Color color, float scale)
        {
            Handles.color = color;
            float size = HandleUtility.GetHandleSize(position) * scale;
            Handles.SphereHandleCap(0, position, Quaternion.identity, size, EventType.Repaint);
        }

        private static void DrawWeldedGroupPoint(Vector3 position, WeldedSample sample)
        {
            Handles.color = sample.Color;
            float scale = sample.Group != null && sample.Group.vertexCount > 1
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
            float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(position), Event.current.mousePosition);
            if (distance > bestHoverDistance) return;
            bestHoverDistance = distance;
            hovered = group;
            hoveredPosition = position;
        }

        private static void EnsureSampleCache()
        {
            if (componentCacheDirty)
                RebuildComponentCache();

            if (sampleCacheDirty)
                RebuildSampleCache();
        }

        private static void RebuildComponentCache()
        {
            ProjectionSnapshots.Clear();
            WeldedSnapshots.Clear();
            totalProjectionPointCount = 0;
            totalWeldedGroupCount = 0;

            var projectionComponents = Resources.FindObjectsOfTypeAll<ReFitProjectionDebugComponent>();
            if (projectionComponents != null)
            {
                for (int i = 0; i < projectionComponents.Length; i++)
                {
                    var component = projectionComponents[i];
                    if (!TryBuildProjectionSnapshot(component, out var snapshot))
                        continue;

                    ProjectionSnapshots.Add(snapshot);
                    totalProjectionPointCount += snapshot.PointCount;
                }
            }

            var weldedComponents = Resources.FindObjectsOfTypeAll<ReFitWeldedGroupDebugComponent>();
            if (weldedComponents != null)
            {
                for (int i = 0; i < weldedComponents.Length; i++)
                {
                    var component = weldedComponents[i];
                    if (!TryBuildWeldedSnapshot(component, out var snapshot))
                        continue;

                    WeldedSnapshots.Add(snapshot);
                    totalWeldedGroupCount += snapshot.GroupCount;
                }
            }

            componentCacheDirty = false;
            sampleCacheDirty = true;
        }

        private static bool TryBuildProjectionSnapshot(
            ReFitProjectionDebugComponent component,
            out ProjectionSnapshot snapshot)
        {
            snapshot = null;
            if (!IsVisibleSceneComponent(component) ||
                component.data == null ||
                component.data.points == null ||
                component.data.points.Length == 0)
            {
                return false;
            }

            var renderer = ResolveRenderer(component, component.targetRenderer);
            if (renderer == null)
                return false;

            int count = CountValid(component.data.points);
            if (count == 0)
                return false;

            snapshot = new ProjectionSnapshot
            {
                Component = component,
                Renderer = renderer,
                Points = component.data.points,
                PointCount = count
            };
            return true;
        }

        private static bool TryBuildWeldedSnapshot(
            ReFitWeldedGroupDebugComponent component,
            out WeldedSnapshot snapshot)
        {
            snapshot = null;
            if (!IsVisibleSceneComponent(component) ||
                component.data == null ||
                component.data.groups == null ||
                component.data.groups.Length == 0)
            {
                return false;
            }

            var renderer = ResolveRenderer(component, component.targetRenderer);
            if (renderer == null)
                return false;

            int count = CountValid(component.data.groups);
            if (count == 0)
                return false;

            snapshot = new WeldedSnapshot
            {
                Component = component,
                Renderer = renderer,
                Groups = component.data.groups,
                GroupCount = count
            };
            return true;
        }

        private static bool IsVisibleSceneComponent(Component component)
        {
            if (component == null || EditorUtility.IsPersistent(component) || component.gameObject == null)
                return false;

            if (!component.gameObject.scene.IsValid())
                return false;

            var projection = component as ReFitProjectionDebugComponent;
            if (projection != null)
                return projection.visible;

            var welded = component as ReFitWeldedGroupDebugComponent;
            return welded == null || welded.visible;
        }

        private static SkinnedMeshRenderer ResolveRenderer(Component component, SkinnedMeshRenderer explicitRenderer)
        {
            if (explicitRenderer != null)
                return explicitRenderer;

            return component != null ? component.GetComponent<SkinnedMeshRenderer>() : null;
        }

        private static int CountValid(ReFitProjectionDebugPoint[] points)
        {
            int count = 0;
            for (int i = 0; i < points.Length; i++)
                if (points[i] != null)
                    count++;
            return count;
        }

        private static int CountValid(ReFitWeldedGroupDebugPoint[] groups)
        {
            int count = 0;
            for (int i = 0; i < groups.Length; i++)
                if (groups[i] != null)
                    count++;
            return count;
        }

        private static void RebuildSampleCache()
        {
            ProjectionSamples.Clear();
            WeldedSamples.Clear();

            int budget = DisplaySampleBudget;
            AddProjectionSamples(budget);
            AddWeldedSamples(budget);
            LastStatus =
                $"Projection gizmos display {ProjectionSamples.Count}/{totalProjectionPointCount} rays and " +
                $"{WeldedSamples.Count}/{totalWeldedGroupCount} welded markers. Budget: {budget}.";
            sampleCacheDirty = false;
        }

        private static void AddProjectionSamples(int budget)
        {
            if (totalProjectionPointCount <= 0)
                return;

            int targetCount = Mathf.Min(Mathf.Max(1, budget), totalProjectionPointCount);
            double step = (double)totalProjectionPointCount / targetCount;
            double next = 0d;
            int globalIndex = 0;

            for (int s = 0; s < ProjectionSnapshots.Count; s++)
            {
                var snapshot = ProjectionSnapshots[s];
                for (int i = 0; i < snapshot.Points.Length; i++)
                {
                    var point = snapshot.Points[i];
                    if (point == null)
                        continue;

                    if (ProjectionSamples.Count < targetCount && globalIndex + 0.0001d >= next)
                    {
                        ProjectionSamples.Add(new ProjectionSample
                        {
                            Component = snapshot.Component,
                            Renderer = snapshot.Renderer,
                            Point = point,
                            TargetColor = DecisionColor(point)
                        });
                        next += step;
                    }

                    globalIndex++;
                }
            }
        }

        private static void AddWeldedSamples(int budget)
        {
            if (totalWeldedGroupCount <= 0)
                return;

            int targetCount = Mathf.Min(Mathf.Max(1, budget), totalWeldedGroupCount);
            double step = (double)totalWeldedGroupCount / targetCount;
            double next = 0d;
            int globalIndex = 0;

            for (int s = 0; s < WeldedSnapshots.Count; s++)
            {
                var snapshot = WeldedSnapshots[s];
                for (int i = 0; i < snapshot.Groups.Length; i++)
                {
                    var group = snapshot.Groups[i];
                    if (group == null)
                        continue;

                    if (WeldedSamples.Count < targetCount && globalIndex + 0.0001d >= next)
                    {
                        WeldedSamples.Add(new WeldedSample
                        {
                            Component = snapshot.Component,
                            Renderer = snapshot.Renderer,
                            Group = group,
                            Color = WeldedGroupColor(group)
                        });
                        next += step;
                    }

                    globalIndex++;
                }
            }
        }

        private static void BuildActiveRendererSet()
        {
            ActiveRendererIds.Clear();
            foreach (var renderer in XRayMeshEdgeService.ActiveRenderers)
                if (renderer != null)
                    ActiveRendererIds.Add(renderer.GetInstanceID());
        }

        private static void MarkComponentCacheDirty()
        {
            componentCacheDirty = true;
            sampleCacheDirty = true;
        }

        private static void MarkSampleCacheDirty()
        {
            sampleCacheDirty = true;
        }

        private static void ClearCaches()
        {
            ProjectionSnapshots.Clear();
            WeldedSnapshots.Clear();
            ProjectionSamples.Clear();
            WeldedSamples.Clear();
            ActiveRendererIds.Clear();
            totalProjectionPointCount = 0;
            totalWeldedGroupCount = 0;
            componentCacheDirty = true;
            sampleCacheDirty = true;
        }

        private static Color WeldedGroupColor(ReFitWeldedGroupDebugPoint group)
        {
            if (group != null)
            {
                switch (group.islandPropagationStatus)
                {
                    case ReFitIslandPropagationStatus.Applied:
                        return IslandAppliedColor;
                    case ReFitIslandPropagationStatus.Candidate:
                        return IslandCandidateColor;
                    case ReFitIslandPropagationStatus.RejectedNoSupport:
                    case ReFitIslandPropagationStatus.RejectedUnstableSupport:
                    case ReFitIslandPropagationStatus.RejectedWeakDonor:
                    case ReFitIslandPropagationStatus.RejectedInsufficientComponentSupport:
                    case ReFitIslandPropagationStatus.RejectedWeight:
                    case ReFitIslandPropagationStatus.RejectedBudget:
                        return IslandRejectedColor;
                }
            }

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
                return InvalidColor;
            if (point.sourceUsedRelaxedFallback || point.targetUsedRelaxedFallback)
                return RelaxedColor;

            switch (point.weightDecision)
            {
                case ReFitWeightDecision.Projected:
                    return ProjectedColor;
                case ReFitWeightDecision.Blended:
                    return BlendedColor;
                case ReFitWeightDecision.Original:
                case ReFitWeightDecision.ExtraPreserved:
                    return PreservedColor;
                case ReFitWeightDecision.Fallback:
                    return FallbackColor;
                default:
                    return UnknownColor;
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
            var label =
                $"welded group {group.groupIndex}\n" +
                $"representative vertex {group.representativeVertexIndex}\n" +
                $"vertices {Mathf.Max(1, group.vertexCount)}";
            if (group.islandPropagationStatus != ReFitIslandPropagationStatus.None)
            {
                label +=
                    $"\nisland propagation {group.islandPropagationStatus}" +
                    $"\ncomponent {group.islandPropagationComponentIndex} ({group.islandPropagationComponentSize} groups), support {group.islandPropagationSupportComponentIndex}:{group.islandPropagationSupportGroupIndex}" +
                    $"\nsupport distance {group.islandPropagationSupportDistance * 1000f:0.###}mm, weight {group.islandPropagationReceiverWeight:0.###}" +
                    $"\ncurrent {group.islandPropagationCurrentCorrectionMagnitude * 1000f:0.###}mm, target {group.islandPropagationTargetCorrectionMagnitude * 1000f:0.###}mm, applied {group.islandPropagationAppliedCorrectionMagnitude * 1000f:0.###}mm";
                if (!string.IsNullOrEmpty(group.islandPropagationReason))
                    label += "\n" + group.islandPropagationReason;
            }

            return label;
        }

        private static string Empty(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        private static string Format(Vector3 value)
        {
            return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
        }

        private sealed class ProjectionSnapshot
        {
            public ReFitProjectionDebugComponent Component;
            public SkinnedMeshRenderer Renderer;
            public ReFitProjectionDebugPoint[] Points;
            public int PointCount;
        }

        private sealed class WeldedSnapshot
        {
            public ReFitWeldedGroupDebugComponent Component;
            public SkinnedMeshRenderer Renderer;
            public ReFitWeldedGroupDebugPoint[] Groups;
            public int GroupCount;
        }

        private struct ProjectionSample
        {
            public ReFitProjectionDebugComponent Component;
            public SkinnedMeshRenderer Renderer;
            public ReFitProjectionDebugPoint Point;
            public Color TargetColor;
        }

        private struct WeldedSample
        {
            public ReFitWeldedGroupDebugComponent Component;
            public SkinnedMeshRenderer Renderer;
            public ReFitWeldedGroupDebugPoint Group;
            public Color Color;
        }
    }
}
