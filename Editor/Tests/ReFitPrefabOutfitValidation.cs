using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Opt-in check of a VRCFury outfit prefab refitted in place, as people drop one on their avatar: the LunaVRC "Classy"
    /// outfit (made for the Novabeast, private local assets) on an Ultinova, each part refitted from the Novabeast with the
    /// window's defaults (armature replacement on). The outfit must stay a packed prefab instance, its parts keep sharing
    /// the one armature its VRCFury Armature Link merges, and each part is fitted on the bodies as its toggle wears it.
    /// With <c>offsetArmature</c> the outfit's armature is moved off the target skeleton first, so the armature really is
    /// replaced: still nothing is unpacked, and the other parts keep their link. Everything lives in a preview scene; the
    /// generated meshes are deleted afterwards.
    /// </summary>
    public static class ReFitPrefabOutfitValidation
    {
        private const string Outfit = "Assets/LunaVRC/Classy/Novabeast Classy.prefab";
        private const string Avatar = "Assets/MCB/Authoring/Ultinova/Ultinova custom model.prefab";
        private const string Source = "Assets/Novabeast_V1_2/FBX/Novabeast.fbx";

        public static string Run(bool offsetArmature)
        {
            foreach (var path in new[] { Outfit, Avatar, Source })
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) return "skipped: missing " + path;
            var lines = new List<string>();
            var scene = EditorSceneManager.NewPreviewScene();
            var created = new List<string>();
            var roots = new List<GameObject>();
            try
            {
                var avatar = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Avatar), scene);
                var source = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Source), scene);
                var outfit = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Outfit), scene);
                roots.AddRange(new[] { avatar, source, outfit });
                outfit.transform.SetParent(avatar.transform, false);
                var parts = outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToDictionary(r => r.name);
                var link = LinkedBone(outfit);
                var armatureBefore = parts["Dress Pants"].bones.ToArray();
                if (offsetArmature)
                {
                    // Another base's skeleton: the outfit's hips 3 cm higher (a prefab override, as a person could have).
                    var hips = parts["Turtle neck"].rootBone;
                    hips.position += Vector3.up * .03f;
                }
                lines.Add($"before: prefab={PrefabUtility.GetPrefabInstanceStatus(outfit)}, link={(link != null ? link.name : "none")}");
                var chainBefore = Baked(parts["Chain"]);

                foreach (var name in new[] { "Turtle neck", "Dress Pants" })
                {
                    var part = parts[name];
                    var request = new ReFitRequest
                    {
                        mode = ReFitMode.MeshToMesh, assetRenderer = part, sourceAvatar = source, targetAvatar = avatar,
                        settings = new ReFitSettings { savePrefab = false }
                    };
                    var result = ReFitService.Execute(request);
                    if (!string.IsNullOrEmpty(result.meshAssetPath)) created.Add(result.meshAssetPath);
                    var codes = string.Join(",", result.report.messages.Where(m => m.severity != ReFitSeverity.Info || m.code.Contains("armature") ||
                        m.code.Contains("worn") || m.code.Contains("prefab") || m.code.Contains("vrcfury")).Select(m => m.code).Distinct());
                    lines.Add($"{name}: success={result.success} codes=[{codes}]");
                    foreach (var m in result.report.messages.Where(m => m.code == "worn-body-shapes" || m.severity == ReFitSeverity.Error))
                        lines.Add("  " + m);
                }

                var chainAfter = Baked(parts["Chain"]);
                float moved = chainBefore.Zip(chainAfter, (a, b) => (a - b).magnitude).Max();
                lines.Add($"untouched Chain moved at most {moved * 1000f:F2} mm");
                bool packed = PrefabUtility.GetPrefabInstanceStatus(outfit) == PrefabInstanceStatus.Connected &&
                              PrefabUtility.IsOutermostPrefabInstanceRoot(outfit);
                lines.Add($"after: prefab={PrefabUtility.GetPrefabInstanceStatus(outfit)} packed={packed}");
                foreach (var part in parts.Values)
                {
                    var bones = part.bones;
                    bool shared = bones.All(b => b == null || b.IsChildOf(outfit.transform));
                    bool original = bones.SequenceEqual(armatureBefore);
                    lines.Add($"  {part.name}: mesh={part.sharedMesh.name} bonesUnderOutfit={shared} originalArmature={original} rootBone={(part.rootBone != null ? part.rootBone.name : "-")}");
                }
                foreach (var linkComponent in outfit.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().FullName == "VF.Model.VRCFury"))
                {
                    var linked = LinkedBone(linkComponent);
                    if (linked == null) continue;
                    int users = outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(r => r.bones.Any(b => b != null && b.IsChildOf(linked)));
                    lines.Add($"  link on '{linkComponent.name}' -> {linked.name} ({AnimationUtility.CalculateTransformPath(linked, outfit.transform)}), skinned users={users}");
                }
                var armatures = outfit.GetComponentsInChildren<Transform>(true).Where(t => t.name == "hips" || t.name == "Hips").ToList();
                lines.Add($"  hips objects in the outfit: {armatures.Count} ({string.Join(", ", armatures.Select(t => AnimationUtility.CalculateTransformPath(t, outfit.transform) + (t.gameObject.activeInHierarchy ? "" : " (hidden)")))})");
            }
            catch (Exception e) { lines.Add("exception: " + e); }
            finally
            {
                foreach (var root in roots) if (root != null) { Undo.ClearUndo(root); Object.DestroyImmediate(root); }
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var path in created) AssetDatabase.DeleteAsset(path);
            }
            var text = string.Join("\n", lines);
            System.IO.Directory.CreateDirectory(ReFitCaseValidation.Output);
            System.IO.File.WriteAllText(ReFitCaseValidation.Output + "prefab-outfit" + (offsetArmature ? "-offset" : "") + ".txt", text);
            return text;
        }

        // The renderer's skinned vertices in the world, at its current shape weights.
        private static Vector3[] Baked(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh, true);
                var matrix = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                return mesh.vertices.Select(v => matrix.MultiplyPoint3x4(v)).ToArray();
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static Transform LinkedBone(GameObject outfit) =>
            outfit.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().FullName == "VF.Model.VRCFury")
                .Select(LinkedBone).FirstOrDefault(t => t != null);

        // The bone a VRCFury Armature Link merges (its "Link From"), or null for other VRCFury components.
        private static Transform LinkedBone(Component component)
        {
            var content = new SerializedObject(component).FindProperty("content");
            if (content == null || content.managedReferenceFullTypename == null || !content.managedReferenceFullTypename.Contains("ArmatureLink")) return null;
            var bone = content.FindPropertyRelative("propBone")?.objectReferenceValue as GameObject;
            return bone != null ? bone.transform : null;
        }
    }
}
