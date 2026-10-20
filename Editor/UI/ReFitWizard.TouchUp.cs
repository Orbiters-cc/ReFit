using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        // ------------------------------------------------------------------
        // Touch up: the push-out brush (ReFitClipFixTool) on a refitted piece of the last run.
        // ------------------------------------------------------------------

        private void BuildTouchUp()
        {
            if (motionItems.Count == 0) return;
            var card = Box(content, "refit-card", "refit-motion");
            var heading = Box(card, "refit-result__heading");
            Text(heading, "Touch up", "refit-section");
            Text(card, "Drag over fabric in the Scene view to push it out of the body. [ and ] change the brush; Esc stops.", "refit-motion__caption");
            var active = ReFitClipFixTool.Active ? ReFitClipFixTool.Piece : null;
            if (active != null)
            {
                int inside = ReFitClipFixTool.Inside;
                Note(card, inside > 0 ? ReFitSeverity.Warning : ReFitSeverity.Info,
                    inside < 0 ? "Reading the body… the brush pushes in a moment."
                    : inside > 0 ? $"{inside} points of {active.name} are inside the body (red in the Scene view)." : $"No point of {active.name} is inside the body.");
                var size = new Slider("Brush", ReFitClipFixTool.MinRadius * 100f, ReFitClipFixTool.MaxRadius * 100f) { value = ReFitClipFixTool.Radius * 100f };
                size.AddToClassList("refit-slider");
                size.tooltip = "The brush's radius (cm).";
                size.RegisterValueChangedCallback(e => ReFitClipFixTool.Radius = e.newValue / 100f);
                card.Add(size);
                var room = new Slider("Room", 0f, ReFitClipFixTool.MaxRoom * 1000f) { value = ReFitClipFixTool.Room * 1000f };
                room.AddToClassList("refit-slider");
                room.tooltip = "How far from the skin the brush leaves the fabric (mm).";
                room.RegisterValueChangedCallback(e => ReFitClipFixTool.Room = e.newValue / 1000f);
                card.Add(room);
            }
            var actions = Box(card, "refit-result__actions");
            if (active != null)
            {
                Spacer(actions);
                actions.Add(BusyButton("Done", "refit-button--primary", null, ReFitClipFixTool.End));
                return;
            }
            Spacer(actions);
            // A piece or body deleted since the run has nothing to push.
            var pieces = motionItems.Where(i => i.clothing != null && i.body != null).ToList();
            foreach (var item in pieces)
            {
                var captured = item;
                string label = pieces.Count > 1 ? "Push out: " + item.clothing.name : "Push out by hand";
                actions.Add(BusyButton(label, pieces.Count > 1 ? "refit-button--ghost" : "refit-button--primary", "Preparing…", () =>
                {
                    if (captured.clothing == null || captured.body == null) return;
                    string error = ReFitClipFixTool.Begin(captured.clothing, captured.body);
                    if (error != null) Debug.LogWarning("[ReFit] Push out: " + error + ".");
                }));
            }
        }
    }
}
