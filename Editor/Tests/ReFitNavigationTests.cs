using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitNavigationTests
    {
        public static void RunOrThrow()
        {
            bool existed = EditorPrefs.HasKey(ReFitBlendshapeHistory.Key);
            string history = EditorPrefs.GetString(ReFitBlendshapeHistory.Key);
            try
            {
                EditorPrefs.DeleteKey(ReFitBlendshapeHistory.Key);
                ReFitBlendshapeHistory.Record(new[] { "older", "muscles", "unavailable", "belly", "muscles" });
                var recent = ReFitBlendshapeHistory.Suggestions(new[] { "older", "muscles", "belly", "other" }, ReFitBlendshapeHistory.Read(), "");
                Check(string.Join(",", recent) == "muscles,belly,older", "Recents must be unique, ordered and available.");
                Check(ReFitBlendshapeHistory.Suggestions(new[] { "Orbit Muscles", "belly" }, recent, " MUSC ").Single() == "Orbit Muscles", "Case-insensitive search failed.");
                Check(ReFitBlendshapeHistory.Suggestions(Enumerable.Range(0, 100).Select(i => "shape" + i), recent, "shape").Count == 30, "Search results are not bounded.");
                var before = EditorPrefs.GetString(ReFitBlendshapeHistory.Key);
                var failed = ReFitService.Execute(new ReFitRequest());
                Check(!failed.success && before == EditorPrefs.GetString(ReFitBlendshapeHistory.Key), "Failed refit changed recents.");
                Check(JsonUtility.FromJson<ReFitCommissionClient.CommissionPage>("{\"requests\":[{\"id\":\"test\",\"type\":\"REFIT\",\"name\":\"Hoodie\",\"progress\":{\"active\":true,\"percent\":65,\"label\":\"In progress\"}}]}").requests[0].progress.percent == 65, "Website progress DTO changed.");
                var row = ReFitWizard.CreateCommissionRow(new ReFitCommissionClient.Commission
                {
                    id = "test", name = new string('a', 200), progress = new ReFitCommissionClient.Progress { active = true, percent = 165, label = "In progress" }
                }, false);
                Check(row.Q(className: "refit-commission-fill").style.width.value.value == 100, "Progress is not clamped.");
                Check(row.Q(className: "refit-commission-status").parent == row.Q(className: "refit-commission-name").parent,
                    "Commission status must share the title and creator row.");
                Check(row.Query<Label>().ToList().Any(l => l.text == "No creator assigned"), "Missing creator fallback disappeared.");
                Check(row.Query(className: "refit-logo").ToList().Count == 0, "A row should not repeat the ReFit logo.");
                ShortcutAndHeader();
                Debug.Log("[ReFit Navigation Tests] PASS: recents, search, failed-run history, progress DTO/rows, shortcut and header.");
            }
            finally
            {
                if (existed) EditorPrefs.SetString(ReFitBlendshapeHistory.Key, history);
                else EditorPrefs.DeleteKey(ReFitBlendshapeHistory.Key);
            }
        }

        private static void ShortcutAndHeader()
        {
            var group = new GameObject("__ReFitNavigationTest");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            try
            {
                var avatar = new GameObject("Avatar"); avatar.transform.SetParent(group.transform);
                var body = new GameObject("Body"); body.transform.SetParent(avatar.transform);
                body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var garment = new GameObject("Accessory"); garment.transform.SetParent(avatar.transform);
                var child = new GameObject("Hoodie"); child.transform.SetParent(garment.transform);
                var renderer = child.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                Check(ReFitContextMenu.FindAvatar(child) == avatar, "Nested generic avatar inference failed.");
                window.ConfigureAccessory(child);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                Check(typeof(ReFitWizard).GetField("asset", flags).GetValue(window) == renderer, "Shortcut lost selected renderer.");
                Check(typeof(ReFitWizard).GetField("targetAvatar", flags).GetValue(window) == avatar, "Shortcut lost target avatar.");
                Check(typeof(ReFitWizard).GetField("current", flags).GetValue(window).ToString() == "MyAvatarChoice", "Shortcut did not skip redundant setup.");
                window.CreateGUI();
                var header = window.rootVisualElement.Q(className: "refit-header");
                Check(header.Query<Button>(className: "refit-header-button").ToList().Count == 2, "Navigation buttons are not both in the banner.");
                var extra = new GameObject("Other mesh"); extra.transform.SetParent(garment.transform);
                extra.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                window.ConfigureAccessory(garment);
                Check(typeof(ReFitWizard).GetField("asset", flags).GetValue(window) == null, "Ambiguous renderer was guessed.");
                Check(typeof(ReFitWizard).GetField("current", flags).GetValue(window).ToString() == "AssetSelect", "Ambiguous shortcut must retain mesh choice.");
                var standalone = new GameObject("Standalone"); standalone.transform.SetParent(group.transform);
                standalone.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var otherAvatar = new GameObject("Body"); otherAvatar.transform.SetParent(group.transform);
                otherAvatar.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                Check(ReFitContextMenu.FindAvatar(standalone) == null, "Scene group with multiple bodies was guessed as an avatar.");
            }
            finally { Object.DestroyImmediate(window); Object.DestroyImmediate(group); Object.DestroyImmediate(mesh); }
        }

        public static void ProbeCommissionEndpoint()
        {
            ReFitCommissionClient.FetchActiveCommissions(null, (page, error) =>
            {
                System.IO.Directory.CreateDirectory("Temp/ReFitTests");
                System.IO.File.WriteAllText("Temp/ReFitTests/commission-endpoint.txt", error ??
                    $"PASS: {page.requests.Length} request summaries; {page.requests.Count(r => r.type == "REFIT" && r.progress != null && r.progress.active)} active ReFit; paginated={!string.IsNullOrEmpty(page.nextCursor)}");
            });
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
