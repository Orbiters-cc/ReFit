using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>Opt-in private fixture replay. Copies transforms only and never changes the user's avatar.</summary>
    public static class ReFitShortsValidation
    {
        public const string Assets = "Assets/__ReFitShortsQA/";
        public const string Output = "Temp/ReFitTests/shorts/";

        public static string Run(string label, string garment = "OriginalShorts", bool render = true, bool coverage = true, float legSpread = 0)
        {
            bool winterpaw = garment == "Hoodie" || garment == "Glowsticks";
            var bodyPath = winterpaw || garment == "Tanktop" ? Assets + "WinterpawTarget.fbx" : "Assets/@ULTIREX V5/Ultirex_V5_beta.fbx";
            var roots = new List<GameObject>();
            var owned = new List<Mesh>();
            Directory.CreateDirectory(Output);
            try
            {
                var body = CopyModel(bodyPath, "Body", roots);
                var clothing = CopyModel(Assets + garment + ".fbx", null, roots);
                clothing.transform.root.SetParent(body.transform.root, true);
                var originalClothingMesh = clothing.sharedMesh;
                var rotations = new Dictionary<Transform, Quaternion>();
                if (legSpread != 0)
                    foreach (var rig in roots.ToArray())
                    {
                        var map = HumanoidBoneMapper.GetHumanoidMap(rig, new ReFitReport());
                        foreach (var key in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
                        {
                            if (!map.TryGetValue(key, out var bone) || bone == null || rotations.ContainsKey(bone)) continue;
                            rotations[bone] = bone.localRotation;
                            float side = Mathf.Sign(Vector3.Dot(bone.position - body.transform.root.position, body.transform.root.right));
                            bone.rotation = Quaternion.AngleAxis(side * legSpread, body.transform.root.forward) * bone.rotation;
                        }
                    }
                var spreadSnapshot = legSpread != 0 ? MeshSnapshot.Capture(clothing, true, null, null) : null;
                if (legSpread != 0 && rotations.Count != 4)
                    throw new InvalidOperationException("Star-pose replay requires both upper legs on both independent copied rigs.");
                var shapes = winterpaw ? new[] { "orbit muscles" } : garment == "Tanktop" ? new[] { "Belly" } : new[] { "MuscleOrbit", "bodybuilder" };
                var request = new ReFitRequest
                {
                    mode = ReFitMode.Blendshape, assetRenderer = clothing,
                    targetAvatar = body.transform.root.gameObject, targetBodyRenderer = body,
                    targetBlendshapes = shapes.ToList(),
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false, captureProjectionDebug = true, maxProjectionDebugGroups = 0, preserveLowerBodyCoverage = coverage }
                };
                if (winterpaw)
                {
                    var source = CopyModel(Assets + "WinterpawSource.fbx", "Body", roots);
                    request.mode = ReFitMode.MeshAndBlendshape;
                    request.sourceAvatar = source.transform.root.gameObject;
                    request.sourceBodyRenderer = source;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var computation = new ReFitEngine().Run(request);
                watch.Stop();
                if (!computation.success) throw new InvalidOperationException(string.Join("\n", computation.report.messages));
                owned.Add(computation.mesh);
                if (spreadSnapshot != null)
                {
                    var converted = Object.Instantiate(originalClothingMesh); owned.Add(converted);
                    for (int s = originalClothingMesh.blendShapeCount; s < computation.mesh.blendShapeCount; s++)
                        for (int f = 0; f < computation.mesh.GetBlendShapeFrameCount(s); f++)
                        {
                            var delta = new Vector3[converted.vertexCount];
                            computation.mesh.GetBlendShapeFrameVertices(s, f, delta, null, null);
                            for (int i = 0; i < delta.Length; i++)
                                delta[i] = spreadSnapshot.skinMatrices[i].inverse.MultiplyVector(clothing.transform.localToWorldMatrix.MultiplyVector(delta[i]));
                            converted.AddBlendShapeFrame(computation.mesh.GetBlendShapeName(s), computation.mesh.GetBlendShapeFrameWeight(s, f), delta, null, null);
                        }
                    computation.mesh = converted;
                    foreach (var pair in rotations) pair.Key.localRotation = pair.Value;
                }
                var lines = new List<string> { "garment=" + garment + ", engineMs=" + watch.ElapsedMilliseconds + ", legSpread=" + legSpread + ", posedBones=" + rotations.Count, string.Join("\n", computation.report.messages), computation.clearanceCorrectionStats?.Summary(label) };
                File.WriteAllText(Output + label + "-" + garment + "-projection.json", JsonUtility.ToJson(computation.projectionDebug));
                string resultPath = Assets + label + "-" + garment + ".asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(resultPath) != null) throw new InvalidOperationException("Use a new label; saved QA output already exists: " + resultPath);
                AssetDatabase.CreateAsset(Object.Instantiate(computation.mesh), resultPath);
                var bodyBase = Bake(body, body.sharedMesh, new Dictionary<string, float>()); owned.Add(bodyBase);
                var original = Bake(clothing, clothing.sharedMesh, new Dictionary<string, float>()); owned.Add(original);
                if (render) Render(label + "-" + garment + "-original", bodyBase, original);
                foreach (string shape in shapes.Concat(shapes.Length > 1 ? new[] { "combined" } : new string[0]))
                {
                    foreach (float weight in new[] { 25f, 50f, 75f, 100f })
                    {
                        var bodyWeights = shapes.ToDictionary(n => n, n => shape == "combined" || n == shape ? weight : 0f);
                        var clothWeights = bodyWeights.ToDictionary(p => "refit_" + p.Key, p => p.Value);
                        clothWeights["refit"] = 100f;
                        var bodyAt = Bake(body, body.sharedMesh, bodyWeights);
                        var clothAt = Bake(clothing, computation.mesh, clothWeights);
                        try
                        {
                            lines.Add(shape + "=" + weight + ": " + Measure(bodyBase, original, bodyAt, clothAt, weight == 100f));
                            if (render && weight == 100f) Render(label + "-" + garment + "-" + shape, bodyAt, clothAt);
                        }
                        finally { Object.DestroyImmediate(bodyAt); Object.DestroyImmediate(clothAt); }
                    }
                }
                File.WriteAllLines(Output + label + "-" + garment + ".txt", lines);
                return string.Join("\n", lines);
            }
            finally
            {
                foreach (var mesh in owned) Object.DestroyImmediate(mesh);
                foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            }
        }

        internal static SkinnedMeshRenderer CopyModel(string path, string rendererName, List<GameObject> roots)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null) throw new InvalidOperationException("Missing private fixture: " + path);
            var source = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => rendererName == null || r.name == rendererName);
            var map = new Dictionary<Transform, Transform>();
            var root = CopyTransforms(model.transform, null, map); roots.Add(root.gameObject);
            var renderer = map[source.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = source.sharedMesh;
            renderer.bones = source.bones.Select(b => b == null ? null : map[b]).ToArray();
            renderer.rootBone = source.rootBone == null ? null : map[source.rootBone];
            var animator = model.GetComponent<Animator>();
            if (animator != null) { var copy = root.gameObject.AddComponent<Animator>(); copy.enabled = false; copy.avatar = animator.avatar; }
            return renderer;
        }

        private static Transform CopyTransforms(Transform source, Transform parent, Dictionary<Transform, Transform> map)
        {
            var copy = new GameObject(source.name) { hideFlags = HideFlags.HideAndDontSave }.transform;
            copy.SetParent(parent, false); copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation; copy.localScale = source.localScale;
            map.Add(source, copy);
            foreach (Transform child in source) CopyTransforms(child, copy, map);
            return copy;
        }

        internal static Mesh Bake(SkinnedMeshRenderer source, Mesh mesh, Dictionary<string, float> weights)
        {
            var go = new GameObject("__ReFitShortsBake") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                go.transform.localScale = source.transform.lossyScale;
                var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                renderer.bones = source.bones; renderer.rootBone = source.rootBone;
                var overrides = Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(i => i, i => weights.TryGetValue(mesh.GetBlendShapeName(i), out float w) ? w / 100f : 0f);
                var snapshot = MeshSnapshot.Capture(renderer, false, overrides, null);
                var baked = Object.Instantiate(mesh); baked.ClearBlendShapes();
                baked.vertices = snapshot.worldVertices; baked.normals = snapshot.worldNormals; baked.RecalculateBounds();
                return baked;
            }
            finally { Object.DestroyImmediate(go); }
        }

        internal static string Measure(Mesh bodyBase, Mesh clothingBase, Mesh body, Mesh clothing, bool confirm = false)
        {
            var a = new MeshSnapshot { worldVertices = bodyBase.vertices, triangles = bodyBase.triangles };
            var b = new MeshSnapshot { worldVertices = body.vertices, triangles = body.triangles };
            var ia = SurfaceBvh.Build(a); var ib = SurfaceBvh.Build(b);
            var p = clothingBase.vertices; var q = clothing.vertices; var tris = clothing.triangles;
            int eligible = 0, inside = 0, preexisting = 0, confirmed = 0, alreadyInside = 0; float worst = 0;
            var details = new List<string>();
            for (int s = 0; s < p.Length + tris.Length / 3; s++)
            {
                int t = (s - p.Length) * 3;
                var x = s < p.Length ? p[s] : (p[tris[t]] + p[tris[t + 1]] + p[tris[t + 2]]) / 3f;
                var y = s < p.Length ? q[s] : (q[tris[t]] + q[tris[t + 1]] + q[tris[t + 2]]) / 3f;
                var ha = ia.ClosestPoint(x, .2f, null); var hb = ib.ClosestPoint(y, .2f, null);
                if (!ha.found || !hb.found) continue;
                float ga = Vector3.Dot(x - ha.position, a.FaceNormal(ha.triangle));
                float gb = Vector3.Dot(y - hb.position, b.FaceNormal(hb.triangle));
                if (ga < 0) { preexisting++; continue; }
                eligible++;
                if (gb < -.001f)
                {
                    inside++; worst = Mathf.Max(worst, -gb);
                    if (confirm)
                    {
                        double before = Math.Abs(ReFitMeshIntersectionChecks.Winding(a, x));
                        double after = Math.Abs(ReFitMeshIntersectionChecks.Winding(b, y));
                        if (before > .5) alreadyInside++; else if (after > .5) confirmed++;
                        if (details.Count < 12) details.Add($"sample={s}, position={y:F4}, gap={gb*1000:F3}mm, winding={before:F3}->{after:F3}");
                    }
                }
            }
            return $"exteriorBaseSamples={eligible}, penetratingCandidates={inside}, worstMm={worst * 1000:F3}, preexistingCandidates={preexisting}, volumeConfirmedNew={confirmed}, volumeAlreadyInside={alreadyInside}\n" + string.Join("\n", details);
        }

        internal static void Render(string label, Mesh body, Mesh clothing)
        {
            var preview = new PreviewRenderUtility();
            var skin = new Material(Shader.Find("Standard")) { color = new Color(.6f, .22f, .7f) };
            var cloth = new Material(Shader.Find("Standard")) { color = new Color(.12f, .65f, .7f) };
            try
            {
                preview.camera.orthographic = true; preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.11f, .12f, .14f); preview.ambientColor = new Color(.6f, .6f, .6f);
                preview.lights[0].intensity = 1.2f; preview.lights[1].intensity = .7f;
                var directions = new[] { Vector3.forward, new Vector3(1, .1f, 1).normalized, Vector3.back, Vector3.right };
                var names = new[] { "front", "quarter", "rear", "side" };
                for (int view = 0; view < names.Length; view++)
                {
                    var center = clothing.bounds.center;
                    preview.camera.transform.position = center + directions[view] * 4;
                    preview.camera.transform.LookAt(center, Vector3.up);
                    preview.camera.orthographicSize = Mathf.Max(clothing.bounds.extents.y, clothing.bounds.extents.x * .9f) * 1.3f;
                    preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 20;
                    preview.lights[0].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                    preview.lights[1].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                    preview.BeginPreview(new Rect(0, 0, 900, 900), GUIStyle.none);
                    for (int i = 0; i < body.subMeshCount; i++) preview.DrawMesh(body, Matrix4x4.identity, skin, i);
                    for (int i = 0; i < clothing.subMeshCount; i++) preview.DrawMesh(clothing, Matrix4x4.identity, cloth, i);
                    preview.Render(true); var render = (RenderTexture)preview.EndPreview();
                    var previous = RenderTexture.active; var image = new Texture2D(900, 900, TextureFormat.RGB24, false);
                    try { RenderTexture.active = render; image.ReadPixels(new Rect(0, 0, 900, 900), 0, 0); image.Apply(); File.WriteAllBytes(Output + label + "-" + names[view] + ".png", image.EncodeToPNG()); }
                    finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                }
            }
            finally { preview.Cleanup(); Object.DestroyImmediate(skin); Object.DestroyImmediate(cloth); }
        }
    }
}
