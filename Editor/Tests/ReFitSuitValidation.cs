using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Private fixture: a suit made for another avatar, roughly placed over Druffle (clipping already at rest), refitted on a
    /// body blendshape. Works on copies of the original meshes; reports clothing inside the body at 0 and 100 and renders the
    /// input, the scene's current result and the new one. Never changes the live scene.
    /// </summary>
    public static class ReFitSuitValidation
    {
        public const string Output = "Temp/ReFitTests/suit";
        private const string AvatarName = "Druffle (Complete Setup)(PC) (TailLong)", SuitName = "[P.0.E] - FashionSuit - Vulper";

        public static string Run(string label, bool notMadeForAvatar, string shape = "MarvEdit", float tightness = .93f,
            bool layers = true, bool render = true, string only = null, bool keepLayerOrder = true, string focusPoints = null)
        {
            var avatar = Resources.FindObjectsOfTypeAll<Transform>().First(t => t.parent == null && t.name == AvatarName && t.gameObject.scene.IsValid());
            var suit = avatar.GetComponentsInChildren<Transform>(true).First(t => t.name == SuitName);
            return Run(label, avatar, suit, notMadeForAvatar, shape, tightness, layers, render, only, keepLayerOrder, focusPoints);
        }

        /// <summary>
        /// A garment made for another body of the same rig, worn by a scene avatar as is: copies of both in a preview scene
        /// (e.g. the Hoodie made for Rexouium on the Ultirex custom base).
        /// </summary>
        public static string RunPlaced(string label, string avatarName, string prefabPath, string shape, bool notMadeForAvatar, string focusPoints = null, bool keepLayerOrder = true)
        {
            var source = Resources.FindObjectsOfTypeAll<Transform>().First(t => t.parent == null && t.name == avatarName && t.gameObject.scene.IsValid());
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var avatar = Object.Instantiate(source.gameObject).transform;
                avatar.name = avatarName;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar.gameObject, scene);
                foreach (var gizmo in avatar.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("__XRayGizmos_")).ToArray()) Object.DestroyImmediate(gizmo.gameObject);
                var outfit = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), avatar).transform;
                outfit.localPosition = Vector3.zero; outfit.localRotation = Quaternion.identity; outfit.localScale = Vector3.one;
                return Run(label, avatar, outfit, notMadeForAvatar, shape, .93f, true, true, null, keepLayerOrder, focusPoints);
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static string Run(string label, Transform avatar, Transform suit, bool notMadeForAvatar, string shape, float tightness,
            bool layers, bool render, string only, bool keepLayerOrder, string focusPoints)
        {
            var body = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body" && !r.transform.IsChildOf(suit));
            if (body.sharedMesh.GetBlendShapeIndex(shape) < 0) throw new InvalidOperationException("No body blendshape " + shape);
            Directory.CreateDirectory(Output);
            var lines = new List<string> { $"{label}: notMadeForAvatar={notMadeForAvatar} shape={shape} tightness={tightness} layers={layers}" };
            var owned = new List<Object>();
            var weights = new[] { 0f, 100f };
            var states = new[] { "input", "current", "new" };
            var baked = states.ToDictionary(s => s, s => weights.ToDictionary(w => w, w => new List<Mesh>()));
            var materials = new List<Material[]>();
            var generated = new List<(SkinnedMeshRenderer live, Mesh mesh, Dictionary<string, float> values, ReFitGeneratedAssetMetadataData metadata)>();
            var focus = weights.ToDictionary(w => w, w => new List<(float ratio, Vector3 center)>());
            try
            {
                var bodies = weights.ToDictionary(w => w, w => BodyAt(body, shape, w, owned));
                var order = ReFitOutfit.InnerFirst(body, suit.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                lines.Add("order: " + string.Join(", ", order.Select(r => r.name)));
                foreach (var live in order.Where(r => only == null || r.name == only))
                {
                    var request = new ReFitRequest
                    {
                        mode = notMadeForAvatar ? ReFitMode.MeshAndBlendshape : ReFitMode.Blendshape,
                        assetRenderer = live, targetAvatar = avatar.gameObject, targetBodyRenderer = body,
                        targetBlendshapes = new List<string> { shape }, settings = new ReFitSettings(),
                    };
                    ReFitSettingsPresets.ApplyTightness(request.settings, tightness);
                    request.settings.coverDifferentBaseBody = notMadeForAvatar;
                    request.settings.coverageKeepsLayerOrder = notMadeForAvatar && keepLayerOrder;
                    var root = ReFitAccessoryValidation.OriginalInput(live, request, out var input);
                    // Not made for this avatar: the mesh is refitted onto the same body, from its current placement.
                    if (notMadeForAvatar) { request.sourceAvatar = request.targetAvatar; request.sourceBodyRenderer = request.targetBodyRenderer; }
                    try
                    {
                        // Every other part of the outfit: refitted ones as refitted, the others as authored.
                        if (notMadeForAvatar && layers)
                            foreach (var other in order.Where(o => o != live))
                            {
                                var done = generated.FirstOrDefault(g => g.live == other);
                                if (done.live != null) { request.coverageLayers.Add(Layer(root.transform, avatar, other, done.mesh, done.values, done.metadata)); continue; }
                                var otherRecord = other.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>();
                                bool applied = otherRecord != null && otherRecord.Applied;
                                var authored = applied ? otherRecord.original.blendShapeNames.Select((n, i) => (n, w: otherRecord.original.blendShapeWeights[i])).ToDictionary(x => x.n, x => x.w) : Values(other);
                                request.coverageLayers.Add(Layer(root.transform, avatar, other, applied ? otherRecord.original.mesh : other.sharedMesh, authored, null));
                            }
                        var started = DateTime.Now;
                        var result = new ReFitEngine().Run(request);
                        if (!result.success) throw new InvalidOperationException(live.name + ": " + string.Join("\n", result.report.messages.Select(m => m.text)));
                        owned.Add(result.mesh);
                        var values = Values(input);
                        if (!string.IsNullOrEmpty(result.primaryShapeName)) values[result.primaryShapeName] = 100;
                        string generatedShape = Generated(result, shape);
                        generated.Add((live, result.mesh, values, result.generatedMetadata));
                        var current = Values(live);
                        var record = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>();
                        string currentShape = record != null && record.Applied ? record.shapes.Where(s => s.source == shape).Select(s => s.generated).FirstOrDefault() : null;
                        lines.Add($"{live.name}: {(DateTime.Now - started).TotalSeconds:F1}s, shape '{generatedShape}', primary '{result.primaryShapeName}'");
                        foreach (float w in weights)
                        {
                            var input0 = Keep(ReFitAccessoryValidation.Bake(input, input.sharedMesh, values.Where(p => p.Key != result.primaryShapeName).ToDictionary(p => p.Key, p => p.Value)), owned);
                            if (currentShape != null) current[currentShape] = w;
                            var currentMesh = Keep(ReFitAccessoryValidation.Bake(live, live.sharedMesh, current), owned);
                            var fitted = new Dictionary<string, float>(values);
                            if (generatedShape != null) fitted[generatedShape] = w;
                            var next = Keep(ReFitAccessoryValidation.Bake(input, result.mesh, fitted), owned);
                            baked["input"][w].Add(input0); baked["current"][w].Add(currentMesh); baked["new"][w].Add(next);
                            lines.Add($"  {shape} {w}: input {Clipping(bodies[w], input0)} | current {Clipping(bodies[w], currentMesh)} | new {Clipping(bodies[w], next)}");
                            var worst = Worst(input0, next);
                            focus[w].AddRange(worst.Take(2));
                            lines.Add("    " + ReFitAccessoryValidation.Quality($"new-{w}", input0, next) + " worst at " + string.Join(" ", worst.Select(x => $"{x.ratio:F1}x@{x.center:F3}")));
                        }
                        lines.AddRange(result.report.messages.Where(m => m.severity != ReFitSeverity.Info || m.code.Contains("coverage")).Select(m => "    [" + m.code + "] " + m.text));
                        materials.Add(live.sharedMaterials);
                    }
                    finally { Object.DestroyImmediate(root); }
                }
                if (render)
                    foreach (float w in weights)
                        foreach (var state in states)
                        {
                            RenderOutfit($"{label}-{state}-{w}", bodies[w].mesh, body.sharedMaterials, baked[state][w], materials);
                            // Close-ups where the new result stretches most, for every state at that weight.
                            var points = focusPoints != null
                                ? focusPoints.Split(';').Select(p => p.Split(',').Select(float.Parse).ToArray()).Select(v => new Vector3(v[0], v[1], v[2])).ToList()
                                : focus[w].OrderByDescending(x => x.ratio).Select(x => x.center).Take(4).ToList();
                            RenderOutfit($"{label}-{state}-{w}-focus", bodies[w].mesh, body.sharedMaterials, baked[state][w], materials, points);
                        }
                if (render)
                {
                    var heat = baked["new"][0f].Select((mesh, i) => Heat(mesh, baked["input"][0f][i], owned)).ToList();
                    var unlit = new Material(Shader.Find("Sprites/Default"));
                    owned.Add(unlit);
                    var heatMaterials = heat.Select(m => Enumerable.Repeat(unlit, m.subMeshCount).ToArray()).ToList();
                    var points = focusPoints != null
                        ? focusPoints.Split(';').Select(p => p.Split(',').Select(float.Parse).ToArray()).Select(v => new Vector3(v[0], v[1], v[2])).ToList()
                        : focus[0f].OrderByDescending(x => x.ratio).Select(x => x.center).Take(4).ToList();
                    RenderOutfit($"{label}-heat-0", bodies[0f].mesh, body.sharedMaterials, heat, heatMaterials, plain: true);
                    RenderOutfit($"{label}-heat-0-focus", bodies[0f].mesh, body.sharedMaterials, heat, heatMaterials, points, plain: true);
                }
                File.WriteAllLines(Output + "/" + label + ".txt", lines);
                return string.Join("\n", lines);
            }
            finally { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); }
        }

        /// <summary>Why the clipped groups of a part count as hidden (its own fabric or a garment worn over it).</summary>
        public static string DebugHidden(string part, string shape = "MarvEdit", bool all = false)
        {
            var avatar = Resources.FindObjectsOfTypeAll<Transform>().First(t => t.parent == null && t.name == AvatarName && t.gameObject.scene.IsValid());
            var suit = avatar.GetComponentsInChildren<Transform>(true).First(t => t.name == SuitName);
            var body = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body" && !r.transform.IsChildOf(suit));
            var owned = new List<Object>();
            try
            {
                var order = ReFitOutfit.InnerFirst(body, suit.GetComponentsInChildren<SkinnedMeshRenderer>(true));
                var live = order.First(r => r.name == part);
                var bodyAt = BodyAt(body, shape, 0, owned);
                var request = new ReFitRequest { targetAvatar = avatar.gameObject, targetBodyRenderer = body, assetRenderer = live };
                var root = ReFitAccessoryValidation.OriginalInput(live, request, out var input);
                owned.Add(root);
                var asset = MeshSnapshot.Capture(input, true, null, null);
                var outer = order.Where(o => o != live).ToList();
                var occluders = outer.Select(o => (MeshSnapshot.Capture(o, false, null, null), o.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>() != null)).ToList();
                var by = new int[asset.GroupCount];
                var hidden = ReFitSurfaceCoverage.HiddenGroups(asset, bodyAt.snapshot, bodyAt.bvh, occluders, by, null);
                var lines = new List<string> { part + " occluders: " + string.Join(", ", outer.Select(o => o.name)) };
                var groups = new Dictionary<int, List<Vector3>>();
                for (int g = 0; g < hidden.Length; g++)
                {
                    var p = asset.worldVertices[asset.groupRep[g]];
                    var hit = bodyAt.bvh.ClosestPoint(p, .08f);
                    bool inside = hit.found && Vector3.Dot(p - hit.position, bodyAt.snapshot.BaryNormal(hit.triangle, hit.bary)) < -.001f;
                    if (!inside && !all) continue;
                    int key = hidden[g] ? by[g] : -2;
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Vector3>();
                    list.Add(p);
                }
                foreach (var pair in groups.OrderBy(p => p.Key))
                {
                    var b = new Bounds(pair.Value[0], Vector3.zero); foreach (var v in pair.Value) b.Encapsulate(v);
                    string name = pair.Key == -2 ? "not hidden" : pair.Key == -1 ? "own fabric" : outer[pair.Key].name;
                    lines.Add($"  {(all ? "all" : "clipped")} {name}: {pair.Value.Count} groups, bounds min {b.min:F3} max {b.max:F3}");
                }
                return string.Join(System.Environment.NewLine, lines);
            }
            finally { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); }
        }

        /// <summary>Why clipped groups of a placed garment inside a box do not move: hidden, closed detail or closed tube.</summary>
        public static string DebugPlaced(string avatarName, string prefabPath, Vector3 min, Vector3 max)
        {
            var source = Resources.FindObjectsOfTypeAll<Transform>().First(t => t.parent == null && t.name == avatarName && t.gameObject.scene.IsValid());
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var avatar = Object.Instantiate(source.gameObject).transform;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar.gameObject, scene);
                var outfit = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), avatar).transform;
                outfit.localPosition = Vector3.zero; outfit.localRotation = Quaternion.identity; outfit.localScale = Vector3.one;
                var body = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body" && !r.transform.IsChildOf(outfit));
                var garment = outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true).First();
                var surface = MeshSnapshot.Capture(body, false, null, null);
                var index = SurfaceBvh.Build(surface);
                var asset = MeshSnapshot.Capture(garment, true, null, null);
                var by = new int[asset.GroupCount];
                var hidden = ReFitSurfaceCoverage.HiddenGroups(asset, surface, index, new List<(MeshSnapshot, bool)>(), by, null);
                var tubes = ReFitTubeField.Build(asset, new ReFitSettings().preserveClosedTubes).groups;
                var details = ReFitSurfaceCoverage.DetailGroups(asset, tubes);
                var box = new Bounds(); box.SetMinMax(min, max);
                int clipped = 0, hid = 0, tube = 0, detail = 0, free = 0;
                for (int g = 0; g < asset.GroupCount; g++)
                {
                    var p = asset.worldVertices[asset.groupRep[g]];
                    if (!box.Contains(p)) continue;
                    var hit = index.ClosestPoint(p, .1f);
                    if (!hit.found || Vector3.Dot(p - hit.position, surface.BaryNormal(hit.triangle, hit.bary)) >= -.001f) continue;
                    clipped++;
                    if (hidden[g]) hid++; else if (tubes[g]) tube++; else if (details[g]) detail++; else free++;
                }
                return $"clipped {clipped}: hidden {hid}, tube {tube}, detail {detail}, free {free} (asset groups {asset.GroupCount}, tubes {tubes.Count(t => t)}, details {details.Count(d => d)})";
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }

        /// <summary>The scene's outfit as it is now (e.g. after the wizard), at 0 and 100 of the shape: clipping and renders.</summary>
        public static string RenderLive(string label, string shape = "MarvEdit", string focusPoints = null)
        {
            var avatar = Resources.FindObjectsOfTypeAll<Transform>().First(t => t.parent == null && t.name == AvatarName && t.gameObject.scene.IsValid());
            var suit = avatar.GetComponentsInChildren<Transform>(true).First(t => t.name == SuitName);
            var body = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Body" && !r.transform.IsChildOf(suit));
            var owned = new List<Object>();
            var lines = new List<string> { label };
            try
            {
                foreach (float w in new[] { 0f, 100f })
                {
                    var bodyAt = BodyAt(body, shape, w, owned);
                    var meshes = new List<Mesh>(); var materials = new List<Material[]>();
                    foreach (var live in suit.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null))
                    {
                        var values = Values(live);
                        var record = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>();
                        if (record != null) foreach (var pair in record.shapes.Where(x => x.source == shape)) values[pair.generated] = w;
                        var baked = Keep(ReFitAccessoryValidation.Bake(live, live.sharedMesh, values), owned);
                        meshes.Add(baked); materials.Add(live.sharedMaterials);
                        lines.Add($"  {live.name} {shape} {w}: {Clipping(bodyAt, baked)}");
                    }
                    var points = focusPoints?.Split(';').Select(p => p.Split(',').Select(float.Parse).ToArray()).Select(v => new Vector3(v[0], v[1], v[2])).ToList();
                    RenderOutfit($"{label}-{w}", bodyAt.mesh, body.sharedMaterials, meshes, materials);
                    if (points != null) RenderOutfit($"{label}-{w}-focus", bodyAt.mesh, body.sharedMaterials, meshes, materials, points);
                }
                return string.Join(System.Environment.NewLine, lines);
            }
            finally { foreach (var value in owned) if (value != null) Object.DestroyImmediate(value); }
        }

        private sealed class Body
        {
            public Mesh mesh;
            public MeshSnapshot snapshot;
            public SurfaceBvh bvh;
        }

        private static Body BodyAt(SkinnedMeshRenderer body, string shape, float weight, List<Object> owned)
        {
            var values = Values(body);
            values[shape] = weight;
            var overrides = new Dictionary<int, float>();
            for (int s = 0; s < body.sharedMesh.blendShapeCount; s++) overrides[s] = values[body.sharedMesh.GetBlendShapeName(s)] / 100;
            var snapshot = MeshSnapshot.Capture(body, false, overrides, null);
            return new Body { mesh = Keep(ReFitAccessoryValidation.Bake(body, body.sharedMesh, values), owned), snapshot = snapshot, bvh = SurfaceBvh.Build(snapshot) };
        }

        // Vertices inside the body: below the nearest surface and confirmed by the winding number (open or folded body
        // regions make the nearest face alone unreliable).
        private static string Clipping(Body body, Mesh cloth)
        {
            var points = cloth.vertices;
            var depths = new List<float>();
            var gate = new object();
            Parallel.For(0, points.Length, i =>
            {
                var hit = body.bvh.ClosestPoint(points[i], .08f);
                if (!hit.found) return;
                float signed = Vector3.Dot(points[i] - hit.position, body.snapshot.BaryNormal(hit.triangle, hit.bary));
                if (signed > -.0005f || ReFitMeshIntersectionChecks.Winding(body.snapshot, points[i]) < .5) return;
                lock (gate) depths.Add(-signed);
            });
            depths.Sort();
            return depths.Count == 0 ? "inside 0" : $"inside {depths.Count} ({100f * depths.Count / points.Length:F1}%), depth p50 {depths[depths.Count / 2] * 1000:F1}mm max {depths[depths.Count - 1] * 1000:F1}mm";
        }

        // An inner garment's result on the copied hierarchy, as an outer one's coverage layer: its mesh on the bones it was
        // refitted with (the recorded original's when the scene's garment is already refitted).
        private static SkinnedMeshRenderer Layer(Transform root, Transform avatar, SkinnedMeshRenderer live, Mesh mesh, Dictionary<string, float> values, ReFitGeneratedAssetMetadataData metadata)
        {
            Transform Copy(Transform t) => t == null ? null : ReFitUtility.ResolvePath(root, ReFitUtility.IndexPath(t, avatar));
            var record = live.GetComponent<Orbiters.Toolkit.VRChat.OrbitersRefit>();
            bool applied = record != null && record.Applied;
            var layer = Copy(live.transform).gameObject.AddComponent<SkinnedMeshRenderer>();
            layer.sharedMesh = mesh;
            if (metadata != null) layer.gameObject.AddComponent<ReFitGeneratedAssetMetadata>().data = metadata;
            layer.bones = (applied ? record.original.bones.ToArray() : live.bones).Select(Copy).ToArray();
            layer.rootBone = Copy(applied ? record.original.rootBone : live.rootBone);
            foreach (var pair in values)
            {
                int index = mesh.GetBlendShapeIndex(pair.Key);
                if (index >= 0) layer.SetBlendShapeWeight(index, pair.Value);
            }
            return layer;
        }

        // Centers of the most stretched triangles (world), to aim close-ups.
        private static List<(float ratio, Vector3 center)> Worst(Mesh before, Mesh after)
        {
            var a = before.vertices; var b = after.vertices; var t = before.triangles;
            var worst = new List<(float ratio, Vector3 center)>();
            for (int i = 0; i < t.Length; i += 3)
            {
                float ratio = 0;
                for (int j = 0; j < 3; j++)
                {
                    int u = t[i + j], v = t[i + (j + 1) % 3]; float length = (a[u] - a[v]).magnitude;
                    if (length > 1e-6f) ratio = Mathf.Max(ratio, (b[u] - b[v]).magnitude / length);
                }
                worst.Add((ratio, (b[t[i]] + b[t[i + 1]] + b[t[i + 2]]) / 3));
            }
            return worst.OrderByDescending(x => x.ratio).Take(4).ToList();
        }

        private static string Generated(ReFitComputation result, string shape)
        {
            if (result.secondarySourceShapeNames != null)
                for (int i = 0; i < result.secondarySourceShapeNames.Length; i++)
                    if (result.secondarySourceShapeNames[i] == shape) return result.secondaryShapeNames[i];
            return null;
        }

        private static Dictionary<string, float> Values(SkinnedMeshRenderer renderer) => Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
            .ToDictionary(i => renderer.sharedMesh.GetBlendShapeName(i), renderer.GetBlendShapeWeight);

        private static Mesh Keep(Mesh mesh, List<Object> owned) { owned.Add(mesh); return mesh; }

        // How far each vertex moved: blue untouched, green 5 mm, red 15 mm or more.
        private static Mesh Heat(Mesh moved, Mesh reference, List<Object> owned)
        {
            var mesh = Keep(Object.Instantiate(moved), owned);
            var a = moved.vertices; var b = reference.vertices;
            mesh.colors = a.Select((v, i) =>
            {
                float t = Mathf.Clamp01((v - b[i]).magnitude / .015f);
                return t < .33f ? Color.Lerp(new Color(.15f, .25f, .9f), new Color(.1f, .85f, .2f), t / .33f) : Color.Lerp(new Color(.1f, .85f, .2f), new Color(1f, .1f, .05f), (t - .33f) / .67f);
            }).ToArray();
            return mesh;
        }

        private static void RenderOutfit(string name, Mesh body, Material[] bodyMaterials, List<Mesh> cloth, List<Material[]> materials, List<Vector3> focus = null, bool plain = false)
        {
            var combined = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            var parts = new List<CombineInstance>(); var mats = new List<Material>();
            for (int i = 0; i < cloth.Count; i++)
                for (int s = 0; s < cloth[i].subMeshCount; s++)
                {
                    parts.Add(new CombineInstance { mesh = cloth[i], subMeshIndex = s, transform = Matrix4x4.identity });
                    mats.Add(materials[i][Mathf.Min(s, materials[i].Length - 1)]);
                }
            var clay = new Material(Shader.Find("Standard")) { color = new Color(.32f, .55f, .75f) };
            try
            {
                combined.CombineMeshes(parts.ToArray(), false, false);
                Render(name, body, bodyMaterials, combined, mats.ToArray(), focus);
                if (!plain) Render(name + "-surface", body, bodyMaterials, combined, mats.Select(_ => clay).ToArray(), focus);
            }
            finally { Object.DestroyImmediate(combined); Object.DestroyImmediate(clay); }
        }

        private static void Render(string name, Mesh body, Material[] bodyMaterials, Mesh cloth, Material[] clothMaterials, List<Vector3> focus = null)
        {
            var bounds = body.bounds;
            float height = bounds.size.y, bottom = bounds.min.y;
            var c = bounds.center;
            // A close-up looks at each point from outside the body, horizontally from the body's axis.
            var views = focus != null ? focus.Select((p, i) => (name: $"focus{i}", center: p, direction: (new Vector3(p.x - c.x, 0, p.z - c.z).normalized + Vector3.up * .15f).normalized, size: .07f)).ToArray() : new (string name, Vector3 center, Vector3 direction, float size)[]
            {
                ("front", c, Vector3.forward, height * .54f),
                ("three-quarter", c, new Vector3(-1, .15f, 1).normalized, height * .54f),
                ("side", c, Vector3.left, height * .54f),
                ("back", c, Vector3.back, height * .54f),
                ("legs-front", new Vector3(c.x, bottom + height * .27f, c.z), new Vector3(0, .05f, 1).normalized, height * .19f),
                ("legs-left", new Vector3(c.x, bottom + height * .27f, c.z), new Vector3(-1, .05f, .05f).normalized, height * .19f),
                ("legs-right", new Vector3(c.x, bottom + height * .27f, c.z), new Vector3(1, .05f, .05f).normalized, height * .19f),
                ("legs-back", new Vector3(c.x, bottom + height * .27f, c.z), new Vector3(0, .05f, -1).normalized, height * .19f),
                ("torso", new Vector3(c.x, bottom + height * .62f, c.z), new Vector3(-.4f, .05f, 1).normalized, height * .17f),
                ("torso-back", new Vector3(c.x, bottom + height * .62f, c.z), new Vector3(.3f, .05f, -1).normalized, height * .17f),
            };
            var preview = new PreviewRenderUtility();
            try
            {
                preview.camera.orthographic = true; preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.18f, .19f, .21f, 1);
                preview.ambientColor = new Color(.65f, .65f, .65f);
                preview.lights[0].intensity = 1.2f; preview.lights[1].intensity = .7f;
                foreach (var view in views)
                {
                    preview.camera.transform.position = view.center + view.direction * 4;
                    preview.camera.transform.LookAt(view.center, Vector3.up);
                    preview.camera.orthographicSize = view.size;
                    preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 20;
                    preview.lights[0].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                    preview.lights[1].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                    preview.BeginPreview(new Rect(0, 0, 700, 700), GUIStyle.none);
                    RenderTexture texture;
                    try
                    {
                        ReFitAccessoryValidation.Draw(preview, body, bodyMaterials);
                        ReFitAccessoryValidation.Draw(preview, cloth, clothMaterials);
                        preview.Render(true);
                    }
                    finally { texture = (RenderTexture)preview.EndPreview(); }
                    var previous = RenderTexture.active; var image = new Texture2D(700, 700, TextureFormat.RGB24, false);
                    try
                    {
                        RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, 700, 700), 0, 0); image.Apply();
                        File.WriteAllBytes(Output + "/" + name + "-" + view.name + ".png", image.EncodeToPNG());
                    }
                    finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                }
            }
            finally { preview.Cleanup(); }
        }
    }
}
