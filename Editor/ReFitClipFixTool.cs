using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat.Refit;
#endif
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The "push out" brush for a refitted piece, in the Scene view: fabric under the brush lying inside the body, or closer to
    /// it than <see cref="Room"/>, moves out along the body's distance field (<see cref="ReFitBodyField"/>, whose inside and
    /// outside come from the air around the body, not from the nearest skin), most at the brush's centre and none at its edge,
    /// shared with its neighbours so no spike is made. The body and the piece are taken as they show when the brush starts
    /// (pose and shapes); the pushes go into the piece's own mesh (its rest shape, through each vertex's skinning), with Undo,
    /// and the mesh asset is saved when the brush ends. A mesh other pieces may wear (a refit kept for reuse is worn by every
    /// avatar on that base) is copied next to it at the first push and the piece wears the copy (its refit record follows):
    /// the brush never changes another avatar. Each stroke is one undo step of positions and normals only
    /// (<see cref="ReFitPushOutUndo"/>), and the stroked fabric's normals follow it. [ and ] change the brush's size, Esc
    /// ends it.
    /// </summary>
    internal static class ReFitClipFixTool
    {
        private const string RadiusPreference = "Orbiters.ReFit.PushOut.Radius", RoomPreference = "Orbiters.ReFit.PushOut.Room";
        public const float MinRadius = .005f, MaxRadius = .15f, MaxRoom = .01f;
        // The field: a cell this size (grown to stay under MaxCells) over the piece and this margin around it.
        private const float FieldCell = .0025f, FieldMargin = .04f;
        private const int FieldCells = 4000000;
        // Each step of a stroke moves the fabric this share of the way out; the brush acts at most this often (seconds).
        private const float StepShare = .5f;
        private const double StepInterval = .03;
        private const int MaxDots = 3000;
        // The control's hint, and the label marking a mesh the brush copied for one piece (pushed in place from then on).
        private static readonly int ControlHint = "ReFitPushOut".GetHashCode();
        private const string OwnLabel = "ReFitPushOut";

        private sealed class Session
        {
            public SkinnedMeshRenderer clothing, body;
            public Mesh mesh;
            // Built in the background once the brush starts: the brush pushes once it is there.
            public ReFitBodyField field;
            public Task<ReFitBodyField> building;
            public CancellationTokenSource cancel;
            // The piece as shown, welded: groups of vertices at one place, their neighbours, positions (world).
            public int[] groupOf, groupRep;
            public List<int>[] neighbours;
            public Vector3[] position;
            public MeshSnapshot surface;
            public SurfaceBvh index;
            // Per vertex: world to mesh space (the inverse of its skinning when the brush started).
            public Matrix4x4[] toMesh;
            public Vector3[] meshVertices;
            public int inside;
            public bool stroking;
            public double lastStep;
            public bool hasHit;
            // The mesh is the piece's own to push (copied when other pieces may wear it).
            public bool owned;
            // The stroke under way: the mesh before it and the vertices it moved.
            public Vector3[] strokeBefore, strokeNormalsBefore;
            public readonly HashSet<int> touched = new HashSet<int>();
            public Vector3 hitPoint, hitNormal;
        }

        private static Session session;

        /// <summary>The brush changed something worth showing (the piece's points inside the body), or ended.</summary>
        public static event Action Changed;

        public static bool Active => session != null;
        public static SkinnedMeshRenderer Piece => session?.clothing;
        /// <summary>The piece's points inside the body, as last measured; -1 while the body is still being read.</summary>
        public static int Inside => session == null ? 0 : session.field == null ? -1 : session.inside;

        public static float Radius
        {
            get => Mathf.Clamp(EditorPrefs.GetFloat(RadiusPreference, .03f), MinRadius, MaxRadius);
            set { EditorPrefs.SetFloat(RadiusPreference, Mathf.Clamp(value, MinRadius, MaxRadius)); SceneView.RepaintAll(); }
        }

        /// <summary>The room the brush leaves between the skin and the fabric (metres).</summary>
        public static float Room
        {
            get => Mathf.Clamp(EditorPrefs.GetFloat(RoomPreference, .002f), 0f, MaxRoom);
            set => EditorPrefs.SetFloat(RoomPreference, Mathf.Clamp(value, 0f, MaxRoom));
        }

        /// <summary>Starts the brush on <paramref name="piece"/> worn on <paramref name="body"/>; returns why it cannot, or null.</summary>
        public static string Begin(SkinnedMeshRenderer piece, SkinnedMeshRenderer body)
        {
            End();
            if (piece == null || body == null || piece.sharedMesh == null || body.sharedMesh == null) return "no piece or body";
            var mesh = piece.sharedMesh;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (!string.IsNullOrEmpty(path) && !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return "its mesh is part of a model file: refit it first";
            var s = new Session { clothing = piece, body = body, mesh = mesh };
            Prepare(s);
            session = s;
            SceneView.duringSceneGui += OnSceneGui;
            Undo.undoRedoPerformed += OnUndoRedo;
            Changed?.Invoke();
            SceneView.RepaintAll();
            return null;
        }

        public static void End()
        {
            if (session == null) return;
            var mesh = session.mesh;
            session.cancel?.Cancel();
            if (session.stroking) FinishStroke(session);
            session = null;
            EditorApplication.update -= WaitForField;
            SceneView.duringSceneGui -= OnSceneGui;
            Undo.undoRedoPerformed -= OnUndoRedo;
            if (mesh != null && EditorUtility.IsPersistent(mesh)) AssetDatabase.SaveAssetIfDirty(mesh);
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        // The piece and the body as shown now; the field over the piece.
        private static void Prepare(Session s)
        {
            var bakedBody = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            var bakedPiece = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var bodyWorld = Baked(s.body, bakedBody);
                var bodyTriangles = s.body.sharedMesh.triangles;
                var world = Baked(s.clothing, bakedPiece);
                var triangles = s.mesh.triangles;
                Weld(s, world, triangles);
                var bounds = new Bounds(s.position[0], Vector3.zero);
                foreach (var p in s.position) bounds.Encapsulate(p);
                bounds.Expand(2f * FieldMargin);
                s.toMesh = InverseSkinning(s.clothing, s.mesh);
                s.meshVertices = s.mesh.vertices;
                // The body's field takes seconds: the brush shows at once and pushes once the field is there.
                s.cancel = new CancellationTokenSource();
                var token = s.cancel.Token;
                s.building = Task.Run(() =>
                {
                    var body = MeshSnapshot.FromWorld(bodyWorld, bodyTriangles, false);
                    return ReFitBodyField.Build(body, SurfaceBvh.Build(body), bounds, FieldCell, FieldCells, token, 0);
                }, token);
                EditorApplication.update += WaitForField;
            }
            finally
            {
                Object.DestroyImmediate(bakedBody);
                Object.DestroyImmediate(bakedPiece);
            }
        }

        private static void WaitForField()
        {
            var s = session;
            if (s == null || s.building == null) { EditorApplication.update -= WaitForField; return; }
            if (!s.building.IsCompleted) return;
            EditorApplication.update -= WaitForField;
            TakeField(s);
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        private static void TakeField(Session s)
        {
            if (s.field != null || s.building == null) return;
            try { s.field = s.building.Result; }
            catch (Exception ex) { Debug.LogWarning("[ReFit] Push out: the body could not be read: " + ex.GetBaseException().Message); End(); return; }
            Measure(s);
        }

        private static Vector3[] Baked(SkinnedMeshRenderer renderer, Mesh into)
        {
            renderer.BakeMesh(into, true);
            var m = renderer.transform.localToWorldMatrix;
            var world = into.vertices;
            for (int i = 0; i < world.Length; i++) world[i] = m.MultiplyPoint3x4(world[i]);
            return world;
        }

        // Vertices at one place (UV and normal seams) are one group: the brush moves them together.
        private static void Weld(Session s, Vector3[] world, int[] triangles)
        {
            var byPosition = new Dictionary<Vector3, int>();
            s.groupOf = new int[world.Length];
            var reps = new List<int>();
            for (int v = 0; v < world.Length; v++)
            {
                if (!byPosition.TryGetValue(world[v], out int g)) { g = reps.Count; byPosition[world[v]] = g; reps.Add(v); }
                s.groupOf[v] = g;
            }
            s.groupRep = reps.ToArray();
            s.position = new Vector3[reps.Count];
            for (int g = 0; g < reps.Count; g++) s.position[g] = world[reps[g]];
            var sets = new HashSet<int>[reps.Count];
            for (int g = 0; g < sets.Length; g++) sets[g] = new HashSet<int>();
            var groupTriangles = new int[triangles.Length];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = s.groupOf[triangles[t]], b = s.groupOf[triangles[t + 1]], c = s.groupOf[triangles[t + 2]];
                groupTriangles[t] = a; groupTriangles[t + 1] = b; groupTriangles[t + 2] = c;
                sets[a].Add(b); sets[a].Add(c); sets[b].Add(a); sets[b].Add(c); sets[c].Add(a); sets[c].Add(b);
            }
            s.neighbours = new List<int>[sets.Length];
            for (int g = 0; g < sets.Length; g++) { sets[g].Remove(g); s.neighbours[g] = new List<int>(sets[g]); }
            s.surface = MeshSnapshot.FromWorld(s.position, groupTriangles, false);
            s.index = SurfaceBvh.Build(s.surface);
        }

        // World to mesh space per vertex: the inverse of its skinning matrix (bones weighted), or of the renderer's matrix.
        private static Matrix4x4[] InverseSkinning(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var weights = mesh.boneWeights;
            var bindposes = mesh.bindposes;
            var bones = renderer.bones;
            var rendererMatrix = renderer.transform.localToWorldMatrix;
            var boneMatrices = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                boneMatrices[b] = bones[b] != null && b < bindposes.Length ? bones[b].localToWorldMatrix * bindposes[b] : rendererMatrix;
            var result = new Matrix4x4[mesh.vertexCount];
            Parallel.For(0, result.Length, v =>
            {
                if (weights.Length != result.Length || boneMatrices.Length == 0) { result[v] = rendererMatrix.inverse; return; }
                var w = weights[v];
                var m = new Matrix4x4();
                float total = 0f;
                void Add(int bone, float weight)
                {
                    if (weight <= 0f || bone < 0 || bone >= boneMatrices.Length) return;
                    for (int c = 0; c < 16; c++) m[c] += boneMatrices[bone][c] * weight;
                    total += weight;
                }
                Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                result[v] = total > 0f ? m.inverse : rendererMatrix.inverse;
            });
            return result;
        }

        private static void Measure(Session s)
        {
            if (s.field == null) return;
            int inside = 0;
            for (int g = 0; g < s.position.Length; g++) if (s.field.Sample(s.position[g]) < 0f) inside++;
            s.inside = inside;
        }

        private static void OnUndoRedo()
        {
            if (session == null) return;
            // The stroke's positions first (also handled on their own when the brush has ended; done once either way).
            ReFitPushOutUndo.Apply();
            if (session.clothing == null || session.body == null || session.clothing.sharedMesh != session.mesh) { End(); return; }
            // The mesh came back as it was: the piece is read again (the field stays: the body has not moved).
            var field = session.field;
            var building = session.building;
            var bakedPiece = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var world = Baked(session.clothing, bakedPiece);
                Weld(session, world, session.mesh.triangles);
                session.meshVertices = session.mesh.vertices;
                session.field = field;
                session.building = building;
                Measure(session);
            }
            finally { Object.DestroyImmediate(bakedPiece); }
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------
        // The brush
        // ------------------------------------------------------------------

        private static void OnSceneGui(SceneView view)
        {
            var s = session;
            if (s == null) return;
            if (s.clothing == null || s.body == null || s.clothing.sharedMesh != s.mesh) { End(); return; }
            var e = Event.current;
            // Asked on every event: the id follows the controls drawn before it in this view.
            int id = GUIUtility.GetControlID(ControlHint, FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(id);
                    break;
                case EventType.MouseMove:
                    Aim(s, e.mousePosition);
                    view.Repaint();
                    break;
                case EventType.MouseDown:
                    if (e.button != 0 || e.alt) break;
                    Aim(s, e.mousePosition);
                    if (!s.hasHit || !Own(s)) break;
                    GUIUtility.hotControl = id;
                    StartStroke(s);
                    s.lastStep = 0;
                    Step(s);
                    // The pressed brush shows at once.
                    view.Repaint();
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) { Aim(s, e.mousePosition); break; }
                    Aim(s, e.mousePosition);
                    if (s.hasHit && EditorApplication.timeSinceStartup - s.lastStep >= StepInterval) Step(s);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    FinishStroke(s);
                    Measure(s);
                    Changed?.Invoke();
                    e.Use();
                    break;
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.LeftBracket) { Radius /= 1.25f; e.Use(); }
                    else if (e.keyCode == KeyCode.RightBracket) { Radius *= 1.25f; e.Use(); }
                    else if (e.keyCode == KeyCode.Escape) { End(); e.Use(); }
                    break;
                case EventType.Repaint:
                    Draw(s, view);
                    break;
            }
        }

        private static void Aim(Session s, Vector2 mouse)
        {
            var ray = HandleUtility.GUIPointToWorldRay(mouse);
            var hit = s.index.Raycast(ray.origin, ray.direction.normalized, 1000f);
            s.hasHit = hit.found;
            if (!hit.found) return;
            s.hitPoint = hit.position;
            var n = s.surface.FaceNormal(hit.triangle);
            s.hitNormal = Vector3.Dot(n, ray.direction) > 0f ? -n : n;
        }

        /// <summary>One step of the brush at a point (tests: a stroke without the Scene view); the points inside after it.</summary>
        internal static int PushAt(Vector3 point)
        {
            var s = session;
            if (s == null) return 0;
            if (s.field == null) { s.building.Wait(); TakeField(s); }
            if (s == null || session != s || !Own(s)) return 0;
            s.hitPoint = point;
            s.hasHit = true;
            StartStroke(s);
            Step(s);
            FinishStroke(s);
            Measure(s);
            return s.inside;
        }

        /// <summary>The mesh the brush pushes now (tests: the piece's own copy once it pushed a shared mesh).</summary>
        internal static Mesh PushedMesh => session?.mesh;

        // ------------------------------------------------------------------
        // The piece's own mesh, strokes and their undo steps
        // ------------------------------------------------------------------

        // The piece pushes a mesh of its own: a mesh asset other pieces may wear (not one the brush copied for this piece, or
        // worn by another loaded renderer) is copied next to it first, and the piece wears the copy, with Undo. False when the
        // copy could not be made.
        private static bool Own(Session s)
        {
            if (s.owned) return true;
            var mesh = s.mesh;
            if (EditorUtility.IsPersistent(mesh) && (!AssetDatabase.GetLabels(mesh).Contains(OwnLabel) || WornElsewhere(mesh, s.clothing)))
            {
                Mesh copy;
                string path;
                try
                {
                    // Seconds on a body-sized piece with many shapes: shown at once, on the press that asked.
                    EditorUtility.DisplayProgressBar("ReFit", $"Copying the mesh of {s.clothing.name} so the push changes it alone…", .5f);
                    string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh))?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal)) folder = "Assets";
                    path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(s.clothing.name + " pushed out", "Pushed out") + ".asset");
                    copy = Object.Instantiate(mesh);
                    copy.name = mesh.name;
                    AssetDatabase.CreateAsset(copy, path);
                    AssetDatabase.SetLabels(copy, new[] { OwnLabel });
                    // Gone again once nothing wears it (the copy undone, the piece refitted).
                    GeneratedAssets.Track(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    Debug.LogWarning("[ReFit] Push out: the piece's own copy of its mesh could not be saved: " + ex.Message);
                    return false;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                bool recorded = false;
#if REFIT_VRCHAT_AVATARS
                var record = RefitRecords.Find(s.clothing);
                if (record != null && record.Applied && record.mesh == mesh) recorded = RefitRecords.ReplaceMesh(record, copy, path, "Push the fabric out");
#endif
                if (!recorded)
                {
                    Undo.RecordObject(s.clothing, "Push the fabric out");
                    s.clothing.sharedMesh = copy;
                    EditorUtility.SetDirty(s.clothing);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(s.clothing);
                }
                s.mesh = copy;
                Debug.Log($"[ReFit] Push out: {s.clothing.name} wears its own copy of its mesh ({path}): the pushes never change another avatar.");
            }
            s.owned = true;
            return true;
        }

        // Another loaded renderer wears the mesh. Hidden editor copies (a posing preview, an estimate, a build) don't count:
        // made again each session, they would make the brush copy its own copy again every time.
        private static bool WornElsewhere(Mesh mesh, SkinnedMeshRenderer piece) =>
            Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().Any(r => r != null && r != piece && r.sharedMesh == mesh &&
                                                                         !EditorUtility.IsPersistent(r) && !IsEditorCopy(r, piece));

        // In a preview scene other than the piece's (a hidden avatar copy has one of its own), or never saved (it or an
        // object above it).
        private static bool IsEditorCopy(Component component, Component piece)
        {
            if (component.gameObject.scene != piece.gameObject.scene && EditorSceneManager.IsPreviewSceneObject(component)) return true;
            if ((component.hideFlags & HideFlags.DontSaveInEditor) != 0) return true;
            for (var t = component.transform; t != null; t = t.parent)
                if ((t.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0) return true;
            return false;
        }

        private static void StartStroke(Session s)
        {
            s.stroking = true;
            s.touched.Clear();
            s.strokeBefore = (Vector3[])s.meshVertices.Clone();
            s.strokeNormalsBefore = s.mesh.normals;
        }

        // The stroke's fabric gets normals from its faces as they are now, and the stroke becomes one undo step.
        private static void FinishStroke(Session s)
        {
            if (!s.stroking) return;
            s.stroking = false;
            if (s.touched.Count == 0 || s.mesh == null || s.strokeBefore == null) return;
            MeshNormals.Recalculate(s.mesh, s.touched);
            ReFitPushOutUndo.For(s.mesh).Record(s.strokeBefore, s.strokeNormalsBefore, "Push the fabric out");
            EditorUtility.SetDirty(s.mesh);
            s.strokeBefore = s.strokeNormalsBefore = null;
            s.touched.Clear();
        }

        /// <summary>Where the piece's points inside the body are now (tests).</summary>
        internal static List<Vector3> InsidePoints()
        {
            var points = new List<Vector3>();
            if (session == null) return points;
            if (session.field == null) { session.building.Wait(); TakeField(session); }
            foreach (var p in session.position) if (session.field.Sample(p) < 0f) points.Add(p);
            return points;
        }

        // One step of a stroke: the fabric under the brush moves out of the body (or to the room), shared with its neighbours.
        private static void Step(Session s)
        {
            if (s.field == null) return;
            s.lastStep = EditorApplication.timeSinceStartup;
            float radius = Radius, room = Room;
            var touched = new List<int>();
            for (int g = 0; g < s.position.Length; g++)
                if ((s.position[g] - s.hitPoint).sqrMagnitude <= radius * radius) touched.Add(g);
            if (touched.Count == 0) return;
            var move = new Dictionary<int, Vector3>(touched.Count * 2);
            foreach (int g in touched)
            {
                float d = s.field.Sample(s.position[g], out var gradient);
                float need = room - d;
                if (need <= 0f || gradient.sqrMagnitude < 1e-8f) continue;
                float t = (s.position[g] - s.hitPoint).magnitude / radius;
                float falloff = (1f - t * t) * (1f - t * t);
                move[g] = gradient.normalized * (need * falloff * StepShare);
            }
            if (move.Count == 0) return;
            // Shared with the neighbours: each moved group and the ring around it take the mean of their own and their
            // neighbours' moves, so a lone point never stands out.
            var shared = new Dictionary<int, Vector3>(move.Count * 2);
            foreach (var pair in move)
            {
                shared[pair.Key] = Vector3.zero;
                foreach (int n in s.neighbours[pair.Key]) shared[n] = Vector3.zero;
            }
            var keys = new List<int>(shared.Keys);
            foreach (int g in keys)
            {
                var own = move.TryGetValue(g, out var m) ? m : Vector3.zero;
                var mean = Vector3.zero;
                foreach (int n in s.neighbours[g]) mean += move.TryGetValue(n, out var o) ? o : Vector3.zero;
                if (s.neighbours[g].Count > 0) mean /= s.neighbours[g].Count;
                // Never less than its own move outwards: a point in the body still leaves it.
                var blended = Vector3.Lerp(own, mean, .5f);
                shared[g] = own.sqrMagnitude > blended.sqrMagnitude ? own : blended;
            }
            foreach (var pair in shared)
            {
                if (pair.Value.sqrMagnitude < 1e-14f) continue;
                s.position[pair.Key] += pair.Value;
            }
            // Into the mesh: every vertex of a moved group, through its own skinning.
            for (int v = 0; v < s.meshVertices.Length; v++)
                if (shared.TryGetValue(s.groupOf[v], out var world) && world.sqrMagnitude >= 1e-14f)
                {
                    s.meshVertices[v] += s.toMesh[v].MultiplyVector(world);
                    s.touched.Add(v);
                }
            s.mesh.vertices = s.meshVertices;
            s.mesh.RecalculateBounds();
            EditorUtility.SetDirty(s.mesh);
            s.surface.worldVertices = s.position;
            s.surface.CacheFaceNormals();
            s.index.Refit(s.surface);
        }

        private static void Draw(Session s, SceneView view)
        {
            if (s.field == null) return;
            float radius = Radius;
            // The piece's points inside the body around the brush (or everywhere when it is off the piece).
            Handles.color = new Color(1f, .42f, .37f, .9f);
            int dots = 0;
            var center = s.hasHit ? s.hitPoint : Vector3.zero;
            float reach = radius * 3f;
            for (int g = 0; g < s.position.Length && dots < MaxDots; g++)
            {
                var p = s.position[g];
                if (s.hasHit && (p - center).sqrMagnitude > reach * reach) continue;
                if (s.field.Sample(p) >= 0f) continue;
                Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * .012f, EventType.Repaint);
                dots++;
            }
            if (!s.hasHit) return;
            Handles.color = new Color(0f, .855f, .427f, s.stroking ? 1f : .8f);
            Handles.DrawWireDisc(s.hitPoint, s.hitNormal, radius, 2f);
            Handles.color = new Color(0f, .855f, .427f, .12f);
            Handles.DrawSolidDisc(s.hitPoint, s.hitNormal, radius);
        }
    }
}
