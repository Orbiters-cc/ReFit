using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The wizard's smart defaults, read from the scene without changing it: the avatars, the clothing worn on them, the
    /// avatar a piece is worn on, its body, the base it was made from when an Orbiters tool knows it (MCB custom bases), the
    /// body shapes the avatar keeps switched on under the clothing, and how snug it should fit.
    /// </summary>
    internal static class ReFitAutoSetup
    {
        /// <summary>A body moving less than this under the clothing does not need the clothing to follow (metres).</summary>
        internal const float ShapeMinMove = 0.0015f;
        /// <summary>How far around the clothing the body counts as under it (metres).</summary>
        internal const float ShapeReach = 0.03f;
        internal const float ClothingTightness = 0.93f;
        internal const float AccessoryTightness = 0f;

        /// <summary>Humanoid or VRChat avatars of the open scenes, in hierarchy order.</summary>
        internal static List<GameObject> SceneAvatars()
        {
            var found = new List<GameObject>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var candidates = new List<Transform>();
                    foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                        if (animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman) candidates.Add(animator.transform);
                    if (DescriptorType != null)
                        foreach (var descriptor in root.GetComponentsInChildren(DescriptorType, true)) candidates.Add(descriptor.transform);
                    // Hierarchy order; an avatar nested in another (a clothing prefab with its own Animator) is not a separate avatar.
                    foreach (var candidate in candidates.Distinct().OrderBy(t => Depth(t)).ThenBy(t => t.GetSiblingIndex()))
                        if (!found.Any(a => candidate.IsChildOf(a.transform))) found.Add(candidate.gameObject);
                }
            }
            return found;
        }

        private static readonly Type DescriptorType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false)).FirstOrDefault(t => t != null);

        private static int Depth(Transform transform)
        {
            int depth = 0;
            for (var t = transform.parent; t != null; t = t.parent) depth++;
            return depth;
        }

        /// <summary>An avatar root itself: a humanoid Animator or a VRChat avatar descriptor on the object.</summary>
        internal static bool IsAvatar(GameObject item)
        {
            if (item == null) return false;
            var animator = item.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman) return true;
            return DescriptorType != null && item.GetComponent(DescriptorType) != null;
        }

        /// <summary>Inside another avatar: a worn prefab with its own humanoid rig is clothing, not an avatar.</summary>
        internal static bool InsideAvatar(GameObject item)
        {
            for (var t = item != null ? item.transform.parent : null; t != null; t = t.parent)
                if (IsAvatar(t.gameObject)) return true;
            return false;
        }

        /// <summary>The avatar <paramref name="item"/> is worn on, or null for a project file or a loose object.</summary>
        internal static GameObject AvatarOf(GameObject item)
        {
            var avatar = ReFitContextMenu.FindAvatar(item);
            // A worn prefab with its own humanoid rig: the avatar wearing it.
            while (avatar != null && InsideAvatar(avatar))
            {
                var outer = ReFitContextMenu.FindAvatar(avatar);
                if (outer == null || outer == avatar) break;
                avatar = outer;
            }
            return avatar;
        }

        /// <summary>The avatar's body: a renderer named "Body", else the skinned mesh with the most vertices.</summary>
        internal static SkinnedMeshRenderer Body(GameObject avatar, SkinnedMeshRenderer exclude = null)
        {
            if (avatar == null) return null;
            SkinnedMeshRenderer best = null;
            int bestVerts = -1;
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == exclude || smr.sharedMesh == null || IsHelper(smr.transform, avatar.transform)) continue;
                if (string.Equals(smr.name, "Body", StringComparison.OrdinalIgnoreCase)) return smr;
                if (smr.sharedMesh.vertexCount > bestVerts) { bestVerts = smr.sharedMesh.vertexCount; best = smr; }
            }
            return best;
        }

        /// <summary>Skinned meshes an avatar wears besides its body: clothing and accessories, editor helpers left out.</summary>
        internal static List<SkinnedMeshRenderer> Clothing(GameObject avatar)
        {
            var result = new List<SkinnedMeshRenderer>();
            if (avatar == null) return result;
            var body = Body(avatar);
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr != body && smr.sharedMesh != null && smr.sharedMesh.vertexCount > 0 && !IsHelper(smr.transform, avatar.transform))
                    result.Add(smr);
            return result;
        }

        /// <summary>Skinned meshes inside a project file or loose object, for picking one to refit.</summary>
        internal static List<SkinnedMeshRenderer> Meshes(GameObject item) => item == null ? new List<SkinnedMeshRenderer>()
            : item.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();

        /// <summary>How snug it fits by default: clothing (underwear included) tight, accessories loose.</summary>
        internal static float DefaultTightness(SkinnedMeshRenderer clothing, GameObject avatar) =>
            ReFitClothingDetection.IsClothing(clothing, avatar) ? ClothingTightness : AccessoryTightness;

        /// <summary>The custom base an Orbiters tool (MCB) knows the avatar uses, when it can give its original base.</summary>
        internal static CustomBaseInfo OriginalBase(GameObject avatar)
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar)) return null;
            var info = CustomBases.Describe(avatar.transform);
            return info != null && info.CanFit ? info : null;
        }

        /// <summary>
        /// The body shapes the avatar keeps switched on that move its body under <paramref name="clothing"/> (skin within
        /// <see cref="ShapeReach"/> of the clothing, moved by at least <see cref="ShapeMinMove"/>), in the body's order. Shapes the clothing already
        /// has (its creator's) are left out. Measured on hidden copies; the avatar is not changed.
        /// </summary>
        internal static List<string> ActiveShapes(SkinnedMeshRenderer body, SkinnedMeshRenderer clothing)
        {
            var result = new List<string>();
            if (body == null || body.sharedMesh == null || clothing == null || clothing.sharedMesh == null) return result;
            var mesh = body.sharedMesh;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clothing.sharedMesh.blendShapeCount; i++) own.Add(clothing.sharedMesh.GetBlendShapeName(i));
            var active = new List<int>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
                if (Mathf.Abs(body.GetBlendShapeWeight(i)) > 0.5f && !own.Contains(mesh.GetBlendShapeName(i))) active.Add(i);
            if (active.Count == 0) return result;

            var under = new NearSurface(WorldPoints(clothing), ShapeReach);
            var temporary = new GameObject("ReFit shape probe") { hideFlags = HideFlags.HideAndDontSave };
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // Beside the body, in its scene (a preview scene too), never in another.
                if (body.gameObject.scene.IsValid() && temporary.scene != body.gameObject.scene)
                    SceneManager.MoveGameObjectToScene(temporary, body.gameObject.scene);
                temporary.transform.SetPositionAndRotation(body.transform.position, body.transform.rotation);
                var copy = temporary.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = mesh;
                copy.bones = body.bones;
                copy.rootBone = body.rootBone;
                for (int i = 0; i < mesh.blendShapeCount; i++) copy.SetBlendShapeWeight(i, body.GetBlendShapeWeight(i));
                copy.BakeMesh(baked);
                var toWorld = ReFitScenePicture.CaptureMatrix(copy);
                var shown = baked.vertices;
                var near = new List<int>();
                for (int v = 0; v < shown.Length; v++)
                    if (under.Contains(toWorld.MultiplyPoint3x4(shown[v]))) near.Add(v);
                if (near.Count == 0) return result;
                foreach (int shape in active)
                {
                    float weight = copy.GetBlendShapeWeight(shape);
                    copy.SetBlendShapeWeight(shape, 0f);
                    copy.BakeMesh(baked);
                    copy.SetBlendShapeWeight(shape, weight);
                    var without = baked.vertices;
                    foreach (int v in near)
                        if ((shown[v] - without[v]).sqrMagnitude > ShapeMinMove * ShapeMinMove)
                        {
                            result.Add(mesh.GetBlendShapeName(shape));
                            break;
                        }
                }
                return result;
            }
            finally
            {
                Object.DestroyImmediate(baked);
                Object.DestroyImmediate(temporary);
            }
        }

        /// <summary>World positions of a skinned mesh's vertices as posed.</summary>
        internal static Vector3[] WorldPoints(SkinnedMeshRenderer renderer)
        {
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                renderer.BakeMesh(baked);
                var matrix = ReFitScenePicture.CaptureMatrix(renderer);
                var vertices = baked.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                return vertices;
            }
            finally { Object.DestroyImmediate(baked); }
        }

        /// <summary>Points within <c>reach</c> of a point set, looked up in a grid of reach-sized cells.</summary>
        private sealed class NearSurface
        {
            private readonly Dictionary<Vector3Int, List<Vector3>> cells = new Dictionary<Vector3Int, List<Vector3>>();
            private readonly float reach;

            public NearSurface(Vector3[] points, float reach)
            {
                this.reach = reach;
                foreach (var point in points)
                {
                    var key = Cell(point);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Vector3>();
                    list.Add(point);
                }
            }

            public bool Contains(Vector3 point)
            {
                var center = Cell(point);
                float limit = reach * reach;
                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (cells.TryGetValue(center + new Vector3Int(x, y, z), out var list))
                        foreach (var other in list)
                            if ((other - point).sqrMagnitude <= limit) return true;
                return false;
            }

            private Vector3Int Cell(Vector3 point) =>
                new Vector3Int(Mathf.FloorToInt(point.x / reach), Mathf.FloorToInt(point.y / reach), Mathf.FloorToInt(point.z / reach));
        }

        // Editor helpers and ReFit's own debug copies under the avatar are not clothing.
        private static bool IsHelper(Transform transform, Transform root)
        {
            for (var t = transform; t != null && t != root; t = t.parent)
                if ((t.gameObject.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor)) != 0 || t.CompareTag("EditorOnly") ||
                    t.name.StartsWith("__ReFit_Debug_Session_") || t.name.StartsWith("__XRayGizmos_")) return true;
            return false;
        }
    }
}
