using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        // ------------------------------------------------------------------
        // In motion: each refitted piece checked in poses (ReFitMotionCheck) once the result shows, one piece per frame,
        // and a body shrink planned and kept on request.
        // ------------------------------------------------------------------

        private sealed class MotionItem
        {
            public SkinnedMeshRenderer clothing, body;
            public GameObject avatar;
            public ReFitMotionCheck.Result result;
            public ReFitMotionCheck.BodyShrink planned;
            public bool busy;
        }

        private readonly List<MotionItem> motionItems = new List<MotionItem>();
        private IVisualElementScheduledItem motionUpdate;

        private void ClearMotion()
        {
            ReFitClipFixTool.End();
            motionUpdate?.Pause();
            motionUpdate = null;
            motionItems.Clear();
        }

        // The pieces of the last run that can be checked: refitted, on an avatar with their body.
        private void PrepareMotion()
        {
            ClearMotion();
            if (!ReFitRecordIntegration.IsAvailable) return;
            var pieces = outfitResults.Count > 0
                ? outfitResults.Where(r => r.result != null && r.result.success).Select(r => (r.result.sceneRenderer, r.request))
                : lastResult != null && lastResult.success ? new[] { (lastResult.sceneRenderer, lastRequest) } : Enumerable.Empty<(SkinnedMeshRenderer, ReFitRequest)>();
            foreach (var (clothing, request) in pieces)
            {
                if (clothing == null) continue;
                // Unity objects: their own null test, never ?? (a destroyed renderer is not null to it).
                var recorded = ReFitRecordIntegration.BodyOf(clothing);
                var body = recorded != null ? recorded : request?.targetBodyRenderer;
                var avatar = request?.targetAvatar != null ? request.targetAvatar : targetAvatar;
                if (body == null || avatar == null || !clothing.transform.IsChildOf(avatar.transform) || !body.transform.IsChildOf(avatar.transform)) continue;
                motionItems.Add(new MotionItem { clothing = clothing, body = body, avatar = avatar });
            }
            ScheduleMotion();
        }

        // A piece still to check, whose clothing is still there.
        private MotionItem PendingPiece() => motionItems.FirstOrDefault(i => i.result == null && i.clothing != null);

        // The next piece is checked on a later tick, unless one is due already; the result page asks again each time it shows,
        // so a check left when the page changed picks up where it stopped.
        private void ScheduleMotion()
        {
            if (motionUpdate != null || PendingPiece() == null || rootVisualElement == null) return;
            motionUpdate = rootVisualElement.schedule.Execute(CheckNextPiece).StartingIn(PressPaint);
        }

        // One piece per tick, so the page stays live while an outfit is checked.
        private void CheckNextPiece()
        {
            motionUpdate = null;
            var item = PendingPiece();
            if (item == null || page != Page.Result || isExecuting) return;
            Check(item, false);
            // Before the page is built again: a page that fails to build never stops the pieces after this one.
            ScheduleMotion();
            Render();
        }

        private static void Check(MotionItem item, bool planShrink)
        {
            try
            {
                item.result = ReFitMotionCheck.Run(item.avatar, item.body, item.clothing, planShrink,
                    planShrink ? (p, step) => EditorUtility.DisplayProgressBar("ReFit", step, p) : (Action<float, string>)null);
                item.planned = planShrink && item.result.shrink != null && item.result.shrink.vertices.Length > 0 ? item.result.shrink : null;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                item.result = new ReFitMotionCheck.Result { error = ex.Message };
                item.planned = null;
            }
            finally
            {
                if (planShrink) EditorUtility.ClearProgressBar();
                item.busy = false;
            }
        }

        private void BuildMotion()
        {
            if (motionItems.Count == 0) return;
            ScheduleMotion();
            var card = Box(content, "refit-card", "refit-motion");
            var heading = Box(card, "refit-result__heading");
            Text(heading, "In motion", "refit-section");
            Text(heading, "beta", "refit-pill");
            Text(card, "A hidden copy of the avatar raises its arms, twists, steps and crouches; the skin under the clothing should stay under it.",
                "refit-motion__caption");
            foreach (var item in motionItems) BuildMotionItem(card, item, motionItems.Count > 1);
        }

        private void BuildMotionItem(VisualElement card, MotionItem item, bool named)
        {
            if (item.clothing == null) return;
            var block = Box(card, "refit-motion__piece");
            if (named) Text(block, item.clothing.name, "refit-motion__name");
            var result = item.result;
            if (result == null)
            {
                Note(block, ReFitSeverity.Info, "Checking…");
                return;
            }
            if (result.error != null) Note(block, ReFitSeverity.Warning, "Not checked: " + result.error + ".");
            else if (result.Added == 0) Note(block, ReFitSeverity.Info, $"No skin comes through in the {ReFitMotionCheck.Poses.Length - 1} poses.");
            else
            {
                var worst = result.poses.Where(p => p.pose != "rest" && p.added > 0).OrderByDescending(p => p.added).ToList();
                string where = string.Join(", ", worst.Take(3).Select(p => $"{p.pose} {p.added}"));
                Note(block, ReFitSeverity.Warning, $"Skin comes through in {worst.Count} poses, up to {result.WorstMm:0.#} mm ({where} points).");
            }

            var kept = ReFitRecordIntegration.BodyShrinkOf(item.clothing);
            if (item.planned != null)
            {
                var shrink = item.planned;
                Note(block, ReFitSeverity.Info, $"The body moves in by up to {shrink.largestMm:0.#} mm at {shrink.vertices.Length} skin points while this is worn: " +
                                                $"{shrink.before} → {shrink.after} points through over the poses.");
            }
            else if (kept != null)
                Note(block, ReFitSeverity.Info, $"The body moves in under it while it is worn ({kept.Value.points} skin points, up to {kept.Value.largestMm:0.#} mm).");

            var actions = Box(block, "refit-result__actions");
            if (item.planned != null)
            {
                actions.Add(BusyButton("Skip", "refit-button--ghost", null, () => item.planned = null));
                Spacer(actions);
                actions.Add(BusyButton("Keep the shrink", "refit-button--primary", null, () =>
                {
                    if (ReFitRecordIntegration.SetBodyShrink(item.clothing, item.body, item.planned)) item.planned = null;
                }));
                return;
            }
            actions.Add(BusyButton("Check again", "refit-button--ghost", "Checking…", () => Check(item, false)));
            if (kept != null) actions.Add(BusyButton("Remove the shrink", "refit-button--ghost", null, () => ReFitRecordIntegration.SetBodyShrink(item.clothing, item.body, null)));
            Spacer(actions);
            if (result.error == null && result.Added > 0)
                actions.Add(BusyButton(kept != null ? "Shrink again" : "Shrink the body under it", "refit-button--primary", "Planning…", () => Check(item, true)));
        }

        // A button that shows its work at once (pressed, its text) and runs it once that has painted, then builds the page again.
        private Button BusyButton(string text, string style, string busyText, Action work)
        {
            Button button = null;
            button = FlatButton(text, () =>
            {
                if (isExecuting) return;
                if (busyText != null) button.text = busyText;
                button.SetEnabled(false);
                rootVisualElement.schedule.Execute(() =>
                {
                    try { work(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    Render();
                }).StartingIn(PressPaint);
            }, style);
            return button;
        }

        private static void Note(VisualElement parent, ReFitSeverity severity, string text) =>
            AddMessage(parent, new ReFitMessage { severity = severity, text = text });

        private static void Spacer(VisualElement row)
        {
            var spacer = Box(row, "refit-grow");
            spacer.pickingMode = PickingMode.Ignore;
        }
    }
}
