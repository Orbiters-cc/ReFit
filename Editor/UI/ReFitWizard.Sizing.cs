using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        private const float MinimumWidth = 620f;
        private const float MinimumHeight = 560f;
        // Leave room for the native title bar and desktop taskbar around the client area.
        private const float DesktopVerticalReserve = 80f;
        private IVisualElementScheduledItem contentSizeUpdate;

        private void ConfigureContentSizing()
        {
            contentSizeUpdate?.Pause();
            content.contentContainer.AddToClassList("refit-page-content");
            content.contentContainer.style.flexGrow = 0;
            content.contentContainer.style.flexShrink = 0;
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(ScheduleContentSizing);
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(ScheduleContentSizing);
            content.contentContainer.RegisterCallback<GeometryChangedEvent>(ScheduleContentSizing);
            content.contentViewport.RegisterCallback<GeometryChangedEvent>(ScheduleContentSizing);
            ScheduleContentSizing(null);
        }

        private void ScheduleContentSizing(GeometryChangedEvent evt)
        {
            // Wait for wrapping, font metrics and foldouts to settle; coalesce layout events.
            contentSizeUpdate?.Pause();
            contentSizeUpdate = rootVisualElement.schedule.Execute(FitWindowToContent).StartingIn(80);
        }

        private void FitWindowToContent()
        {
            if (content == null || rootVisualElement.panel == null || rootVisualElement.panel.contextType != ContextType.Editor) return;
            float rootHeight = rootVisualElement.layout.height;
            float viewportHeight = content.contentViewport.layout.height;
            float pageHeight = content.contentContainer.layout.height;
            if (!IsLayoutHeight(rootHeight) || !IsLayoutHeight(viewportHeight) || !IsLayoutHeight(pageHeight)) return;

            var desktop = InternalEditorUtility.GetBoundsOfDesktopAtPoint(position.center);
            if (!IsLayoutHeight(desktop.height)) return;
            float availableHeight = Mathf.Max(1f, desktop.height - DesktopVerticalReserve);
            float requiredHeight = CalculateMinimumHeight(rootHeight, viewportHeight, pageHeight, availableHeight);
            var bounds = position;
            var minimum = new Vector2(MinimumWidth, requiredHeight);
            if (minSize != minimum) minSize = minimum;

            // Docked/maximized windows belong to Unity's layout; keep a real scroll fallback there.
            if (!docked && !maximized && bounds.height < requiredHeight - 0.5f)
            {
                bounds.width = Mathf.Max(bounds.width, MinimumWidth);
                bounds.height = requiredHeight;
                bounds.y = Mathf.Clamp(bounds.y, desktop.yMin + 30f, desktop.yMax - 50f - bounds.height);
                position = bounds;
            }

            // Hide only subpixel rounding overflow. Taller pages retain scrolling at the screen cap.
            bool overflows = pageHeight > viewportHeight + 1f;
            var visibility = overflows ? ScrollerVisibility.Auto : ScrollerVisibility.Hidden;
            if (content.verticalScrollerVisibility != visibility) content.verticalScrollerVisibility = visibility;
            if (!overflows) content.scrollOffset = Vector2.zero;
        }

        internal static float CalculateMinimumHeight(float rootHeight, float viewportHeight, float pageHeight, float availableHeight)
        {
            float chromeHeight = Mathf.Max(0f, rootHeight - viewportHeight);
            return Mathf.Min(availableHeight, Mathf.Max(MinimumHeight, Mathf.Ceil(chromeHeight + pageHeight + 2f)));
        }

        private static bool IsLayoutHeight(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}
