using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// The ReFit window's result page for a replayable case, for looking at it (orbiters_editor_window by window ID): the case
    /// is refitted in a preview scene (never the user's), recorded as the window records it, and a hidden window shows the
    /// result with its pieces checked in motion. <see cref="Close"/> releases all of it, the mesh asset the refit wrote too.
    /// </summary>
    public static class ReFitWizardPreview
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static ReFitCaseValidation.Fixture fixture;
        private static EditorWindow window;
        private static string meshPath, createdFolder;

        /// <summary>Opens it; returns the hidden window's instance ID.</summary>
        public static int Open(string caseName, int width = 460, int height = 1800)
        {
            Close();
            var setup = ReFitCaseValidation.Find(caseName) ?? throw new ArgumentException("Unknown case " + caseName);
            fixture = ReFitCaseValidation.Fixture.Create(setup) ?? throw new InvalidOperationException("Missing assets of case " + caseName);
            var request = fixture.Request(true);
            request.settings.captureProjectionDebug = false;
            var folders = new HashSet<string>(AssetDatabase.IsValidFolder("Assets/ReFit") ? AssetDatabase.GetSubFolders("Assets/ReFit") : new string[0]);
            var result = ReFitService.Execute(request);
            if (!result.success) throw new InvalidOperationException(string.Join("\n", result.report.messages));
            meshPath = result.meshAssetPath;
            // The folder the refit made for this piece, when it made one: removed with its mesh.
            string folder = string.IsNullOrEmpty(meshPath) ? null : System.IO.Path.GetDirectoryName(meshPath)?.Replace('\\', '/');
            createdFolder = folder != null && !folders.Contains(folder) ? folder : null;
            ReFitRecordIntegration.TryRegister(request, result, null, null);

            var host = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Orbiters.Toolkit.Editor.EditorWindowHiddenHost")).First(t => t != null);
            window = (EditorWindow)host.GetMethod("Open", Flags).Invoke(null, new object[] { typeof(ReFitWizard), new Vector2(width, height), null });
            var type = typeof(ReFitWizard);
            type.GetField("lastResult", Flags).SetValue(window, result);
            type.GetField("lastRequest", Flags).SetValue(window, request);
            type.GetField("targetAvatar", Flags).SetValue(window, request.targetAvatar);
            type.GetField("asset", Flags).SetValue(window, fixture.clothing);
            type.GetField("page", Flags).SetValue(window, Enum.Parse(type.GetNestedType("Page", Flags), "Result"));
            type.GetMethod("PrepareMotion", Flags).Invoke(window, null);
            // Checked at once (a hidden window's scheduler waits for its repaints).
            var items = (System.Collections.IList)type.GetField("motionItems", Flags).GetValue(window);
            var check = type.GetMethod("Check", Flags);
            foreach (var item in items) check.Invoke(null, new[] { item, (object)false });
            type.GetMethod("Render", Flags).Invoke(window, null);
            window.Repaint();
            return window.GetInstanceID();
        }

        /// <summary>Calls one of the window's methods by name (a button's action), then builds the page again.</summary>
        public static void Invoke(string method, params object[] arguments)
        {
            if (window == null) return;
            var type = typeof(ReFitWizard);
            type.GetMethod(method, Flags).Invoke(window, arguments);
            type.GetMethod("Render", Flags).Invoke(window, null);
            window.Repaint();
        }

        /// <summary>
        /// Renders the open preview's piece in poses of the motion check (front, side and back views, under
        /// <see cref="ReFitCaseValidation.Output"/> as "motion-pose-view.png"); returns the files written.
        /// </summary>
        public static string RenderPoses(params string[] poses)
        {
            if (fixture == null) throw new InvalidOperationException("Open a preview first.");
            var written = new List<string>();
            var meshes = new List<Mesh>();
            ReFitMotionCheck.PoseBaked = (pose, body, bodyTriangles, cloth, clothTriangles) =>
            {
                if (!poses.Contains(pose)) return;
                Mesh Make(Vector3[] vertices, int[] triangles)
                {
                    var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = vertices, triangles = triangles };
                    mesh.RecalculateNormals();
                    meshes.Add(mesh);
                    return mesh;
                }
                var bodyMesh = Make(body, bodyTriangles);
                var clothMesh = Make(cloth, clothTriangles);
                var bounds = bodyMesh.bounds;
                string label = "motion-" + pose.Replace(' ', '-');
                ReFitCaseValidation.RenderViews(label, bodyMesh, clothMesh, false, bounds.center, bounds.extents.y * 1.05f, "front", "side", "back");
                written.Add(label);
            };
            try { return ReFitMotionCheck.Run(fixture.target.transform.root.gameObject, fixture.target, fixture.clothing) + "\nrendered: " + string.Join(", ", written); }
            finally
            {
                ReFitMotionCheck.PoseBaked = null;
                foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>The open preview's motion check as text, with a body shrink planned when asked.</summary>
        public static string Motion(bool planShrink) => fixture == null ? "no preview" :
            ReFitMotionCheck.Run(fixture.target.transform.root.gameObject, fixture.target, fixture.clothing, planShrink).ToString();

        /// <summary>The refitted piece of the open preview.</summary>
        public static SkinnedMeshRenderer Piece => fixture?.clothing;

        public static void Close()
        {
            if (window != null) window.Close();
            window = null;
            ReFitClipFixTool.End();
            fixture?.Dispose();
            fixture = null;
            if (!string.IsNullOrEmpty(meshPath)) AssetDatabase.DeleteAsset(meshPath);
            if (createdFolder != null && AssetDatabase.IsValidFolder(createdFolder) && AssetDatabase.FindAssets("", new[] { createdFolder }).Length == 0)
                AssetDatabase.DeleteAsset(createdFolder);
            meshPath = createdFolder = null;
        }
    }
}
