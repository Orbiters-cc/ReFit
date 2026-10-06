using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>Opt-in private-asset verification. Renders only copied mesh data; never applies a refit to the live scene.</summary>
    public static class ReFitAccessoryValidation
    {
        public const string Output = "Temp/ReFitTests/accessories";
        public static void RunGlowsticks() => Run("Glowsticks Combined", "glowsticks");
        public static void RunHoodie() => Run("Hoodie", "hoodie");

#if REFIT_VRCHAT_AVATARS
        public static void RunHoodieControl(string label)
        {
            var roots = new List<GameObject>(); var owned = new List<Mesh>();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            int undo = Undo.GetCurrentGroup(); Undo.IncrementCurrentGroup(); undo = Undo.GetCurrentGroup();
            var clothMaterial = new Material(Shader.Find("Standard")) { color = new Color(.3f, .5f, .7f) };
            var bodyMaterial = new Material(Shader.Find("Standard")) { color = new Color(.7f, .7f, .7f) };
            Directory.CreateDirectory(Output);
            try
            {
                var source = ReFitShortsValidation.CopyModel("Assets/my custom winterpaw orbit/default_MasculineCanine.v1.5.fbx", "Body", roots);
                var target = ReFitShortsValidation.CopyModel("Assets/my custom winterpaw orbit/ulti paw v2.8.fbx", "Body", roots);
                var hoodie = ReFitShortsValidation.CopyModel("Assets/Hoodie/Model/Hoodie.fbx", "Hoodie", roots);
                foreach (var root in roots) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                var garment = hoodie.transform.root;
                garment.SetParent(target.transform.root, true);
                var attachment = garment.gameObject.AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
                attachment.body = target;
                Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentFit.Fit(attachment, target.transform.root);
                var request = new ReFitRequest { sourceAvatar = source.transform.root.gameObject, sourceBodyRenderer = source,
                    targetAvatar = target.transform.root.gameObject, targetBodyRenderer = target, assetRenderer = hoodie,
                    mode = ReFitMode.MeshAndBlendshape, targetBlendshapes = new List<string> { "orbit muscles" },
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false, prefixTransferredShapes = false } };
                ReFitSettingsPresets.ApplyTightness(request.settings, Orbiters.Toolkit.Editor.Refit.RefitPreferences.Tightness);
                var lines = new List<string>();
                foreach (bool enabled in new[] { false, true })
                {
                    request.settings.coverDifferentBaseBody = enabled;
                    var result = new ReFitEngine().Run(request);
                    if (!result.success) throw new InvalidOperationException("Hoodie control failed.");
                    owned.Add(result.mesh);
                    if (!result.mesh.boneWeights.SequenceEqual(hoodie.sharedMesh.boneWeights) || !result.mesh.triangles.SequenceEqual(hoodie.sharedMesh.triangles))
                        throw new InvalidOperationException("Hoodie topology or skin weights changed.");
                    foreach (float muscle in new[] { 0f, 50f, 100f })
                    {
                        var weights = new Dictionary<string, float> { { "refit", 100 }, { "orbit muscles", muscle }, { "Hood up", 100 } };
                        var body = Bake(target, target.sharedMesh, weights); owned.Add(body);
                        var input = Bake(hoodie, hoodie.sharedMesh, weights); owned.Add(input);
                        var output = Bake(hoodie, result.mesh, weights); owned.Add(output);
                        Render(label + (enabled ? "-on-" : "-off-") + muscle, body, new[] { bodyMaterial }, output, new[] { clothMaterial });
                        lines.Add(Quality((enabled ? "on-" : "off-") + muscle, input, output));
                    }
                }
                File.WriteAllLines(Output + "/" + label + ".txt", lines);
            }
            finally
            {
                Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(undo);
                foreach (var mesh in owned) if (mesh != null) Object.DestroyImmediate(mesh);
                foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
                Object.DestroyImmediate(clothMaterial); Object.DestroyImmediate(bodyMaterial);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        /// <summary>Exact pack replay, with recorded inputs under copied avatars and identical cameras for each variant.</summary>
        public static void RunOutfit(string label, bool coverage, bool allShapes = false)
        {
            var avatar = Resources.FindObjectsOfTypeAll<Orbiters.Toolkit.VRChat.OrbitersAttachment>().First(a =>
                !EditorUtility.IsPersistent(a) && a.transform.root.name == "MasculineCanine" && a.name.Contains("FishingOutfit"));
            var bodyRenderer = avatar.body;
            var owned = new List<Mesh>(); var materials = new List<Material[]>();
            var before = new List<Mesh>(); var previous = new List<Mesh>(); var after = new List<Mesh>();
            var generated = new Dictionary<SkinnedMeshRenderer, Mesh>();
            var lines = new List<string>();
            Directory.CreateDirectory(Output);
            var bodyWeights = Enumerable.Range(0, bodyRenderer.sharedMesh.blendShapeCount)
                .ToDictionary(i => bodyRenderer.sharedMesh.GetBlendShapeName(i), bodyRenderer.GetBlendShapeWeight);
            var body = Bake(bodyRenderer, bodyRenderer.sharedMesh, bodyWeights); owned.Add(body);
            try
            {
                foreach (var live in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
                    .OrderBy(Orbiters.Toolkit.Editor.VRChat.Refit.ClothingCoverage.Layer))
                {
                    var request = Request(live);
                    var root = OriginalInput(live, request, out var input); request.assetRenderer = input;
                    try
                    {
                        if (!input.transform.IsChildOf(request.targetAvatar.transform)) throw new InvalidOperationException("Lost target hierarchy.");
                        ReFitSettingsPresets.ApplyTightness(request.settings, Orbiters.Toolkit.Editor.Refit.RefitPreferences.Tightness);
                        request.settings.coverDifferentBaseBody = coverage;
                        request.targetBlendshapes = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>().shapes.Select(s => s.source)
                            .Where(s => allShapes || s == "orbit muscles" || (bodyWeights.TryGetValue(s, out var weight) && weight != 0)).ToList();
                        if (coverage)
                            foreach (var inner in generated.Where(pair => Orbiters.Toolkit.Editor.VRChat.Refit.ClothingCoverage.Layer(pair.Key) <
                                Orbiters.Toolkit.Editor.VRChat.Refit.ClothingCoverage.Layer(live)))
                            {
                                var transform = ReFitUtility.ResolvePath(root.transform, ReFitUtility.IndexPath(inner.Key.transform, avatar.transform.root));
                                var layer = transform.gameObject.AddComponent<SkinnedMeshRenderer>();
                                layer.sharedMesh = inner.Value;
                                layer.bones = inner.Key.bones.Select(b => b != null ? ReFitUtility.ResolvePath(root.transform, ReFitUtility.IndexPath(b, avatar.transform.root)) : null).ToArray();
                                layer.rootBone = inner.Key.rootBone != null ? ReFitUtility.ResolvePath(root.transform, ReFitUtility.IndexPath(inner.Key.rootBone, avatar.transform.root)) : null;
                                for (int s = 0; s < inner.Key.sharedMesh.blendShapeCount; s++)
                                {
                                    int index = layer.sharedMesh.GetBlendShapeIndex(inner.Key.sharedMesh.GetBlendShapeName(s));
                                    if (index >= 0) layer.SetBlendShapeWeight(index, inner.Key.GetBlendShapeWeight(s));
                                }
                                layer.SetBlendShapeWeight(layer.sharedMesh.GetBlendShapeIndex("refit"), 100);
                                request.coverageLayers.Add(layer);
                            }
                        var weights = Enumerable.Range(0, live.sharedMesh.blendShapeCount)
                            .ToDictionary(i => live.sharedMesh.GetBlendShapeName(i), live.GetBlendShapeWeight);
                        weights["refit"] = 100;
                        var a = Bake(input, input.sharedMesh, weights); owned.Add(a); before.Add(a);
                        var b = Bake(live, live.sharedMesh, weights); owned.Add(b); previous.Add(b);
                        var result = new ReFitEngine().Run(request);
                        if (!result.success) throw new InvalidOperationException(string.Join("\n", result.report.messages.Select(m => m.text)));
                        owned.Add(result.mesh);
                        generated.Add(live, result.mesh);
                        var c = Bake(input, result.mesh, weights); owned.Add(c); after.Add(c); materials.Add(live.sharedMaterials);
                        lines.Add(live.name + ": " + Quality(label, a, c));
                        lines.AddRange(result.report.messages.Where(m => m.code.Contains("coverage")).Select(m => m.text));
                        UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { result.mesh }, Output + "/" + label + "-" + live.name + ".asset", true);
                    }
                    finally
                    {
                        Object.DestroyImmediate(root);
                        if (request.sourceAvatar != null && !EditorUtility.IsPersistent(request.sourceAvatar)) Object.DestroyImmediate(request.sourceAvatar);
                    }
                }
                RenderOutfit(label + "-input", body, bodyRenderer.sharedMaterials, before, materials);
                RenderOutfit(label + "-previous", body, bodyRenderer.sharedMaterials, previous, materials);
                RenderOutfit(label, body, bodyRenderer.sharedMaterials, after, materials);
                File.WriteAllLines(Output + "/" + label + ".txt", lines);
            }
            finally { foreach (var mesh in owned) if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        private static void RenderOutfit(string name, Mesh body, Material[] bodyMaterials, List<Mesh> cloth, List<Material[]> materials)
        {
            var combined = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var parts = new List<CombineInstance>(); var mats = new List<Material>();
            for (int i = 0; i < cloth.Count; i++)
                for (int s = 0; s < cloth[i].subMeshCount; s++)
                {
                    parts.Add(new CombineInstance { mesh = cloth[i], subMeshIndex = s, transform = Matrix4x4.identity });
                    mats.Add(materials[i][Mathf.Min(s, materials[i].Length - 1)]);
                }
            try
            {
                combined.CombineMeshes(parts.ToArray(), false, false);
                Render(name, body, bodyMaterials, combined, mats.ToArray());
                // A second, neutral surface view exposes folds and intersections hidden by dark cloth textures.
                var clay = new Material(Shader.Find("Standard")) { color = new Color(.32f, .55f, .75f) };
                try { Render(name + "-surface", body, bodyMaterials, combined, new[] { clay }); }
                finally { Object.DestroyImmediate(clay); }
            }
            finally { Object.DestroyImmediate(combined); }
        }

        public static void RenderSavedOutfit(string label)
        {
            var attachment = Resources.FindObjectsOfTypeAll<Orbiters.Toolkit.VRChat.OrbitersAttachment>().First(a =>
                !EditorUtility.IsPersistent(a) && a.transform.root.name == "MasculineCanine" && a.name.Contains("FishingOutfit"));
            foreach (float muscle in new[] { 0f, 50f, 100f })
            {
                var meshes = new List<Mesh>(); var mats = new List<Material[]>(); var owned = new List<Object>();
                try
                {
                    var bodyWeights = Enumerable.Range(0, attachment.body.sharedMesh.blendShapeCount)
                        .ToDictionary(i => attachment.body.sharedMesh.GetBlendShapeName(i), attachment.body.GetBlendShapeWeight);
                    bodyWeights["orbit muscles"] = muscle;
                    var body = Bake(attachment.body, attachment.body.sharedMesh, bodyWeights); owned.Add(body);
                    foreach (var live in attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null))
                    {
                        var request = Request(live); var root = OriginalInput(live, request, out var input);
                        owned.Add(root); if (request.sourceAvatar != null && !EditorUtility.IsPersistent(request.sourceAvatar)) owned.Add(request.sourceAvatar);
                        var loaded = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(Output + "/" + label + "-" + live.name + ".asset");
                        owned.AddRange(loaded);
                        var values = Enumerable.Range(0, live.sharedMesh.blendShapeCount).ToDictionary(i => live.sharedMesh.GetBlendShapeName(i), live.GetBlendShapeWeight);
                        values["orbit muscles"] = muscle; values["refit"] = 100;
                        var baked = Bake(input, (Mesh)loaded[0], values); owned.Add(baked); meshes.Add(baked); mats.Add(live.sharedMaterials);
                    }
                    RenderOutfit(label + "-muscles-" + muscle, body, attachment.body.sharedMaterials, meshes, mats);
                }
                finally { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); }
            }
        }

        public static void RunCoverage(string rendererName = "Hoodie", bool allShapes = false)
        {
            var live = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().First(r =>
                !EditorUtility.IsPersistent(r) && r.name == rendererName && r.transform.root.name == "MasculineCanine");
            var request = Request(live);
            var root = OriginalInput(live, request, out var input); request.assetRenderer = input;
            var owned = new List<Mesh>(); var lines = new List<string>();
            if (allShapes) request.targetBlendshapes = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>().shapes.Select(s => s.source).ToList();
            Directory.CreateDirectory(Output);
            try
            {
                if (!input.transform.IsChildOf(request.targetAvatar.transform)) throw new InvalidOperationException("Lost target hierarchy.");
                ReFitSettingsPresets.ApplyTightness(request.settings, Orbiters.Toolkit.Editor.Refit.RefitPreferences.Tightness);
                var weights = new Dictionary<string, float> { { "Hood up", 100 }, { "refit", 100 } };
                var body = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, new Dictionary<string, float>()); owned.Add(body);
                var original = Bake(input, input.sharedMesh, weights); owned.Add(original);
                var previous = Bake(live, live.sharedMesh, weights); owned.Add(previous);
                Render("coverage-original", body, live.GetComponentInParent<Orbiters.Toolkit.VRChat.OrbitersAttachment>().body.sharedMaterials, original, live.sharedMaterials);
                Render("coverage-previous", body, request.targetBodyRenderer.sharedMaterials, previous, live.sharedMaterials);
                foreach (bool enabled in new[] { false, true })
                {
                    request.settings.coverDifferentBaseBody = enabled;
                    var computation = new ReFitEngine().Run(request);
                    if (!computation.success) throw new InvalidOperationException(string.Join("\n", computation.report.messages.Select(m => m.text)));
                    owned.Add(computation.mesh);
                    string label = enabled ? "coverage-new" : "coverage-normal";
                    foreach (float value in new[] { 0f, 50f, 100f })
                    {
                        weights["orbit muscles"] = value;
                        var cloth = Bake(input, computation.mesh, weights); owned.Add(cloth);
                        var skin = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, weights); owned.Add(skin);
                        Render(label + "-" + value, skin, request.targetBodyRenderer.sharedMaterials, cloth, input.sharedMaterials);
                        lines.Add(Quality(label + "-" + value, original, cloth));
                    }
                    foreach (var m in computation.report.messages.Where(m => m.code.Contains("coverage"))) lines.Add(m.text);
                    var currentWeights = Enumerable.Range(0, live.sharedMesh.blendShapeCount)
                        .ToDictionary(i => live.sharedMesh.GetBlendShapeName(i), live.GetBlendShapeWeight);
                    var currentCloth = Bake(input, computation.mesh, currentWeights); owned.Add(currentCloth);
                    var currentBody = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, currentWeights); owned.Add(currentBody);
                    Render(label + "-current", currentBody, request.targetBodyRenderer.sharedMaterials, currentCloth, input.sharedMaterials);
                    lines.Add(Quality(label + "-current", original, currentCloth));
                    lines.Add(label + "-current: " + ReFitShortsValidation.Measure(body, original, currentBody, currentCloth, true));
                    UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { computation.mesh }, Output + "/" + label + ".asset", true);
                }
                File.WriteAllLines(Output + "/coverage.txt", lines);
            }
            finally
            {
                foreach (var mesh in owned) if (mesh != null) Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(root);
                if (request.sourceAvatar != null && !EditorUtility.IsPersistent(request.sourceAvatar)) Object.DestroyImmediate(request.sourceAvatar);
            }
        }
#endif

        public static void BenchmarkGlowsticks()
        {
            var live = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().First(r =>
                !EditorUtility.IsPersistent(r) && r.name == "Glowsticks Combined" && r.transform.root.name == "MasculineCanine");
            var request = Request(live);
            var root = OriginalInput(live, request, out var input); request.assetRenderer = input;
            string[] names = { "orbit muscles", "orbit eyes", "orbit face", "orbit paws", "jawline", "goatee", "heavy cheek fluff" };
            var lines = new List<string>(); Mesh single = null;
            Directory.CreateDirectory(Output);
            try
            {
                foreach (int count in new[] { 1, 7 })
                    foreach (bool enabled in new[] { false, true, true, false })
                    {
                        request.targetBlendshapes = names.Take(count).ToList(); request.settings.preserveClosedTubes = enabled;
                        var watch = System.Diagnostics.Stopwatch.StartNew(); var result = new ReFitEngine().Run(request); watch.Stop();
                        try
                        {
                            if (!result.success || names.Take(count).Any(n => result.mesh.GetBlendShapeIndex(n) < 0))
                                throw new InvalidOperationException("Benchmark shape transfer failed.");
                            lines.Add($"shapes={count}, tube preservation={enabled}, engine={watch.Elapsed.TotalMilliseconds:F1}ms");
                            if (enabled && count == 1 && single == null) single = Object.Instantiate(result.mesh);
                            if (enabled && count == 7)
                            {
                                var a = new Vector3[single.vertexCount]; var b = new Vector3[single.vertexCount];
                                single.GetBlendShapeFrameVertices(single.GetBlendShapeIndex(names[0]), 0, a, null, null);
                                result.mesh.GetBlendShapeFrameVertices(result.mesh.GetBlendShapeIndex(names[0]), 0, b, null, null);
                                float worst = a.Zip(b, (x, y) => (x - y).magnitude).Max();
                                if (worst > .000001f) throw new InvalidOperationException("Batch transfer changed the muscle output.");
                                lines.Add($"PASS: single/batch muscle delta difference={worst * 1000:F6}mm");
                            }
                            File.WriteAllLines(Output + "/benchmark.txt", lines);
                        }
                        finally { if (result.mesh != null) Object.DestroyImmediate(result.mesh); }
                    }
                lines.Add("COMPLETE"); File.WriteAllLines(Output + "/benchmark.txt", lines);
            }
            finally { if (single != null) Object.DestroyImmediate(single); Object.DestroyImmediate(root); }
        }

        private static void Run(string rendererName, string label)
        {
            var renderer = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().FirstOrDefault(r =>
                !EditorUtility.IsPersistent(r) && r.name == rendererName && r.transform.root.name == "MasculineCanine");
            if (renderer == null) throw new InvalidOperationException("Private scene accessory is unavailable: " + rendererName);
            var request = Request(renderer);
            var inputRoot = OriginalInput(renderer, request, out var input);
            request.assetRenderer = input;
            var oldMesh = renderer.sharedMesh;
            var oldBones = renderer.bones;
            var oldWeights = Enumerable.Range(0, oldMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            Directory.CreateDirectory(Output);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ReFitComputation computation = null;
            try
            {
                computation = new ReFitEngine().Run(request);
                watch.Stop();
                if (!computation.success) throw new InvalidOperationException(string.Join("\n", computation.report.messages));
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { computation.mesh }, Output + "/" + label + "-roundtrip.asset", true);
                var reloaded = UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(Output + "/" + label + "-roundtrip.asset");
                try { CompareShapes(computation.mesh, reloaded.OfType<Mesh>().Single(), .00000001f); }
                finally { foreach (var item in reloaded) Object.DestroyImmediate(item); }
                float nativeDrift = CheckNativeShapes(input, computation.mesh, label == "glowsticks");
                var original = Bake(input, input.sharedMesh, new Dictionary<string, float> { { "Hood up", 100 } });
                var corrected = Bake(input, computation.mesh, new Dictionary<string, float> { { "Hood up", 100 }, { "refit", 100 }, { "orbit muscles", 100 } });
                var primary = Bake(input, computation.mesh, new Dictionary<string, float> { { "Hood up", 100 }, { "refit", 100 } });
                var bodyBase = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, new Dictionary<string, float>());
                var bodyShaped = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, new Dictionary<string, float> { { "orbit muscles", 100 } });
                var owned = new List<Mesh> { original, corrected, primary, bodyBase, bodyShaped };
                var lines = new List<string> { $"{label}: engine={watch.Elapsed.TotalMilliseconds:F1}ms" };
                lines.Add($"PASS: serialized mesh round-trip and unchanged skin weights; {input.sharedMesh.blendShapeCount} authored shapes, maximum original-to-output world drift={nativeDrift * 1000:F6}mm.");
                try
                {
                    lines.Add(Quality("primary", original, primary, label == "glowsticks"));
                    lines.Add(Quality("muscles", original, corrected, label == "glowsticks"));
                    var inputSnapshot = MeshSnapshot.Capture(input, true, null, null);
                    int tubes = ReFitTubeField.Build(inputSnapshot, true).Count;
                    lines.Add("tube components=" + tubes);
                    if (label == "hoodie" && tubes != 0) throw new InvalidOperationException("Hoodie cloth was classified as a tube.");
                    if (label == "glowsticks" && tubes != 24) throw new InvalidOperationException("Not all 24 closed rings were detected.");
                    var intersections = ReFitMeshIntersectionChecks.Intersections(original.vertices, original.vertices, original.triangles);
                    var components = TriangleComponents(inputSnapshot);
                    Func<long, bool> selfIntersection = pair => components[(int)(pair >> 32)] == components[(int)(pair & uint.MaxValue)];
                    lines.Add("original nonadjacent triangle intersections=" + intersections.Count);
                    lines.Add("original within-component intersections=" + intersections.Count(selfIntersection));
                    foreach (var m in computation.report.messages) if (m.code == "tube-field") lines.Add(m.text);
                    for (int step = 0; step <= 4; step++)
                    {
                        var at = Bake(input, computation.mesh, new Dictionary<string, float> { { "Hood up", 100 }, { "refit", 100 }, { "orbit muscles", step * 25 } });
                        var bodyAt = Bake(request.targetBodyRenderer, request.targetBodyRenderer.sharedMesh, new Dictionary<string, float> { { "orbit muscles", step * 25 } });
                        try
                        {
                            lines.Add(Quality("weight=" + step * 25, original, at, label == "glowsticks"));
                            var collisions = ReFitMeshIntersectionChecks.Intersections(original.vertices, at.vertices, at.triangles);
                            lines.Add($"weight={step * 25}: nonadjacent triangle intersections={collisions.Count}, new pairs={collisions.Count(p => !intersections.Contains(p))}");
                            lines.Add($"weight={step * 25}: within-component intersections={collisions.Count(selfIntersection)}");
                            if (label == "glowsticks" && collisions.Any(selfIntersection)) throw new InvalidOperationException("A glowstick tube intersects itself.");
                            lines.Add(Clearance(original, at, bodyBase, bodyAt, label == "glowsticks"));
                        }
                        finally { Object.DestroyImmediate(at); Object.DestroyImmediate(bodyAt); }
                    }
                    if (label == "hoodie")
                    {
                        request.settings.preserveClosedTubes = false;
                        var unchanged = new ReFitEngine().Run(request);
                        try
                        {
                            if (!unchanged.success) throw new InvalidOperationException("Hoodie control run failed.");
                            CompareShapes(computation.mesh, unchanged.mesh, 0.000001f);
                            float controlDrift = CheckNativeShapes(input, unchanged.mesh, false);
                            if (Mathf.Abs(controlDrift - nativeDrift) > .000001f) throw new InvalidOperationException("Hoodie authored-shape drift regressed against the control.");
                            lines.Add($"Hoodie control original-to-output authored-shape drift={controlDrift * 1000:F6}mm (existing pose baking, not a tube-path difference).");
                            lines.Add("PASS: Hoodie output with tube preservation on/off matches within 0.001mm for all vertices and shapes.");
                        }
                        finally { if (unchanged.mesh != null) Object.DestroyImmediate(unchanged.mesh); }
                    }
                    Render(label + "-original", bodyBase, request.targetBodyRenderer.sharedMaterials, original, renderer.sharedMaterials);
                    Render(label + "-corrected", bodyShaped, request.targetBodyRenderer.sharedMaterials, corrected, renderer.sharedMaterials);
                    string oldPath = label == "glowsticks" ? "Assets/ReFit/Glowsticks Combined/Glowsticks Combined_ScenePoseDefault_ReFit.asset" : null;
                    var previous = oldPath != null ? AssetDatabase.LoadAssetAtPath<Mesh>(oldPath) : null;
                    if (previous != null)
                    {
                        var before = Bake(renderer, previous, new Dictionary<string, float> { { "refit", 100 }, { "orbit muscles", 100 } });
                        try { Render(label + "-previous", bodyShaped, request.targetBodyRenderer.sharedMaterials, before, renderer.sharedMaterials); }
                        finally { Object.DestroyImmediate(before); }
                    }
                    File.WriteAllLines(Output + "/" + label + ".txt", lines);
                    Debug.Log("[ReFit Accessory Validation] " + string.Join("\n", lines));
                }
                finally { foreach (var mesh in owned) Object.DestroyImmediate(mesh); }
                if (renderer.sharedMesh != oldMesh || !renderer.bones.SequenceEqual(oldBones) ||
                    !Enumerable.Range(0, oldMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).SequenceEqual(oldWeights))
                    throw new InvalidOperationException("Validation changed the live accessory.");
            }
            finally
            {
                if (computation?.mesh != null) Object.DestroyImmediate(computation.mesh);
                Object.DestroyImmediate(inputRoot);
            }
        }

        internal static GameObject OriginalInput(SkinnedMeshRenderer live, ReFitRequest request, out SkinnedMeshRenderer input)
        {
            // Copy transforms only. Instantiating an avatar would execute arbitrary editor components.
            var map = new Dictionary<Transform, Transform>();
            var root = CopyTransforms(live.transform.root, null, map);
            var targetBody = request.targetBodyRenderer;
            var copiedBody = map[targetBody.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
            copiedBody.sharedMesh = targetBody.sharedMesh; copiedBody.sharedMaterials = targetBody.sharedMaterials;
            copiedBody.bones = targetBody.bones.Select(b => b == null ? null : map[b]).ToArray();
            copiedBody.rootBone = targetBody.rootBone != null ? map[targetBody.rootBone] : null;
            for (int s = 0; s < targetBody.sharedMesh.blendShapeCount; s++) copiedBody.SetBlendShapeWeight(s, targetBody.GetBlendShapeWeight(s));
            var animator = request.targetAvatar.GetComponent<Animator>();
            if (animator != null)
            {
                var copy = root.gameObject.AddComponent<Animator>(); copy.enabled = false; copy.avatar = animator.avatar;
            }
            // Preserve the asset-on-target relationship; treating this copy as a standalone accessory
            // would deliberately take a different staging/pose path than the actual MCB request.
            request.targetAvatar = root.gameObject;
            request.targetBodyRenderer = copiedBody;
            input = map[live.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
            input.sharedMesh = live.sharedMesh; input.sharedMaterials = live.sharedMaterials;
            input.bones = live.bones.Select(b => b == null ? null : map[b]).ToArray();
            input.rootBone = live.rootBone != null ? map[live.rootBone] : null;
#if REFIT_VRCHAT_AVATARS
            // Start from the mesh as it was before its refit, as recorded on the renderer.
            var record = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>();
            if (record != null && record.Applied)
            {
                var original = record.original;
                input.sharedMesh = original.mesh;
                input.bones = original.bones.Select(b => b == null ? null : map[b]).ToArray();
                input.rootBone = original.rootBone != null ? map[original.rootBone] : null;
                for (int s = 0; s < original.blendShapeNames.Count; s++)
                {
                    int index = input.sharedMesh.GetBlendShapeIndex(original.blendShapeNames[s]);
                    if (index >= 0) input.SetBlendShapeWeight(index, original.blendShapeWeights[s]);
                }
                foreach (var state in original.transforms)
                {
                    if (state.transform == null || !map.TryGetValue(state.transform, out var copy)) continue;
                    copy.localPosition = state.localPosition;
                    copy.localRotation = state.localRotation;
                    copy.localScale = state.localScale;
                }
            }
#endif
            return root.gameObject;
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

        private static void CompareShapes(Mesh a, Mesh b, float tolerance)
        {
            if (a.vertexCount != b.vertexCount || a.blendShapeCount != b.blendShapeCount || !a.triangles.SequenceEqual(b.triangles))
                throw new InvalidOperationException("Control run topology or shape count differs.");
            if (!a.boneWeights.SequenceEqual(b.boneWeights) || !a.bindposes.SequenceEqual(b.bindposes))
                throw new InvalidOperationException("Control run skinning differs.");
            var av = a.vertices; var bv = b.vertices;
            for (int s = -1; s < a.blendShapeCount; s++)
            {
                if (s >= 0)
                {
                    if (a.GetBlendShapeName(s) != b.GetBlendShapeName(s)) throw new InvalidOperationException("Shape names differ.");
                    if (a.GetBlendShapeFrameCount(s) != b.GetBlendShapeFrameCount(s)) throw new InvalidOperationException("Shape frames differ.");
                    for (int f = 0; f < a.GetBlendShapeFrameCount(s); f++)
                    {
                        if (a.GetBlendShapeFrameWeight(s, f) != b.GetBlendShapeFrameWeight(s, f)) throw new InvalidOperationException("Frame weights differ.");
                        a.GetBlendShapeFrameVertices(s, f, av, null, null); b.GetBlendShapeFrameVertices(s, f, bv, null, null);
                        for (int i = 0; i < av.Length; i++)
                            if ((av[i] - bv[i]).magnitude > tolerance) throw new InvalidOperationException($"Non-tube output changed at shape {s}, frame {f}, vertex {i}.");
                    }
                    continue;
                }
                for (int i = 0; i < av.Length; i++)
                    if ((av[i] - bv[i]).magnitude > tolerance) throw new InvalidOperationException($"Non-tube output changed at shape {s}, vertex {i}.");
            }
        }

        private static float CheckNativeShapes(SkinnedMeshRenderer input, Mesh output, bool assertExact)
        {
            if (!input.sharedMesh.boneWeights.SequenceEqual(output.boneWeights)) throw new InvalidOperationException("Original skin weights changed.");
            float worst = 0;
            for (int s = -1; s < input.sharedMesh.blendShapeCount; s++)
            {
                string name = s >= 0 ? input.sharedMesh.GetBlendShapeName(s) : "";
                if (s >= 0 && output.GetBlendShapeIndex(name) < 0) throw new InvalidOperationException("Authored shape was lost: " + name);
                int frames = s < 0 ? 1 : input.sharedMesh.GetBlendShapeFrameCount(s);
                for (int f = 0; f < frames; f++)
                {
                    var values = new Dictionary<string, float>();
                    if (s >= 0) values[name] = input.sharedMesh.GetBlendShapeFrameWeight(s, f);
                    var a = Bake(input, input.sharedMesh, values); Mesh b = null;
                    try
                    {
                        b = Bake(input, output, values);
                        float error = a.vertices.Zip(b.vertices, (x, y) => (x - y).magnitude).Max();
                        worst = Mathf.Max(worst, error);
                        if (assertExact && error > .000001f) throw new InvalidOperationException($"Authored shape {name} changed by {error * 1000:F4}mm.");
                    }
                    finally { Object.DestroyImmediate(a); if (b != null) Object.DestroyImmediate(b); }
                }
            }
            return worst;
        }

        private static int[] TriangleComponents(MeshSnapshot snapshot)
        {
            var groups = Enumerable.Repeat(-1, snapshot.GroupCount).ToArray(); int component = 0;
            for (int seed = 0; seed < groups.Length; seed++)
            {
                if (groups[seed] >= 0) continue;
                var queue = new Queue<int>(); queue.Enqueue(seed); groups[seed] = component;
                while (queue.Count > 0)
                    foreach (int n in snapshot.groupAdjacency[queue.Dequeue()])
                        if (groups[n] < 0) { groups[n] = component; queue.Enqueue(n); }
                component++;
            }
            return Enumerable.Range(0, snapshot.triangles.Length / 3).Select(t => groups[snapshot.groupOfVertex[snapshot.triangles[t * 3]]]).ToArray();
        }

        private static string Clearance(Mesh original, Mesh after, Mesh baseBody, Mesh shapedBody, bool assertNoNewPenetration)
        {
            var a = new MeshSnapshot { worldVertices = baseBody.vertices, triangles = baseBody.triangles };
            var b = new MeshSnapshot { worldVertices = shapedBody.vertices, triangles = shapedBody.triangles };
            var ia = SurfaceBvh.Build(a); var ib = SurfaceBvh.Build(b);
            var na = SurfaceNormals(a); var nb = SurfaceNormals(b);
            var p = original.vertices; var q = after.vertices; var triangles = original.triangles;
            int checkedCount = 0, crossings = 0, preexisting = 0, unconfirmed = 0; float worst = 0;
            var details = new List<string>();
            // Independent nearest-surface queries, including triangle centers (not only solver vertices).
            for (int sample = 0; sample < p.Length + triangles.Length / 3; sample++)
            {
                Vector3 x, y;
                if (sample < p.Length) { x = p[sample]; y = q[sample]; }
                else
                {
                    int t = (sample - p.Length) * 3;
                    x = (p[triangles[t]] + p[triangles[t + 1]] + p[triangles[t + 2]]) / 3;
                    y = (q[triangles[t]] + q[triangles[t + 1]] + q[triangles[t + 2]]) / 3;
                }
                var ha = ia.ClosestPoint(x, .15f, t => na[t].sqrMagnitude > .5f);
                var hb = ib.ClosestPoint(y, .15f, t => nb[t].sqrMagnitude > .5f);
                if (!ha.found || !hb.found) continue;
                float ga = Vector3.Dot(x - ha.position, na[ha.triangle]);
                float gb = Vector3.Dot(y - hb.position, nb[hb.triangle]);
                if (ga < .001f) continue;
                checkedCount++;
                if (gb < -.001f)
                {
                    // Multiple internal/collapsed surfaces can reverse the nearest-face sign without
                    // crossing the exterior skin. Confirm candidates independently against the volume.
                    double wa = Math.Abs(ReFitMeshIntersectionChecks.Winding(a, x));
                    double wb = Math.Abs(ReFitMeshIntersectionChecks.Winding(b, y));
                    if (wa > .5) { preexisting++; continue; }
                    if (wb <= .5) { unconfirmed++; continue; }
                    crossings++; worst = Mathf.Max(worst, -gb);
                    if (details.Count < 16) details.Add($"sample={sample} pos={x:F4}->{y:F4}, gap={ga * 1000:F3}->{gb * 1000:F3}mm, bodyTri={ha.triangle}->{hb.triangle}");
                }
            }
            if (assertNoNewPenetration && crossings > 0) throw new InvalidOperationException("New tube penetration: " + string.Join("\n", details));
            return $"independent clearance: outside-normal candidates={checkedCount}, newly >1mm inside (volume confirmed)={crossings}, worst={worst * 1000:F3}mm, already inside={preexisting}, unconfirmed={unconfirmed}\n" + string.Join("\n", details);
        }

        private static Vector3[] SurfaceNormals(MeshSnapshot mesh)
        {
            var normals = new Vector3[mesh.triangles.Length / 3];
            for (int t = 0; t < normals.Length; t++)
            {
                var a = mesh.worldVertices[mesh.triangles[t * 3]];
                var u = mesh.worldVertices[mesh.triangles[t * 3 + 1]] - a;
                var v = mesh.worldVertices[mesh.triangles[t * 3 + 2]] - a;
                var n = Vector3.Cross(u, v);
                // Collapsed hidden body polygons have no meaningful inside/outside normal.
                // Do not let the runtime's Vector3.up fallback validate or invalidate clearance.
                if (n.sqrMagnitude > 1e-20f && n.sqrMagnitude > u.sqrMagnitude * v.sqrMagnitude * 1e-10f)
                    normals[t] = n / n.magnitude;
            }
            return normals;
        }

        private static ReFitRequest Request(SkinnedMeshRenderer renderer)
        {
#if REFIT_VRCHAT_AVATARS
            // The avatar's custom base as MCB describes it to the Orbiters tools.
            var info = Orbiters.Toolkit.Editor.Refit.CustomBases.Describe(renderer.transform.root);
            if (info == null || !info.CanFit) throw new InvalidOperationException("MCB configuration is required for this private test.");
            var original = info.ResolveOriginal();
            if (original?.Body == null) throw new InvalidOperationException("MCB could not resolve the original base.");
            return new ReFitRequest
            {
                mode = ReFitMode.MeshAndBlendshape, assetRenderer = renderer, targetAvatar = renderer.transform.root.gameObject,
                sourceAvatar = original.Avatar, sourceBodyRenderer = original.Body, targetBodyRenderer = info.Body,
                targetBlendshapes = new List<string> { "orbit muscles" },
                settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false, prefixTransferredShapes = false }
            };
#else
            throw new InvalidOperationException("This private test needs a VRChat avatar project with MCB.");
#endif
        }

        internal static Mesh Bake(SkinnedMeshRenderer original, Mesh input, Dictionary<string, float> values)
        {
            var go = new GameObject("__ReFitValidationRenderer") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
                go.transform.localScale = original.transform.lossyScale;
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = input; renderer.bones = original.bones; renderer.rootBone = original.rootBone;
                var overrides = new Dictionary<int, float>();
                for (int s = 0; s < input.blendShapeCount; s++) overrides[s] = values.TryGetValue(input.GetBlendShapeName(s), out float v) ? v / 100 : 0;
                var snapshot = MeshSnapshot.Capture(renderer, false, overrides, null);
                var mesh = Object.Instantiate(input);
                mesh.ClearBlendShapes(); mesh.vertices = snapshot.worldVertices; mesh.normals = snapshot.worldNormals;
                mesh.RecalculateBounds();
                return mesh;
            }
            finally { Object.DestroyImmediate(go); }
        }

        internal static string Quality(string label, Mesh before, Mesh after, bool assertTube = false)
        {
            var a = before.vertices; var b = after.vertices; var triangles = before.triangles;
            var ratios = new List<float>(); int over = 0, reversed = 0, degenerate = 0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                bool bad = false;
                for (int j = 0; j < 3; j++)
                {
                    int u = triangles[t + j], v = triangles[t + (j + 1) % 3]; float length = (a[u] - a[v]).magnitude;
                    if (length < 1e-6f) continue;
                    float ratio = (b[u] - b[v]).magnitude / length; ratios.Add(ratio); if (ratio > 2) bad = true;
                }
                if (bad) over++;
                int x = triangles[t], y = triangles[t + 1], z = triangles[t + 2];
                var n = Vector3.Cross(b[y] - b[x], b[z] - b[x]);
                if (n.sqrMagnitude < 1e-16f) degenerate++;
                if (Vector3.Dot(Vector3.Cross(a[y] - a[x], a[z] - a[x]), n) < 0) reversed++;
            }
            ratios.Sort();
            if (label == "coverage-new-0")
            {
                var details = new List<string>();
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int x = triangles[t], y = triangles[t + 1], z = triangles[t + 2];
                    if (Vector3.Dot(Vector3.Cross(a[y] - a[x], a[z] - a[x]), Vector3.Cross(b[y] - b[x], b[z] - b[x])) < 0)
                        details.Add($"triangle {t / 3}: center={(a[x] + a[y] + a[z]) / 3:F4}, correction={(b[x]-a[x]):F4}");
                }
                File.WriteAllLines(Output + "/coverage-reversals.txt", details);
            }
            string summary = $"{label}: edgeP95={ratios[(int)(ratios.Count * .95)]:F3}, max={ratios.Last():F3}, trianglesOver2={over}, orientationReversals={reversed}, degenerate={degenerate}";
            // This private fixture grows substantially around the lower legs. Report every >2x edge,
            // but reject >3x spikes, collapsed faces and reversals throughout the slider range.
            if (assertTube && (reversed != 0 || degenerate != 0 || ratios.Last() > 3))
                throw new InvalidOperationException("Tube triangle quality regression: " + summary);
            return summary;
        }

        private static void Render(string name, Mesh body, Material[] bodyMaterials, Mesh accessory, Material[] accessoryMaterials)
        {
            var preview = new PreviewRenderUtility();
            try
            {
                preview.camera.orthographic = true; preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.18f, .19f, .21f, 1);
                preview.ambientColor = new Color(.65f, .65f, .65f);
                preview.lights[0].intensity = 1.2f; preview.lights[1].intensity = .7f;
                var centers = new[] { body.bounds.center, body.bounds.center, new Vector3(-.38f, 1.38f, 0), new Vector3(0, .85f, 0), new Vector3(-.33f, 1.42f, 0), new Vector3(0, 1.15f, 0), new Vector3(.33f, 1.42f, 0) };
                var directions = new[] { Vector3.forward, new Vector3(-1, .15f, 1).normalized, new Vector3(-1, .2f, 1).normalized, new Vector3(-.2f, .1f, 1).normalized, new Vector3(-1, .1f, -1).normalized, Vector3.back, new Vector3(1, .1f, -1).normalized };
                var names = new[] { "front", "three-quarter", "arm", "leg", "rear-shoulder", "back", "right-shoulder" };
                for (int view = 0; view < names.Length; view++)
                {
                    preview.camera.transform.position = centers[view] + directions[view] * 4;
                    preview.camera.transform.LookAt(centers[view], Vector3.up);
                    preview.camera.orthographicSize = view < 2 ? body.bounds.extents.y * 1.08f : view == 5 ? .55f : .29f;
                    preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 20;
                    preview.lights[0].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                    preview.lights[1].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                    preview.BeginPreview(new Rect(0, 0, 1200, 1200), GUIStyle.none);
                    Draw(preview, body, bodyMaterials); Draw(preview, accessory, accessoryMaterials);
                    preview.Render(true);
                    var render = (RenderTexture)preview.EndPreview();
                    var previous = RenderTexture.active; var image = new Texture2D(1200, 1200, TextureFormat.RGB24, false);
                    try
                    {
                        RenderTexture.active = render; image.ReadPixels(new Rect(0, 0, 1200, 1200), 0, 0); image.Apply();
                        File.WriteAllBytes(Output + "/" + name + "-" + names[view] + ".png", image.EncodeToPNG());
                    }
                    finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                }
            }
            finally { preview.Cleanup(); }
        }

        internal static void Draw(PreviewRenderUtility preview, Mesh mesh, Material[] materials)
        {
            for (int s = 0; s < mesh.subMeshCount && materials.Length > 0; s++)
                if (materials[Mathf.Min(s, materials.Length - 1)] != null)
                    preview.DrawMesh(mesh, Matrix4x4.identity, materials[Mathf.Min(s, materials.Length - 1)], s);
        }
    }
}
