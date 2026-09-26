using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Searchable batch selection. Filtering never changes the selected shapes.</summary>
    internal sealed class ReFitBlendshapePicker : VisualElement
    {
        private readonly List<string> available;
        private readonly List<string> selection;
        private readonly Action changed;
        private readonly ToolbarSearchField search;
        private readonly VisualElement results;
        private readonly VisualElement chips;
        private readonly Label count;
        private readonly Button selectVisible;
        private readonly Button clear;
        private readonly Dictionary<string, Button> rows = new Dictionary<string, Button>(StringComparer.Ordinal);
        private List<string> matches;

        internal ReFitBlendshapePicker(IEnumerable<string> names, List<string> selection, Action changed)
        {
            this.selection = selection;
            this.changed = changed;
            var recent = ReFitBlendshapeHistory.Read();
            available = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal)
                .OrderBy(n => recent.Contains(n) ? recent.IndexOf(n) : int.MaxValue)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var valid = new HashSet<string>(available, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            selection.RemoveAll(n => !valid.Contains(n) || !seen.Add(n));

            search = new ToolbarSearchField { name = "refit-blendshape-search", tooltip = "Search all body blendshapes; selections stay selected across searches" };
            search.AddToClassList("refit-shape-search");
            Add(search);

            var toolbar = new VisualElement();
            toolbar.AddToClassList("refit-shape-toolbar");
            count = new Label();
            count.AddToClassList("refit-shape-count");
            toolbar.Add(count);
            selectVisible = ImmediateButton("Select all shown", () =>
            {
                foreach (var name in matches)
                    if (!selection.Contains(name)) selection.Add(name);
                RefreshSelection();
            });
            selectVisible.name = "refit-shape-select-visible";
            selectVisible.AddToClassList("refit-back");
            toolbar.Add(selectVisible);
            clear = ImmediateButton("Clear", () => { selection.Clear(); RefreshSelection(); });
            clear.name = "refit-shape-clear";
            clear.AddToClassList("refit-back");
            toolbar.Add(clear);
            Add(toolbar);

            results = new VisualElement { name = "refit-shape-results" };
            results.AddToClassList("refit-shape-results");
            var resultScroll = new ScrollView(ScrollViewMode.Vertical) { name = "refit-shape-scroll" };
            resultScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            resultScroll.AddToClassList("refit-shape-scroll");
            resultScroll.Add(results);
            Add(resultScroll);
            chips = new VisualElement { name = "refit-shape-selections" };
            chips.AddToClassList("refit-shape-suggestions");
            var selectionScroll = new ScrollView(ScrollViewMode.Vertical) { name = "refit-selection-scroll" };
            selectionScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            selectionScroll.AddToClassList("refit-selection-scroll");
            selectionScroll.Add(chips);
            Add(selectionScroll);
            search.RegisterValueChangedCallback(_ => RefreshMatches());
            RefreshMatches();
        }

        private void RefreshMatches()
        {
            string term = (search.value ?? string.Empty).Trim();
            matches = available.Where(n => n.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            results.Clear();
            rows.Clear();
            foreach (string name in matches)
            {
                var row = ImmediateButton(name, () =>
                {
                    if (!selection.Remove(name)) selection.Add(name);
                    RefreshSelection();
                });
                row.userData = name;
                row.tooltip = name;
                row.AddToClassList("refit-shape-suggestion");
                row.AddToClassList("refit-shape-row");
                rows.Add(name, row);
                results.Add(row);
            }
            if (matches.Count == 0)
            {
                var empty = new Label("No matching blendshapes. Your selections are kept below.");
                empty.AddToClassList("refit-help");
                results.Add(empty);
            }
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            count.text = $"{selection.Count} selected · {matches.Count} shown";
            foreach (var pair in rows)
            {
                bool selected = selection.Contains(pair.Key);
                pair.Value.text = (selected ? "✓  " : "+  ") + pair.Key;
                pair.Value.EnableInClassList("refit-shape-row--selected", selected);
                pair.Value.EnableInClassList("refit-shape-suggestion--selected", selected);
            }
            clear.SetEnabled(selection.Count > 0);
            selectVisible.SetEnabled(matches.Any(n => !selection.Contains(n)));
            chips.Clear();
            foreach (string name in selection)
            {
                var chip = ImmediateButton(name + "  ×", () => { selection.Remove(name); RefreshSelection(); });
                chip.userData = name;
                chip.tooltip = "Remove " + name;
                chip.AddToClassList("refit-shape-suggestion");
                chip.AddToClassList("refit-shape-suggestion--selected");
                chips.Add(chip);
            }
            changed?.Invoke();
        }

        // Commit local selection on press, and suppress only the matching mouse click.
        // Keyboard/programmatic clicks still use Unity's normal Button activation.
        private static Button ImmediateButton(string text, Action action)
        {
            bool handledPress = false;
            var button = new Button(() =>
            {
                if (handledPress) { handledPress = false; return; }
                action();
            }) { text = text };
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !button.enabledInHierarchy) return;
                handledPress = true;
                action();
            }, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.schedule.Execute(() => handledPress = false), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerCancelEvent>(_ => handledPress = false);
            return button;
        }
    }
}
