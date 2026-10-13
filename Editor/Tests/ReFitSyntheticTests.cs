using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
#endif
using Orbiters.Toolkit.Editor.Refit;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    // Small procedural bodies and garments that run on any machine: the drape over a grown belly, skin enclosed in another
    // part of the body (and not under a fluff card), the authored depth of sagging edges, the BVH refit, tiny triangles'
    // normals, and the window locking its choices while a run works.
    public static partial class ReFitDeterministicTestRunner
    {
        private const float BodyRadius = .1f, SleeveRadius = .105f, BellyDepth = .02f, BeltThickness = .003f;
        private const int BeltAround = 72, BeltSection = 6;

        private static void RunSyntheticChecks(List<string> failures)
        {
            RunCase(failures, "Drape covers a grown belly and carries a belt with the fabric under each part of it", Drape_CoversBellyAndCarriesRingLocally);
            RunCase(failures, "Drape stops when the refit is cancelled", Drape_StopsWhenCancelled);
            RunCase(failures, "Enclosed skin is hidden inside closed parts only, never under a fluff card", EnclosedSkin_OnlyInsideClosedParts);
            RunCase(failures, "Tiny triangles keep their face normal", FaceNormal_TinyTrianglesKeepTheirDirection);
            RunCase(failures, "A refitted BVH answers like a rebuilt one", Bvh_RefitMatchesRebuild);
            RunCase(failures, "Coarse clothing keeps the depth its edges sagged into the body as authored", AuthoredClearance_SaggingEdgesStayAsAuthored);
            RunCase(failures, "The window locks and snapshots its choices while a run works", Window_LocksChoicesWhileRunning);
            RunCase(failures, "A window run holds the avatar, keeps the original under added shapes and gives a failed fit back",
                Window_RunKeepsOriginalsAndEarlierFits);
        }

        // ---- Geometry -----------------------------------------------------------------------------------------------------

        // An open cylinder (a sleeve, a torso) around the Y axis, faces turned out; radius per angle and height.
        private static void AddCylinder(List<Vector3> vertices, List<int> triangles, int around, int rows, float bottom, float top,
            Func<float, float, float> radius)
        {
            int start = vertices.Count;
            for (int i = 0; i < around; i++)
            {
                float a = i * 2 * Mathf.PI / around;
                for (int j = 0; j < rows; j++)
                {
                    float y = Mathf.Lerp(bottom, top, j / (rows - 1f));
                    float r = radius(a, y);
                    vertices.Add(new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a)));
                }
            }
            for (int i = 0; i < around; i++)
                for (int j = 0; j + 1 < rows; j++)
                {
                    int p = start + i * rows + j, q = start + (i + 1) % around * rows + j;
                    triangles.AddRange(new[] { p, p + 1, q, q, p + 1, q + 1 });
                }
        }

        // A closed ring around the Y axis at height y (a belt), faces turned out of its tube.
        private static List<int> AddRing(List<Vector3> vertices, List<int> triangles, float radius, float thickness, float y, int around, int section)
        {
            int start = vertices.Count;
            var added = new List<int>();
            for (int i = 0; i < around; i++)
                for (int j = 0; j < section; j++)
                {
                    float a = i * 2 * Mathf.PI / around, b = j * 2 * Mathf.PI / section;
                    added.Add(vertices.Count);
                    vertices.Add(new Vector3((radius + thickness * Mathf.Cos(b)) * Mathf.Cos(a), y + thickness * Mathf.Sin(b),
                        (radius + thickness * Mathf.Cos(b)) * Mathf.Sin(a)));
                    int p = start + i * section + j, q = start + (i + 1) % around * section + j;
                    int r = start + i * section + (j + 1) % section, s = start + (i + 1) % around * section + (j + 1) % section;
                    triangles.AddRange(new[] { p, r, q, q, r, s });
                }
            return added;
        }

        // A closed box, each face a grid of cells, faces turned out.
        private static void AddBox(List<Vector3> vertices, List<int> triangles, Vector3 center, Vector3 size, int cells)
        {
            var half = size * .5f;
            var faces = new[]
            {
                (n: Vector3.forward, u: Vector3.right, v: Vector3.up), (n: Vector3.back, u: Vector3.up, v: Vector3.right),
                (n: Vector3.right, u: Vector3.up, v: Vector3.forward), (n: Vector3.left, u: Vector3.forward, v: Vector3.up),
                (n: Vector3.up, u: Vector3.forward, v: Vector3.right), (n: Vector3.down, u: Vector3.right, v: Vector3.forward)
            };
            foreach (var (n, u, v) in faces)
            {
                var du = Vector3.Scale(u, size); var dv = Vector3.Scale(v, size);
                var origin = center + Vector3.Scale(n, half) - du * .5f - dv * .5f;
                AddGrid(vertices, triangles, origin, du, dv, cells);
            }
        }

        // A flat grid from origin along du and dv; its faces turn along Cross(du, dv).
        private static void AddGrid(List<Vector3> vertices, List<int> triangles, Vector3 origin, Vector3 du, Vector3 dv, int cells)
        {
            int start = vertices.Count;
            for (int i = 0; i <= cells; i++)
                for (int j = 0; j <= cells; j++)
                    vertices.Add(origin + du * (i / (float)cells) + dv * (j / (float)cells));
            for (int i = 0; i < cells; i++)
                for (int j = 0; j < cells; j++)
                {
                    int p = start + i * (cells + 1) + j, q = p + cells + 1;
                    triangles.AddRange(new[] { p, q, q + 1, p, q + 1, p + 1 });
                }
        }

        // The grown body: the base's cylinder with a belly at the front (+Z), up to BellyDepth out at the navel.
        private static float BellyRadius(float angle, float y)
        {
            float front = Mathf.Max(0f, Mathf.Sin(angle));
            float height = Mathf.Clamp01(1f - y * y / (.12f * .12f));
            return BodyRadius + BellyDepth * front * front * height * height;
        }

        private static float AngleOf(Vector3 point) => Mathf.Atan2(point.z, point.x);

        private static MeshSnapshot Snapshot(List<Vector3> vertices, List<int> triangles, bool topology) =>
            MeshSnapshot.FromWorld(vertices.ToArray(), triangles.ToArray(), topology);

        // ---- Drape --------------------------------------------------------------------------------------------------------

        private struct DrapeFixture
        {
            public MeshSnapshot asset, source, target;
            public bool[] belt;
            public int sleeveVertices;
            public ReFitDrape drape;
        }

        private static DrapeFixture CreateDrapeFixture()
        {
            var bodyVertices = new List<Vector3>(); var bodyTriangles = new List<int>();
            AddCylinder(bodyVertices, bodyTriangles, 96, 61, -.3f, .3f, (a, y) => BodyRadius);
            var source = Snapshot(bodyVertices, bodyTriangles, false);
            bodyVertices.Clear(); bodyTriangles.Clear();
            AddCylinder(bodyVertices, bodyTriangles, 96, 61, -.3f, .3f, BellyRadius);
            var target = Snapshot(bodyVertices, bodyTriangles, false);

            var vertices = new List<Vector3>(); var triangles = new List<int>();
            AddCylinder(vertices, triangles, 72, 31, -.15f, .15f, (a, y) => SleeveRadius);
            int sleeve = vertices.Count;
            var beltVertices = AddRing(vertices, triangles, .112f, BeltThickness, 0f, BeltAround, BeltSection);
            var asset = Snapshot(vertices, triangles, true);
            var belt = new bool[asset.GroupCount];
            foreach (int v in beltVertices) belt[asset.groupOfVertex[v]] = true;
            var drape = new ReFitDrape(asset, null, source, SurfaceBvh.Build(source), target, null, null, belt, null);
            return new DrapeFixture { asset = asset, source = source, target = target, belt = belt, sleeveVertices = sleeve, drape = drape };
        }

        internal static void Drape_CoversBellyAndCarriesRingLocally()
        {
            var fixture = CreateDrapeFixture();
            var asset = fixture.asset;
            var deltas = new Vector3[asset.GroupCount];
            var report = new ReFitReport();
            float progress = -1f;
            fixture.drape.Apply(fixture.target, null, null, deltas, report, "synthetic belly", CancellationToken.None, t => progress = t);
            AssertTrue(Mathf.Approximately(progress, 1f), "The drape did not report its progress to the end.");
            AssertReportContains(report, "drape", "The drape did not report what it covered.");
            AssertReportDoesNotContain(report, "drape-limited", "The drape left skin showing through the sleeve.");

            float worstSleeve = float.MaxValue, backSleeve = 0f, worstBelt = float.MaxValue, backBelt = 0f, frontBelt = 0f;
            float underLow = float.MaxValue, underHigh = 0f;
            for (int v = 0; v < asset.worldVertices.Length; v++)
            {
                int g = asset.groupOfVertex[v];
                var moved = asset.worldVertices[v] + deltas[g];
                float angle = AngleOf(asset.worldVertices[v]);
                float radius = new Vector2(moved.x, moved.z).magnitude;
                float gap = radius - BellyRadius(AngleOf(moved), moved.y);
                bool back = Mathf.Sin(angle) < -.5f;
                if (v < fixture.sleeveVertices)
                {
                    worstSleeve = Mathf.Min(worstSleeve, gap);
                    if (back) backSleeve = Mathf.Max(backSleeve, deltas[g].magnitude);
                    // The sleeve under the front of the belt (its rows around the belt's height).
                    if (Mathf.Sin(angle) > .95f && Mathf.Abs(asset.worldVertices[v].y) < .011f)
                    {
                        underLow = Mathf.Min(underLow, deltas[g].magnitude);
                        underHigh = Mathf.Max(underHigh, deltas[g].magnitude);
                    }
                }
                else
                {
                    worstBelt = Mathf.Min(worstBelt, gap);
                    if (back) backBelt = Mathf.Max(backBelt, deltas[g].magnitude);
                    if (Mathf.Sin(angle) > .95f) frontBelt = Mathf.Max(frontBelt, deltas[g].magnitude);
                }
            }
            // The belt stays a round tube: every cross-section keeps its thickness (each part carried, not torn apart).
            float worstThickness = 0f;
            for (int i = 0; i < BeltAround; i++)
            {
                var center = Vector3.zero;
                for (int j = 0; j < BeltSection; j++) center += Moved(fixture.sleeveVertices + i * BeltSection + j);
                center /= BeltSection;
                for (int j = 0; j < BeltSection; j++)
                    worstThickness = Mathf.Max(worstThickness, Mathf.Abs((Moved(fixture.sleeveVertices + i * BeltSection + j) - center).magnitude - BeltThickness));
            }
            Vector3 Moved(int v) => asset.worldVertices[v] + deltas[asset.groupOfVertex[v]];
            AssertLessOrEqual(worstThickness, BeltThickness * .2f, "The belt's tube lost its round cross-section.");
            AssertGreater(worstSleeve, -.0015f, "The draped sleeve still lies inside the grown belly.");
            AssertLessOrEqual(backSleeve, .001f, "The drape moved fabric where the body did not grow.");
            AssertGreater(worstBelt, 0f, "A part of the belt sank into the body (a ring carried whole by the largest push).");
            AssertLessOrEqual(backBelt, .003f, "The back of the belt moved with the belly at the front.");
            // The drape lifts fabric until skin shows less than its 1 mm tolerance through it, not always to skin plus clearance:
            // under the belt (whose own faces hide that skin) the sleeve rests near the skin, and the belt rides on it.
            AssertTrue(frontBelt >= underLow - .0005f && frontBelt <= underHigh + .0005f,
                $"The front of the belt ({frontBelt * 1000:F2}mm) did not follow the fabric under it ({underLow * 1000:F2}-{underHigh * 1000:F2}mm).");
            AssertGreater(frontBelt, BellyDepth - (SleeveRadius - BodyRadius) - .0015f, "The front of the belt did not follow the fabric over the belly.");
            Debug.Log($"[ReFit Tests] Synthetic drape: sleeve gap >= {worstSleeve * 1000:F2}mm, back sleeve {backSleeve * 1000:F2}mm, " +
                      $"belt gap >= {worstBelt * 1000:F2}mm, belt back {backBelt * 1000:F2}mm / front {frontBelt * 1000:F2}mm (sleeve under it {underLow * 1000:F2}-{underHigh * 1000:F2}mm).");
        }

        internal static void Drape_StopsWhenCancelled()
        {
            var fixture = CreateDrapeFixture();
            var deltas = new Vector3[fixture.asset.GroupCount];
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool stopped = false;
                try { fixture.drape.Apply(fixture.target, null, null, deltas, new ReFitReport(), "cancelled", cancellation.Token); }
                catch (OperationCanceledException) { stopped = true; }
                AssertTrue(stopped, "A cancelled drape kept working.");
            }
        }

        // ---- Enclosed skin ------------------------------------------------------------------------------------------------

        internal static void EnclosedSkin_OnlyInsideClosedParts()
        {
            var torsoCenter = Vector3.zero; var torsoSize = new Vector3(.2f, .3f, .2f);

            var vertices = new List<Vector3>(); var triangles = new List<int>();
            AddBox(vertices, triangles, torsoCenter, torsoSize, 10);
            AssertTrue(Snapshot(vertices, triangles, false).HideEnclosedSkin() == 0, "A plain closed body hid skin.");

            // A breast added over the flat chest: the chest under it is enclosed.
            int torsoTriangles = triangles.Count / 3;
            AddBox(vertices, triangles, new Vector3(0f, .05f, .1f), new Vector3(.08f, .08f, .08f), 8);
            var withBreast = Snapshot(vertices, triangles, false);
            AssertGreater(withBreast.HideEnclosedSkin(), 0, "Skin enclosed in an added part was not hidden.");
            int enclosedChest = 0, openChest = 0;
            for (int t = 0; t < torsoTriangles; t++)
            {
                var center = (withBreast.worldVertices[triangles[t * 3]] + withBreast.worldVertices[triangles[t * 3 + 1]] +
                              withBreast.worldVertices[triangles[t * 3 + 2]]) / 3f;
                if (Mathf.Abs(center.z - .1f) > 1e-4f) continue;
                bool hidden = withBreast.hiddenTriangles[t];
                if (Mathf.Abs(center.x) < .03f && center.y > .02f && center.y < .08f) { AssertTrue(hidden, "Chest skin inside the breast stayed visible."); enclosedChest++; }
                if (Mathf.Abs(center.x) > .06f || center.y < -.02f) { AssertTrue(!hidden, "Chest skin outside the breast was hidden."); openChest++; }
            }
            AssertTrue(enclosedChest > 0 && openChest > 0, "The enclosed-skin fixture measured nothing.");

            // A single-sided fluff card standing over the chest: open air under it, nothing enclosed.
            vertices.Clear(); triangles.Clear();
            AddBox(vertices, triangles, torsoCenter, torsoSize, 10);
            AddGrid(vertices, triangles, new Vector3(-.05f, -.05f, .13f), new Vector3(.1f, 0f, 0f), new Vector3(0f, .1f, 0f), 5);
            AssertTrue(Snapshot(vertices, triangles, false).HideEnclosedSkin() == 0, "Skin under a single-sided fluff card was hidden.");
        }

        internal static void FaceNormal_TinyTrianglesKeepTheirDirection()
        {
            var tiny = MeshSnapshot.FromWorld(new[] { Vector3.zero, new Vector3(.0005f, 0f, 0f), new Vector3(0f, .0005f, 0f) }, new[] { 0, 1, 2 }, false);
            AssertTrue((tiny.FaceNormal(0) - Vector3.forward).magnitude < 1e-4f, $"A half-millimetre triangle lost its normal: {tiny.FaceNormal(0)}.");
            var flat = new MeshSnapshot
            {
                worldVertices = new[] { Vector3.zero, new Vector3(.001f, 0f, 0f), new Vector3(.002f, 0f, 0f) },
                triangles = new[] { 0, 1, 2 }
            };
            AssertTrue(flat.FaceNormal(0) == Vector3.zero, "A degenerate triangle without vertex normals claimed a direction.");
            flat.worldNormals = new[] { Vector3.right, Vector3.right, Vector3.right };
            AssertTrue((flat.FaceNormal(0) - Vector3.right).magnitude < 1e-4f, "A degenerate triangle did not take its corners' normal.");
        }

        internal static void Bvh_RefitMatchesRebuild()
        {
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            AddBox(vertices, triangles, Vector3.zero, new Vector3(.2f, .3f, .2f), 6);
            AddCylinder(vertices, triangles, 24, 6, -.2f, .2f, (a, y) => .15f);
            var built = Snapshot(vertices, triangles, false);
            var bvh = SurfaceBvh.Build(built);
            var moved = new Vector3[vertices.Count];
            for (int i = 0; i < moved.Length; i++)
                moved[i] = vertices[i] * 1.1f + new Vector3(Mathf.Sin(i * 1.7f), Mathf.Cos(i * 2.3f), Mathf.Sin(i * .9f)) * .01f + Vector3.right * .02f;
            var movedSnapshot = new MeshSnapshot { worldVertices = moved, triangles = triangles.ToArray() };
            bvh.Refit(movedSnapshot);
            var rebuilt = SurfaceBvh.Build(movedSnapshot);
            for (int q = 0; q < 300; q++)
            {
                var point = new Vector3(Mathf.Sin(q * 12.9898f), Mathf.Sin(q * 78.233f), Mathf.Sin(q * 37.719f)) * .3f;
                var a = bvh.ClosestPoint(point, .5f); var b = rebuilt.ClosestPoint(point, .5f);
                AssertTrue(a.found == b.found && (!a.found || Mathf.Abs(a.distance - b.distance) < 1e-5f), "A refitted BVH found another closest point.");
                var direction = (-point).normalized;
                var ra = bvh.Raycast(point, direction, 1f); var rb = rebuilt.Raycast(point, direction, 1f);
                AssertTrue(ra.found == rb.found && (!ra.found || Mathf.Abs(ra.distance - rb.distance) < 1e-5f), "A refitted BVH found another ray hit.");
            }
        }

        // ---- Authored clearance -------------------------------------------------------------------------------------------

        // An octagonal sleeve 4 mm out of a round body at its corners: its flat sides cut 4 mm into the body as authored. A fit
        // onto the same body must keep them there, not lift spikes of fabric out where the sides sag.
        internal static void AuthoredClearance_SaggingEdgesStayAsAuthored()
        {
            var objects = new List<Object>();
            // A preview scene: the open scene is never touched.
            var scene = EditorSceneManager.NewPreviewScene();
            ReFitComputation computation = null;
            try
            {
                SkinnedMeshRenderer Renderer(string name, List<Vector3> vertices, List<int> triangles)
                {
                    var mesh = new Mesh { name = name, vertices = vertices.ToArray(), triangles = triangles.ToArray() };
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    objects.Add(mesh);
                    var go = new GameObject("__ReFitSynthetic_" + name);
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                    var renderer = go.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    return renderer;
                }
                var bodyVertices = new List<Vector3>(); var bodyTriangles = new List<int>();
                AddCylinder(bodyVertices, bodyTriangles, 128, 41, -.25f, .25f, (a, y) => BodyRadius);
                var source = Renderer("Source", bodyVertices, bodyTriangles);
                var target = Renderer("Target", bodyVertices, bodyTriangles);
                var sleeveVertices = new List<Vector3>(); var sleeveTriangles = new List<int>();
                AddCylinder(sleeveVertices, sleeveTriangles, 8, 9, -.1f, .1f, (a, y) => BodyRadius + .004f);
                var sleeve = Renderer("Sleeve", sleeveVertices, sleeveTriangles);
                var settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false };
                ReFitSettingsPresets.ApplyTightness(settings, ReFitAutoSetup.ClothingTightness);
                computation = new ReFitEngine().Run(new ReFitRequest
                {
                    mode = ReFitMode.MeshToMesh, assetRenderer = sleeve,
                    sourceAvatar = source.gameObject, sourceBodyRenderer = source,
                    targetAvatar = target.gameObject, targetBodyRenderer = target,
                    settings = settings
                });
                AssertTrue(computation.success, "The sagging sleeve fixture failed: " + FormatReport(computation.report));
                var primary = new Vector3[computation.mesh.vertexCount];
                computation.mesh.GetBlendShapeFrameVertices(computation.mesh.GetBlendShapeIndex(computation.primaryShapeName), 0, primary, null, null);
                float largest = primary.Max(d => d.magnitude);
                AssertLessOrEqual(largest, .0005f, "The fit lifted the sides that sag into the body as authored.");
            }
            finally
            {
                if (computation?.mesh != null) Object.DestroyImmediate(computation.mesh);
                EditorSceneManager.ClosePreviewScene(scene);
                for (int i = objects.Count - 1; i >= 0; i--) if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            }
        }

        // ---- The window ---------------------------------------------------------------------------------------------------

        // A custom base for the window run's avatar only (it cannot fit from an original): the run reads it once it holds the
        // avatar, which tells, whatever the run's timing (a failing run never waits for a frame), that the hold was taken.
        private sealed class HeldBase : ICustomBaseProvider
        {
            public const string Key = "refit-synthetic-base-1";
            public Transform avatar;
            public bool readWhileHeld;

            public CustomBaseInfo Describe(Transform avatarRoot)
            {
                if (avatar == null || avatarRoot != avatar) return null;
#if REFIT_VRCHAT_AVATARS
                readWhileHeld |= RefitRunner.IsRunning(avatarRoot);
#else
                readWhileHeld = true;
#endif
                return new CustomBaseInfo { Key = Key, Name = "ReFit synthetic base 1.0" };
            }
        }

        // Real runs through the window, driven as the editor's updates drive them, in a preview scene: a run takes its choices
        // when it starts and holds the avatar until it ends; shapes added to a refit keep the real original; a fit that fails
        // gives the earlier fit back.
        internal static void Window_RunKeepsOriginalsAndEarlierFits()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            const string name = "__ReFitWindowRunQA";
            const string folder = ReFitAssetPipeline.OutputRoot + "/" + name;
            var type = typeof(ReFitWizard);
            bool hadRoot = AssetDatabase.IsValidFolder(ReFitAssetPipeline.OutputRoot), hadFolder = AssetDatabase.IsValidFolder(folder);
            bool recording = ReFitRecordIntegration.Enabled, debug = ReFitDebugService.Enabled;
            var restoreHistory = KeepHistory();
            var scene = EditorSceneManager.NewPreviewScene();
            var meshes = new List<Mesh>();
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            var heldBase = new HeldBase();
            GameObject avatar = null;
            try
            {
                CustomBases.Register(heldBase);
                ReFitRecordIntegration.Enabled = true;
                ReFitDebugService.Enabled = false;
                SkinnedMeshRenderer Renderer(string objectName, Transform parent, List<Vector3> vertices, List<int> triangles)
                {
                    var mesh = new Mesh { name = objectName, vertices = vertices.ToArray(), triangles = triangles.ToArray() };
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    meshes.Add(mesh);
                    var go = new GameObject(objectName);
                    go.transform.SetParent(parent, false);
                    var renderer = go.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    return renderer;
                }
                GameObject Avatar(string objectName)
                {
                    var root = new GameObject(objectName);
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                    var vertices = new List<Vector3>(); var triangles = new List<int>();
                    AddCylinder(vertices, triangles, 64, 31, -.25f, .25f, (a, y) => BodyRadius);
                    var body = Renderer("Body", root.transform, vertices, triangles);
                    // Shapes growing the front of the body: a belly low, a chest high.
                    foreach (var (shape, low, high) in new[] { ("Belly", -.15f, .05f), ("Chest", .05f, .2f) })
                    {
                        var deltas = new Vector3[vertices.Count];
                        for (int i = 0; i < deltas.Length; i++)
                        {
                            var v = vertices[i];
                            if (v.y < low || v.y > high) continue;
                            deltas[i] = new Vector3(v.x, 0f, v.z).normalized * (.01f * Mathf.Max(0f, v.z / BodyRadius));
                        }
                        body.sharedMesh.AddBlendShapeFrame(shape, 100f, deltas, null, null);
                    }
                    return root;
                }
                avatar = Avatar(name + "_Avatar");
                heldBase.avatar = avatar.transform;
                var other = Avatar(name + "_Other");
                var sleeveVertices = new List<Vector3>(); var sleeveTriangles = new List<int>();
                AddCylinder(sleeveVertices, sleeveTriangles, 48, 13, -.12f, .12f, (a, y) => SleeveRadius);
                var sleeve = Renderer(name, avatar.transform, sleeveVertices, sleeveTriangles);
                var original = sleeve.sharedMesh;

                var shapes = (List<string>)type.GetField("blendshapes", flags).GetValue(window);
                ((ReFitSettings)type.GetField("settings", flags).GetValue(window)).savePrefab = false;
                type.GetField("targetAvatar", flags).SetValue(window, avatar);
                type.GetField("asset", flags).SetValue(window, sleeve);
                shapes.Add("Belly");
                window.CreateGUI();

                var executing = type.GetField("isExecuting", flags);
                ReFitResult Run(Action whileRunning)
                {
                    heldBase.readWhileHeld = false;
                    type.GetMethod("StartExecution", flags).Invoke(window, null);
                    AssertTrue((bool)executing.GetValue(window), "The run did not start.");
                    whileRunning?.Invoke();
                    var pump = type.GetMethod("PumpExecution", flags);
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while ((bool)executing.GetValue(window))
                    {
                        if (timer.Elapsed.TotalSeconds > 60) throw new Exception("The window's run did not finish.");
                        pump.Invoke(window, null);
                        Thread.Sleep(1);
                    }
                    var result = (ReFitResult)type.GetField("lastResult", flags).GetValue(window);
                    AssertTrue(heldBase.readWhileHeld, "A running refit did not hold its avatar while it read its custom base. " + FormatReport(result?.report));
#if REFIT_VRCHAT_AVATARS
                    AssertTrue(!RefitRunner.IsRunning(avatar.transform), "The run did not let go of the avatar.");
#endif
                    return result;
                }

                // A run with the choices it started with, whatever is chosen while it works.
                var first = Run(() =>
                {
                    type.GetField("targetAvatar", flags).SetValue(window, other);
                    shapes.Add("Chest");
                });
                AssertTrue(first != null && first.success, "The window's run failed: " + FormatReport(first?.report));
                var firstRequest = (ReFitRequest)type.GetField("lastRequest", flags).GetValue(window);
                AssertTrue(firstRequest.targetAvatar == avatar && firstRequest.targetBlendshapes.SequenceEqual(new[] { "Belly" }),
                    "The run followed choices made after it started.");
                var fitted = sleeve.sharedMesh;
                AssertTrue(fitted != original, "The run did not apply its result.");
                type.GetField("targetAvatar", flags).SetValue(window, avatar);
                shapes.Clear();

                // Shapes added to the refit: the record keeps the real original and both shapes.
                shapes.Add("Chest");
                var second = Run(null);
                AssertTrue(second != null && second.success, "Adding a shape failed: " + FormatReport(second?.report));
                var shaped = sleeve.sharedMesh;
#if REFIT_VRCHAT_AVATARS
                var record = sleeve.GetComponent<OrbitersRefit>();
                AssertTrue(record != null && record.Applied && record.original.mesh == original,
                    "Shapes added to a refit replaced its original with the refitted mesh.");
                AssertTrue(record.baseKey == HeldBase.Key, $"The refit was recorded for custom base '{record.baseKey}', not the avatar's.");
                var sources = record.shapes.Select(x => x.source).ToList();
                AssertTrue(sources.Contains("Belly") && sources.Contains("Chest"), "The refit lost its earlier shapes: " + string.Join(", ", sources));
#endif

                // Placed by hand, a fit that fails (no such shape) gives the earlier refit back.
                type.GetField("madeForAvatar", flags).SetValue(window, false);
                shapes.Clear();
                shapes.Add("No such shape");
                // The engine reports the missing shape as an error: expected here.
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("blendshapes-missing"));
                var third = Run(null);
                AssertTrue(third != null && !third.success, "The failing fit succeeded.");
                AssertTrue(sleeve.sharedMesh == shaped, "A failed fit left the piece without its earlier refit.");
#if REFIT_VRCHAT_AVATARS
                AssertTrue(record != null && record.Applied && record.original.mesh == original, "A failed fit lost the refit's record.");
#endif
            }
            finally
            {
                Object.DestroyImmediate(window);
                CustomBases.Unregister(heldBase);
                ReFitRecordIntegration.Enabled = recording;
                ReFitDebugService.Enabled = debug;
                restoreHistory();
                if (avatar != null)
                {
                    foreach (var component in avatar.GetComponentsInChildren<Component>(true)) Undo.ClearUndo(component);
                    foreach (var transform in avatar.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(transform.gameObject);
                }
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
                if (!hadFolder && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (!hadRoot && AssetDatabase.IsValidFolder(ReFitAssetPipeline.OutputRoot)) AssetDatabase.DeleteAsset(ReFitAssetPipeline.OutputRoot);
            }
        }

        internal static void Window_LocksChoicesWhileRunning()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(ReFitWizard);
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("__ReFitWindowLockQA");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Belly", 100f, new Vector3[3], null, null);
            var window = ScriptableObject.CreateInstance<ReFitWizard>();
            try
            {
                GameObject Avatar(string name)
                {
                    var avatar = new GameObject(name); avatar.transform.SetParent(root.transform);
                    var body = new GameObject("Body"); body.transform.SetParent(avatar.transform);
                    body.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                    return avatar;
                }
                var first = Avatar("First");
                var second = Avatar("Second");
                var shirtObject = new GameObject("Shirt"); shirtObject.transform.SetParent(first.transform);
                var shirt = shirtObject.AddComponent<SkinnedMeshRenderer>(); shirt.sharedMesh = mesh;
                type.GetField("targetAvatar", flags).SetValue(window, first);
                type.GetField("asset", flags).SetValue(window, shirt);
                var shapes = (List<string>)type.GetField("blendshapes", flags).GetValue(window);
                shapes.Add("Belly");
                window.CreateGUI();

                // A run's choices are a copy: what changes in the window meanwhile never reaches it.
                var run = type.GetMethod("Current", flags).Invoke(window, null);
                type.GetField("targetAvatar", flags).SetValue(window, second);
                shapes.Add("Later");
                var request = (ReFitRequest)type.GetMethod("RequestFor", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { run, (object)shirt });
                AssertTrue(request.targetAvatar == first, "A run followed the avatar chosen after it started.");
                AssertTrue(request.targetBlendshapes.SequenceEqual(new[] { "Belly" }), "A run followed shapes picked after it started.");
                type.GetField("targetAvatar", flags).SetValue(window, first);
                shapes.Remove("Later");

                // Running: the page is locked, and neither the Hierarchy nor the navigation change it.
                type.GetField("isExecuting", flags).SetValue(window, true);
                type.GetMethod("Render", flags).Invoke(window, null);
                var content = (VisualElement)type.GetField("content", flags).GetValue(window);
                AssertTrue(!content.enabledSelf, "The studio stayed editable while a run works.");
                type.GetMethod("OnSelectionChanged", flags).Invoke(window, null);
                AssertTrue((Object)type.GetField("asset", flags).GetValue(window) == shirt, "A selection replaced the piece of a run.");
                var pageType = type.GetNestedType("Page", BindingFlags.NonPublic);
                type.GetMethod("Go", flags).Invoke(window, new[] { Enum.Parse(pageType, "Settings") });
                AssertTrue(type.GetField("page", flags).GetValue(window).ToString() == "Studio", "The settings opened during a run.");
                type.GetField("isExecuting", flags).SetValue(window, false);
                type.GetMethod("Render", flags).Invoke(window, null);
                AssertTrue(content.enabledSelf, "The studio stayed locked after the run.");

                // Another avatar chooses again what the piece was made for and its shapes; back on its wearer it fits it as is.
                var setAvatar = type.GetMethod("SetAvatar", flags);
                setAvatar.Invoke(window, new object[] { second });
                AssertTrue(type.GetField("madeFor", flags).GetValue(window).ToString() == "Other" &&
                           (Object)type.GetField("sourceAvatar", flags).GetValue(window) == first, "Moving the piece did not take its wearer as the body it was made for.");
                AssertTrue(!shapes.Contains("Belly"), "The previous avatar's shapes stayed picked for another avatar.");
                setAvatar.Invoke(window, new object[] { first });
                AssertTrue(type.GetField("madeFor", flags).GetValue(window).ToString() == "Target" &&
                           type.GetField("sourceAvatar", flags).GetValue(window) as Object == null, "Back on its avatar, the piece was fitted from that same avatar.");

                // Placed by hand on its avatar, without shapes: it can run, a mesh fit from where it was placed.
                shapes.Clear();
                type.GetField("madeForAvatar", flags).SetValue(window, false);
                type.GetMethod("UpdateMode", flags).Invoke(window, null);
                AssertTrue(type.GetMethod("MissingChoice", flags, null, Type.EmptyTypes, null).Invoke(window, null) == null,
                    "Placed by hand without shapes could not run.");
                var placed = (ReFitRequest)type.GetMethod("BuildRequest", flags).Invoke(window, new object[] { shirt });
                AssertTrue(placed.mode == ReFitMode.MeshToMesh && placed.sourceAvatar == first && placed.settings.coverDifferentBaseBody,
                    $"Placed by hand without shapes built a {placed.mode} request.");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }

    /// <summary>Test Runner entry points for the synthetic checks (the full suite runs from Tools > Orbiters > ReFit).</summary>
    public sealed class ReFitSyntheticTests
    {
        [Test] public void DrapeCoversBellyAndCarriesRingLocally() => ReFitDeterministicTestRunner.Drape_CoversBellyAndCarriesRingLocally();
        [Test] public void DrapeStopsWhenCancelled() => ReFitDeterministicTestRunner.Drape_StopsWhenCancelled();
        [Test] public void EnclosedSkinOnlyInsideClosedParts() => ReFitDeterministicTestRunner.EnclosedSkin_OnlyInsideClosedParts();
        [Test] public void FaceNormalTinyTriangles() => ReFitDeterministicTestRunner.FaceNormal_TinyTrianglesKeepTheirDirection();
        [Test] public void BvhRefitMatchesRebuild() => ReFitDeterministicTestRunner.Bvh_RefitMatchesRebuild();
        [Test] public void AuthoredClearanceKeepsSaggingEdges() => ReFitDeterministicTestRunner.AuthoredClearance_SaggingEdgesStayAsAuthored();
        [Test] public void WindowLocksChoicesWhileRunning() => ReFitDeterministicTestRunner.Window_LocksChoicesWhileRunning();
        [Test] public void WindowRunKeepsOriginalsAndEarlierFits() => ReFitDeterministicTestRunner.Window_RunKeepsOriginalsAndEarlierFits();
    }
}
