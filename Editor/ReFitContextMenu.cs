using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    internal static class ReFitContextMenu
    {
        [MenuItem("GameObject/ReFit", false, -100)]
        private static void Open(MenuCommand command)
        {
            var selected = command.context as GameObject ?? Selection.activeGameObject;
            if (selected != null) ReFitWizard.OpenForAccessory(selected);
        }

        [MenuItem("GameObject/ReFit", true)]
        private static bool Validate() => Selection.gameObjects.Length == 1 &&
            Selection.activeGameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true).Any(r => r.sharedMesh != null);

        [MenuItem("CONTEXT/SkinnedMeshRenderer/ReFit")]
        private static void OpenRenderer(MenuCommand command)
        {
            var renderer = command.context as SkinnedMeshRenderer;
            if (renderer != null && renderer.sharedMesh != null) ReFitWizard.OpenForAccessory(renderer.gameObject);
        }

        internal static GameObject FindAvatar(GameObject accessory)
        {
            if (accessory == null || EditorUtility.IsPersistent(accessory)) return null;
            for (var t = accessory.transform.parent; t != null; t = t.parent)
            {
                foreach (var component in t.GetComponents<Component>())
                {
                    if (component == null) continue;
                    if (component.GetType().Name == "VRCAvatarDescriptor") return t.gameObject;
                    if (component is Animator animator && animator.avatar != null && animator.avatar.isHuman) return t.gameObject;
                }
                // Generic rigs need a distinct body, not merely the scene's top-level grouping object.
                var bodies = t.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null &&
                    !r.transform.IsChildOf(accessory.transform) && string.Equals(r.name, "Body", System.StringComparison.OrdinalIgnoreCase)).ToArray();
                if (bodies.Length == 1) return t.gameObject;
                if (bodies.Length > 1) return null;
            }
            return null;
        }
    }
}
