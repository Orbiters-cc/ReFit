#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    internal struct ReFitProgressButtonData
    {
        public string text;
        public bool enabled;
        public bool isRunning;
        public float progress;
        public Color fillColor;
        public Color trackColor;
    }

    /// <summary>ReFit's standalone progress button, matching the integrated MCB control without depending on MCB.</summary>
    internal sealed class ReFitProgressButtonElement : Button
    {
        private readonly Func<ReFitProgressButtonData> dataProvider;
        private readonly VisualElement track;
        private readonly VisualElement fill;
        private readonly Label label;
        private IVisualElementScheduledItem animation;
        private float displayedProgress = 1f;
        private Color displayedFillColor;
        private Color displayedTrackColor;
        private bool wasRunning;
        private double lastUpdateTime;

        public ReFitProgressButtonElement(Action clicked, Func<ReFitProgressButtonData> dataProvider)
            : base(clicked)
        {
            this.dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            text = string.Empty;
            AddToClassList("refit-progress-button");

            track = new VisualElement();
            track.AddToClassList("refit-progress-button__track");
            Add(track);

            fill = new VisualElement();
            fill.AddToClassList("refit-progress-button__fill");
            track.Add(fill);

            label = new Label();
            label.AddToClassList("refit-progress-button__label");
            Add(label);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                lastUpdateTime = EditorApplication.timeSinceStartup;
                var data = this.dataProvider();
                displayedFillColor = data.fillColor;
                displayedTrackColor = data.isRunning ? data.trackColor : data.fillColor;
                displayedProgress = data.isRunning ? Mathf.Clamp01(data.progress) : 1f;
                wasRunning = data.isRunning;
                UpdateVisuals();
                animation = schedule.Execute(UpdateVisuals).Every(16);
            });

            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                animation?.Pause();
                animation = null;
            });
        }

        private void UpdateVisuals()
        {
            var data = dataProvider();
            bool running = data.isRunning;
            float targetProgress = running ? Mathf.Clamp01(data.progress) : 1f;
            Color targetTrackColor = running ? data.trackColor : data.fillColor;

            if (running && !wasRunning)
            {
                displayedProgress = 0f;
                displayedTrackColor = targetTrackColor;
            }

            double now = EditorApplication.timeSinceStartup;
            float deltaTime = lastUpdateTime > 0d
                ? Mathf.Clamp((float)(now - lastUpdateTime), 0f, 0.1f)
                : 0.016f;
            lastUpdateTime = now;

            float progressSmoothing = 1f - Mathf.Exp(-deltaTime * 10f);
            float colorSmoothing = 1f - Mathf.Exp(-deltaTime * 7f);
            displayedProgress = Mathf.Lerp(displayedProgress, targetProgress, progressSmoothing);
            displayedFillColor = Color.Lerp(displayedFillColor, data.fillColor, colorSmoothing);
            displayedTrackColor = Color.Lerp(displayedTrackColor, targetTrackColor, colorSmoothing);

            label.text = data.text ?? string.Empty;
            SetEnabled(data.enabled);
            track.style.backgroundColor = displayedTrackColor;
            fill.style.backgroundColor = displayedFillColor;
            fill.style.width = Length.Percent(Mathf.Clamp01(displayedProgress) * 100f);
            wasRunning = running;
        }
    }
}
#endif
