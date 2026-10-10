using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitNavigationTests
    {
        public static void RunOrThrow()
        {
            ReFitCommissionImageTests.RunOrThrow();
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
                SmartDefaults();
                Debug.Log("[ReFit Navigation Tests] PASS: recents, search, failed-run history, progress DTO/rows, shortcut, header and smart defaults.");
            }
            finally
            {
                if (existed) EditorPrefs.SetString(ReFitBlendshapeHistory.Key, history);
                else EditorPrefs.DeleteKey(ReFitBlendshapeHistory.Key);
            }
        }

        private static void ShortcutAndHeader()
        {
            // A preview scene: the open scene is never touched.
            var scene = EditorSceneManager.NewPreviewScene();
            var group = new GameObject("__ReFitNavigationTest");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(group, scene);
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
                Check(typeof(ReFitWizard).GetField("page", flags).GetValue(window).ToString() == "Studio", "Shortcut did not open the studio.");
                Check(typeof(ReFitWizard).GetField("madeFor", flags).GetValue(window).ToString() == "Target", "A piece worn on its avatar must default to fitting that avatar.");
                Check(Mathf.Abs((float)typeof(ReFitWizard).GetField("clearanceTightnessPreset", flags).GetValue(window) - ReFitAutoSetup.ClothingTightness) < 0.001f,
                    "A hoodie must default to the snug clothing fit.");
                window.CreateGUI();
                var header = window.rootVisualElement.Q(className: "refit-header");
                Check(header.Query<Button>(className: "refit-header-button").ToList().Count == 2, "Navigation buttons are not both in the banner.");
                Check(window.rootVisualElement.Query(className: "refit-piece--selected").ToList().Count == 1, "The chosen piece is not lit in the strip.");
                Check(window.rootVisualElement.Query<ReFitProgressButtonElement>().ToList().Count == 1, "The studio must show exactly one ReFit button.");
                var extra = new GameObject("Other mesh"); extra.transform.SetParent(garment.transform);
                extra.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                window.ConfigureAccessory(garment);
                Check(typeof(ReFitWizard).GetField("asset", flags).GetValue(window) == null, "Ambiguous renderer was guessed.");
                Check((Object)typeof(ReFitWizard).GetField("targetAvatar", flags).GetValue(window) == avatar, "Ambiguous shortcut lost its avatar.");
                Check(window.rootVisualElement.Query(className: "refit-piece").ToList().Count == 2, "Ambiguous shortcut must offer every mesh to pick.");
                Check(window.rootVisualElement.Query<ReFitProgressButtonElement>().ToList().Count == 0, "Nothing to refit yet, but the ReFit button is shown.");
                var standalone = new GameObject("Standalone"); standalone.transform.SetParent(group.transform);
                standalone.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var otherAvatar = new GameObject("Body"); otherAvatar.transform.SetParent(group.transform);
                otherAvatar.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                Check(ReFitContextMenu.FindAvatar(standalone) == null, "Scene group with multiple bodies was guessed as an avatar.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(group);
                Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>Underwear and swimwear count as clothing; the shapes an avatar keeps on under a piece are found.</summary>
        private static void SmartDefaults()
        {
            foreach (var name in new[] { "Jockstrap", "classicJockstrap", "Underwear", "SportBra", "Bra", "Swim Trunks", "Bikini top", "Boxers", "Lingerie_Set", "Bodysuit" })
                Check(Named(name), name + " was not recognised as clothing.");
            // Loose one-pieces are left to the body clothing detection.
            foreach (var name in new[] { "Bracelet", "Brass ring", "Earrings", "Zebra tail", "Hat", "Collar", "WickerOnesie" })
                Check(!Named(name), name + " was taken for snug clothing by name.");

            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("__ReFitActiveShapes");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var near = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.01f, 0f, 0f), new Vector3(0f, 0.01f, 0f) };
            var far = near.Select(v => v + Vector3.right).ToArray();
            var bodyMesh = new Mesh { vertices = near.Concat(far).ToArray(), triangles = new[] { 0, 1, 2, 3, 4, 5 } };
            Vector3[] Moves(bool closeBy, float amount) => Enumerable.Range(0, 6).Select(i => (i < 3) == closeBy ? new Vector3(0f, 0f, amount) : Vector3.zero).ToArray();
            bodyMesh.AddBlendShapeFrame("Near", 100f, Moves(true, 0.01f), null, null);
            bodyMesh.AddBlendShapeFrame("Far", 100f, Moves(false, 0.01f), null, null);
            bodyMesh.AddBlendShapeFrame("Own", 100f, Moves(true, 0.01f), null, null);
            bodyMesh.AddBlendShapeFrame("Off", 100f, Moves(true, 0.01f), null, null);
            bodyMesh.AddBlendShapeFrame("Tiny", 100f, Moves(true, 0.0005f), null, null);
            var clothMesh = new Mesh { vertices = near.Select(v => v + new Vector3(0f, 0f, 0.005f)).ToArray(), triangles = new[] { 0, 1, 2 } };
            clothMesh.AddBlendShapeFrame("Own", 100f, new Vector3[3], null, null);
            try
            {
                var bodyObject = new GameObject("Body"); bodyObject.transform.SetParent(root.transform);
                var body = bodyObject.AddComponent<SkinnedMeshRenderer>(); body.sharedMesh = bodyMesh;
                foreach (var shape in new[] { "Near", "Far", "Own", "Tiny" }) body.SetBlendShapeWeight(bodyMesh.GetBlendShapeIndex(shape), 100f);
                var clothObject = new GameObject("Shirt"); clothObject.transform.SetParent(root.transform);
                var cloth = clothObject.AddComponent<SkinnedMeshRenderer>(); cloth.sharedMesh = clothMesh;
                var found = ReFitAutoSetup.ActiveShapes(body, cloth);
                Check(found.Count == 1 && found[0] == "Near",
                    "Active shapes must be the ones moving the body under the clothing, not its own, far or tiny ones: " + string.Join(", ", found));
                Check(body.GetBlendShapeWeight(bodyMesh.GetBlendShapeIndex("Near")) == 100f, "Measuring active shapes changed the avatar.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(bodyMesh);
                Object.DestroyImmediate(clothMesh);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static bool Named(string name)
        {
            var item = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            var mesh = new Mesh { name = name };
            try
            {
                var renderer = item.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                return ReFitClothingDetection.NamedClothing(renderer);
            }
            finally { Object.DestroyImmediate(item); Object.DestroyImmediate(mesh); }
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
