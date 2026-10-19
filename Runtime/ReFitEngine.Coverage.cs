using System.Collections.Generic;
using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.ReFit
{
    public partial class ReFitEngine
    {
        // The staged copy of a garment of the target avatar, or null.
        private static SkinnedMeshRenderer Staged(ReFitRequest request, NormalizedStage stage, SkinnedMeshRenderer original)
        {
            if (original == null || original == request.assetRenderer || original.sharedMesh == null ||
                !original.transform.IsChildOf(request.targetAvatar.transform)) return null;
            var transform = ReFitUtility.ResolvePath(stage.targetRoot.transform, ReFitUtility.IndexPath(original.transform, request.targetAvatar.transform));
            return transform != null ? transform.GetComponent<SkinnedMeshRenderer>() : null;
        }

        // Body shapes (and the garment's own refit, its primary shape) at zero: the garment as authored.
        private static Dictionary<int, float> AuthoredWeights(NormalizedStage stage, SkinnedMeshRenderer renderer)
        {
            var metadata = renderer.GetComponent<ReFitGeneratedAssetMetadata>()?.data;
            var names = new Dictionary<string, string>();
            if (metadata?.transferredShapes != null)
                foreach (var shape in metadata.transferredShapes)
                    if (shape != null && !string.IsNullOrEmpty(shape.sourceName) && !string.IsNullOrEmpty(shape.generatedName))
                        names[shape.sourceName] = shape.generatedName;
            var overrides = new Dictionary<int, float>();
            for (int b = 0; b < stage.targetBody.sharedMesh.blendShapeCount; b++)
            {
                string source = stage.targetBody.sharedMesh.GetBlendShapeName(b);
                int index = renderer.sharedMesh.GetBlendShapeIndex(names.TryGetValue(source, out var generated) ? generated : source);
                if (index >= 0) overrides[index] = 0;
            }
            int primary = string.IsNullOrEmpty(metadata?.primaryShapeName) ? -1 : renderer.sharedMesh.GetBlendShapeIndex(metadata.primaryShapeName);
            if (primary >= 0) overrides[primary] = 0;
            return overrides;
        }

        // Primary refit is active; its deformed normals are required for layer contact.
        private static void LayerNormals(MeshSnapshot snapshot)
        {
            snapshot.worldNormals = new Vector3[snapshot.worldVertices.Length];
            for (int t = 0; t < snapshot.triangles.Length; t += 3)
            {
                int a = snapshot.triangles[t], b = snapshot.triangles[t + 1], c = snapshot.triangles[t + 2];
                var normal = Vector3.Cross(snapshot.worldVertices[b] - snapshot.worldVertices[a], snapshot.worldVertices[c] - snapshot.worldVertices[a]);
                snapshot.worldNormals[a] += normal; snapshot.worldNormals[b] += normal; snapshot.worldNormals[c] += normal;
            }
            for (int v = 0; v < snapshot.worldNormals.Length; v++) snapshot.worldNormals[v].Normalize();
            snapshot.CacheFaceNormals();
        }

        // The followed shapes the target shows back at zero (the passes without the cloth fit fit there): no longer shown, and
        // the garments worn under it without their own versions of them, which they then add (see CaptureCoverageLayers).
        private static void ShownAtZero(State state)
        {
            foreach (var shape in state.shapes)
            {
                if (shape.shown <= 0f) continue;
                var frame = shape.frames[0];
                for (int layer = 0; layer < state.coverageLayers.Count; layer++)
                {
                    var vertices = state.coverageLayers[layer].worldVertices;
                    // The layer's version as shown, taken away.
                    var taken = frame.layerDeltas[layer];
                    for (int v = 0; v < taken.Length; v++)
                    {
                        vertices[v] += taken[v];
                        taken[v] /= -shape.shown;
                    }
                }
                shape.shown = 0f;
            }
            foreach (var layer in state.coverageLayers) LayerNormals(layer);
        }

        // Snapshot inner garments in the same staged target space as the body. All Unity reads stay
        // on the main thread; the coverage solver subsequently sees only vertices and shape arrays.
        private static void CaptureCoverageLayers(State state, NormalizedStage stage)
        {
            var request = state.request;
            if (!state.settings.coverDifferentBaseBody || request.coverageLayers == null) return;
            foreach (var original in request.coverageLayers)
            {
                var renderer = Staged(request, stage, original);
                if (renderer == null) continue;
                var metadata = renderer.GetComponent<ReFitGeneratedAssetMetadata>()?.data;
                var names = new Dictionary<string, string>();
                if (metadata?.transferredShapes != null)
                    foreach (var shape in metadata.transferredShapes)
                        if (shape != null && !string.IsNullOrEmpty(shape.sourceName) && !string.IsNullOrEmpty(shape.generatedName))
                            names[shape.sourceName] = shape.generatedName;
                var overrides = new Dictionary<int, float>();
                for (int b = 0; b < stage.targetBody.sharedMesh.blendShapeCount; b++)
                {
                    string source = stage.targetBody.sharedMesh.GetBlendShapeName(b);
                    int index = renderer.sharedMesh.GetBlendShapeIndex(names.TryGetValue(source, out var generated) ? generated : source);
                    if (index >= 0) overrides[index] = 0;
                }
                // Over the body as it is fitted: the followed shapes it shows, the layer's own versions of them as they mirror it.
                foreach (var shape in state.shapes)
                {
                    int index = renderer.sharedMesh.GetBlendShapeIndex(names.TryGetValue(shape.sourceName, out var generated) ? generated : shape.sourceName);
                    if (shape.shown > 0f && index >= 0) overrides[index] = Mathf.Clamp(shape.mirrorWeight, 0f, 100f) / 100f;
                }
                var snapshot = MeshSnapshot.Capture(renderer, false, overrides, state.Report);
                LayerNormals(snapshot);
                state.coverageLayers.Add(snapshot);
                state.coverageLayerNames.Add(original.name);
                // As authored: without the layer's own refit (its primary shape), body shapes at zero.
                state.coverageLayersAuthored.Add(state.settings.coverageKeepsLayerOrder
                    ? MeshSnapshot.Capture(renderer, false, AuthoredWeights(stage, renderer), state.Report).worldVertices : null);
                state.coverageLayersRefitted.Add(!string.IsNullOrEmpty(metadata?.primaryShapeName) && renderer.sharedMesh.GetBlendShapeIndex(metadata.primaryShapeName) >= 0);
                var scratch = new BlendShapeEvaluation.Scratch(snapshot.worldVertices.Length);
                foreach (var shape in state.shapes)
                {
                    int index = renderer.sharedMesh.GetBlendShapeIndex(names.TryGetValue(shape.sourceName, out var generated) ? generated : shape.sourceName);
                    foreach (var frame in shape.frames)
                    {
                        var deltas = new Vector3[snapshot.worldVertices.Length];
                        // A shape the body shows is taken away: the layer loses its own as shown.
                        if (index >= 0)
                            BlendShapeEvaluation.Add(renderer.sharedMesh, index, shape.shown > 0f ? Mathf.Clamp(shape.mirrorWeight, 0f, 100f) : frame.weight,
                                BlendShapeEvaluation.ClampWeights, deltas, null, scratch);
                        float sign = shape.shown > 0f ? -1f : 1f;
                        for (int v = 0; v < deltas.Length; v++) deltas[v] = snapshot.skinMatrices[v].MultiplyVector(deltas[v]) * sign;
                        frame.layerDeltas.Add(deltas);
                    }
                }
            }
        }
    }
}
