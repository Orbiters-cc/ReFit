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

        /// <summary>A fit kept for a refitted renderer (Orbiters Toolkit's record keeps it): one choice of the avatar's "Fits" menu.</summary>
        internal sealed class KeptFit
        {
            public string name;
            /// <summary>The renderer wears it now.</summary>
            public bool current;
            internal object fit;
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

        /// <summary>The body a refitted renderer was fitted to, as its record keeps it (null when it has no record).</summary>
        public static SkinnedMeshRenderer BodyOf(SkinnedMeshRenderer renderer)
        {
            var record = RefitRecords.Find(renderer);
            return record != null && record.Applied ? record.body : null;
        }

        /// <summary>The body shrink recorded with a refitted renderer: how many skin points and how far at most (mm), or null.</summary>
        public static (int points, float largestMm)? BodyShrinkOf(SkinnedMeshRenderer renderer)
        {
            var record = RefitRecords.Find(renderer);
            if (record == null || !record.Applied || !record.HasBodyShrink) return null;
            float largest = 0f;
            foreach (var offset in record.bodyShrinkOffsets) largest = Mathf.Max(largest, offset.magnitude);
            return (record.bodyShrinkVertices.Count, largest * 1000f);
        }

        /// <summary>The fits kept for a refitted renderer, in the order they were kept.</summary>
        public static List<KeptFit> KeptFits(SkinnedMeshRenderer renderer)
        {
            var record = RefitRecords.Find(renderer);
            var list = new List<KeptFit>();
            if (record == null) return list;
            var current = RefitRecords.CurrentFit(record);
            foreach (var fit in record.keptFits)
                if (fit != null && fit.Mesh != null) list.Add(new KeptFit { name = fit.name, current = fit == current, fit = fit });
            return list;
        }

        /// <summary>Whether the renderer's current fit is kept already.</summary>
        public static bool IsKept(SkinnedMeshRenderer renderer) => RefitRecords.CurrentFit(RefitRecords.Find(renderer)) != null;

        /// <summary>Keeps the renderer's current fit under <paramref name="name"/>, with Undo; false when it has no applied refit.</summary>
        public static bool KeepFit(SkinnedMeshRenderer renderer, string name)
        {
            var record = RefitRecords.Find(renderer);
            return record != null && record.Applied && RefitRecords.KeepFit(record, name) != null;
        }

        /// <summary>Puts a kept fit back on the renderer, with Undo (the current one is kept first); false when it cannot be.</summary>
        public static bool UseFit(SkinnedMeshRenderer renderer, KeptFit fit) =>
            fit?.fit is RefitKeptFit kept && RefitRecords.UseFit(RefitRecords.Find(renderer), kept);

        /// <summary>Forgets a kept fit, with Undo.</summary>
        public static void ForgetFit(SkinnedMeshRenderer renderer, KeptFit fit)
        {
            if (fit?.fit is RefitKeptFit kept) RefitRecords.RemoveKeptFit(RefitRecords.Find(renderer), kept);
        }

        /// <summary>Where the avatar's menu offers a renderer's kept fits.</summary>
        public static string FitsMenu(SkinnedMeshRenderer renderer) => RefitKeptFitsBuild.MenuRoot + "/" + (renderer != null ? renderer.name : "");

        /// <summary>
        /// Keeps a body shrink with a refitted renderer's record, with Undo: the avatar's build moves that skin in while the
        /// clothing shows. Null removes it. False when the renderer has no record to keep it with.
        /// </summary>
        public static bool SetBodyShrink(SkinnedMeshRenderer renderer, SkinnedMeshRenderer body, ReFitMotionCheck.BodyShrink shrink)
        {
            var record = RefitRecords.Find(renderer);
            if (record == null || !record.Applied) return false;
            if (shrink == null || shrink.vertices.Length == 0) RefitRecords.SetBodyShrink(record, null, new int[0], new Vector3[0]);
            else RefitRecords.SetBodyShrink(record, body != null ? body.sharedMesh : null, shrink.vertices, shrink.offsets);
            return true;
        }
#else
        public static bool IsAvailable => false;
        public static IReadOnlyList<RefittedAsset> GetRefittedAssets() => new List<RefittedAsset>();
        public static bool TryReset(RefittedAsset asset, bool restored) => false;
        public static IDisposable Hold(GameObject avatar) => new Release(null);
        public static PreviousFit StartAgain(SkinnedMeshRenderer renderer) => null;
        public static bool TryRegister(ReFitRequest request, ReFitResult result, string baseKey, string baseName) => false;
        public static SkinnedMeshRenderer BodyOf(SkinnedMeshRenderer renderer) => null;
        public static (int points, float largestMm)? BodyShrinkOf(SkinnedMeshRenderer renderer) => null;
        public static bool SetBodyShrink(SkinnedMeshRenderer renderer, SkinnedMeshRenderer body, ReFitMotionCheck.BodyShrink shrink) => false;
        public static List<KeptFit> KeptFits(SkinnedMeshRenderer renderer) => new List<KeptFit>();
        public static bool IsKept(SkinnedMeshRenderer renderer) => false;
        public static bool KeepFit(SkinnedMeshRenderer renderer, string name) => false;
        public static bool UseFit(SkinnedMeshRenderer renderer, KeptFit fit) => false;
        public static void ForgetFit(SkinnedMeshRenderer renderer, KeptFit fit) { }
        public static string FitsMenu(SkinnedMeshRenderer renderer) => "";
#endif
    }
}
