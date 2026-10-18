using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        // ------------------------------------------------------------------
        // Fits: the fits kept for each refitted piece of the last run, one of which the avatar's menu picks in VRChat.
        // ------------------------------------------------------------------

        private void BuildFits()
        {
            if (motionItems.Count == 0) return;
            var card = Box(content, "refit-card", "refit-motion");
            var heading = Box(card, "refit-result__heading");
            Text(heading, "Fits", "refit-section");
            Text(card, "Keep this fit, then refit the piece again (looser, tighter…) to add another. In VRChat, the avatar's menu picks one.",
                "refit-motion__caption");
            foreach (var item in motionItems)
            {
                var piece = item.clothing;
                if (piece == null) continue;
                var block = Box(card, "refit-motion__piece");
                if (motionItems.Count > 1) Text(block, piece.name, "refit-motion__name");
                var fits = ReFitRecordIntegration.KeptFits(piece);
                foreach (var fit in fits)
                {
                    var captured = fit;
                    var row = Box(block, "refit-row", "refit-fit");
                    var dot = Box(row, "refit-row__dot");
                    if (!fit.current) dot.AddToClassList("refit-row__dot--idle");
                    Text(row, fit.name, "refit-row__name");
                    if (fit.current) Text(row, "worn", "refit-pill");
                    else row.Add(BusyButton("Use", "refit-button--small", null, () => ReFitRecordIntegration.UseFit(piece, captured)));
                    row.Add(BusyButton("Forget", "refit-button--small", null, () => ReFitRecordIntegration.ForgetFit(piece, captured)));
                }
                if (fits.Count > 1) Note(block, ReFitSeverity.Info, $"In VRChat: {ReFitRecordIntegration.FitsMenu(piece)} in the avatar's menu (needs VRCFury).");
                if (ReFitRecordIntegration.IsKept(piece)) continue;
                var keep = Box(block, "refit-result__actions");
                var name = new TextField { value = "Fit " + (fits.Count + 1) };
                name.AddToClassList("refit-field");
                name.AddToClassList("refit-fit__name");
                keep.Add(name);
                keep.Add(BusyButton("Keep this fit", "refit-button--primary", null, () => ReFitRecordIntegration.KeepFit(piece, name.value)));
            }
        }
    }
}
