using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitWindowLayoutTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Render the real wizard tree offscreen; never open, focus or move the user's windows.</summary>
        public static void RunOrThrow()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var host = new GameObject("__ReFitWindowLayoutQA") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, scene);
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(620, 1400, 24);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var names = new[] { "Belly", "orbit muscles", "orbit face", "Big Face - Buff", "Big Face - Chonky",
                    "Fox Face", "orbit face blink fix", "orbit face blink fix left", "orbit face blink fix right",
                    "Face - Smile", "Face - Jaw", "Face - Cheeks", "Face - Ears", "Face - Nose", "Face - Eyes" }
                    .Concat(Enumerable.Range(0, 482).Select(i => "Body shape " + i)).ToArray();
                foreach (var name in names) mesh.AddBlendShapeFrame(name, 100f, new Vector3[3], null, null);
                var body = new GameObject("Body"); body.transform.SetParent(host.transform);
                body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var type = typeof(ReFitWizard);
                type.GetField("targetAvatar", InstanceFlags).SetValue(window, host);
                type.GetField("current", InstanceFlags).SetValue(window,
                    Enum.Parse(type.GetNestedType("Step", BindingFlags.NonPublic), "BlendshapeSelect"));
                ((List<string>)type.GetField("blendshapes", InstanceFlags).GetValue(window))
                    .AddRange(new[] { "Belly", "orbit muscles", "orbit face" });
                window.CreateGUI();
                texture.Create();
                settings.targetTexture = texture;
                settings.scaleMode = PanelScaleMode.ConstantPixelSize;
                settings.clearColor = true;
                settings.colorClearValue = new Color(0.176f, 0.176f, 0.176f);
                var theme = AssetDatabase.FindAssets("t:ThemeStyleSheet").FirstOrDefault();
                if (theme != null) settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(theme));
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                document.rootVisualElement.style.width = 620;
                window.rootVisualElement.style.width = 620;
                window.rootVisualElement.style.unityFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                document.rootVisualElement.Add(window.rootVisualElement);
                var scroll = window.rootVisualElement.Q<ScrollView>(className: "refit-scroll");
                SetHeight(document, window, 560);
                Layout(document);
                var buttons = window.rootVisualElement.Query<Button>(className: "refit-shape-row").ToList();
                Check(buttons.Count == names.Length, "Compact picker lost available shapes.");
                Check(buttons.GroupBy(b => Mathf.RoundToInt(b.layout.y)).Any(g => g.Count() > 1), "Shape buttons did not wrap side by side.");
                var shapeScroll = window.rootVisualElement.Q<ScrollView>("refit-shape-scroll");
                Check(shapeScroll.contentContainer.layout.height > shapeScroll.contentViewport.layout.height,
                    "Large blendshape lists must scroll inside their own area.");
                Check(shapeScroll.layout.height <= 221, "Blendshape area exceeds its height budget.");

                float needed = 560;
                for (int i = 0; i < 4; i++)
                {
                    needed = ReFitWizard.CalculateMinimumHeight(window.rootVisualElement.layout.height,
                        scroll.contentViewport.layout.height, scroll.contentContainer.layout.height, 1200);
                    SetHeight(document, window, needed);
                    Layout(document);
                }
                Check(scroll.contentContainer.layout.height <= scroll.contentViewport.layout.height + 1f,
                    "Measured minimum height still leaves page overflow.");
                Check(needed < 900, "Compact fixture unexpectedly needs a very tall window.");
                SaveImage(document, texture, "compact-blendshapes.png", (int)needed);

                var selection = (List<string>)type.GetField("blendshapes", InstanceFlags).GetValue(window);
                selection.Clear(); selection.AddRange(names);
                var picker = window.rootVisualElement.Q<ReFitBlendshapePicker>();
                typeof(ReFitBlendshapePicker).GetMethod("RefreshSelection", InstanceFlags).Invoke(picker, null);
                Layout(document);
                var selectedScroll = window.rootVisualElement.Q<ScrollView>("refit-selection-scroll");
                Check(selectedScroll.layout.height <= 89, "Select all makes the page grow without limit.");
                Check(selectedScroll.contentContainer.layout.height > selectedScroll.contentViewport.layout.height,
                    "All selected chips must remain reachable in their own scroll area.");
                float allSelectedHeight = ReFitWizard.CalculateMinimumHeight(needed, scroll.contentViewport.layout.height,
                    scroll.contentContainer.layout.height, 1200);
                SetHeight(document, window, allSelectedHeight); Layout(document);
                Check(scroll.contentContainer.layout.height <= scroll.contentViewport.layout.height + 1f,
                    "Selecting all 497 shapes introduces a page scrollbar.");
                selection.Clear(); selection.AddRange(new[] { "Belly", "orbit muscles", "orbit face" });
                typeof(ReFitBlendshapePicker).GetMethod("RefreshSelection", InstanceFlags).Invoke(picker, null);
                Layout(document);

                var search = window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>();
                search.value = "blink";
                Layout(document);
                float filtered = ReFitWizard.CalculateMinimumHeight(needed, scroll.contentViewport.layout.height,
                    scroll.contentContainer.layout.height, 1200);
                Check(filtered <= needed, "Filtering raised the required minimum height.");
                Check(window.rootVisualElement.Q("refit-shape-selections").childCount == 3, "Filtering lost selected chips.");

                var tall = new VisualElement(); tall.style.height = 2000;
                scroll.Add(tall); Layout(document);
                float capped = ReFitWizard.CalculateMinimumHeight(needed, scroll.contentViewport.layout.height,
                    scroll.contentContainer.layout.height, 800);
                Check(capped == 800, "Tall page exceeded the available screen height.");
                SetHeight(document, window, capped); Layout(document);
                Check(scroll.contentContainer.layout.height > scroll.contentViewport.layout.height, "Tall content cannot scroll at the screen cap.");
                Debug.Log($"[ReFit Window Layout] PASS: 497 wrapping shapes scroll internally, page fits at {needed}px, all-selected page {allSelectedHeight}px, screen cap {capped}px.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(texture);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void SetHeight(UIDocument document, ReFitWizard window, float height)
        {
            document.rootVisualElement.style.height = height;
            window.rootVisualElement.style.height = height;
        }

        private static void Layout(UIDocument document)
        {
            var panel = document.rootVisualElement.panel;
            panel.GetType().GetMethod("Update", InstanceFlags).Invoke(panel, null);
            panel.GetType().GetMethod("ValidateLayout", InstanceFlags).Invoke(panel, null);
        }

        private static void SaveImage(UIDocument document, RenderTexture texture, string name, int height)
        {
            texture.Release();
            texture.height = height;
            texture.Create();
            Layout(document);
            var panel = document.rootVisualElement.panel;
            panel.GetType().GetMethod("Repaint", InstanceFlags).Invoke(panel, new object[] { new Event { type = EventType.Repaint } });
            var previous = RenderTexture.active;
            var image = new Texture2D(texture.width, height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, texture.width, height), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Temp/ReFitTests");
                File.WriteAllBytes("Temp/ReFitTests/" + name, image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
