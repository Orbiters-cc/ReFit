using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    internal sealed class ReFitCommissionPhoto
    {
        public string name;
        public byte[] bytes;
    }

    internal static class ReFitCommissionCapture
    {
        private sealed class Part
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material[] materials;
        }

        // Only renderer data is copied; avatar scripts, constraints and physics never run in the preview.
        internal static List<ReFitCommissionPhoto> Capture(GameObject avatar, SkinnedMeshRenderer accessory, string primaryShape)
        {
            if (accessory == null || accessory.sharedMesh == null)
                throw new InvalidOperationException("The refitted accessory is no longer available in the scene.");
            if (avatar == null || !avatar.scene.IsValid())
            {
                var animators = accessory.GetComponentsInParent<Animator>();
                avatar = animators.Length > 0 ? animators[animators.Length - 1].gameObject : accessory.transform.root.gameObject;
            }
            var renderers = new HashSet<Renderer>(avatar.GetComponentsInChildren<Renderer>(false));
            renderers.Add(accessory);
            var parts = new List<Part>();
            var preview = new PreviewRenderUtility();
            var photos = new List<ReFitCommissionPhoto>();
            try
            {
                Bounds bounds = new Bounds();
                bool hasBounds = false;
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    bool debug = false;
                    for (var parent = renderer.transform; parent != null; parent = parent.parent)
                        if (parent.name.StartsWith("__ReFit_Debug_Session_")) { debug = true; break; }
                    if (debug) continue;
                    Mesh mesh = null;
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null && skinned.sharedMesh != null)
                    {
                        var temporary = new GameObject("ReFit capture renderer") { hideFlags = HideFlags.HideAndDontSave };
                        try
                        {
                            // Matching the original parent preserves nonuniform scale and shear during skinning.
                            temporary.transform.SetParent(skinned.transform.parent, false);
                            temporary.transform.localPosition = skinned.transform.localPosition;
                            temporary.transform.localRotation = skinned.transform.localRotation;
                            temporary.transform.localScale = skinned.transform.localScale;
                            var copy = temporary.AddComponent<SkinnedMeshRenderer>();
                            copy.sharedMesh = skinned.sharedMesh;
                            copy.bones = skinned.bones;
                            copy.rootBone = skinned.rootBone;
                            for (int shape = 0; shape < copy.sharedMesh.blendShapeCount; shape++)
                                copy.SetBlendShapeWeight(shape, skinned.GetBlendShapeWeight(shape));
                            int index = skinned == accessory && !string.IsNullOrEmpty(primaryShape)
                                ? copy.sharedMesh.GetBlendShapeIndex(primaryShape) : -1;
                            if (index >= 0) copy.SetBlendShapeWeight(index, 100f);
                            mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                            copy.BakeMesh(mesh);
                        }
                        catch { if (mesh != null) Object.DestroyImmediate(mesh); throw; }
                        finally { Object.DestroyImmediate(temporary); }
                    }
                    else if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null) mesh = Object.Instantiate(filter.sharedMesh);
                    }
                    if (mesh == null) continue;
                    var matrix = renderer.localToWorldMatrix;
                    parts.Add(new Part { mesh = mesh, matrix = matrix, materials = renderer.sharedMaterials });
                    foreach (var vertex in mesh.vertices)
                    {
                        Vector3 point = matrix.MultiplyPoint3x4(vertex);
                        if (!hasBounds) { bounds = new Bounds(point, Vector3.zero); hasBounds = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                if (!hasBounds || bounds.size.sqrMagnitude < 0.000001f)
                    throw new InvalidOperationException("No visible avatar geometry was found for the commission previews.");

                preview.camera.orthographic = true;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(0.18f, 0.19f, 0.21f, 1f);
                preview.ambientColor = new Color(0.65f, 0.65f, 0.65f);
                preview.lights[0].intensity = 1.2f;
                preview.lights[1].intensity = 0.7f;
                var directions = new[] { Vector3.forward, new Vector3(1, 0, 1).normalized, Vector3.right, new Vector3(0, 1.5f, 1).normalized };
                var names = new[] { "front", "three-quarter", "side", "elevated" };
                float radius = bounds.extents.magnitude;
                for (int view = 0; view < directions.Length; view++)
                {
                    Vector3 direction = avatar.transform.rotation * directions[view];
                    preview.camera.transform.position = bounds.center + direction * (radius * 3f + 1f);
                    preview.camera.transform.LookAt(bounds.center, avatar.transform.up);
                    float halfExtent = 0f;
                    foreach (var part in parts)
                        foreach (var vertex in part.mesh.vertices)
                        {
                            Vector3 point = preview.camera.transform.InverseTransformPoint(part.matrix.MultiplyPoint3x4(vertex));
                            halfExtent = Mathf.Max(halfExtent, Mathf.Abs(point.x), Mathf.Abs(point.y));
                        }
                    preview.camera.orthographicSize = halfExtent * 1.08f;
                    preview.camera.nearClipPlane = 0.01f;
                    preview.camera.farClipPlane = radius * 8f + 10f;
                    preview.lights[0].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                    preview.lights[1].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                    preview.BeginPreview(new Rect(0, 0, 1024, 1024), GUIStyle.none);
                    foreach (var part in parts)
                        for (int submesh = 0; submesh < part.mesh.subMeshCount; submesh++)
                            if (part.materials.Length > 0 && part.materials[Mathf.Min(submesh, part.materials.Length - 1)] != null)
                                preview.DrawMesh(part.mesh, part.matrix, part.materials[Mathf.Min(submesh, part.materials.Length - 1)], submesh);
                    preview.Render(true);
                    var render = (RenderTexture)preview.EndPreview();
                    var previous = RenderTexture.active;
                    var image = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
                    try
                    {
                        RenderTexture.active = render;
                        image.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
                        image.Apply();
                        photos.Add(new ReFitCommissionPhoto { name = "refit-" + names[view] + ".jpg", bytes = image.EncodeToJPG(85) });
                    }
                    finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                }
                return photos;
            }
            finally
            {
                preview.Cleanup();
                foreach (var part in parts) Object.DestroyImmediate(part.mesh);
            }
        }
    }
}
