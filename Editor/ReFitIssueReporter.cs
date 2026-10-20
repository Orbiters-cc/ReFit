using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// ReFit's opt-in diagnostics (off by default): its own warnings and errors, sent by Orbiters Toolkit's shared reporter,
    /// which masks them and bounds what is sent. Its card is in the wizard's settings (ReFitWizard.Diagnostics).
    /// </summary>
    [InitializeOnLoad]
    internal static class ReFitIssueReporter
    {
        internal const string ToolName = "ReFit";

        internal static IssueReporterOptions Options() => new IssueReporterOptions
        {
            ToolName = ToolName,
            SettingsKey = "ReFit",
            Namespaces = new[] { "Orbiters.ReFit." },
            TestNamespaces = new[] { "Orbiters.ReFit.Editor.Tests" }
        };

        // Registered as the editor loads, so what ReFit logs while it starts is not missed.
        internal static readonly OrbitersIssueReporter Reporter = OrbitersIssueReporter.Register(Options());

        static ReFitIssueReporter()
        {
        }
    }
}
