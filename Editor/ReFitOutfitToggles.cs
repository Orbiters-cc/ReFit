using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// What an outfit does to the body while one of its parts is worn, read from its own setup without referencing the tools
    /// that build it: VRCFury toggles turning the part on (their blendshape actions on the body or on every renderer) and
    /// Modular Avatar Shape Changers on the part or above it. Outfits hide fluff and shrink the skin under them this way;
    /// their parts were fitted on that body (see <see cref="ReFitRequest.wornBodyShapes"/>).
    /// </summary>
    internal static class ReFitOutfitToggles
    {
        private const string VrcFury = "VF.Model.VRCFury", ShapeChanger = "nadena.dev.modular_avatar.core.ModularAvatarShapeChanger";

        /// <summary>Body shape weights (0..100) by name while <paramref name="part"/> is worn; empty when nothing sets any.</summary>
        public static Dictionary<string, float> WornBodyShapes(SkinnedMeshRenderer part, SkinnedMeshRenderer body)
        {
            var shapes = new Dictionary<string, float>();
            if (part == null) return shapes;
            var outfit = OutfitRoot(part.transform, body);
            foreach (var component in outfit.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().FullName;
                if (type == VrcFury) ReadVrcFury(component, part.transform, outfit, body, shapes);
                else if (type == ShapeChanger && part.transform.IsChildOf(component.transform)) ReadShapeChanger(component, body, shapes);
            }
            return shapes;
        }

        // The outfit holding the part: its object directly under the avatar (the body's root), or the part's own root.
        private static Transform OutfitRoot(Transform part, SkinnedMeshRenderer body)
        {
            var avatar = body != null ? body.transform.root : null;
            var top = part;
            while (top.parent != null && top.parent != avatar) top = top.parent;
            return top;
        }

        private static void ReadVrcFury(Component component, Transform part, Transform outfit, SkinnedMeshRenderer body, Dictionary<string, float> shapes)
        {
            var serialized = new SerializedObject(component);
            var content = serialized.FindProperty("content");
            if (content != null && content.propertyType == SerializedPropertyType.ManagedReference) ReadFeature(content, part, outfit, body, shapes);
            var features = serialized.FindProperty("config.features");
            if (features != null && features.isArray)
                for (int i = 0; i < features.arraySize; i++)
                    ReadFeature(features.GetArrayElementAtIndex(i), part, outfit, body, shapes);
        }

        // A toggle turning the part (or an object holding it, inside the outfit) on: its blendshape actions on the body.
        private static void ReadFeature(SerializedProperty feature, Transform part, Transform outfit, SkinnedMeshRenderer body, Dictionary<string, float> shapes)
        {
            if (feature == null || feature.managedReferenceFullTypename == null || !feature.managedReferenceFullTypename.EndsWith("VF.Model.Feature.Toggle")) return;
            var actions = feature.FindPropertyRelative("state.actions");
            if (actions == null || !actions.isArray) return;
            bool showsPart = false;
            for (int i = 0; i < actions.arraySize && !showsPart; i++)
            {
                var action = actions.GetArrayElementAtIndex(i);
                if (action.managedReferenceFullTypename == null || !action.managedReferenceFullTypename.EndsWith("ObjectToggleAction")) continue;
                var obj = action.FindPropertyRelative("obj")?.objectReferenceValue as GameObject;
                // Mode 0 turns the object on (1 off, 2 flips it).
                var mode = action.FindPropertyRelative("mode");
                if (obj != null && (mode == null || mode.intValue != 1) && part.IsChildOf(obj.transform) && obj.transform.IsChildOf(outfit))
                    showsPart = true;
            }
            if (!showsPart) return;
            for (int i = 0; i < actions.arraySize; i++)
            {
                var action = actions.GetArrayElementAtIndex(i);
                if (action.managedReferenceFullTypename == null || !action.managedReferenceFullTypename.EndsWith("BlendShapeAction")) continue;
                string name = action.FindPropertyRelative("blendShape")?.stringValue;
                var value = action.FindPropertyRelative("blendShapeValue");
                bool all = action.FindPropertyRelative("allRenderers")?.boolValue ?? false;
                var renderer = action.FindPropertyRelative("renderer")?.objectReferenceValue as SkinnedMeshRenderer;
                if (string.IsNullOrEmpty(name) || value == null || !(all || (body != null && renderer == body))) continue;
                if (body != null && body.sharedMesh != null && body.sharedMesh.GetBlendShapeIndex(name) < 0) continue;
                shapes[name] = value.floatValue;
            }
        }

        // Shapes the changer sets (or deletes: hidden all the same) on the body while it is active.
        private static void ReadShapeChanger(Component component, SkinnedMeshRenderer body, Dictionary<string, float> shapes)
        {
            if (body == null || body.sharedMesh == null) return;
            var serialized = new SerializedObject(component);
            if (serialized.FindProperty("m_inverted")?.boolValue == true) return;
            var list = serialized.FindProperty("m_shapes");
            if (list == null || !list.isArray) return;
            string bodyPath = AnimationUtility.CalculateTransformPath(body.transform, body.transform.root);
            for (int i = 0; i < list.arraySize; i++)
            {
                var shape = list.GetArrayElementAtIndex(i);
                string path = shape.FindPropertyRelative("Object.referencePath")?.stringValue;
                string name = shape.FindPropertyRelative("ShapeName")?.stringValue;
                if (string.IsNullOrEmpty(name) || path != bodyPath || body.sharedMesh.GetBlendShapeIndex(name) < 0) continue;
                // ChangeType: 0 deletes the shape's vertices (fully applied), 1 sets the value.
                bool delete = shape.FindPropertyRelative("ChangeType")?.enumValueIndex == 0;
                shapes[name] = delete ? 100f : shape.FindPropertyRelative("Value")?.floatValue ?? 0f;
            }
        }
    }
}
