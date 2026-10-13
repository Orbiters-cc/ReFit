using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Posing;
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

        /// <summary>A refit taken back to its original for a new run: <see cref="Keep"/> puts it back when the run fails.</summary>
        internal sealed class PreviousFit
        {
            private Action keep;
            public PreviousFit(Action keep) => this.keep = keep;

            /// <summary>The run failed or stopped: the renderer gets its previous fit back (once).</summary>
            public void Keep()
            {
                var action = keep;
                keep = null;
                action?.Invoke();
            }

            /// <summary>The run succeeded: the previous fit is gone for good.</summary>
            public void Drop() => keep = null;
        }

        private sealed class Release : IDisposable
        {
            private Action release;
            public Release(Action release) => this.release = release;

            public void Dispose()
            {
                var action = release;
                release = null;
                action?.Invoke();
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

        /// <summary>
        /// Marks the avatar as being refitted until disposed, as the Orbiters tools' own refits do: the custom base cannot change
        /// meanwhile (MCB's version switch waits), and its posing preview rests, so the clothing is captured and fitted where
        /// it rests.
        /// </summary>
        public static IDisposable Hold(GameObject avatar)
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar)) return new Release(null);
            var root = avatar.transform;
            var held = RefitRunner.Hold(root);
            IDisposable paused = null;
            try { paused = AccessoryPoseSync.Pause(root); }
            catch { held.Dispose(); throw; }
            return new Release(() =>
            {
                try { paused.Dispose(); }
                finally { held.Dispose(); }
            });
        }

        /// <summary>
        /// Puts a refitted mesh back to its original for a new refit, so it does not stack on the old one; its record stays
        /// until the new one replaces it. Null when it is not refitted. Keep the result to give the old fit back on failure.
        /// </summary>
        public static PreviousFit StartAgain(SkinnedMeshRenderer renderer)
        {
            var record = renderer != null && !EditorUtility.IsPersistent(renderer) ? renderer.GetComponent<OrbitersRefit>() : null;
            if (record == null || !record.Applied) return null;
            var root = RefitRecords.AvatarRoot(renderer.transform);
            var fitted = RefitRecords.Capture(root, renderer);
            string metadata = RefitEngine.Current?.SaveMetadata(renderer);
            RefitRecords.Restore(root, renderer, record.original, "Refit again");
            return new PreviousFit(() =>
            {
                if (renderer == null) return;
                RefitRecords.Restore(root, renderer, fitted, "Keep previous refit");
                if (metadata != null) RefitEngine.Current?.LoadMetadata(renderer, metadata);
            });
        }

        /// <summary>Takes the refit back for good (MCB forgets its saved fit too); restores the original mesh unless <paramref name="restored"/> (ReFit restored it itself).</summary>
        public static bool TryReset(RefittedAsset asset, bool restored)
        {
            var record = asset?.renderer != null ? asset.renderer.GetComponent<OrbitersRefit>() : null;
            if (record == null) return false;
            RefitRecords.Discard(record, restore: !restored);
            return true;
        }

        /// <param name="baseKey">The avatar's custom base and version the fit was made for (null when none is known): MCB keeps
        /// it for that version only.</param>
        public static bool TryRegister(ReFitRequest request, ReFitResult result, string baseKey, string baseName)
        {
            if (request == null || result == null || !result.success || result.sceneRenderer == null || result.mesh == null)
                return false;
            if (!Enabled)
            {
                // Not recorded: the record of the fit this one replaced (taken back for the run) does not stay behind.
                var stale = result.sceneRenderer.GetComponent<OrbitersRefit>();
                if (stale != null && !stale.Applied) RefitRecords.Remove(stale, restore: false);
                return false;
            }
            var renderer = result.sceneRenderer;
            if (EditorUtility.IsPersistent(renderer) || !renderer.gameObject.scene.IsValid()) return false;
            var root = RefitRecords.AvatarRoot(renderer.transform);
            var body = request.targetBodyRenderer != null && request.targetBodyRenderer.transform.IsChildOf(root)
                ? request.targetBodyRenderer
                : AttachmentPlanner.Body(root, renderer.transform);
            // Shapes added to a refitted mesh (the run started from its refit): its original, fit and earlier shapes stay, so
            // every tool's reset still gives back the real original, and a fit made for another base stays marked so.
            var existing = renderer.GetComponent<OrbitersRefit>();
            bool adding = request.mode == ReFitMode.Blendshape && existing != null && existing.mesh != null && existing.mesh == result.originalMesh;
            var original = adding ? existing.original
                : result.originalRendererState != null ? result.originalRendererState.ToRecordState(root)
                : new RefitRendererState { mesh = result.originalMesh };
            var shapes = adding ? new List<RefitShape>(existing.shapes) : new List<RefitShape>();
            int count = System.Math.Min(result.secondarySourceShapeNames?.Length ?? 0, result.secondaryShapeNames?.Length ?? 0);
            for (int i = 0; i < count; i++) shapes.Add(new RefitShape(result.secondarySourceShapeNames[i], result.secondaryShapeNames[i]));
            var kind = adding ? existing.kind : request.mode == ReFitMode.Blendshape ? OrbitersRefit.FitKind.Shapes : OrbitersRefit.FitKind.Fitted;
            if (adding && !string.IsNullOrEmpty(existing.baseKey)) { baseKey = existing.baseKey; baseName = existing.baseName; }
            RefitRecords.Register(renderer, original, result.mesh, result.meshAssetPath, body, shapes, kind, baseKey, baseName, "ReFit");
            return true;
        }
#else
        public static bool IsAvailable => false;
        public static IReadOnlyList<RefittedAsset> GetRefittedAssets() => new List<RefittedAsset>();
        public static bool TryReset(RefittedAsset asset, bool restored) => false;
        public static IDisposable Hold(GameObject avatar) => new Release(null);
        public static PreviousFit StartAgain(SkinnedMeshRenderer renderer) => null;
        public static bool TryRegister(ReFitRequest request, ReFitResult result, string baseKey, string baseName) => false;
#endif
    }
}
