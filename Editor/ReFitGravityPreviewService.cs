using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Editor-only preview and apply service for optional post-refit gravity blendshapes.</summary>
    [InitializeOnLoad]
    internal static class ReFitGravityPreviewService
    {
        public const float MaximumGravityWeight = 100f;
        private const int MaxPreviewEdges = 12000;
        private static ReFitGravityPreview activePreview;
        private static SkinnedMeshRenderer activeRenderer;
        private static float activeWeight;
        private static int[] previewEdges;

        static ReFitGravityPreviewService()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        public static bool TryCreatePreview(ReFitResult result, ReFitRequest request, out ReFitGravityPreview preview)
        {
            preview = null;
            if (result == null || !result.success || result.sceneRenderer == null || request == null)
                return false;

            var bodyRenderer = ResolveTargetBodyRenderer(request, result.sceneRenderer);
            if (bodyRenderer == null)
                return false;

            var shapeNames = result.GeneratedShapeNames();
            if (shapeNames.Length == 0)
                return false;

            preview = ReFitGravityRelaxation.GeneratePreview(
                result.sceneRenderer,
                bodyRenderer,
                request.targetAvatar,
                shapeNames,
                ReFitGravityRelaxation.DefaultSettings,
                result.report);

            return preview != null &&
                   preview.candidate != null &&
                   preview.candidate.isCandidate &&
                   preview.frames != null &&
                   preview.frames.Length > 0;
        }

        public static void ShowPreview(SkinnedMeshRenderer renderer, ReFitGravityPreview preview, float weight)
        {
            activeRenderer = renderer;
            activePreview = preview;
            activeWeight = ClampWeight(weight);
            previewEdges = BuildPreviewEdges(preview);
            SceneView.RepaintAll();
        }

        public static void SetWeight(float weight)
        {
            activeWeight = ClampWeight(weight);
            SceneView.RepaintAll();
        }

        public static void ClearPreview(ReFitGravityPreview preview = null)
        {
            if (preview != null && activePreview != preview)
                return;
            activePreview = null;
            activeRenderer = null;
            activeWeight = 0f;
            previewEdges = null;
            SceneView.RepaintAll();
        }

        public static string[] Apply(ReFitResult result, ReFitGravityPreview preview, float defaultWeight)
        {
            if (result == null || result.sceneRenderer == null || result.mesh == null || preview == null ||
                preview.frames == null || preview.frames.Length == 0)
                return new string[0];

            var mesh = result.mesh;
            if (mesh != result.sceneRenderer.sharedMesh)
                mesh = result.sceneRenderer.sharedMesh;
            if (mesh == null)
                return new string[0];

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Apply ReFit gravity");
            Undo.RecordObject(mesh, "Apply ReFit gravity");
            Undo.RecordObject(result.sceneRenderer, "Apply ReFit gravity");

            var addedNames = new List<string>();
            for (int i = 0; i < preview.frames.Length; i++)
            {
                var frame = preview.frames[i];
                if (frame == null || frame.localDeltas == null || frame.localDeltas.Length != mesh.vertexCount)
                    continue;

                string shapeName = UniqueShapeName(mesh, frame.gravityShapeName);
                mesh.AddBlendShapeFrame(shapeName, 100f, frame.localDeltas, null, null);
                int index = mesh.GetBlendShapeIndex(shapeName);
                if (index >= 0)
                    result.sceneRenderer.SetBlendShapeWeight(index, ClampWeight(defaultWeight));
                addedNames.Add(shapeName);
            }

            if (addedNames.Count == 0)
                return addedNames.ToArray();

            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(result.sceneRenderer);
            result.gravityShapeNames = addedNames.ToArray();
            result.gravityDefaultWeight = ClampWeight(defaultWeight);

            ApplyPrefabDefaultWeights(result, mesh, addedNames, result.gravityDefaultWeight);

            AssetDatabase.SaveAssets();
            result.report?.Info("gravity-applied",
                $"Added {addedNames.Count} gravity blendshape(s): {string.Join(", ", addedNames)}.");
            ClearPreview(preview);
            return addedNames.ToArray();
        }

        private static float ClampWeight(float weight)
        {
            return Mathf.Clamp(weight, 0f, MaximumGravityWeight);
        }

        private static void OnSceneGui(SceneView view)
        {
            if (activePreview == null || activeRenderer == null || previewEdges == null)
                return;
            if (activeRenderer.gameObject == null || !activeRenderer.gameObject.scene.IsValid())
            {
                ClearPreview();
                return;
            }

            var vertices = activePreview.previewLocalVertices;
            var skinMatrices = activePreview.previewSkinMatrices;
            if (vertices == null || skinMatrices == null || vertices.Length != skinMatrices.Length)
                return;

            float weight = Mathf.Clamp01(activeWeight / 100f);
            var previousZTest = Handles.zTest;
            var previousColor = Handles.color;
            Handles.zTest = CompareFunction.Always;
            Handles.color = new Color(0.08f, 0.95f, 1f, 0.62f);

            for (int i = 0; i + 1 < previewEdges.Length; i += 2)
            {
                int a = previewEdges[i];
                int b = previewEdges[i + 1];
                if (a < 0 || b < 0 || a >= vertices.Length || b >= vertices.Length)
                    continue;
                Handles.DrawLine(PreviewWorldPoint(a, vertices, skinMatrices, weight),
                    PreviewWorldPoint(b, vertices, skinMatrices, weight));
            }

            Handles.color = previousColor;
            Handles.zTest = previousZTest;
        }

        private static Vector3 PreviewWorldPoint(int vertex, Vector3[] vertices, Matrix4x4[] skinMatrices, float weight)
        {
            var local = vertices[vertex];
            if (activePreview.frames != null)
            {
                for (int i = 0; i < activePreview.frames.Length; i++)
                {
                    var frame = activePreview.frames[i];
                    if (frame?.localDeltas != null && vertex < frame.localDeltas.Length)
                        local += frame.localDeltas[vertex] * weight;
                }
            }
            return skinMatrices[vertex].MultiplyPoint3x4(local);
        }

        private static int[] BuildPreviewEdges(ReFitGravityPreview preview)
        {
            if (preview == null || preview.triangles == null || preview.triangles.Length < 3)
                return null;

            var edges = new List<int>(Mathf.Min(preview.triangles.Length * 2, MaxPreviewEdges * 2));
            var seen = new HashSet<long>();
            for (int t = 0; t + 2 < preview.triangles.Length; t += 3)
            {
                AddEdge(preview.triangles[t], preview.triangles[t + 1], seen, edges);
                AddEdge(preview.triangles[t + 1], preview.triangles[t + 2], seen, edges);
                AddEdge(preview.triangles[t + 2], preview.triangles[t], seen, edges);
            }

            if (edges.Count <= MaxPreviewEdges * 2)
                return edges.ToArray();

            int edgeCount = edges.Count / 2;
            int stride = Mathf.CeilToInt(edgeCount / (float)MaxPreviewEdges);
            var sampled = new List<int>(MaxPreviewEdges * 2);
            for (int e = 0; e < edgeCount; e += stride)
            {
                sampled.Add(edges[e * 2]);
                sampled.Add(edges[e * 2 + 1]);
            }
            return sampled.ToArray();
        }

        private static void AddEdge(int a, int b, HashSet<long> seen, List<int> edges)
        {
            int min = Mathf.Min(a, b);
            int max = Mathf.Max(a, b);
            long key = ((long)min << 32) ^ (uint)max;
            if (!seen.Add(key))
                return;
            edges.Add(a);
            edges.Add(b);
        }

        private static SkinnedMeshRenderer ResolveTargetBodyRenderer(ReFitRequest request, SkinnedMeshRenderer resultRenderer)
        {
            if (request.targetBodyRenderer != null)
                return request.targetBodyRenderer;
            if (request.targetAvatar == null)
                return null;

            SkinnedMeshRenderer bestBodyToken = null;
            SkinnedMeshRenderer bestByVertexCount = null;
            int bestTokenVertices = -1;
            int bestVertices = -1;
            var renderers = request.targetAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (renderer == null || renderer == resultRenderer || mesh == null)
                    continue;

                string key = ReFitUtility.NormalizeName(renderer.name + " " + renderer.gameObject.name + " " + mesh.name);
                bool bodyLike = key.Contains("body") || key.Contains("torso") || key.Contains("base");
                if (bodyLike && mesh.vertexCount > bestTokenVertices)
                {
                    bestTokenVertices = mesh.vertexCount;
                    bestBodyToken = renderer;
                }
                if (mesh.vertexCount > bestVertices)
                {
                    bestVertices = mesh.vertexCount;
                    bestByVertexCount = renderer;
                }
            }

            return bestBodyToken != null ? bestBodyToken : bestByVertexCount;
        }

        private static string UniqueShapeName(Mesh mesh, string desired)
        {
            if (string.IsNullOrEmpty(desired))
                desired = "gravity";
            if (mesh.GetBlendShapeIndex(desired) < 0)
                return desired;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = desired + " " + i;
                if (mesh.GetBlendShapeIndex(candidate) < 0)
                    return candidate;
            }
            return desired + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static void ApplyPrefabDefaultWeights(ReFitResult result, Mesh mesh, List<string> shapeNames, float weight)
        {
            if (string.IsNullOrEmpty(result.prefabAssetPath) || shapeNames == null || shapeNames.Count == 0)
                return;

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(result.prefabAssetPath);
                if (root == null)
                    return;

                bool changed = false;
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh != mesh && renderer.name != result.sceneRenderer.name)
                        continue;
                    for (int i = 0; i < shapeNames.Count; i++)
                    {
                        int index = renderer.sharedMesh != null ? renderer.sharedMesh.GetBlendShapeIndex(shapeNames[i]) : -1;
                        if (index < 0)
                            continue;
                        renderer.SetBlendShapeWeight(index, weight);
                        changed = true;
                    }
                }

                if (changed)
                    PrefabUtility.SaveAsPrefabAsset(root, result.prefabAssetPath);
            }
            catch (Exception e)
            {
                result.report?.Warn("gravity-prefab-default-skipped",
                    $"Could not update the gravity blendshape defaults on the generated prefab: {e.Message}");
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
