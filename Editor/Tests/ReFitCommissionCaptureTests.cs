using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitCommissionCaptureTests
    {
        // Requires a graphics-capable editor; intentionally separate from headless geometry tests.
        [MenuItem("Tools/Orbiters/ReFit/Run Commission Capture Tests")]
        public static void RunOrThrow()
        {
            var avatar = new GameObject("Capture test avatar") { hideFlags = HideFlags.HideAndDontSave };
            Mesh mesh = null;
            Material material = null;
            var previousSelection = Selection.activeObject;
            var previousRenderTarget = RenderTexture.active;
            try
            {
                avatar.AddComponent<Animator>();
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                body.transform.SetParent(avatar.transform, false);
                body.transform.localPosition = new Vector3(0, 2, 0);
                body.transform.localScale = new Vector3(1, 2, 0.6f);
                material = new Material(Shader.Find("Unlit/Color")) { color = Color.green };
                body.GetComponent<Renderer>().sharedMaterial = material;
                var accessory = new GameObject("Capture test accessory");
                accessory.transform.SetParent(avatar.transform, false);
                accessory.AddComponent<Animator>();
                var bone = new GameObject("Bone").transform;
                bone.SetParent(accessory.transform, false);
                mesh = Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);
                mesh.bindposes = new[] { Matrix4x4.identity };
                mesh.boneWeights = Enumerable.Range(0, mesh.vertexCount).Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
                var deltas = Enumerable.Repeat(Vector3.right * 0.4f, mesh.vertexCount).ToArray();
                mesh.AddBlendShapeFrame("refit", 100, deltas, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
                var renderer = accessory.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh; renderer.sharedMaterial = material; renderer.bones = new[] { bone }; renderer.rootBone = bone;
                renderer.SetBlendShapeWeight(0, 0);
                var matrix = renderer.localToWorldMatrix;
                var children = avatar.GetComponentsInChildren<Transform>().Length;
                var photos = ReFitCommissionCapture.Capture(null, renderer, "refit");
                Require(photos.Count == 4 && photos.Select(p => p.name).Distinct().Count() == 4, "Four distinct named views are required.");
                Require(photos.Select(p => Convert.ToBase64String(p.bytes)).Distinct().Count() == 4, "Views must not be duplicates.");
                foreach (var photo in photos)
                {
                    var image = new Texture2D(2, 2);
                    try
                    {
                        Require(image.LoadImage(photo.bytes) && image.width == 1024 && image.height == 1024, "Preview must decode at 1024 square.");
                        var pixels = image.GetPixels32();
                        Require(pixels.Count(p => p.g > p.r + 30 && p.g > p.b + 30) > 50000, "Avatar and accessory must visibly render.");
                    }
                    finally { Object.DestroyImmediate(image); }
                }
                // Supplying the enclosing avatar explicitly must match automatic resolution past the accessory Animator.
                var explicitPhotos = ReFitCommissionCapture.Capture(avatar, renderer, "refit");
                Require(photos.Zip(explicitPhotos, (a, b) => a.bytes.SequenceEqual(b.bytes)).All(same => same), "Nested accessory Animator must not hide the avatar.");
                var withoutRefit = ReFitCommissionCapture.Capture(avatar, renderer, null);
                Require(!photos[0].bytes.SequenceEqual(withoutRefit[0].bytes), "Refit must be enabled in the captured copy even when disabled in the scene.");
                Require(renderer.GetBlendShapeWeight(0) == 0 && renderer.localToWorldMatrix == matrix, "Capture changed source renderer state.");
                Require(children == avatar.GetComponentsInChildren<Transform>().Length, "Capture leaked temporary scene objects.");
                Require(Selection.activeObject == previousSelection && RenderTexture.active == previousRenderTarget, "Capture changed editor selection or render target.");
                Debug.Log("[ReFit] Commission capture tests passed: four visible views, avatar fallback, immutable source, cleanup.");
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                if (mesh != null) Object.DestroyImmediate(mesh);
                if (material != null) Object.DestroyImmediate(material);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
