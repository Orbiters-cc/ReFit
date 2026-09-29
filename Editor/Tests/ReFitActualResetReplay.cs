using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        /// <summary>
        /// Explicit actual-asset reset replay. Reads the named scene avatar and source prefab, works only on copies in
        /// a preview scene, and writes diagnostic text/PNGs outside Assets. This measures reset, not cross-avatar fit quality.
        /// </summary>
        public static string RunActualRexouiumResetReplayOrThrow()
        {
            const string avatarName = "Rexouium1.6 Default Setup";
            const string prefabPath = "Assets/Hoodie/Hoodie Prefab.prefab";
            string output = Path.GetFullPath("Temp/ReFitTests/ActualReset");
            Directory.CreateDirectory(output);
            var source = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t => t.name == avatarName &&
                t.parent == null && t.gameObject.scene.IsValid() && !EditorSceneManager.IsPreviewScene(t.gameObject.scene));
            AssertTrue(source != null, "The specified Rexouium avatar is not in an open scene.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            AssertTrue(prefab != null, "The hoodie source prefab is unavailable.");
            var scene = EditorSceneManager.NewPreviewScene();
            var holder = new GameObject("ReFit actual reset replay");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, scene);
            ReFitComputation comp = null;
            ReFitRendererState state = null;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            try
            {
                var avatar = Object.Instantiate(source.gameObject, holder.transform, false);
                avatar.name = source.name;
                var body = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(r => r.sharedMesh != null && r.bones.Length > 0)
                    .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                AssertTrue(body != null, "The avatar copy has no skinned body.");
                // Replacement deliberately does not run when sourceAvatar and targetAvatar are the same object.
                // Use a second copy of the same real avatar to exercise replacement without introducing fit-quality
                // differences; keep that reference copy hidden from the diagnostic photos.
                var referenceAvatar = Object.Instantiate(source.gameObject, holder.transform, false);
                referenceAvatar.name = source.name + " (source reference)";
                referenceAvatar.SetActive(false);
                string bodyPath = AnimationUtility.CalculateTransformPath(body.transform, avatar.transform);
                var referenceBodyTransform = string.IsNullOrEmpty(bodyPath) ? referenceAvatar.transform : referenceAvatar.transform.Find(bodyPath);
                var referenceBody = referenceBodyTransform != null ? referenceBodyTransform.GetComponent<SkinnedMeshRenderer>() : null;
                AssertTrue(referenceBody != null, "The source reference copy has no corresponding body renderer.");
                AssertTrue(body.sharedMesh.blendShapeCount > 0, "The avatar body has no blendshape for the combined reset replay.");
                var bodyShapes = Enumerable.Range(0, body.sharedMesh.blendShapeCount).Select(body.sharedMesh.GetBlendShapeName).ToArray();
                string targetShape = bodyShapes.FirstOrDefault(name => name.IndexOf("belly", StringComparison.OrdinalIgnoreCase) >= 0) ?? bodyShapes[0];
                var clothing = Object.Instantiate(prefab, avatar.transform, false);
                clothing.name = prefab.name;
                var renderer = clothing.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(r => r.sharedMesh != null && r.bones.Length > 0)
                    .OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
                AssertTrue(renderer != null, "The hoodie prefab has no skinned renderer.");
                AssertTrue(renderer.GetComponent<ReFitGeneratedAssetMetadata>() == null &&
                           !AssetDatabase.GetAssetPath(renderer.sharedMesh).StartsWith("Assets/ReFit/", StringComparison.Ordinal),
                    "The hoodie source is already generated; this replay requires its original source mesh.");
                foreach (var behaviour in holder.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                holder.SetActive(true);
                AssertTrue(renderer.transform.IsChildOf(avatar.transform), "The replay lost its target-nested semantic hierarchy.");
                var originalMesh = renderer.sharedMesh;
                var originalBones = renderer.bones.Select(b => ActualPath(clothing.transform, b)).ToArray();
                string originalRoot = ActualPath(clothing.transform, renderer.rootBone);
                var originalHierarchy = clothing.GetComponentsInChildren<Transform>(true).ToDictionary(t => ActualPath(clothing.transform, t),
                    t => (position: t.localPosition, rotation: t.localRotation, scale: t.localScale));
                string[] references = ActualReferences(clothing);
                WriteActualDiagnostics(output, "00_input", clothing, renderer);
                CaptureActualResetViews(output, "00_input", avatar, renderer, scene);
                var request = new ReFitRequest
                {
                    mode = ReFitMode.MeshAndBlendshape, assetRenderer = renderer,
                    sourceAvatar = referenceAvatar, targetAvatar = avatar, sourceBodyRenderer = referenceBody, targetBodyRenderer = body,
                    targetBlendshape = targetShape,
                    settings = new ReFitSettings { replaceArmature = true, transferWeights = true, savePrefab = false }
                };
                comp = new ReFitEngine().Run(request);
                File.WriteAllLines(Path.Combine(output, "computation.txt"), new[] { "Target body blendshape=" + targetShape }
                    .Concat(comp.report.messages.Select(m => m.code + ": " + m.text)));
                AssertComputationSucceeded(comp);
                AssertTrue(comp.armatureReplaced, "The computation did not exercise armature replacement.");
                var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report, out state);
                AssertTrue(applied == renderer && state != null, "Apply did not return the copied renderer and reset state.");
                WriteActualDiagnostics(output, "03_armature_replaced", clothing, renderer);
                CaptureActualResetViews(output, "03_armature_replaced", avatar, renderer, scene);
                AssertTrue(state.Restore(renderer, "ReFit actual reset replay"), "Reset failed.");
                WriteActualDiagnostics(output, "06_reset", clothing, renderer);
                CaptureActualResetViews(output, "06_reset", avatar, renderer, scene);
                AssertTrue(renderer.sharedMesh == originalMesh, "Reset did not restore the original mesh reference.");
                AssertTrue(renderer.bones.Select(b => ActualPath(clothing.transform, b)).SequenceEqual(originalBones), "Reset changed the original skin bindings.");
                AssertTrue(ActualPath(clothing.transform, renderer.rootBone) == originalRoot, "Reset changed the root bone.");
                var restored = clothing.GetComponentsInChildren<Transform>(true).ToDictionary(t => ActualPath(clothing.transform, t));
                AssertTrue(restored.Keys.OrderBy(p => p).SequenceEqual(originalHierarchy.Keys.OrderBy(p => p)), "Reset left missing or extra hierarchy objects.");
                foreach (var pair in originalHierarchy)
                {
                    var t = restored[pair.Key];
                    AssertTrue(Vector3.Distance(t.localPosition, pair.Value.position) < 0.0001f &&
                               Quaternion.Angle(t.localRotation, pair.Value.rotation) < 0.01f &&
                               Vector3.Distance(t.localScale, pair.Value.scale) < 0.0001f, "Reset changed local transform " + pair.Key);
                }
                AssertTrue(ActualReferences(clothing).SequenceEqual(references), "Reset changed serialized bone/object references. Compare diagnostic files.");
                string result = "PASS actual reset: " + avatarName + " / " + prefabPath + "; " + originalBones.Length +
                    " skin bones, " + originalHierarchy.Count + " hierarchy objects, " + references.Length + " serialized references. " + output;
                File.WriteAllText(Path.Combine(output, "result.txt"), result);
                Debug.Log(result);
                return result;
            }
            finally
            {
                state?.DiscardRemoved();
                Undo.RevertAllDownToGroup(undoGroup);
                if (comp != null) DestroyComputationMesh(comp);
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static string ActualPath(Transform root, Transform t) => t == null ? "<null>" :
            t.IsChildOf(root) ? AnimationUtility.CalculateTransformPath(t, root) : "<external>" + AnimationUtility.CalculateTransformPath(t, null);

        private static string[] ActualReferences(GameObject clothing)
        {
            var result = new List<string>();
            foreach (var component in clothing.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var p = serialized.GetIterator();
                    while (p.Next(true))
                    {
                        if (p.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var t = p.objectReferenceValue is GameObject go ? go.transform : p.objectReferenceValue as Transform;
                        if (t == null) continue;
                        result.Add(ActualPath(clothing.transform, component.transform) + "|" + component.GetType().FullName + "|" +
                                   p.propertyPath + "=" + ActualPath(clothing.transform, t));
                    }
                }
            }
            return result.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }

        private static void WriteActualDiagnostics(string output, string stage, GameObject clothing, SkinnedMeshRenderer renderer)
        {
            var lines = new List<string> { "Renderer=" + ActualPath(clothing.transform, renderer.transform),
                "Mesh=" + AssetDatabase.GetAssetPath(renderer.sharedMesh), "Root=" + ActualPath(clothing.transform, renderer.rootBone) };
            foreach (var t in clothing.GetComponentsInChildren<Transform>(true))
                lines.Add("Hierarchy " + ActualPath(clothing.transform, t) + " pos=" + t.localPosition.ToString("F6") +
                          " rot=" + t.localRotation.eulerAngles.ToString("F3") + " scale=" + t.localScale.ToString("F6"));
            var weights = renderer.sharedMesh.GetAllBoneWeights();
            for (int i = 0; i < renderer.bones.Length; i++)
            {
                double total = 0; int count = 0;
                foreach (var weight in weights) if (weight.boneIndex == i && weight.weight > 0) { total += weight.weight; count++; }
                lines.Add("Skin " + i + " " + ActualPath(clothing.transform, renderer.bones[i]) + " weight=" + total.ToString("F6") + " vertices=" + count);
            }
            lines.AddRange(ActualReferences(clothing).Select(s => "Reference " + s));
            File.WriteAllLines(Path.Combine(output, stage + ".txt"), lines);
        }

        private static void CaptureActualResetViews(string output, string stage, GameObject avatar, SkinnedMeshRenderer clothing, Scene scene)
        {
            var cameraObject = new GameObject("Reset replay camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; camera.scene = scene; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.13f, 0.13f, 0.13f); camera.fieldOfView = 35f;
            var light = cameraObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.1f;
            var rt = new RenderTexture(768, 768, 24);
            var image = new Texture2D(768, 768, TextureFormat.RGB24, false);
            var oldTarget = RenderTexture.active;
            try
            {
                var bounds = clothing.bounds;
                float distance = Mathf.Max(bounds.extents.magnitude * 3.8f, 1f);
                camera.targetTexture = rt;
                foreach (float yaw in new[] { 0f, 55f, 180f })
                {
                    var direction = Quaternion.AngleAxis(yaw, Vector3.up) * avatar.transform.forward;
                    camera.transform.position = bounds.center + direction * distance;
                    camera.transform.LookAt(bounds.center);
                    camera.Render();
                    RenderTexture.active = rt;
                    image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); image.Apply();
                    File.WriteAllBytes(Path.Combine(output, stage + "_" + yaw + ".png"), image.EncodeToPNG());
                }
            }
            finally
            {
                RenderTexture.active = oldTarget;
                Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(rt); Object.DestroyImmediate(image);
            }
        }
    }
}
