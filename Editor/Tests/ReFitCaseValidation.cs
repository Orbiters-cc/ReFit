using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Armature;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Opt-in replays of reported refits, from private local assets under <c>Assets/ReFitCases</c>: a jockstrap made for the
    /// Winterpaw base refitted onto an Ultipaw ("made for another avatar base", then "Yes, it was made for it"), and a
    /// Wickerbeast onesie refitted onto a ThiccWiker ("No, I placed it myself": the different-base coverage). Transforms and
    /// mesh references are copied into a preview scene; the user's scene, selection and avatars are never touched.
    /// Images, CSV and reports go to <see cref="Output"/>.
    /// </summary>
    public static class ReFitCaseValidation
    {
        public const string Output = "Temp/ReFitTests/cases/";
        private const string StatusKey = "Orbiters.ReFit.CaseValidation.Status";

        /// <summary>One reported refit: the avatars and the clothing as the user had them, and the wizard's choices.</summary>
        public sealed class Case
        {
            public string name, sourcePath, targetPath, clothingPath;
            /// <summary>Target body blendshape weights as set in the user's scene (by name).</summary>
            public Dictionary<string, float> targetWeights = new Dictionary<string, float>();
            /// <summary>Body shapes transferred with the fit (MeshAndBlendshape when any).</summary>
            public List<string> shapes = new List<string>();
            /// <summary>The wizard's "No, I placed it myself": the different-base coverage pass.</summary>
            public bool coverDifferentBaseBody;
            /// <summary>Close-ups around the crotch in addition to views framing the whole clothing.</summary>
            public bool fullBody;
        }

        public static readonly Case Jockstrap = new Case
        {
            name = "jockstrap",
            sourcePath = "Assets/my custom winterpaw orbit/default_MasculineCanine.v1.5.fbx",
            targetPath = "Assets/my custom winterpaw orbit/ulti paw v2.8.fbx",
            clothingPath = "Assets/ReFitCases/Jockstrap/own - classicJockstrap - wpCanine.fbx",
            shapes = { "orbit muscles" },
        };

        public static readonly Case Onesie = new Case
        {
            name = "onesie",
            sourcePath = "Assets/ReFitCases/Wickerbeast/Wickerbeast Basebody 2.fbx",
            targetPath = "Assets/ReFitCases/Wickerbeast/hinderance.fbx",
            clothingPath = "Assets/ReFitCases/Wickerbeast/WickerOnesie.prefab",
            targetWeights = { { "Claw.short", 100 }, { "VVorthy - normal fix - Reverted", 100 } },
            shapes = { "VVorthy - normal fix - Reverted" },
            coverDifferentBaseBody = true,
            fullBody = true,
        };

        /// <summary>False, with the first missing asset, when the case's private assets are not in this project.</summary>
        public static bool Available(Case setup, out string missing)
        {
            foreach (var path in new[] { setup.sourcePath, setup.targetPath, setup.clothingPath })
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) { missing = path; return false; }
            missing = null;
            return true;
        }

        public static Case Find(string name) => name == Onesie.name ? Onesie : name == Jockstrap.name ? Jockstrap : null;

        /// <summary>"queued", "done ..." or "failed ..." of the last <see cref="Queue"/>d run.</summary>
        public static string Status => SessionState.GetString(StatusKey, "");

        /// <summary>Runs <see cref="Run"/> on the next editor update so an MCP call returns at once; poll <see cref="Status"/>.</summary>
        /// <param name="settings">Optional "field=value;field=value" overrides of <see cref="ReFitSettings"/> for comparisons.</param>
        /// <param name="render">"none", "fit" (refit renders only) or "all" (also baselines and heatmaps).</param>
        public static void Queue(string caseName, string label, bool replaceArmature = true, string render = "all", string settings = null)
        {
            SessionState.SetString(StatusKey, "queued " + label);
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                EditorApplication.update -= tick;
                try { SessionState.SetString(StatusKey, "done " + label + "\n" + Run(Find(caseName), label, replaceArmature, render, settings).text); }
                catch (Exception e) { SessionState.SetString(StatusKey, "failed " + label + "\n" + e); }
            };
            EditorApplication.update += tick;
            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>Clipping and triangle quality of the refitted clothing in one pose (and shape).</summary>
        public struct Metrics
        {
            public int samples, inside1mm, inside3mm, reversed, stretched;
            public float worstMm, edgeMax;
            /// <summary>At rest: vertices moved more than 5 mm unlike the clothing around them (see <see cref="Spikes"/>), and the worst.</summary>
            public int spikes;
            public float spikeMm;
        }

        public sealed class Result
        {
            public readonly Dictionary<string, Metrics> metrics = new Dictionary<string, Metrics>();
            /// <summary>Clothing vertices whose skin weight on one bone differs from a neighbor's by more than 0.35.</summary>
            public int weightTears = -1;
            /// <summary>Share of the fitted clothing's leg weight on the leg of the other side than the vertex.</summary>
            public float oppositeLegShare = -1;
            public string text;
        }

        /// <summary>The copies a run works on: source and target avatars, the clothing worn under the target.</summary>
        public sealed class Fixture : IDisposable
        {
            public readonly List<GameObject> roots = new List<GameObject>();
            public SkinnedMeshRenderer source, target, clothing;
            public Case setup;
            public UnityEngine.SceneManagement.Scene scene;

            /// <summary>Null when a private asset of the case is missing.</summary>
            public static Fixture Create(Case setup)
            {
                if (!Available(setup, out _)) return null;
                var fixture = new Fixture { setup = setup, scene = EditorSceneManager.NewPreviewScene() };
                try
                {
                    fixture.source = ReFitShortsValidation.CopyModel(setup.sourcePath, "Body", fixture.roots);
                    fixture.target = ReFitShortsValidation.CopyModel(setup.targetPath, "Body", fixture.roots);
                    fixture.clothing = ReFitShortsValidation.CopyModel(setup.clothingPath, null, fixture.roots);
                    foreach (var root in fixture.roots) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, fixture.scene);
                    // Worn on the avatar it should fit, where its creator placed it (its own armature).
                    fixture.clothing.transform.root.SetParent(fixture.target.transform.root, true);
                    var mesh = fixture.target.sharedMesh;
                    foreach (var pair in setup.targetWeights)
                    {
                        int index = mesh.GetBlendShapeIndex(pair.Key);
                        if (index >= 0) fixture.target.SetBlendShapeWeight(index, pair.Value);
                    }
                    return fixture;
                }
                catch { fixture.Dispose(); throw; }
            }

            public Transform ClothingRoot => TopUnder(clothing.transform, target.transform.root);

            /// <summary>The wizard's request: source base given, default settings, the wizard's default tightness.</summary>
            public ReFitRequest Request(bool replaceArmature)
            {
                var request = new ReFitRequest
                {
                    mode = setup.shapes.Count > 0 ? ReFitMode.MeshAndBlendshape : ReFitMode.MeshToMesh, assetRenderer = clothing,
                    sourceAvatar = source.transform.root.gameObject, targetAvatar = target.transform.root.gameObject,
                    targetBlendshapes = setup.shapes.Count > 0 ? new List<string>(setup.shapes) : null,
                    settings = new ReFitSettings { replaceArmature = replaceArmature, transferWeights = replaceArmature, savePrefab = false,
                        captureProjectionDebug = true, maxProjectionDebugGroups = 0, coverDifferentBaseBody = setup.coverDifferentBaseBody }
                };
                // Clothing (underwear and swimwear included) fits snug, as the window's default.
                ReFitSettingsPresets.ApplyTightness(request.settings, ReFitClothingDetection.IsClothing(clothing, request.targetAvatar) ? .93f : 0f);
                return request;
            }

            public void Dispose()
            {
                foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
                roots.Clear();
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        public static Result Run(Case setup, string label, bool replaceArmature = true, string render = "all", string settings = null)
        {
            if (setup == null) throw new ArgumentException("Unknown ReFit case.");
            var owned = new List<Object>();
            var lines = new List<string>();
            var result = new Result();
            Directory.CreateDirectory(Output);
            bool fits = render == "fit" || render == "all", all = render == "all";
            label = setup.name + "-" + label;
            using (var fixture = Fixture.Create(setup))
            {
                if (fixture == null) throw new InvalidOperationException("Missing private fixture assets of case " + setup.name + ".");
                try
                {
                    var request = fixture.Request(replaceArmature);
                    Override(request.settings, settings);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var comp = new ReFitEngine().Run(request);
                    watch.Stop();
                    if (!comp.success) throw new InvalidOperationException(string.Join("\n", comp.report.messages));
                    owned.Add(comp.mesh);
                    lines.Add($"label={label}, replaceArmature={replaceArmature}, cover={request.settings.coverDifferentBaseBody}, settings={settings}, " +
                              $"tightness={request.settings.clearanceTightnessFactor:F3}, engineMs={watch.ElapsedMilliseconds}");
                    lines.AddRange(comp.report.messages.Where(m => m.severity != ReFitSeverity.Info || m.code.Contains("spread") ||
                        m.code.Contains("star") || m.code.Contains("weights")).Select(m => m.ToString()));

                    var outputBones = OutputBones(comp, fixture);
                    lines.Add(Bindings(comp, fixture.clothing));
                    lines.Add("authored " + WeightSides(fixture.clothing.sharedMesh, fixture.clothing.bones, fixture.clothing, out _, out _));
                    if (comp.armatureReplaced)
                        lines.Add("fitted " + WeightSides(comp.mesh, outputBones, fixture.clothing, out result.weightTears, out result.oppositeLegShare));
                    lines.Add(BodyCorrespondence(fixture));
                    var shapeName = comp.secondaryShapeNames?.FirstOrDefault();

                    foreach (string pose in new[] { "rest", "spread", "crouch" })
                    {
                        var restore = Pose(pose, fixture);
                        try
                        {
                            var none = new Dictionary<string, float>();
                            var bodyWeights = Weights(fixture.target);
                            var body = Bake(fixture.target, fixture.target.sharedMesh, fixture.target.bones, bodyWeights); owned.Add(body);
                            var sourceBody = Bake(fixture.source, fixture.source.sharedMesh, fixture.source.bones, none); owned.Add(sourceBody);
                            var original = Bake(fixture.clothing, fixture.clothing.sharedMesh, fixture.clothing.bones, none); owned.Add(original);
                            var fitted = Bake(fixture.clothing, comp.mesh, outputBones, Mirrored(comp, bodyWeights, null)); owned.Add(fitted);
                            var metrics = Measure(sourceBody, original, body, fitted, out string text);
                            // At rest the clothing moves only by the refit; posed, its own and the transferred skinning differ too.
                            if (pose == "rest")
                            {
                                metrics.spikes = Spikes(original, fitted, SpikeThreshold, out metrics.spikeMm, out string spikeText);
                                lines.Add($"{pose} spikes: {spikeText}");
                            }
                            result.metrics[pose] = metrics;
                            lines.Add($"{pose} refit: {text}");
                            if (pose != "rest")
                            {
                                Measure(sourceBody, original, sourceBody, original, out string authored);
                                Measure(sourceBody, original, body, original, out string unfitted);
                                lines.Add($"{pose} authored on source: {authored}");
                                lines.Add($"{pose} unfitted on target: {unfitted}");
                            }
                            else
                            {
                                Measure(sourceBody, original, body, original, out string unfitted);
                                lines.Add($"rest unfitted on target: {unfitted}");
                            }
                            if (all)
                            {
                                Render(label + "-authored-" + pose, sourceBody, original, fixture, setup);
                                Render(label + "-unfitted-" + pose, body, original, fixture, setup);
                            }
                            if (fits) Render(label + "-refit-" + pose, body, fitted, fixture, setup);
                            if (pose == "rest")
                            {
                                Dump(label, fixture, comp, original, fitted);
                                if (all)
                                {
                                    Render(label + "-heat-displacement", body, Colored(fitted, Displacement(original, fitted, .03f), owned), fixture, setup, true);
                                    Render(label + "-heat-authored-weights", sourceBody, Colored(original, WeightColors(fixture.clothing.sharedMesh, fixture.clothing.bones, fixture.target.transform.root), owned), fixture, setup, true);
                                    if (comp.armatureReplaced)
                                        Render(label + "-heat-fitted-weights", body, Colored(fitted, WeightColors(comp.mesh, outputBones, fixture.target.transform.root), owned), fixture, setup, true);
                                }
                            }
                            if (shapeName != null && (pose == "rest" || pose == "crouch") && setup.shapes.Any(n => !bodyWeights.ContainsKey(n)))
                            {
                                var shapedWeights = Weights(fixture.target);
                                foreach (var shape in setup.shapes) shapedWeights[shape] = 100;
                                var shapedBody = Bake(fixture.target, fixture.target.sharedMesh, fixture.target.bones, shapedWeights); owned.Add(shapedBody);
                                var shaped = Bake(fixture.clothing, comp.mesh, outputBones, Mirrored(comp, shapedWeights, null)); owned.Add(shaped);
                                result.metrics[pose + "-shape"] = Measure(sourceBody, original, shapedBody, shaped, out string shapedText);
                                lines.Add($"{pose} {string.Join("+", setup.shapes)}: {shapedText}");
                                var fittedRest = Bake(fixture.clothing, comp.mesh, outputBones, Mirrored(comp, bodyWeights, null)); owned.Add(fittedRest);
                                lines.Add($"{pose} {string.Join("+", setup.shapes)} lag: " + ShapeLag(body, shapedBody, fittedRest, shaped));
                                if (fits) Render(label + "-shape-" + pose, shapedBody, shaped, fixture, setup);
                            }
                        }
                        finally { foreach (var pair in restore) pair.Key.localRotation = pair.Value; }
                    }
                }
                finally { foreach (var o in owned) if (o != null) Object.DestroyImmediate(o); }
            }
            result.text = string.Join("\n", lines);
            File.WriteAllLines(Output + label + ".txt", lines);
            return result;
        }

        /// <summary>How far a vertex may move unlike the clothing around it before it counts as a spike (metres).</summary>
        internal const float SpikeThreshold = 0.005f;

        /// <summary>
        /// Vertices whose move from <paramref name="original"/> to <paramref name="fitted"/> differs from the mean move of
        /// their neighbours by more than <paramref name="threshold"/>: single vertices pulled out of the surface (spikes, tents),
        /// which clipping counts miss. Coincident vertices (UV and normal seams) are welded first.
        /// </summary>
        internal static int Spikes(Mesh original, Mesh fitted, float threshold, out float worstMm, out string text)
        {
            var before = original.vertices;
            var after = fitted.vertices;
            var triangles = fitted.triangles;
            var weld = new int[before.Length];
            var first = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < before.Length; i++)
            {
                var key = Vector3Int.RoundToInt(before[i] * 100000f);
                if (!first.TryGetValue(key, out int w)) first[key] = w = i;
                weld[i] = w;
            }
            var neighbours = new HashSet<int>[before.Length];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[triangles[t + e]], b = weld[triangles[t + (e + 1) % 3]];
                    if (a == b) continue;
                    (neighbours[a] ??= new HashSet<int>()).Add(b);
                    (neighbours[b] ??= new HashSet<int>()).Add(a);
                }
            int count = 0;
            float worst = 0f;
            var worstAt = Vector3.zero;
            for (int i = 0; i < before.Length; i++)
            {
                if (weld[i] != i || neighbours[i] == null) continue;
                var mean = Vector3.zero;
                foreach (int n in neighbours[i]) mean += after[n] - before[n];
                mean /= neighbours[i].Count;
                float deviation = (after[i] - before[i] - mean).magnitude;
                if (deviation > threshold) count++;
                if (deviation > worst) { worst = deviation; worstAt = after[i]; }
            }
            worstMm = worst * 1000f;
            text = $"over{threshold * 1000f:0}mm={count}, worst={worstMm:F1}mm@({worstAt.x:F3}, {worstAt.y:F3}, {worstAt.z:F3})";
            return count;
        }

        /// <summary>The refit shape at 100 and each transferred shape at the weight of the body shape it follows.</summary>
        private static Dictionary<string, float> Mirrored(ReFitComputation comp, Dictionary<string, float> bodyWeights, string unused)
        {
            var weights = new Dictionary<string, float> { { comp.primaryShapeName ?? "refit", 100 } };
            if (comp.secondaryShapeNames != null)
                for (int i = 0; i < comp.secondaryShapeNames.Length; i++)
                    if (bodyWeights.TryGetValue(comp.secondarySourceShapeNames[i], out float weight)) weights[comp.secondaryShapeNames[i]] = weight;
            return weights;
        }

        private static Dictionary<string, float> Weights(SkinnedMeshRenderer renderer)
        {
            var weights = new Dictionary<string, float>();
            var mesh = renderer.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (renderer.GetBlendShapeWeight(i) != 0) weights[mesh.GetBlendShapeName(i)] = renderer.GetBlendShapeWeight(i);
            return weights;
        }

        /// <summary>Applies "field=value;field=value" to public <see cref="ReFitSettings"/> fields (bool, int, float).</summary>
        internal static void Override(ReFitSettings settings, string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;
            foreach (var pair in spec.Split(';').Where(p => p.Contains("=")))
            {
                var parts = pair.Split('=');
                if (parts[0].Trim() == "tightness")
                {
                    ReFitSettingsPresets.ApplyTightness(settings, float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }
                var field = typeof(ReFitSettings).GetField(parts[0].Trim());
                if (field == null) throw new ArgumentException("Unknown ReFit setting " + parts[0]);
                field.SetValue(settings, Convert.ChangeType(parts[1].Trim(), field.FieldType, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static Transform TopUnder(Transform t, Transform root)
        {
            while (t.parent != null && t.parent != root) t = t.parent;
            return t;
        }

        /// <summary>The bones the result is skinned to, resolved on the copies.</summary>
        internal static Transform[] OutputBones(ReFitComputation comp, Fixture fixture)
        {
            if (!comp.armatureReplaced) return fixture.clothing.bones;
            return comp.bones.Select(b => b == null ? null
                : b.origin == ReFitBoneOrigin.Target ? ReFitUtility.ResolvePath(fixture.target.transform.root, b.path)
                : b.origin == ReFitBoneOrigin.Asset ? ReFitUtility.ResolvePath(fixture.ClothingRoot, b.path)
                : ReFitUtility.ResolvePath(fixture.source.transform.root, b.path)).ToArray();
        }

        /// <summary>
        /// Thighs turned out ("spread") or raised and turned out with bent knees ("crouch") on every copied rig: the bones
        /// named like humanoid thighs and shins, each turned once, parents first.
        /// </summary>
        internal static Dictionary<Transform, Quaternion> Pose(string pose, Fixture fixture)
        {
            var restore = new Dictionary<Transform, Quaternion>();
            if (pose == "rest") return restore;
            var root = fixture.target.transform.root;
            var visited = new HashSet<Transform>();
            foreach (var rig in fixture.roots.Select(r => r.transform.root).Distinct())
                foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                {
                    if (!visited.Add(t) || !BoneNames.TryInferHumanoid(t.name, out var bone)) continue;
                    bool thigh = bone == HumanBodyBones.LeftUpperLeg || bone == HumanBodyBones.RightUpperLeg;
                    bool shin = bone == HumanBodyBones.LeftLowerLeg || bone == HumanBodyBones.RightLowerLeg;
                    if (!thigh && !(shin && pose == "crouch")) continue;
                    restore[t] = t.localRotation;
                    float side = Mathf.Sign(Vector3.Dot(t.position - root.position, root.right));
                    if (thigh)
                    {
                        var rotation = Quaternion.AngleAxis(side * (pose == "spread" ? 35f : 25f), root.forward);
                        if (pose == "crouch") rotation = rotation * Quaternion.AngleAxis(-75f, root.right);
                        t.rotation = rotation * t.rotation;
                    }
                    else t.rotation = Quaternion.AngleAxis(100f, root.right) * t.rotation;
                }
            return restore;
        }

        internal static Mesh Bake(SkinnedMeshRenderer source, Mesh mesh, Transform[] bones, Dictionary<string, float> weights)
        {
            var go = new GameObject("__ReFitCaseBake") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                go.transform.localScale = source.transform.lossyScale;
                var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
                renderer.bones = bones;
                var overrides = Enumerable.Range(0, mesh.blendShapeCount).ToDictionary(i => i, i => weights.TryGetValue(mesh.GetBlendShapeName(i), out float w) ? w / 100f : 0f);
                var snapshot = MeshSnapshot.Capture(renderer, false, overrides, null);
                var baked = Object.Instantiate(mesh); baked.ClearBlendShapes();
                baked.vertices = snapshot.worldVertices; baked.normals = snapshot.worldNormals; baked.RecalculateBounds();
                return baked;
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// Clothing samples (vertices, triangle centers) outside the body they were authored on, now under the body surface,
        /// with edge stretch and orientation reversals against the authored clothing in the same pose. Nearest-face signs are
        /// cross-checked with the solid-angle winding of the body for the deepest ones.
        /// </summary>
        internal static Metrics Measure(Mesh authoredBody, Mesh authored, Mesh body, Mesh clothing, out string text)
        {
            var a = new MeshSnapshot { worldVertices = authoredBody.vertices, triangles = authoredBody.triangles };
            var b = new MeshSnapshot { worldVertices = body.vertices, triangles = body.triangles };
            var ia = SurfaceBvh.Build(a); var ib = SurfaceBvh.Build(b);
            var p = authored.vertices; var q = clothing.vertices; var tris = clothing.triangles;
            var metrics = new Metrics();
            var deepest = new List<(float depth, Vector3 at)>();
            for (int s = 0; s < p.Length + tris.Length / 3; s++)
            {
                int t = (s - p.Length) * 3;
                var x = s < p.Length ? p[s] : (p[tris[t]] + p[tris[t + 1]] + p[tris[t + 2]]) / 3f;
                var y = s < p.Length ? q[s] : (q[tris[t]] + q[tris[t + 1]] + q[tris[t + 2]]) / 3f;
                var ha = ia.ClosestPoint(x, .1f, null); var hb = ib.ClosestPoint(y, .1f, null);
                if (!ha.found || !hb.found) continue;
                if (Vector3.Dot(x - ha.position, a.FaceNormal(ha.triangle)) < 0) continue;
                metrics.samples++;
                float gap = Vector3.Dot(y - hb.position, b.FaceNormal(hb.triangle));
                if (gap < -.001f) { metrics.inside1mm++; deepest.Add((-gap, y)); }
                if (gap < -.003f) metrics.inside3mm++;
                metrics.worstMm = Mathf.Max(metrics.worstMm, -gap * 1000);
            }
            int confirmed = 0;
            foreach (var candidate in deepest.OrderByDescending(d => d.depth).Take(24))
                if (Math.Abs(ReFitMeshIntersectionChecks.Winding(b, candidate.at)) > .5) confirmed++;
            var where = deepest.OrderByDescending(d => d.depth).Take(3).Select(d => $"{d.depth * 1000:F1}mm@{d.at:F3}");

            var ratios = new List<float>();
            var reversedAt = new List<Vector3>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                bool bad = false;
                for (int j = 0; j < 3; j++)
                {
                    int u = tris[t + j], v = tris[t + (j + 1) % 3]; float length = (p[u] - p[v]).magnitude;
                    if (length < 1e-6f) continue;
                    float ratio = (q[u] - q[v]).magnitude / length; ratios.Add(ratio);
                    if (ratio > 2 || ratio < .5f) bad = true;
                }
                if (bad) metrics.stretched++;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if (Vector3.Dot(Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]), Vector3.Cross(q[i1] - q[i0], q[i2] - q[i0])) < 0)
                {
                    metrics.reversed++;
                    if (reversedAt.Count < 4) reversedAt.Add((q[i0] + q[i1] + q[i2]) / 3);
                }
            }
            ratios.Sort();
            metrics.edgeMax = ratios.Count > 0 ? ratios.Last() : 1;
            text = $"samples={metrics.samples}, inside>1mm={metrics.inside1mm}, inside>3mm={metrics.inside3mm}, worst={metrics.worstMm:F1}mm, " +
                   $"windingConfirmedOfTop24={confirmed}, deepest=[{string.Join(" ", where)}]; " +
                   (ratios.Count > 0 ? $"edgeP05={ratios[(int)(ratios.Count * .05)]:F3}, edgeP95={ratios[(int)(ratios.Count * .95)]:F3}, " : "") +
                   $"max={metrics.edgeMax:F3}, trianglesOutside0.5-2x={metrics.stretched}, reversed={metrics.reversed} [{string.Join(" ", reversedAt.Select(r => r.ToString("F3")))}]";
            return metrics;
        }

        /// <summary>
        /// How the clothing follows a body shape: for clothing vertices within 2 cm of the body, the body's motion at the
        /// nearest point along its normal minus the clothing's own motion along it (positive: the body overtakes the clothing).
        /// </summary>
        internal static string ShapeLag(Mesh body, Mesh shapedBody, Mesh clothing, Mesh shapedClothing)
        {
            var b = new MeshSnapshot { worldVertices = body.vertices, triangles = body.triangles };
            var index = SurfaceBvh.Build(b);
            var bodyDelta = shapedBody.vertices.Select((v, i) => v - b.worldVertices[i]).ToArray();
            var p = clothing.vertices; var q = shapedClothing.vertices;
            int near = 0, lagging = 0; float worst = 0;
            var worstAt = new List<(float lag, Vector3 at)>();
            for (int i = 0; i < p.Length; i++)
            {
                var hit = index.ClosestPoint(p[i], .02f, null);
                if (!hit.found) continue;
                near++;
                int t = hit.triangle * 3;
                var d = bodyDelta[b.triangles[t]] * hit.bary.x + bodyDelta[b.triangles[t + 1]] * hit.bary.y + bodyDelta[b.triangles[t + 2]] * hit.bary.z;
                var n = b.FaceNormal(hit.triangle);
                float lag = Vector3.Dot(d - (q[i] - p[i]), n);
                if (lag > .003f) { lagging++; worstAt.Add((lag, p[i])); }
                worst = Mathf.Max(worst, lag);
            }
            return $"nearVertices={near}, lagging>3mm={lagging}, worst={worst * 1000:F1}mm, at=[{string.Join(" ", worstAt.OrderByDescending(w => w.lag).Take(4).Select(w => $"{w.lag * 1000:F0}mm@{w.at:F3}"))}]";
        }

        /// <summary>Source/transfer hits on the other side of the body's midline than the clothing point they bind, and far bindings.</summary>
        internal static string Bindings(ReFitComputation comp, SkinnedMeshRenderer clothing)
        {
            var points = comp.projectionDebug?.points;
            if (points == null) return "bindings: no projection debug";
            var m = clothing.transform.localToWorldMatrix;
            float mid = clothing.transform.root.position.x;
            int sourceCross = 0, targetCross = 0, relaxed = 0, invalid = 0, over2 = 0, over5 = 0, transferOver2 = 0;
            var examples = new List<string>();
            foreach (var p in points)
            {
                if (!p.sourceValid || !p.targetValid) { invalid++; continue; }
                var at = m.MultiplyPoint3x4(p.assetLocalPoint); var s = m.MultiplyPoint3x4(p.sourceHitLocalPoint); var t = m.MultiplyPoint3x4(p.targetHitLocalPoint);
                bool sideA = at.x - mid > .004f, sideB = at.x - mid < -.004f;
                bool sc = (sideA && s.x - mid < -.002f) || (sideB && s.x - mid > .002f);
                bool tc = (sideA && t.x - mid < -.002f) || (sideB && t.x - mid > .002f);
                if (sc) sourceCross++;
                if (tc) targetCross++;
                if (p.sourceUsedRelaxedFallback || p.targetUsedRelaxedFallback) relaxed++;
                if (p.sourceDistance > .02f) over2++;
                if (p.sourceDistance > .05f) over5++;
                if (p.targetDistance > .02f) transferOver2++;
                if ((sc || tc || p.sourceDistance > .05f) && examples.Count < 8)
                    examples.Add($"g{p.groupIndex} at={at:F3} src={s:F3}({p.sourceHitRegion}) tgt={t:F3}({p.targetHitRegion}) region={p.assetRegion} d={p.sourceDistance * 1000:F0}mm");
            }
            return $"bindings: groups={points.Length}, invalid={invalid}, sourceCrossesMidline={sourceCross}, transferCrossesMidline={targetCross}, relaxed={relaxed}, " +
                   $"sourceDistance>2cm={over2}, >5cm={over5}, transferDistance>2cm={transferOver2}\n  " + string.Join("\n  ", examples);
        }

        /// <summary>
        /// Skin weight on the opposite leg (by bone side) for clothing vertices clearly on one side, per-bone totals, and
        /// tears: welded neighbors whose weight on one bone differs by more than 0.35 (they separate when that bone moves).
        /// </summary>
        internal static string WeightSides(Mesh mesh, Transform[] bones, SkinnedMeshRenderer clothing, out int tears, out float oppositeShare)
        {
            var root = clothing.transform.root;
            var vertices = mesh.vertices; var weights = mesh.boneWeights;
            var m = clothing.transform.localToWorldMatrix;
            float mid = root.position.x;
            float opposite = 0, legTotal = 0; int contaminated = 0;
            var used = new Dictionary<string, float>();
            for (int i = 0; i < vertices.Length; i++)
            {
                float x = m.MultiplyPoint3x4(vertices[i]).x - mid;
                foreach (var (index, value) in Influences(weights[i]))
                {
                    if (value <= 0 || index < 0 || index >= bones.Length || bones[index] == null) continue;
                    string name = bones[index].name;
                    used[name] = (used.TryGetValue(name, out float total) ? total : 0) + value;
                    float boneSide = Vector3.Dot(bones[index].position - root.position, root.right);
                    if (Mathf.Abs(boneSide) < .02f || Mathf.Abs(x) < .01f) continue;
                    legTotal += value;
                    if (Mathf.Sign(boneSide) != Mathf.Sign(x)) { opposite += value; if (value > .1f) contaminated++; }
                }
            }
            tears = 0;
            var triangles = mesh.triangles;
            var torn = new HashSet<int>();
            for (int t = 0; t < triangles.Length; t += 3)
                for (int j = 0; j < 3; j++)
                {
                    int u = triangles[t + j], v = triangles[t + (j + 1) % 3];
                    foreach (var (index, value) in Influences(weights[u]).Concat(Influences(weights[v])))
                        if (value > 0 && Mathf.Abs(WeightOf(weights[u], index) - WeightOf(weights[v], index)) > .35f) { torn.Add(u); torn.Add(v); break; }
                }
            tears = torn.Count;
            oppositeShare = legTotal > 0 ? opposite / legTotal : 0;
            return $"weights: oppositeSideWeight={opposite:F2}/{legTotal:F2}, verticesOver0.1Opposite={contaminated}, tornVertices={tears}, " +
                   $"bones=[{string.Join(", ", used.OrderByDescending(p => p.Value).Select(p => $"{p.Key}:{p.Value:F0}"))}]";
        }

        private static IEnumerable<(int index, float value)> Influences(BoneWeight w)
        {
            yield return (w.boneIndex0, w.weight0);
            yield return (w.boneIndex1, w.weight1);
            yield return (w.boneIndex2, w.weight2);
            yield return (w.boneIndex3, w.weight3);
        }

        private static float WeightOf(BoneWeight w, int bone) =>
            (w.boneIndex0 == bone ? w.weight0 : 0) + (w.boneIndex1 == bone ? w.weight1 : 0) + (w.boneIndex2 == bone ? w.weight2 : 0) + (w.boneIndex3 == bone ? w.weight3 : 0);

        /// <summary>
        /// Where the bodies disagree around the clothing: source body points near the clothing, their nearest target point,
        /// and whether the dominant-weight regions of both triangles agree.
        /// </summary>
        internal static string BodyCorrespondence(Fixture fixture)
        {
            var source = MeshSnapshot.Capture(fixture.source, false, null, null);
            var target = MeshSnapshot.Capture(fixture.target, false, null, null);
            var clothing = MeshSnapshot.Capture(fixture.clothing, false, null, null);
            var sourceRegions = TriangleRegions(source, fixture.source.transform.root.gameObject);
            var targetRegions = TriangleRegions(target, fixture.target.transform.root.gameObject);
            var bounds = new Bounds(clothing.worldVertices[0], Vector3.zero);
            foreach (var v in clothing.worldVertices) bounds.Encapsulate(v);
            bounds.Expand(.03f);
            var targetIndex = SurfaceBvh.Build(target);
            var distances = new List<float>(); int mismatch = 0;
            var counts = new Dictionary<string, int>();
            for (int t = 0; t < source.triangles.Length / 3; t++)
            {
                var c = (source.worldVertices[source.triangles[t * 3]] + source.worldVertices[source.triangles[t * 3 + 1]] + source.worldVertices[source.triangles[t * 3 + 2]]) / 3;
                if (!bounds.Contains(c)) continue;
                var hit = targetIndex.ClosestPoint(c, .1f, null);
                if (!hit.found) continue;
                distances.Add(hit.distance);
                string key = sourceRegions[t] + "->" + targetRegions[hit.triangle];
                counts[key] = (counts.TryGetValue(key, out int n) ? n : 0) + 1;
                if (sourceRegions[t] != targetRegions[hit.triangle]) mismatch++;
            }
            if (distances.Count == 0) return "body: no source triangle near the clothing";
            distances.Sort();
            return $"body: sourceTrianglesNearClothing={distances.Count}, nearestTargetMm p50={distances[distances.Count / 2] * 1000:F1} " +
                   $"p95={distances[(int)(distances.Count * .95f)] * 1000:F1} max={distances.Last() * 1000:F1}, regionMismatch={mismatch} " +
                   $"[{string.Join(", ", counts.OrderByDescending(p => p.Value).Take(8).Select(p => p.Key + ":" + p.Value))}]";
        }

        private static BodyRegion[] TriangleRegions(MeshSnapshot body, GameObject root)
        {
            var map = HumanoidBoneMapper.GetHumanoidMap(root, null);
            var boneRegions = HumanoidBoneMapper.ClassifyBones(root, map);
            var perBone = body.bones.Select(b => b != null && boneRegions.TryGetValue(b, out var r) ? r : BodyRegion.Unknown).ToArray();
            var result = new BodyRegion[body.triangles.Length / 3];
            for (int t = 0; t < result.Length; t++)
            {
                var votes = new float[7];
                for (int k = 0; k < 3; k++)
                {
                    float best = 0; var region = BodyRegion.Unknown;
                    foreach (var (index, value) in Influences(body.boneWeights[body.triangles[t * 3 + k]]))
                        if (value > best && index >= 0 && index < perBone.Length && perBone[index] != BodyRegion.Unknown) { best = value; region = perBone[index]; }
                    votes[(int)region + 1] += 1;
                }
                int pick = 0;
                for (int r = 1; r < votes.Length; r++) if (votes[r] > votes[pick]) pick = r;
                result[t] = (BodyRegion)(pick - 1);
            }
            return result;
        }

        /// <summary>Per vertex: authored and fitted world positions (rest), binding diagnostics of its group, as CSV.</summary>
        private static void Dump(string label, Fixture fixture, ReFitComputation comp, Mesh original, Mesh fitted)
        {
            var asset = MeshSnapshot.Capture(fixture.clothing, true, null, null);
            var byGroup = new Dictionary<int, ReFitProjectionDebugPoint>();
            if (comp.projectionDebug?.points != null) foreach (var p in comp.projectionDebug.points) byGroup[p.groupIndex] = p;
            var m = fixture.clothing.transform.localToWorldMatrix;
            var a = original.vertices; var b = fitted.vertices;
            var csv = new List<string> { "v,group,ax,ay,az,fx,fy,fz,region,srcHitX,srcHitY,srcHitZ,srcDist,srcRegion,tgtHitX,tgtHitY,tgtHitZ,tgtDist,tgtRegion,falloff,decision,chainX,chainY,chainZ,rawX,rawY,rawZ,refitX,refitY,refitZ" };
            var raw = comp.debugPrimaryRawLocalDeltas;
            var refit = new Vector3[comp.mesh.vertexCount];
            int refitIndex = comp.primaryShapeName != null ? comp.mesh.GetBlendShapeIndex(comp.primaryShapeName) : -1;
            if (refitIndex >= 0) comp.mesh.GetBlendShapeFrameVertices(refitIndex, 0, refit, null, null);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < a.Length; i++)
            {
                int g = asset.groupOfVertex[i];
                byGroup.TryGetValue(g, out var p);
                var s = p != null ? m.MultiplyPoint3x4(p.sourceHitLocalPoint) : Vector3.zero;
                var t = p != null ? m.MultiplyPoint3x4(p.targetHitLocalPoint) : Vector3.zero;
                var c = p != null && p.chainedTargetValid ? m.MultiplyPoint3x4(p.chainedTargetLocalPoint) : Vector3.zero;
                var r = raw != null && i < raw.Length ? m.MultiplyVector(raw[i]) : Vector3.zero;
                var d = m.MultiplyVector(refit[i]);
                csv.Add(string.Join(",", new object[] { i, g, a[i].x, a[i].y, a[i].z, b[i].x, b[i].y, b[i].z, p?.assetRegion, s.x, s.y, s.z, p?.sourceDistance, p?.sourceHitRegion, t.x, t.y, t.z, p?.targetDistance, p?.targetHitRegion, p?.falloff, p?.weightDecision, c.x, c.y, c.z, r.x, r.y, r.z, d.x, d.y, d.z }
                    .Select(o => o is float f ? f.ToString("F5", inv) : Convert.ToString(o, inv))));
            }
            File.WriteAllLines(Output + label + "-vertices.csv", csv);
        }

        private static Color[] Displacement(Mesh before, Mesh after, float full)
        {
            var a = before.vertices; var b = after.vertices;
            return a.Select((v, i) => Heat((b[i] - v).magnitude / full)).ToArray();
        }

        private static Color Heat(float t)
        {
            t = Mathf.Clamp01(t);
            return t < .5f ? Color.Lerp(new Color(.1f, .25f, .9f), new Color(.1f, .9f, .3f), t * 2) : Color.Lerp(new Color(.1f, .9f, .3f), new Color(.95f, .15f, .1f), t * 2 - 1);
        }

        /// <summary>Red: bones on the avatar's left of the midline, green: right, blue: centered bones (hips, spine).</summary>
        private static Color[] WeightColors(Mesh mesh, Transform[] bones, Transform root)
        {
            var side = bones.Select(b => b == null ? 0f : Vector3.Dot(b.position - root.position, root.right)).ToArray();
            return mesh.boneWeights.Select(w =>
            {
                var c = new Color(0, 0, 0, 1);
                foreach (var (index, value) in Influences(w))
                {
                    if (value <= 0 || index < 0 || index >= side.Length) continue;
                    if (side[index] < -.02f) c.r += value; else if (side[index] > .02f) c.g += value; else c.b += value;
                }
                return c;
            }).ToArray();
        }

        private static Mesh Colored(Mesh mesh, Color[] colors, List<Object> owned)
        {
            var copy = Object.Instantiate(mesh);
            copy.colors = colors;
            owned.Add(copy);
            return copy;
        }

        private static readonly string[] ViewNames = { "front", "side", "low-quarter", "below", "back" };
        private static readonly Vector3[] ViewDirections =
        {
            Vector3.forward, Vector3.right, new Vector3(.7f, -.55f, .8f).normalized, new Vector3(0, -1, .25f).normalized, Vector3.back
        };

        /// <summary>
        /// Crotch close-ups (centered between the target's thighs) from five directions; for full-body clothing also front,
        /// side and back views framing the whole clothing. Vertex-colored clothing is drawn unlit.
        /// </summary>
        internal static void Render(string label, Mesh body, Mesh clothing, Fixture fixture, Case setup, bool colored = false)
        {
            var map = HumanoidBoneMapper.GetHumanoidMap(fixture.target.transform.root.gameObject, null);
            Vector3 crotch = clothing.bounds.center;
            float size = Mathf.Max(clothing.bounds.extents.x, clothing.bounds.extents.y, clothing.bounds.extents.z) * 1.25f;
            if (setup.fullBody && map.TryGetValue(HumanBodyBones.LeftUpperLeg, out var left) && map.TryGetValue(HumanBodyBones.RightUpperLeg, out var right))
            {
                crotch = (left.position + right.position) / 2 + Vector3.down * .1f;
                size = .34f;
            }
            RenderViews(label, body, clothing, colored, crotch, size, ViewNames);
            if (setup.fullBody)
                RenderViews(label + "-full", body, clothing, colored, clothing.bounds.center, Mathf.Max(clothing.bounds.extents.y, clothing.bounds.extents.x * .9f) * 1.15f,
                    "front", "side", "back");
        }

        private static void RenderViews(string label, Mesh body, Mesh clothing, bool colored, Vector3 center, float size, params string[] views)
        {
            var preview = new PreviewRenderUtility();
            var skin = new Material(Shader.Find("Standard")) { color = new Color(.62f, .5f, .45f) };
            var cloth = colored ? new Material(Shader.Find("Sprites/Default")) : new Material(Shader.Find("Standard")) { color = new Color(.12f, .62f, .72f) };
            try
            {
                preview.camera.orthographic = true; preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.11f, .12f, .14f); preview.ambientColor = new Color(.55f, .55f, .55f);
                preview.lights[0].intensity = 1.15f; preview.lights[1].intensity = .7f;
                for (int view = 0; view < ViewNames.Length; view++)
                {
                    if (!views.Contains(ViewNames[view])) continue;
                    preview.camera.transform.position = center + ViewDirections[view] * 4;
                    preview.camera.transform.LookAt(center, Mathf.Abs(ViewDirections[view].y) > .9f ? Vector3.forward : Vector3.up);
                    preview.camera.orthographicSize = size;
                    preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 20;
                    preview.lights[0].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(25, -30, 0);
                    preview.lights[1].transform.rotation = preview.camera.transform.rotation * Quaternion.Euler(0, 140, 0);
                    preview.BeginPreview(new Rect(0, 0, 900, 900), GUIStyle.none);
                    for (int i = 0; i < body.subMeshCount; i++) preview.DrawMesh(body, Matrix4x4.identity, skin, i);
                    for (int i = 0; i < clothing.subMeshCount; i++) preview.DrawMesh(clothing, Matrix4x4.identity, cloth, i);
                    preview.Render(true);
                    var render = (RenderTexture)preview.EndPreview();
                    var previous = RenderTexture.active; var image = new Texture2D(900, 900, TextureFormat.RGB24, false);
                    try
                    {
                        RenderTexture.active = render; image.ReadPixels(new Rect(0, 0, 900, 900), 0, 0); image.Apply();
                        File.WriteAllBytes(Output + label + "-" + ViewNames[view] + ".png", image.EncodeToPNG());
                    }
                    finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                }
            }
            finally { preview.Cleanup(); Object.DestroyImmediate(skin); Object.DestroyImmediate(cloth); }
        }
    }
}
