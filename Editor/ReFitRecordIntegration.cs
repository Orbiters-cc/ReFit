using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
#endif

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// Records the wizard's results with Orbiters Toolkit, as MCB and My Avatar record theirs: the transferred blendshapes
    /// follow the body's animations when the avatar is built, MCB keeps them per custom base version, and any of these tools
    /// can restore the original mesh. VRChat avatar projects only.
    /// </summary>
    internal static class ReFitRecordIntegration
    {
        internal sealed class RefittedAsset
        {
            public SkinnedMeshRenderer renderer;

            public string DisplayName
            {
                get
                {
                    if (renderer == null) return "Re-fitted asset";
                    var root = renderer.transform.root;
                    return root != null && root != renderer.transform ? $"{renderer.name}  ({root.name})" : renderer.name;
                }
            }
        }

        private const string EnabledPreference = "Orbiters.ReFit.RecordResults";

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPreference, true);
            set => EditorPrefs.SetBool(EnabledPreference, value);
        }

#if REFIT_VRCHAT_AVATARS
        public static bool IsAvailable => true;

        /// <summary>Refitted meshes in the open scenes, whichever tool refitted them.</summary>
        public static IReadOnlyList<RefittedAsset> GetRefittedAssets() =>
            Resources.FindObjectsOfTypeAll<OrbitersRefit>()
                .Where(r => r != null && r.gameObject.scene.IsValid() && r.gameObject.scene.isLoaded && r.Applied)
                .Select(r => new RefittedAsset { renderer = r.GetComponent<SkinnedMeshRenderer>() })
                .ToList();

        /// <summary>Forgets the refit; restores the original mesh unless <paramref name="restored"/> (ReFit restored it itself).</summary>
        /// <summary>Puts a refitted mesh back to its original (its record removed), so a new refit does not stack on it.</summary>
        public static bool TryRestore(SkinnedMeshRenderer renderer)
        {
            var record = renderer != null ? renderer.GetComponent<OrbitersRefit>() : null;
            if (record == null || !record.Applied) return false;
            RefitRecords.Remove(record, restore: true);
            return true;
        }

        public static bool TryReset(RefittedAsset asset, bool restored)
        {
            var record = asset?.renderer != null ? asset.renderer.GetComponent<OrbitersRefit>() : null;
            if (record == null) return false;
            RefitRecords.Remove(record, restore: !restored);
            return true;
        }

        public static bool TryRegister(ReFitRequest request, ReFitResult result)
        {
            if (!Enabled || request == null || result == null || !result.success || result.sceneRenderer == null || result.mesh == null)
                return false;
            var renderer = result.sceneRenderer;
            if (EditorUtility.IsPersistent(renderer) || !renderer.gameObject.scene.IsValid()) return false;
            var root = RefitRecords.AvatarRoot(renderer.transform);
            var body = request.targetBodyRenderer != null && request.targetBodyRenderer.transform.IsChildOf(root)
                ? request.targetBodyRenderer
                : AttachmentPlanner.Body(root, renderer.transform);
            var original = result.originalRendererState != null
                ? result.originalRendererState.ToRecordState(root)
                : new RefitRendererState { mesh = result.originalMesh };
            var shapes = new List<RefitShape>();
            int count = System.Math.Min(result.secondarySourceShapeNames?.Length ?? 0, result.secondaryShapeNames?.Length ?? 0);
            for (int i = 0; i < count; i++) shapes.Add(new RefitShape(result.secondarySourceShapeNames[i], result.secondaryShapeNames[i]));
            RefitRecords.Register(renderer, original, result.mesh, result.meshAssetPath, body, shapes,
                request.mode == ReFitMode.Blendshape ? OrbitersRefit.FitKind.Shapes : OrbitersRefit.FitKind.Fitted, null, null, "ReFit");
            return true;
        }
#else
        public static bool IsAvailable => false;
        public static IReadOnlyList<RefittedAsset> GetRefittedAssets() => new List<RefittedAsset>();
        public static bool TryReset(RefittedAsset asset, bool restored) => false;
        public static bool TryRestore(SkinnedMeshRenderer renderer) => false;
        public static bool TryRegister(ReFitRequest request, ReFitResult result) => false;
#endif
    }
}
