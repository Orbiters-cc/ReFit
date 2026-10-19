using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Every replayable case (<see cref="ReFitCaseValidation.AllCases"/> whose private assets are in this project), one per
    /// editor update so Unity stays responsive and an MCP call returns at once: clipping at rest, legs spread and crouched,
    /// spikes, triangle quality, skin weight tears, the engine's time and each phase's. One TSV row per case under
    /// <see cref="Output"/>; <see cref="Compare"/> lines two runs up. Poll <see cref="Status"/>. Never touches the scene.
    /// </summary>
    public static class ReFitRegressionSuite
    {
        // Library, not Temp: Unity empties Temp when it starts, and a run lost to a crash must not take its rows with it.
        public const string Output = "Library/ReFitTests/regression/";
        private const string Key = "Orbiters.ReFit.Regression.";

        /// <summary>"running label i/n case", "done label ..." or "failed ...".</summary>
        public static string Status => SessionState.GetString(Key + "Status", "");

        private static readonly string[] Columns =
        {
            "case", "engineMs", "restSpikes", "restSpikeMm", "rest3mm", "restWorstMm", "restReversed", "restStretched",
            "spread3mm", "spreadWorstMm", "crouch3mm", "crouchWorstMm", "shape3mm", "shapeWorstMm", "crouchShape3mm",
            "weightTears", "oppositeLeg", "motionAdded", "motionWorstMm", "restUnder3mm", "restUnderWorstMm", "bareUnder3mm",
            "shapeUnder3mm", "phases",
        };
        // Counts where more is worse: a case whose count grows by more than a tenth (and 2) got worse.
        private static readonly string[] WorseWhenMore =
            { "restSpikes", "rest3mm", "spread3mm", "crouch3mm", "shape3mm", "weightTears", "motionAdded", "restUnder3mm", "bareUnder3mm", "shapeUnder3mm" };

        /// <param name="only">Comma-separated case names, or null for every available case.</param>
        /// <param name="settings">"field=value;..." overrides of <see cref="ReFitSettings"/>, as <see cref="ReFitCaseValidation.Queue"/>.</param>
        /// <param name="render">"none", "fit" or "all", as <see cref="ReFitCaseValidation.Run"/>.</param>
        public static void Start(string label, string only = null, string settings = null, string render = "none")
        {
            var wanted = only?.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet();
            var names = ReFitCaseValidation.AllCases.Where(c => (wanted == null || wanted.Contains(c.name)) && ReFitCaseValidation.Available(c, out _))
                .Select(c => c.name).ToList();
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + label + ".tsv", string.Join("\t", Columns) + "\n");
            SessionState.SetString(Key + "Queue", string.Join("\n", names));
            SessionState.SetString(Key + "Label", label);
            SessionState.SetString(Key + "Settings", settings ?? "");
            SessionState.SetString(Key + "Render", render);
            SessionState.SetInt(Key + "Total", names.Count);
            SessionState.SetString(Key + "Status", $"queued {label} 0/{names.Count}");
            Schedule();
        }

        // A domain reload (another package compiled in the same editor) drops the scheduled update; the queue is kept in
        // SessionState, and the case running then had finished: a reload only comes between editor updates.
        [InitializeOnLoadMethod]
        private static void Resume()
        {
            string status = Status;
            if (status.StartsWith("running") || status.StartsWith("queued")) Schedule();
        }

        private static void Schedule()
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () => { EditorApplication.update -= tick; Step(); };
            EditorApplication.update += tick;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void Step()
        {
            var queue = SessionState.GetString(Key + "Queue", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string label = SessionState.GetString(Key + "Label", "");
            int total = SessionState.GetInt(Key + "Total", 0);
            if (queue.Count == 0)
            {
                SessionState.SetString(Key + "Status", $"done {label} {total}/{total}\n" + File.ReadAllText(Output + label + ".tsv"));
                return;
            }
            string name = queue[0];
            queue.RemoveAt(0);
            SessionState.SetString(Key + "Queue", string.Join("\n", queue));
            SessionState.SetString(Key + "Status", $"running {label} {total - queue.Count}/{total} {name}");
            string row;
            try
            {
                string settings = SessionState.GetString(Key + "Settings", "");
                var result = ReFitCaseValidation.Run(ReFitCaseValidation.Find(name), label, true, SessionState.GetString(Key + "Render", "none"),
                    settings.Length > 0 ? settings : null);
                row = Row(name, result);
            }
            catch (Exception e)
            {
                row = name + "\tFAILED " + e.GetType().Name + ": " + e.Message.Replace('\n', ' ').Replace('\t', ' ');
            }
            File.AppendAllText(Output + label + ".tsv", row + "\n");
            Schedule();
        }

        private static string Row(string name, ReFitCaseValidation.Result result)
        {
            string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
            ReFitCaseValidation.Metrics M(string pose) => result.metrics.TryGetValue(pose, out var m) ? m : new ReFitCaseValidation.Metrics { samples = -1 };
            var rest = M("rest");
            var spread = M("spread");
            var crouch = M("crouch");
            var shape = M("rest-shape");
            var crouchShape = M("crouch-shape");
            var bare = M("rest-bare");
            string phases = string.Join(";", result.phases.Select(p => p.phase.Replace(';', ',') + "=" + p.ms.ToString("F0", CultureInfo.InvariantCulture)));
            return string.Join("\t", name, result.engineMs, rest.spikes, F(rest.spikeMm), rest.inside3mm, F(rest.worstMm), rest.reversed, rest.stretched,
                spread.inside3mm, F(spread.worstMm), crouch.inside3mm, F(crouch.worstMm), shape.samples < 0 ? "-" : shape.inside3mm.ToString(),
                shape.samples < 0 ? "-" : F(shape.worstMm), crouchShape.samples < 0 ? "-" : crouchShape.inside3mm.ToString(),
                result.weightTears, result.oppositeLegShare.ToString("F3", CultureInfo.InvariantCulture),
                result.motion == null || result.motion.error != null ? "-" : result.motion.Added.ToString(),
                result.motion == null || result.motion.error != null ? "-" : F(result.motion.WorstMm),
                rest.under3mm, F(rest.underWorstMm), bare.samples < 0 ? "-" : bare.under3mm.ToString(),
                shape.samples < 0 ? "-" : shape.under3mm.ToString(), phases);
        }

        /// <summary>
        /// Two runs side by side, per case: each metric as "before → after", the engine time and its change; a summary of the
        /// cases that got worse (more clipping or spikes, more than 10 % slower) at the end.
        /// </summary>
        public static string Compare(string before, string after)
        {
            var a = Read(before);
            var b = Read(after);
            var lines = new List<string>();
            var worse = new List<string>();
            long totalA = 0, totalB = 0;
            // Columns by name: runs written before a column was added line up with the ones after.
            foreach (var name in a.Keys.Concat(b.Keys).Distinct().Where(n => n != Header))
            {
                if (!a.TryGetValue(name, out var x) || !b.TryGetValue(name, out var y))
                {
                    lines.Add($"{name}: only in {(a.ContainsKey(name) ? before : after)}");
                    continue;
                }
                string Cell(Dictionary<string, string[]> run, string[] row, string column)
                {
                    int c = Array.IndexOf(run[Header], column);
                    return c >= 0 && c < row.Length ? row[c] : null;
                }
                if (Cell(a, x, "phases") == null || Cell(b, y, "phases") == null)
                {
                    lines.Add($"{name}: {before} {string.Join(" ", x.Skip(1))} | {after} {string.Join(" ", y.Skip(1))}");
                    continue;
                }
                var parts = new List<string>();
                foreach (var column in Columns.Skip(1).Where(c => c != "phases"))
                {
                    string va = Cell(a, x, column), vb = Cell(b, y, column);
                    if (va != null && vb != null && va != vb) parts.Add($"{column} {va}→{vb}");
                }
                lines.Add($"{name}: " + (parts.Count == 0 ? "same" : string.Join(", ", parts)));
                long msA = long.Parse(x[1]), msB = long.Parse(y[1]);
                totalA += msA; totalB += msB;
                foreach (var column in WorseWhenMore)
                    if (int.TryParse(Cell(a, x, column), out int va) && int.TryParse(Cell(b, y, column), out int vb) && vb > va + Math.Max(2, va / 10))
                        worse.Add($"{name} {column} {va}→{vb}");
                if (msB > msA * 1.1 + 200) worse.Add($"{name} engineMs {msA}→{msB}");
            }
            lines.Add($"total engine time {totalA} → {totalB} ms");
            lines.Add(worse.Count == 0 ? "no case got worse" : "worse: " + string.Join("; ", worse));
            return string.Join("\n", lines);
        }

        private const string Header = "case";

        // Rows by case name, and the header row under "case".
        private static Dictionary<string, string[]> Read(string label) =>
            File.ReadAllLines(Output + label + ".tsv").Where(l => l.Length > 0).Select(l => l.Split('\t'))
                .GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.Last());
    }
}
