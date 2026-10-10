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

        /// <summary>
        /// Render the real window tree offscreen (never open, focus or move the user's windows): the stage beside the page
        /// in a wide window and above it in a narrow one, pages scrolling inside a window that keeps its size, and a large
        /// blendshape list scrolling inside the picker.
        /// </summary>
        public static void RunOrThrow()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var host = new GameObject("__ReFitWindowLayoutQA") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, scene);
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            var texture = new RenderTexture(1080, 720, 24);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            var clothMesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try
            {
                var names = new[] { "Belly", "orbit muscles", "orbit face", "Big Face - Buff", "Big Face - Chonky",
                    "Fox Face", "orbit face blink fix", "orbit face blink fix left", "orbit face blink fix right",
                    "Face - Smile", "Face - Jaw", "Face - Cheeks", "Face - Ears", "Face - Nose", "Face - Eyes" }
                    .Concat(Enumerable.Range(0, 482).Select(i => "Body shape " + i)).ToArray();
                foreach (var name in names) mesh.AddBlendShapeFrame(name, 100f, new Vector3[3], null, null);
                var body = new GameObject("Body"); body.transform.SetParent(host.transform);
                body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var shirt = new GameObject("Shirt"); shirt.transform.SetParent(host.transform);
                var shirtRenderer = shirt.AddComponent<SkinnedMeshRenderer>(); shirtRenderer.sharedMesh = clothMesh;
                var type = typeof(ReFitWizard);
                // The studio with a piece chosen and the body shapes picker open.
                type.GetField("targetAvatar", InstanceFlags).SetValue(window, host);
                type.GetField("asset", InstanceFlags).SetValue(window, shirtRenderer);
                type.GetField("shapesOpen", InstanceFlags).SetValue(window, true);
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
                window.rootVisualElement.style.unityFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                document.rootVisualElement.Add(window.rootVisualElement);
                var scroll = window.rootVisualElement.Q<ScrollView>(className: "refit-scroll");
                var stage = window.rootVisualElement.Q<ReFitStageView>();

                // Narrow: the stage tops the page, and the page scrolls inside the window.
                SetSize(document, window, 620, 560);
                Check(stage.parent == scroll.contentContainer && scroll.contentContainer.IndexOf(stage) == 0,
                    "A narrow window must show the stage on top of the page.");
                Check(scroll.contentContainer.layout.height > scroll.contentViewport.layout.height + 1f,
                    "The page must scroll inside a small window instead of growing it.");
                var buttons = window.rootVisualElement.Query<Button>(className: "orbiters-shape-row").ToList();
                Check(buttons.Count == names.Length, "Compact picker lost available shapes.");
                Check(buttons.GroupBy(b => Mathf.RoundToInt(b.layout.y)).Any(g => g.Count() > 1), "Shape buttons did not wrap side by side.");
                var shapeScroll = window.rootVisualElement.Q<ScrollView>("orbiters-shape-scroll");
                Check(shapeScroll.contentContainer.layout.height > shapeScroll.contentViewport.layout.height,
                    "Large blendshape lists must scroll inside their own area.");
                Check(shapeScroll.layout.height <= 221, "Blendshape area exceeds its height budget.");
                SaveImage(document, texture, "layout-narrow.png", 620, 560);

                var selection = (List<string>)type.GetField("blendshapes", InstanceFlags).GetValue(window);
                selection.Clear(); selection.AddRange(names);
                var picker = window.rootVisualElement.Q<ReFitBlendshapePicker>();
                typeof(Orbiters.Toolkit.Editor.BlendshapePicker).GetMethod("RefreshSelection", InstanceFlags).Invoke(picker, null);
                Layout(document);
                var selectedScroll = window.rootVisualElement.Q<ScrollView>("orbiters-selection-scroll");
                Check(selectedScroll.layout.height <= 89, "Select all makes the picker grow without limit.");
                Check(selectedScroll.contentContainer.layout.height > selectedScroll.contentViewport.layout.height,
                    "All selected chips must remain reachable in their own scroll area.");
                selection.Clear(); selection.AddRange(new[] { "Belly", "orbit muscles", "orbit face" });
                typeof(Orbiters.Toolkit.Editor.BlendshapePicker).GetMethod("RefreshSelection", InstanceFlags).Invoke(picker, null);
                var search = window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>();
                search.value = "blink";
                Layout(document);
                Check(window.rootVisualElement.Q("orbiters-shape-selections").childCount == 3, "Filtering lost selected chips.");

                // Wide: the stage stands left of the page.
                SetSize(document, window, 1080, 720);
                Check(stage.parent != scroll.contentContainer, "A wide window must show the stage beside the page.");
                Check(stage.worldBound.xMax <= scroll.worldBound.xMin + 1f && stage.worldBound.height > 400f,
                    $"The stage must stand left of the page, as tall as the window (stage {stage.worldBound}, page {scroll.worldBound}).");
                SaveImage(document, texture, "layout-wide.png", 1080, 720);

                // The settings page keeps the window's height and scrolls.
                float before = window.rootVisualElement.layout.height;
                var pageType = type.GetNestedType("Page", BindingFlags.NonPublic);
                type.GetMethod("Go", InstanceFlags).Invoke(window, new[] { Enum.Parse(pageType, "Settings") });
                Layout(document);
                Check(Mathf.Approximately(window.rootVisualElement.layout.height, before), "Opening the settings changed the window's height.");
                Check(scroll.contentContainer.layout.height > scroll.contentViewport.layout.height + 1f,
                    "The settings must scroll inside the window.");
                Debug.Log($"[ReFit Window Layout] PASS: stage on top when narrow and beside the page when wide, pages scroll inside a fixed window, {names.Length} shapes scroll inside the picker.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(clothMesh);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(texture);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void SetSize(UIDocument document, ReFitWizard window, float width, float height)
        {
            document.rootVisualElement.style.width = width;
            document.rootVisualElement.style.height = height;
            window.rootVisualElement.style.width = width;
            window.rootVisualElement.style.height = height;
            // The layout switch follows the window's geometry: settle, switch, settle again.
            Layout(document);
            typeof(ReFitWizard).GetMethod("UpdateLayout", InstanceFlags).Invoke(window, null);
            Layout(document);
        }

        private static void Layout(UIDocument document)
        {
            var panel = document.rootVisualElement.panel;
            panel.GetType().GetMethod("Update", InstanceFlags).Invoke(panel, null);
            panel.GetType().GetMethod("ValidateLayout", InstanceFlags).Invoke(panel, null);
        }

        private static void SaveImage(UIDocument document, RenderTexture texture, string name, int width, int height)
        {
            texture.Release();
            texture.width = width;
            texture.height = height;
            texture.Create();
            Layout(document);
            var panel = document.rootVisualElement.panel;
            panel.GetType().GetMethod("Repaint", InstanceFlags).Invoke(panel, new object[] { new Event { type = EventType.Repaint } });
            var previous = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                Directory.CreateDirectory("Temp/ReFitTests");
                File.WriteAllBytes("Temp/ReFitTests/" + name, image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
