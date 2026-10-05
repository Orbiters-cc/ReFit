using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        public static void RunMultiBlendshapeTestsOrThrow()
        {
            MultiBlendshapePicker_PreservesSelection();
            MultiBlendshapeWizard_SnapshotsRequests();
            MultiBlendshapeBatch_MatchesIndividualTransfers();
            Debug.Log("[ReFit Multi Blendshape Tests] PASS: selection, wizard request snapshots, batch geometry equivalence in both modes.");
        }

        private static void Press(Button button)
        {
            using (var evt = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        private static void Search(ToolbarSearchField search, string value)
        {
            string previous = search.value;
            search.SetValueWithoutNotify(value);
            using (var evt = ChangeEvent<string>.GetPooled(previous, value))
            {
                evt.target = search;
                search.SendEvent(evt);
            }
        }

        private static void MultiBlendshapePicker_PreservesSelection()
        {
            var selection = new List<string> { "missing", "Belly", "Belly" };
            int changes = 0;
            var names = Enumerable.Range(0, 45).Select(i => "Flex " + i).Concat(new[] { "Belly", "Muscles" }).ToArray();
            var picker = new ReFitBlendshapePicker(names, selection, () => changes++);
            var host = new GameObject("__ReFitPickerTest") { hideFlags = HideFlags.HideAndDontSave };
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(620, 560, 0);
            panel.targetTexture = texture;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = panel;
                document.rootVisualElement.Add(picker);
                AssertTrue(selection.SequenceEqual(new[] { "Belly" }), "Stale or duplicate selection survived body refresh.");
                var search = picker.Q<ToolbarSearchField>();
                Search(search, " MUSC ");
                var muscle = picker.Query<Button>(className: "orbiters-shape-row").ToList().Single();
                Press(muscle);
                AssertTrue(selection.SequenceEqual(new[] { "Belly", "Muscles" }), "Pointer-down did not add a second shape immediately.");
                typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(muscle.clickable, new object[] { null });
                AssertTrue(selection.Count == 2, "Click after pointer-down toggled selection twice.");
                typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(muscle.clickable, new object[] { null });
                AssertTrue(selection.SequenceEqual(new[] { "Belly" }), "Normal click fallback did not deselect.");
                Search(search, "Flex");
                AssertTrue(picker.Query<Button>(className: "orbiters-shape-row").ToList().Count == 45,
                    "Matching shapes beyond the first 30 are inaccessible.");
                Press(picker.Q<Button>("orbiters-shape-select-visible"));
                AssertTrue(selection.Count == 46 && !selection.Contains("Muscles"), "Select all shown included hidden names or lost prior selection.");
                Search(search, "no matches");
                AssertTrue(selection.Count == 46 && !picker.Q<Button>("orbiters-shape-select-visible").enabledSelf,
                    "Empty search results changed selection or left bulk selection enabled.");
                var chip = picker.Q("orbiters-shape-selections").Query<Button>().ToList().First(b => (string)b.userData == "Belly");
                Press(chip);
                AssertTrue(!selection.Contains("Belly") && selection.Count == 45, "Removing a hidden selection failed.");
                Press(picker.Q<Button>("orbiters-shape-clear"));
                AssertTrue(selection.Count == 0 && changes >= 7, "Clear did not publish the empty selection.");
                }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(panel);
                Object.DestroyImmediate(texture);
            }
        }

        private static void MultiBlendshapeWizard_SnapshotsRequests()
        {
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(ReFitWizard);
                var selection = (List<string>)type.GetField("blendshapes", flags).GetValue(window);
                selection.AddRange(new[] { "Belly", "Muscles" });
                foreach (var mode in new[] { ReFitMode.Blendshape, ReFitMode.MeshAndBlendshape, ReFitMode.MeshToMesh })
                {
                    type.GetField("mode", flags).SetValue(window, mode);
                    var request = (ReFitRequest)type.GetMethod("BuildRequest", flags).Invoke(window, null);
                    AssertTrue(request.targetBlendshape == null, "Wizard still populates the single-shape field.");
                    if (mode == ReFitMode.MeshToMesh)
                        AssertTrue(request.targetBlendshapes == null, "Mesh-only request leaked previous shape selection.");
                    else
                    {
                        AssertTrue(request.targetBlendshapes.SequenceEqual(selection), "Wizard lost selected shape order.");
                        selection.Add("Later");
                        AssertTrue(request.targetBlendshapes.Count == 2, "Request shares mutable wizard selection.");
                        selection.Remove("Later");
                    }
                }
                type.GetMethod("Restart", flags).Invoke(window, null);
                AssertTrue(selection.Count == 0, "Restart retained the previous batch.");
            }
            finally { Object.DestroyImmediate(window); }
        }

        private static void MultiBlendshapeBatch_MatchesIndividualTransfers()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AddLocalizedPeakBlendshape(fixture.target.mesh, LocalizedPeakShapeName);
                foreach (bool fitMesh in new[] { false, true })
                {
                    var request = fitMesh
                        ? BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false)
                        : BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer);
                    request.targetBlendshape = null;
                    request.targetBlendshapes = new List<string> { BodyShapeName, LocalizedPeakShapeName };
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var batch = new ReFitEngine().Run(request);
                    long batchMs = timer.ElapsedMilliseconds;
                    long individualMs = 0;
                    try
                    {
                        AssertComputationSucceeded(batch);
                        AssertTrue(batch.secondarySourceShapeNames.SequenceEqual(request.targetBlendshapes), "Batch source mapping is wrong.");
                        AssertTrue(batch.secondaryShapeNames.Distinct().Count() == 2, "Batch output names are not distinct.");
                        for (int s = 0; s < request.targetBlendshapes.Count; s++)
                        {
                            var singleRequest = request.Clone();
                            singleRequest.targetBlendshapes = null;
                            singleRequest.targetBlendshape = request.targetBlendshapes[s];
                            timer.Restart();
                            var single = new ReFitEngine().Run(singleRequest);
                            individualMs += timer.ElapsedMilliseconds;
                            try
                            {
                                AssertComputationSucceeded(single);
                                var expected = GetBlendShapeDeltas(single.mesh, single.secondaryShapeNames[0]);
                                var actual = GetBlendShapeDeltas(batch.mesh, batch.secondaryShapeNames[s]);
                                AssertTrue(actual.Any(v => v.sqrMagnitude > 0.000001f), "A selected shape has no deformation.");
                                for (int v = 0; v < actual.Length; v++)
                                    AssertLessOrEqual((actual[v] - expected[v]).magnitude, 0.000001f, "Batch changed a transferred vertex.");
                            }
                            finally { DestroyComputationMesh(single); }
                        }
                        Debug.Log($"[ReFit Batch] {request.mode}: batch {batchMs}ms; two individual runs {individualMs}ms (synthetic fixture, not a general benchmark).");
                    }
                    finally { DestroyComputationMesh(batch); }
                }
            }
        }
    }
}
