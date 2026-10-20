using NUnit.Framework;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// ReFit's use of Orbiters Toolkit's shared issue reporter (masking, queueing, sending and the off-by-default setting
    /// are the Toolkit's, tested there): which logs are ReFit's and what its opt-in says.
    /// </summary>
    public sealed class ReFitIssueReportingTests
    {
        [Test]
        public void TheReporterIsReFits()
        {
            Assert.AreEqual("ReFit", ReFitIssueReporter.Reporter.ToolName);
            Assert.AreEqual("ReFit", ReFitIssueReporter.Options().SettingsKey, "The opt-in is kept where it always was.");
        }

        [Test]
        public void OnlyReFitsOwnLogsAreReported()
        {
            var reporter = ReFitIssueReporter.Reporter;
            const string own = "UnityEngine.Debug:LogWarning (object)\nOrbiters.ReFit.Editor.ReFitEngine:Run () (at Packages/orbiters.refit/Editor/ReFitEngine.cs:12)";
            const string throughMyAvatar = "Orbiters.ReFit.Editor.ReFitEngine.Run () (at Packages/orbiters.refit/Editor/ReFitEngine.cs:40)\nOrbiters.MyAvatar.Editor.Clothing.Fit ()";
            const string myAvatarThrows = "Orbiters.MyAvatar.Editor.Clothing.Fit ()\nOrbiters.ReFit.Editor.ReFitWizard.Run ()";
            const string test = "UnityEngine.Debug:LogError (object)\nOrbiters.ReFit.Editor.ReFitEngine:Run ()\nOrbiters.ReFit.Editor.Tests.ReFitSyntheticTests:Run ()";
            Assert.True(reporter.IsOwnLog(own, LogType.Warning));
            Assert.True(reporter.IsOwnLog(throughMyAvatar, LogType.Exception), "Thrown in ReFit, whoever called it.");
            Assert.False(reporter.IsOwnLog(myAvatarThrows, LogType.Exception), "Thrown in My Avatar: My Avatar's.");
            Assert.False(reporter.IsOwnLog(test, LogType.Error), "A test's deliberate failure.");
        }

        [Test]
        public void TheOptInSaysWhatIsSent()
        {
            string text = ReFitIssueReporter.Reporter.ConsentText;
            StringAssert.Contains("ReFit", text);
            StringAssert.Contains("stack traces", text);
            StringAssert.Contains("every Unity project on this computer", text);
            StringAssert.Contains("Orbiters account", text);
        }
    }
}
