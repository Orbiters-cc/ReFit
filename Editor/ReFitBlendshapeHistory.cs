using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    internal static class ReFitBlendshapeHistory
    {
        internal const string Key = "ReFit_RecentSuccessfulBlendshapes";
        [Serializable] private sealed class History { public List<string> names = new List<string>(); }

        internal static List<string> Read()
        {
            try { return JsonUtility.FromJson<History>(EditorPrefs.GetString(Key, "{}"))?.names ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        internal static void Record(IEnumerable<string> names)
        {
            if (names == null) return;
            var history = Read();
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                history.RemoveAll(n => n == name);
                history.Insert(0, name);
            }
            EditorPrefs.SetString(Key, JsonUtility.ToJson(new History { names = history.Take(100).ToList() }));
        }

        internal static List<string> Suggestions(IEnumerable<string> available, IEnumerable<string> recent, string query)
        {
            var valid = new HashSet<string>(available.Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(query))
                return recent.Where(valid.Contains).Distinct().Take(3).ToList();
            string term = query.Trim();
            return valid.Where(n => n.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        }
    }
}
