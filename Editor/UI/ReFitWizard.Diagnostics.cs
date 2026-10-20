using Orbiters.Toolkit.Editor;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        // Settings card: opt-in diagnostics sent to Orbiters by the Toolkit's shared reporter, with what sharing sends.
        private void BuildDiagnosticsSettings()
        {
            var reporter = ReFitIssueReporter.Reporter;
            var card = SettingsCard("Diagnostics");
            var share = new Toggle("Share ReFit diagnostics") { value = reporter.ShareIssueLogs };
            share.AddToClassList("refit-field");
            card.Add(share);

            var levelRow = SetupRow(card, "Send from");
            var level = new SegmentedControl(OrbitersIssueReporter.LevelNames, index => reporter.MinSeverityLevel = index + 1);
            level.AddToClassList("refit-setup__control");
            level.SetIndex(reporter.MinSeverityLevel - 1);
            levelRow.Add(level);
            levelRow.SetEnabled(share.value);
            share.RegisterValueChangedCallback(e =>
            {
                reporter.ShareIssueLogs = e.newValue;
                levelRow.SetEnabled(e.newValue);
            });
            AddInlineHelp(card, reporter.ConsentText);
        }
    }
}
