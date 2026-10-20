using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Posing;
#endif
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The clothing in motion: a hidden copy of the avatar is posed (arms up, forward and down, twists, running steps, a
    /// crouch, elbows bent) and in each pose the skin the clothing covers at rest is checked for coming through it. A skin
    /// point is covered where fabric facing the same way lies over it within <see cref="CoverReach"/>, straight out of it;
    /// in a pose it is compared with the fabric of its patch only (the fabric around where it was), so it never jumps to
    /// another fold or layer. Skin already through the fabric at rest is left out: it is not covered.
    /// <para>On request it also plans a body shrink: the covered skin that comes through in a pose moves straight in by its
    /// depth and a margin (at most <see cref="MaxShrink"/>), never closer than <see cref="ArmatureGap"/> to its bone; a move
    /// that takes a skin point through in another pose is tried shorter in the next round. Offsets are in the body mesh's own
    /// space (before skinning), as a blendshape holds them.</para>
    /// Never changes the scene: everything happens on the copy. VRChat avatar projects only (the copy and the outfit links
    /// are Orbiters Toolkit's).
    /// </summary>
    public static class ReFitMotionCheck
    {
        public const float CoverReach = .05f, Tolerance = .0005f, PatchRadius = .04f, Facing = .5f;
        public const float ShrinkMargin = .003f, MaxShrink = .03f, ArmatureGap = .001f, WorseTolerance = .0005f;
        // Rounds of the shrink planning; a move held back is tried at half its length in the next round, down to MinStep.
        private const int ShrinkIterations = 6;
        private const float MinStep = .0005f;

        /// <summary>Debug: each pose as measured (its name, the body's and the clothing's world vertices and triangles), tests only.</summary>
        internal static Action<string, Vector3[], int[], Vector3[], int[]> PoseBaked;

        /// <summary>The poses checked, in order ("rest" first: what the clothing already allows).</summary>
        public static readonly string[] Poses =
        {
            "rest", "arms up", "arms forward", "arms down", "twist left", "twist right", "step left", "step right", "crouch",
            "elbows at sides", "elbows forward",
        };

        public sealed class PoseResult
        {
            public string pose;
            /// <summary>Covered skin vertices coming through the fabric by more than <see cref="Tolerance"/>.</summary>
            public int through;
            /// <summary>Of those, the ones that come through more than they did at rest.</summary>
            public int added;
            /// <summary>The deepest of all, and the deepest of the added ones.</summary>
            public float worstMm, addedWorstMm;
            /// <summary>Sum of squared depths (m²): one number for a pose's clipping.</summary>
            public double score;
            /// <summary>The body's bones under the most skin coming through, with their counts.</summary>
            public string regions;

            public override string ToString() =>
                $"{pose}: {through} through ({added} more than at rest, up to {addedWorstMm:F1} mm), worst {worstMm:F1} mm" +
                (string.IsNullOrEmpty(regions) ? "" : $" ({regions})");
        }

        public sealed class BodyShrink
        {
            /// <summary>Body vertices moved inwards, and their offsets in the body mesh's space (before skinning).</summary>
            public int[] vertices;
            public Vector3[] offsets;
            public float largestMm;
            /// <summary>Covered skin through the clothing over every pose, before and after the shrink.</summary>
            public int before, after;
            public double scoreBefore, scoreAfter;
            /// <summary>How each planning round went (diagnostics).</summary>
            public string notes;
        }

        public sealed class Result
        {
            public int covered;
            public readonly List<PoseResult> poses = new List<PoseResult>();
            public BodyShrink shrink;
            /// <summary>Why nothing could be checked, or null.</summary>
            public string error;

            /// <summary>Through the clothing in all poses but rest, more than at rest (added clipping), and its worst depth.</summary>
            public int Added => poses.Where(p => p.pose != "rest").Sum(p => p.added);
            public float WorstMm => poses.Where(p => p.pose != "rest").Select(p => p.addedWorstMm).DefaultIfEmpty(0f).Max();

            public override string ToString()
            {
                if (error != null) return "motion check: " + error;
                var sb = new StringBuilder($"motion check: {covered} covered skin vertices; {Added} come through in motion more than at rest, worst {WorstMm:F1} mm");
                foreach (var pose in poses) sb.Append("\n  ").Append(pose);
                if (shrink != null)
                    sb.Append($"\n  body shrink: {shrink.vertices.Length} vertices, up to {shrink.largestMm:F1} mm; through over all poses " +
                              $"{shrink.before} -> {shrink.after} ({shrink.notes})");
                return sb.ToString();
            }
        }

        /// <summary>
        /// Checks <paramref name="clothing"/> on <paramref name="body"/> (both under <paramref name="avatar"/>) in every pose of
        /// <see cref="Poses"/>; with <paramref name="planShrink"/>, also plans a body shrink for what comes through.
        /// </summary>
        public static Result Run(GameObject avatar, SkinnedMeshRenderer body, SkinnedMeshRenderer clothing, bool planShrink = false,
            Action<float, string> progress = null)
        {
            var result = new Result();
            if (avatar == null || body == null || clothing == null || body.sharedMesh == null || clothing.sharedMesh == null)
            {
                result.error = "no avatar, body or clothing";
                return result;
            }
#if REFIT_VRCHAT_AVATARS
            // By sibling index: two objects of one name (two "Body" children) stay apart in the copy.
            var bodyPath = ReFitUtility.IndexPath(body.transform, avatar.transform);
            var clothingPath = ReFitUtility.IndexPath(clothing.transform, avatar.transform);
            if (bodyPath == null || clothingPath == null)
            {
                result.error = "the body and the clothing must be under the avatar";
                return result;
            }
            // Copied at rest: a posing preview of the avatar is paused while the copy is made.
            HiddenAvatarCopy copy;
            using (AccessoryPoseSync.Pause(avatar.transform)) copy = new HiddenAvatarCopy(avatar);
            using (copy)
            {
                var root = copy.Copy.transform;
                var copyBody = ReFitUtility.ResolvePath(root, bodyPath)?.GetComponent<SkinnedMeshRenderer>();
                var copyClothing = ReFitUtility.ResolvePath(root, clothingPath)?.GetComponent<SkinnedMeshRenderer>();
                if (copyBody == null || copyClothing == null)
                {
                    result.error = "the copy has no body or clothing at their places";
                    return result;
                }
                var session = new Session(root, copyBody, copyClothing, avatar.transform);
                try
                {
                    session.Measure(result, progress);
                    if (planShrink && result.covered > 0 && result.error == null) result.shrink = session.PlanShrink(result, progress);
                }
                finally { session.Release(); }
            }
#else
            result.error = "the motion check needs a VRChat avatar project";
#endif
            return result;
        }

#if REFIT_VRCHAT_AVATARS

        // Accepts triangles turned along or against a direction.
        private readonly struct EitherFacing : ITriangleFilter
        {
            private readonly Vector3[] normals;
            private readonly Vector3 direction;
            private readonly float minimum;

            public EitherFacing(Vector3[] normals, Vector3 direction, float minimum)
            {
                this.normals = normals;
                this.direction = direction;
                this.minimum = minimum;
            }

            public bool Accept(int triangle) => Mathf.Abs(Vector3.Dot(normals[triangle], direction)) >= minimum;
        }

        private sealed class Session
        {
            private readonly Transform root;
            private readonly SkinnedMeshRenderer body, clothing;
            private readonly Dictionary<HumanBodyBones, Transform> bones;
            private readonly List<FollowLink> links;
            private readonly Dictionary<Transform, (Vector3, Quaternion, Vector3)> rest = new Dictionary<Transform, (Vector3, Quaternion, Vector3)>();
            private readonly Mesh bakedBody = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            private readonly Mesh bakedClothing = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            private readonly int[] bodyTriangles, clothTriangles;
            private int[] skinVertices;
            // Per covered skin vertex: its patch (clothing triangles), the side the fabric faces it from, how far it came through at rest.
            private int[][] patchOf;
            private float[] side;
            private float[] restDepth;
            private string[] boneNames;
            private int[] dominantBone;

            public Session(Transform root, SkinnedMeshRenderer body, SkinnedMeshRenderer clothing, Transform original)
            {
                this.root = root;
                this.body = body;
                this.clothing = clothing;
                try
                {
                    var clothingTop = clothing.transform;
                    while (clothingTop.parent != null && clothingTop.parent != root) clothingTop = clothingTop.parent;
                    bones = HumanoidBoneMapper.GetHumanoidMap(root.gameObject, null, clothingTop == body.transform ? null : clothingTop);
                    // Followed as the build will (Toolkit's posing preview shares these links): rest is the avatar as uploaded.
                    links = AttachmentFollow.Links(root);
                    int Depth(Transform t) { int d = 0; for (; t != null; t = t.parent) d++; return d; }
                    links.Sort((a, b) => Depth(a.Follower).CompareTo(Depth(b.Follower)));
                    foreach (var t in root.GetComponentsInChildren<Transform>(true)) rest[t] = (t.localPosition, t.localRotation, t.localScale);
                    bodyTriangles = body.sharedMesh.triangles;
                    clothTriangles = clothing.sharedMesh.triangles;
                }
                catch
                {
                    // The baking meshes are made with the session: never left behind by one that could not start.
                    Release();
                    throw;
                }
            }

            public void Release()
            {
                Object.DestroyImmediate(bakedBody);
                Object.DestroyImmediate(bakedClothing);
            }

            private void Restore()
            {
                foreach (var pair in rest) { pair.Key.localPosition = pair.Value.Item1; pair.Key.localRotation = pair.Value.Item2; pair.Key.localScale = pair.Value.Item3; }
            }

            // ------------------------------------------------------------------
            // Poses
            // ------------------------------------------------------------------

            private Transform Bone(HumanBodyBones b) => bones.TryGetValue(b, out var t) ? t : null;

            // The side of the avatar a bone is on (+1 its right, -1 its left in the root's frame... as X of the root).
            private float SideOf(Transform t) { float s = Mathf.Sign(root.InverseTransformPoint(t.position).x); return s == 0f ? 1f : s; }

            // Turns a bone so its child lies along a direction given in the avatar's frame (X mirrored to the bone's side).
            private void Aim(HumanBodyBones bone, HumanBodyBones child, Vector3 local)
            {
                var b = Bone(bone);
                var c = Bone(child);
                if (b == null || c == null) return;
                var direction = root.TransformDirection(new Vector3(local.x * SideOf(b), local.y, local.z)).normalized;
                var current = c.position - b.position;
                if (current.sqrMagnitude < 1e-10f) return;
                b.rotation = Quaternion.FromToRotation(current, direction) * b.rotation;
            }

            private void Turn(HumanBodyBones bone, Vector3 axis, float degrees)
            {
                var b = Bone(bone);
                if (b != null) b.rotation = Quaternion.AngleAxis(degrees, root.TransformDirection(axis)) * b.rotation;
            }

            private void Arms(Vector3 upper, Vector3 lower)
            {
                foreach (bool left in new[] { true, false })
                {
                    Aim(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, upper);
                    Aim(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, lower);
                }
            }

            private void Leg(bool left, Vector3 thigh, Vector3 shin)
            {
                Aim(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, thigh);
                Aim(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot, shin);
            }

            private void Pose(string pose)
            {
                Restore();
                switch (pose)
                {
                    case "arms up": Arms(new Vector3(.35f, .94f, 0f), new Vector3(.3f, .95f, 0f)); break;
                    case "arms forward": Arms(new Vector3(.2f, -.05f, .98f), new Vector3(.15f, 0f, .99f)); break;
                    case "arms down": Arms(new Vector3(.18f, -.98f, .05f), new Vector3(.12f, -.99f, .08f)); break;
                    case "elbows at sides": Arms(new Vector3(.18f, -.98f, 0f), new Vector3(.1f, -.1f, .99f)); break;
                    case "elbows forward": Arms(new Vector3(.25f, -.5f, .83f), new Vector3(-.2f, .55f, .81f)); break;
                    case "twist left":
                    case "twist right":
                        float twist = pose == "twist left" ? 20f : -20f;
                        foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest }) Turn(b, Vector3.up, twist);
                        break;
                    case "step left":
                    case "step right":
                        bool leftForward = pose == "step left";
                        Leg(leftForward, new Vector3(.05f, -.7f, .71f), new Vector3(.03f, -.85f, -.53f));
                        Leg(!leftForward, new Vector3(.05f, -.9f, -.42f), new Vector3(.03f, -.98f, -.2f));
                        break;
                    case "crouch":
                        Leg(true, new Vector3(.25f, -.35f, .9f), new Vector3(.05f, -.9f, -.43f));
                        Leg(false, new Vector3(.25f, -.35f, .9f), new Vector3(.05f, -.9f, -.43f));
                        Turn(HumanBodyBones.Spine, Vector3.right, 20f);
                        break;
                }
                foreach (var link in links) if (link.Follower != null && link.Target != null) link.Apply();
            }

            // ------------------------------------------------------------------
            // Measure
            // ------------------------------------------------------------------

            private static void Bake(SkinnedMeshRenderer renderer, Mesh into, out Vector3[] world, out Vector3[] normals)
            {
                renderer.BakeMesh(into, true);
                var m = renderer.transform.localToWorldMatrix;
                world = into.vertices;
                normals = into.normals;
                for (int i = 0; i < world.Length; i++) world[i] = m.MultiplyPoint3x4(world[i]);
                if (normals.Length != world.Length) normals = new Vector3[world.Length];
                for (int i = 0; i < normals.Length; i++) normals[i] = MeshSnapshot.Unit(m.MultiplyVector(normals[i]));
            }

            public void Measure(Result result, Action<float, string> progress)
            {
                progress?.Invoke(0f, "Measuring the clothing at rest");
                Pose("rest");
                Bake(body, bakedBody, out var restBody, out var restBodyNormals);
                Bake(clothing, bakedClothing, out var restCloth, out _);
                PoseBaked?.Invoke("rest", restBody, bodyTriangles, restCloth, clothTriangles);
                var cloth = MeshSnapshot.FromWorld(restCloth, clothTriangles, false);
                var clothIndex = SurfaceBvh.Build(cloth);
                var main = MeshSnapshot.FromWorld(restBody, bodyTriangles, false).MainSurface();
                var faceNormals = cloth.FaceNormals;
                int bodyCount = restBody.Length;
                var coveredBy = new int[bodyCount];
                var facing = new float[bodyCount];
                Parallel.For(0, bodyCount, v =>
                {
                    coveredBy[v] = -1;
                    if (!main[v]) return;
                    var p = restBody[v];
                    var n = restBodyNormals[v];
                    var hit = clothIndex.ClosestPoint(p, CoverReach, new EitherFacing(faceNormals, n, Facing));
                    if (!hit.found) return;
                    var offset = p - hit.position;
                    float along = Vector3.Dot(offset, faceNormals[hit.triangle]);
                    float d2 = offset.sqrMagnitude;
                    // Straight out of the skin, not beside it (past a hem).
                    if (d2 - along * along > Mathf.Max(1e-8f, .25f * d2)) return;
                    coveredBy[v] = hit.triangle;
                    facing[v] = Mathf.Sign(Vector3.Dot(faceNormals[hit.triangle], n));
                });
                skinVertices = Enumerable.Range(0, bodyCount).Where(v => coveredBy[v] >= 0).ToArray();
                result.covered = skinVertices.Length;
                if (skinVertices.Length == 0) { result.error = "the clothing covers no skin of the body"; return; }

                // Patches: the fabric around each matched triangle, turned the same way, by triangle (shared by many vertices).
                var patches = new Dictionary<int, int[]>();
                var adjacency = TriangleAdjacency(restCloth, clothTriangles);
                patchOf = new int[skinVertices.Length][];
                side = new float[skinVertices.Length];
                for (int k = 0; k < skinVertices.Length; k++)
                {
                    int t = coveredBy[skinVertices[k]];
                    if (!patches.TryGetValue(t, out var patch)) patches[t] = patch = Patch(t, restCloth, faceNormals, adjacency);
                    patchOf[k] = patch;
                    side[k] = facing[skinVertices[k]];
                }
                var weights = body.sharedMesh.boneWeights;
                boneNames = body.bones.Select(b => b != null ? b.name : "?").ToArray();
                dominantBone = skinVertices.Select(v => weights.Length > v ? weights[v].boneIndex0 : -1).ToArray();

                restDepth = Depths(restBody, restCloth);
                // Covered is under the fabric at rest: skin already through it there (a part enclosed in a pouch, fluff, clipping
                // the fit left) is not the clothing's to keep in.
                var under = Enumerable.Range(0, skinVertices.Length).Where(k => restDepth[k] <= Tolerance).ToArray();
                skinVertices = under.Select(k => skinVertices[k]).ToArray();
                patchOf = under.Select(k => patchOf[k]).ToArray();
                side = under.Select(k => side[k]).ToArray();
                dominantBone = under.Select(k => dominantBone[k]).ToArray();
                restDepth = under.Select(k => restDepth[k]).ToArray();
                result.covered = skinVertices.Length;
                if (skinVertices.Length == 0) { result.error = "no skin is under the clothing at rest"; return; }
                for (int p = 0; p < Poses.Length; p++)
                {
                    progress?.Invoke((p + 1f) / (Poses.Length + 1), "Posing: " + Poses[p]);
                    var depths = p == 0 ? restDepth : PoseDepths(Poses[p], null, null);
                    result.poses.Add(Summarize(Poses[p], depths));
                }
                Restore();
            }

            private float[] PoseDepths(string pose, int[] shrunk, Vector3[] offsets)
            {
                Pose(pose);
                Bake(body, bakedBody, out var posedBody, out _);
                Bake(clothing, bakedClothing, out var posedCloth, out _);
                if (shrunk != null) AddSkinned(posedBody, shrunk, offsets);
                if (shrunk == null) PoseBaked?.Invoke(pose, posedBody, bodyTriangles, posedCloth, clothTriangles);
                return Depths(posedBody, posedCloth);
            }

            // How far each covered skin vertex comes through its patch of fabric (negative: still under it).
            private float[] Depths(Vector3[] skin, Vector3[] fabric)
            {
                var depths = new float[skinVertices.Length];
                Parallel.For(0, skinVertices.Length, k =>
                {
                    var p = skin[skinVertices[k]];
                    float best = float.MaxValue, depth = float.NegativeInfinity;
                    foreach (int t in patchOf[k])
                    {
                        Vector3 a = fabric[clothTriangles[t * 3]], b = fabric[clothTriangles[t * 3 + 1]], c = fabric[clothTriangles[t * 3 + 2]];
                        var q = SurfaceBvh.ClosestPointOnTriangle(p, a, b, c, out _);
                        float d = (q - p).sqrMagnitude;
                        if (d >= best) continue;
                        var n = MeshSnapshot.Unit(Vector3.Cross(b - a, c - a));
                        if (n == Vector3.zero) continue;
                        best = d;
                        depth = Vector3.Dot(p - q, n * side[k]);
                    }
                    depths[k] = depth;
                });
                return depths;
            }

            private PoseResult Summarize(string pose, float[] depths)
            {
                var result = new PoseResult { pose = pose };
                var regions = new Dictionary<string, int>();
                for (int k = 0; k < depths.Length; k++)
                {
                    if (!(depths[k] > Tolerance)) continue;
                    result.through++;
                    if (depths[k] > Mathf.Max(0f, restDepth[k]) + Tolerance)
                    {
                        result.added++;
                        result.addedWorstMm = Mathf.Max(result.addedWorstMm, depths[k] * 1000f);
                    }
                    result.worstMm = Mathf.Max(result.worstMm, depths[k] * 1000f);
                    result.score += depths[k] * (double)depths[k];
                    string name = dominantBone[k] >= 0 && dominantBone[k] < boneNames.Length ? boneNames[dominantBone[k]] : "?";
                    regions[name] = regions.TryGetValue(name, out int n) ? n + 1 : 1;
                }
                result.regions = string.Join(", ", regions.OrderByDescending(p => p.Value).Take(3).Select(p => $"{p.Key} {p.Value}"));
                return result;
            }

            private static List<int>[] TriangleAdjacency(Vector3[] vertices, int[] triangles)
            {
                // Triangles sharing a corner (vertices at the same place are one: seams of UVs and normals).
                var byPosition = new Dictionary<Vector3, List<int>>();
                int count = triangles.Length / 3;
                for (int t = 0; t < count; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        var p = vertices[triangles[t * 3 + k]];
                        if (!byPosition.TryGetValue(p, out var list)) byPosition[p] = list = new List<int>(6);
                        list.Add(t);
                    }
                var adjacency = new List<int>[count];
                for (int t = 0; t < count; t++)
                {
                    var set = new HashSet<int>();
                    for (int k = 0; k < 3; k++) foreach (int o in byPosition[vertices[triangles[t * 3 + k]]]) if (o != t) set.Add(o);
                    adjacency[t] = set.ToList();
                }
                return adjacency;
            }

            private int[] Patch(int seed, Vector3[] vertices, Vector3[] normals, List<int>[] adjacency)
            {
                Vector3 Center(int t) => (vertices[clothTriangles[t * 3]] + vertices[clothTriangles[t * 3 + 1]] + vertices[clothTriangles[t * 3 + 2]]) / 3f;
                var center = Center(seed);
                var seen = new HashSet<int> { seed };
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                var patch = new List<int>();
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    patch.Add(t);
                    foreach (int o in adjacency[t])
                    {
                        if (!seen.Add(o)) continue;
                        if ((Center(o) - center).sqrMagnitude > PatchRadius * PatchRadius) continue;
                        if (Mathf.Abs(Vector3.Dot(normals[o], normals[seed])) < Facing) continue;
                        queue.Enqueue(o);
                    }
                }
                return patch.ToArray();
            }

            // ------------------------------------------------------------------
            // Body shrink
            // ------------------------------------------------------------------

            public BodyShrink PlanShrink(Result result, Action<float, string> progress)
            {
                var mesh = body.sharedMesh;
                var weights = mesh.boneWeights;
                var bindposes = mesh.bindposes;
                Pose("rest");
                Bake(body, bakedBody, out var restBody, out var restNormals);
                var restSkin = SkinMatrices(weights, bindposes);
                // Each covered vertex moves straight in (against its normal), never closer than ArmatureGap to the segment of
                // its dominant bone it is nearest: from the bone to one of its child bones.
                var inward = new Vector3[skinVertices.Length];
                var limit = new float[skinVertices.Length];
                var bodyBones = body.bones;
                var boneSet = new HashSet<Transform>(bodyBones.Where(b => b != null));
                for (int k = 0; k < skinVertices.Length; k++)
                {
                    var p = restBody[skinVertices[k]];
                    inward[k] = -restNormals[skinVertices[k]];
                    int b = dominantBone[k];
                    var bone = b >= 0 && b < bodyBones.Length ? bodyBones[b] : null;
                    if (bone == null || inward[k] == Vector3.zero) { limit[k] = 0f; continue; }
                    float nearest = (bone.position - p).magnitude;
                    foreach (Transform c in bone)
                    {
                        if (!boneSet.Contains(c)) continue;
                        var a = bone.position;
                        var ab = c.position - a;
                        float t = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                        nearest = Mathf.Min(nearest, (a + ab * t - p).magnitude);
                    }
                    limit[k] = Mathf.Max(0f, nearest - ArmatureGap);
                }

                var amount = new float[skinVertices.Length];
                var accepted = new float[skinVertices.Length];
                // The deepest each vertex comes through, over the poses.
                var worst = new float[skinVertices.Length];
                var current = new List<float[]> { restDepth };
                for (int p = 1; p < Poses.Length; p++) current.Add(PoseDepths(Poses[p], null, null));
                for (int k = 0; k < worst.Length; k++) worst[k] = current.Max(d => d[k]);
                double Score(List<float[]> all) => all.Sum(d => d.Where(x => x > Tolerance).Sum(x => (double)x * x));
                int Through(List<float[]> all) => all.Sum(d => d.Count(x => x > Tolerance));
                var shrink = new BodyShrink { before = Through(current), scoreBefore = Score(current) };
                double best = shrink.scoreBefore;
                // The move each vertex tries next: what it needs (and a margin), halved each time it is held back.
                var step = new float[skinVertices.Length];
                for (int k = 0; k < step.Length; k++) step[k] = worst[k] > Tolerance ? worst[k] + ShrinkMargin : 0f;
                for (int iteration = 0; iteration < ShrinkIterations; iteration++)
                {
                    progress?.Invoke(.5f + .5f * iteration / ShrinkIterations, "Planning the body shrink");
                    var trial = (float[])accepted.Clone();
                    bool any = false;
                    int needing = 0, capped = 0, keptBack = 0;
                    for (int k = 0; k < trial.Length; k++)
                    {
                        if (!(current.Max(d => d[k]) > Tolerance) || step[k] < MinStep) continue;
                        needing++;
                        if (Mathf.Min(MaxShrink, limit[k]) < trial[k] + step[k]) capped++;
                        trial[k] = Mathf.Min(Mathf.Min(MaxShrink, limit[k]), trial[k] + step[k]);
                        any |= trial[k] > accepted[k];
                    }
                    if (!any) break;
                    var (vertices, offsets) = Offsets(trial, inward, restSkin);
                    var next = new List<float[]> { restDepth };
                    for (int p = 1; p < Poses.Length; p++) next.Add(PoseDepths(Poses[p], vertices, offsets));
                    // A vertex the move takes through the fabric in a pose, or deeper through it, keeps its earlier offset (one still
                    // under the fabric may come a little closer to it).
                    for (int k = 0; k < trial.Length; k++)
                        for (int p = 1; p < Poses.Length; p++)
                            if (next[p][k] > Tolerance && next[p][k] > current[p][k] + WorseTolerance)
                            {
                                trial[k] = accepted[k];
                                step[k] *= .5f;
                                keptBack++;
                                break;
                            }
                    (vertices, offsets) = Offsets(trial, inward, restSkin);
                    next = new List<float[]> { restDepth };
                    for (int p = 1; p < Poses.Length; p++) next.Add(PoseDepths(Poses[p], vertices, offsets));
                    double score = Score(next);
                    shrink.notes += $"round {iteration + 1}: {needing} needing, {capped} capped, {keptBack} kept back, through {Through(current)} -> {Through(next)}; ";
                    // No better: done, unless moves held back are to be tried shorter.
                    if (!(score < best - 1e-12)) { if (keptBack == 0) break; continue; }
                    best = score;
                    // Moved vertices still through try what they still need next.
                    for (int k = 0; k < trial.Length; k++)
                        if (trial[k] > accepted[k]) step[k] = Mathf.Max(0f, next.Max(d => d[k])) + ShrinkMargin;
                    accepted = trial;
                    current = next;
                }
                var (finalVertices, finalOffsets) = Offsets(accepted, inward, restSkin);
                shrink.vertices = finalVertices;
                shrink.offsets = finalOffsets;
                shrink.largestMm = accepted.DefaultIfEmpty(0f).Max() * 1000f;
                shrink.after = Through(current);
                shrink.scoreAfter = Score(current);
                Restore();
                return shrink;
            }

            // Inward offsets of the given lengths, in the body mesh's space before skinning.
            private (int[] vertices, Vector3[] offsets) Offsets(float[] lengths, Vector3[] inward, Matrix4x4[] restSkin)
            {
                var vertices = new List<int>();
                var offsets = new List<Vector3>();
                for (int k = 0; k < lengths.Length; k++)
                {
                    if (lengths[k] <= 0f || inward[k] == Vector3.zero) continue;
                    int v = skinVertices[k];
                    var world = inward[k] * lengths[k];
                    var m = restSkin[v];
                    vertices.Add(v);
                    offsets.Add(m.inverse.MultiplyVector(world));
                }
                return (vertices.ToArray(), offsets.ToArray());
            }

            // Each vertex's skinning matrix in the current pose (renderer space to world).
            private Matrix4x4[] SkinMatrices(BoneWeight[] weights, Matrix4x4[] bindposes)
            {
                var bodyBones = body.bones;
                var boneMatrices = new Matrix4x4[bodyBones.Length];
                for (int b = 0; b < bodyBones.Length && b < bindposes.Length; b++)
                    boneMatrices[b] = bodyBones[b] != null ? bodyBones[b].localToWorldMatrix * bindposes[b] : body.transform.localToWorldMatrix;
                var result = new Matrix4x4[weights.Length];
                Parallel.For(0, weights.Length, v =>
                {
                    var w = weights[v];
                    var m = new Matrix4x4();
                    void Add(int bone, float weight)
                    {
                        if (weight <= 0f || bone < 0 || bone >= boneMatrices.Length) return;
                        for (int c = 0; c < 16; c++) m[c] += boneMatrices[bone][c] * weight;
                    }
                    Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                    result[v] = m;
                });
                return result;
            }

            // The offsets (body mesh space) added to baked world positions through the current pose's skinning.
            private void AddSkinned(Vector3[] world, int[] vertices, Vector3[] offsets)
            {
                var skin = SkinMatrices(body.sharedMesh.boneWeights, body.sharedMesh.bindposes);
                for (int i = 0; i < vertices.Length; i++) world[vertices[i]] += skin[vertices[i]].MultiplyVector(offsets[i]);
            }
        }
#endif
    }
}
