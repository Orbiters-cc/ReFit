using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    // Optimistic concurrency check: never apply an asynchronous result over newer user edits.
    internal sealed class ReFitInputState
    {
        private readonly Dictionary<Object, string> objects = new Dictionary<Object, string>();
        private readonly Dictionary<Mesh, int> meshRevisions = new Dictionary<Mesh, int>();

        public ReFitInputState(ReFitRequest request)
        {
            if (request == null) return;
            Capture(request.sourceAvatar);
            Capture(request.targetAvatar);
            if (request.assetRenderer != null) Capture(request.assetRenderer.transform.root.gameObject);
        }

        private void Capture(GameObject root)
        {
            if (root == null) return;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                Remember(transform);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Remember(renderer);
                var mesh = renderer.sharedMesh;
                if (mesh != null && !meshRevisions.ContainsKey(mesh))
                    meshRevisions.Add(mesh, EditorUtility.GetDirtyCount(mesh));
                foreach (var bone in renderer.bones)
                    for (var current = bone; current != null; current = current.parent) Remember(current);
                Remember(renderer.GetComponent<ReFitGeneratedAssetMetadata>());
            }
        }

        private void Remember(Object obj)
        {
            if (obj != null && !objects.ContainsKey(obj)) objects.Add(obj, EditorJsonUtility.ToJson(obj));
        }

        public bool Unchanged()
        {
            // Native mesh edits increment the revision even when vertex/triangle counts stay equal.
            // Do not serialize every body blendshape twice around each asynchronous run.
            foreach (var entry in meshRevisions)
                if (entry.Key == null || EditorUtility.GetDirtyCount(entry.Key) != entry.Value) return false;
            foreach (var entry in objects)
                if (entry.Key == null || EditorJsonUtility.ToJson(entry.Key) != entry.Value) return false;
            return true;
        }
    }
}
