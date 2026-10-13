using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// Offscreen pictures of an avatar wearing an accessory, as it shows in the scene: the avatar's visible renderers and the
    /// accessory are baked as posed (only renderer data is copied; avatar scripts, constraints and physics never run), then
    /// drawn with their own materials by a preview camera. Used by the wizard's before/after stage and the commission photos.
    /// </summary>
    internal static class ReFitScenePicture
    {
        internal sealed class Part
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material[] materials;
            public bool accessory;
        }

        /// <summary>Baked parts of a picture; dispose to free the baked meshes.</summary>
        internal sealed class Scene : IDisposable
        {
            public readonly List<Part> parts = new List<Part>();
            /// <summary>Everything drawn, and the accessory alone, in world space.</summary>
            public Bounds bounds, accessoryBounds;
            public GameObject avatar;
            /// <summary>The avatar's facing and up when collected (views are relative to them, even once the avatar is gone).</summary>
            public Quaternion facing = Quaternion.identity;
            public Vector3 up = Vector3.up;

            public void Dispose()
            {
                foreach (var part in parts) if (part.mesh != null) Object.DestroyImmediate(part.mesh);
                parts.Clear();
            }
        }

        /// <summary>Where a camera looks from, relative to the avatar's facing.</summary>
        internal enum View { Front, ThreeQuarter, Side, Back, Elevated }

        internal static Vector3 Direction(View view)
        {
            switch (view)
            {
                case View.ThreeQuarter: return new Vector3(1, 0.12f, 1).normalized;
                case View.Side: return Vector3.right;
                case View.Back: return new Vector3(0, 0.08f, -1).normalized;
                case View.Elevated: return new Vector3(0, 1.5f, 1).normalized;
                default: return new Vector3(0, 0.06f, 1).normalized;
            }
        }

        /// <summary>
        /// Bakes the avatar's visible renderers and <paramref name="accessory"/> (even when it lives elsewhere), the
        /// accessory with <paramref name="primaryShape"/> at 100 when it has it. A missing <paramref name="avatar"/> falls
        /// back to the accessory's outermost animator or root.
        /// </summary>
        internal static Scene Collect(GameObject avatar, SkinnedMeshRenderer accessory, string primaryShape)
        {
            if (accessory == null || accessory.sharedMesh == null)
                throw new InvalidOperationException("The accessory is no longer available in the scene.");
            if (avatar == null || !avatar.scene.IsValid())
            {
                var animators = accessory.GetComponentsInParent<Animator>();
                avatar = animators.Length > 0 ? animators[animators.Length - 1].gameObject : accessory.transform.root.gameObject;
            }
            var renderers = new List<Renderer>(avatar.GetComponentsInChildren<Renderer>(false));
            if (!renderers.Contains(accessory)) renderers.Add(accessory);
            var scene = new Scene { avatar = avatar, facing = avatar.transform.rotation, up = avatar.transform.up };
            bool hasBounds = false, hasAccessory = false;
            try
            {
                foreach (var renderer in renderers)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy && renderer != accessory) continue;
                    if (IsDebugCopy(renderer.transform)) continue;
                    var mesh = Bake(renderer, renderer == accessory ? primaryShape : null, out var matrix);
                    if (mesh == null) continue;
                    scene.parts.Add(new Part { mesh = mesh, matrix = matrix, materials = renderer.sharedMaterials, accessory = renderer == accessory });
                    foreach (var vertex in mesh.vertices)
                    {
                        Vector3 point = matrix.MultiplyPoint3x4(vertex);
                        if (!hasBounds) { scene.bounds = new Bounds(point, Vector3.zero); hasBounds = true; }
                        else scene.bounds.Encapsulate(point);
                        if (renderer != accessory) continue;
                        if (!hasAccessory) { scene.accessoryBounds = new Bounds(point, Vector3.zero); hasAccessory = true; }
                        else scene.accessoryBounds.Encapsulate(point);
                    }
                }
                if (!hasBounds || scene.bounds.size.sqrMagnitude < 0.000001f)
                    throw new InvalidOperationException("No visible avatar geometry was found to picture.");
                if (!hasAccessory) scene.accessoryBounds = scene.bounds;
                return scene;
            }
            catch
            {
                scene.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Draws <paramref name="scene"/> from <paramref name="localDirection"/> (where the camera stands, relative to the
        /// avatar's facing): an orthographic camera framing the accessory (or everything) with <paramref name="margin"/> around
        /// it. <paramref name="toDisplay"/> converts a linear project's colours for display in UI images.
        /// </summary>
        internal static Texture2D Render(Scene scene, Vector3 localDirection, bool accessoryOnly, int width, int height, float margin,
            Color background, bool toDisplay)
        {
            var focus = accessoryOnly ? scene.accessoryBounds : scene.bounds;
            var preview = new PreviewRenderUtility();
            var fallback = Fallback();
            try
            {
                var camera = preview.camera;
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                preview.ambientColor = new Color(0.62f, 0.62f, 0.64f);
                preview.lights[0].intensity = 1.25f;
                preview.lights[1].intensity = 0.75f;
                var facing = scene.facing;
                var up = scene.up;
                Vector3 direction = facing * localDirection.normalized;
                float radius = scene.bounds.extents.magnitude;
                var center = focus.center;
                camera.transform.position = center + direction * (radius * 3f + 1f);
                camera.transform.LookAt(center, up);
                // The focused geometry's extent as seen by this camera, so it fills the frame from every side.
                float halfWidth = 0f, halfHeight = 0f;
                foreach (var part in scene.parts)
                {
                    if (accessoryOnly && !part.accessory) continue;
                    foreach (var vertex in part.mesh.vertices)
                    {
                        var local = camera.transform.InverseTransformPoint(part.matrix.MultiplyPoint3x4(vertex));
                        halfWidth = Mathf.Max(halfWidth, Mathf.Abs(local.x));
                        halfHeight = Mathf.Max(halfHeight, Mathf.Abs(local.y));
                    }
                }
                float aspect = width / (float)height;
                camera.orthographicSize = Mathf.Max(0.02f, Mathf.Max(halfHeight, halfWidth / aspect) * margin);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = radius * 8f + 10f;
                preview.lights[0].transform.rotation = camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                preview.lights[1].transform.rotation = camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                preview.BeginPreview(new Rect(0, 0, width, height), GUIStyle.none);
                // BeginPreview clears with its own colour: ours is set after it.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                foreach (var part in scene.parts) Draw(preview, part, fallback);
                preview.Render(true);
                var render = (RenderTexture)preview.EndPreview();
                var previous = RenderTexture.active;
                var image = new Texture2D(width, height, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    RenderTexture.active = render;
                    image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    // A linear project renders linear values into the preview texture: shown as they are, everything looks
                    // far too dark. Converted to sRGB like the Game view does.
                    if (toDisplay && QualitySettings.activeColorSpace == ColorSpace.Linear && !render.sRGB)
                    {
                        var pixels = image.GetPixels();
                        for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
                        image.SetPixels(pixels);
                    }
                    image.Apply();
                }
                finally { RenderTexture.active = previous; }
                return image;
            }
            finally
            {
                preview.Cleanup();
                if (fallback != null) Object.DestroyImmediate(fallback);
            }
        }

        /// <summary>
        /// Draws a part as its renderer does: each submesh with its material, and materials beyond the submeshes drawing the
        /// last submesh again (extra passes). A submesh without its material shows in <paramref name="fallback"/> (plain grey).
        /// </summary>
        internal static void Draw(PreviewRenderUtility preview, Part part, Material fallback)
        {
            if (part.mesh == null || part.mesh.subMeshCount == 0) return;
            int submeshes = part.mesh.subMeshCount;
            int materials = part.materials != null ? part.materials.Length : 0;
            for (int i = 0; i < Mathf.Max(submeshes, materials); i++)
            {
                var material = materials > 0 ? part.materials[Mathf.Min(i, materials - 1)] : null;
                if (material == null) material = fallback;
                if (material != null) preview.DrawMesh(part.mesh, part.matrix, material, Mathf.Min(i, submeshes - 1));
            }
        }

        /// <summary>The plain grey of a renderer without its material, or null when no shader can draw it.</summary>
        internal static Material Fallback()
        {
            var shader = Shader.Find("Standard");
            if (shader == null) return null;
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = new Color(0.62f, 0.63f, 0.66f) };
            material.SetFloat("_Glossiness", 0.2f);
            return material;
        }

        /// <summary>BakeMesh output to world: the bake already holds the fitted skeleton's scale.</summary>
        internal static Matrix4x4 CaptureMatrix(Renderer renderer) => renderer is SkinnedMeshRenderer skinned
            ? Orbiters.Toolkit.Editor.SkinnedMeshBounds.BakedToWorld(skinned) : renderer.localToWorldMatrix;

        private static bool IsDebugCopy(Transform transform)
        {
            for (var parent = transform; parent != null; parent = parent.parent)
                if (parent.name.StartsWith("__ReFit_Debug_Session_")) return true;
            return false;
        }

        // The renderer as posed. A skinned mesh is baked where it is (baking changes nothing); one shown with its refit shape
        // on is baked from a hidden copy beside the avatar, never inside it: an object added under the avatar wakes the tools
        // watching its hierarchy (Modular Avatar, VRCFury, NDMF previews).
        private static Mesh Bake(Renderer renderer, string primaryShape, out Matrix4x4 matrix)
        {
            matrix = renderer.localToWorldMatrix;
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                int index = !string.IsNullOrEmpty(primaryShape) ? skinned.sharedMesh.GetBlendShapeIndex(primaryShape) : -1;
                Mesh mesh = null;
                if (index < 0 || Mathf.Approximately(skinned.GetBlendShapeWeight(index), 100f))
                {
                    try
                    {
                        mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                        skinned.BakeMesh(mesh);
                        matrix = CaptureMatrix(skinned);
                        return mesh;
                    }
                    catch { if (mesh != null) Object.DestroyImmediate(mesh); throw; }
                }
                var temporary = new GameObject("ReFit picture renderer") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    if (skinned.gameObject.scene.IsValid() && temporary.scene != skinned.gameObject.scene)
                        SceneManager.MoveGameObjectToScene(temporary, skinned.gameObject.scene);
                    // The bones place a skinned mesh; a mesh without bones takes the renderer's place and size.
                    temporary.transform.SetPositionAndRotation(skinned.transform.position, skinned.transform.rotation);
                    temporary.transform.localScale = skinned.transform.lossyScale;
                    var copy = temporary.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = skinned.sharedMesh;
                    copy.bones = skinned.bones;
                    copy.rootBone = skinned.rootBone;
                    for (int shape = 0; shape < copy.sharedMesh.blendShapeCount; shape++)
                        copy.SetBlendShapeWeight(shape, skinned.GetBlendShapeWeight(shape));
                    copy.SetBlendShapeWeight(index, 100f);
                    mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    copy.BakeMesh(mesh);
                    matrix = CaptureMatrix(copy);
                    return mesh;
                }
                catch { if (mesh != null) Object.DestroyImmediate(mesh); throw; }
                finally { Object.DestroyImmediate(temporary); }
            }
            if (renderer is MeshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    var mesh = Object.Instantiate(filter.sharedMesh);
                    mesh.hideFlags = HideFlags.HideAndDontSave;
                    return mesh;
                }
            }
            return null;
        }
    }
}
