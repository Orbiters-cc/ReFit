using System;
using System.Collections;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Public editor facade. Computation is read-only; application is a short, undoable transaction.</summary>
    public static class ReFitService
    {
        public static ReFitResult Execute(ReFitRequest request, ReFitProgress progress = null) =>
            Execute(request, progress, CancellationToken.None);

        public static ReFitResult Execute(ReFitRequest request, ReFitProgress progress,
            CancellationToken cancellationToken)
        {
            var result = new ReFitResult();
            try
            {
                request = request?.Clone();
                var preflight = PrepareSceneAsset(request);
                if (preflight.HasErrors) { result.report = preflight; return result; }
                var computation = new ReFitEngine().Run(request, progress, cancellationToken);
                computation.report.messages.InsertRange(0, preflight.messages);
                return Complete(request, computation, progress, cancellationToken);
            }
            catch (Exception e) { RecordException(result, e); }
            return result;
        }

        /// <summary>
        /// Drive this enumerator on the main thread and dispose it when abandoned. Options are copied at
        /// invocation. Cancellation never applies partial output. Completion is called on the driving thread.
        /// </summary>
        public static IEnumerator ExecuteCoroutine(ReFitRequest request, ReFitProgress progress,
            Action<ReFitResult> onComplete) => ExecuteCoroutine(request, progress, onComplete, CancellationToken.None);

        public static IEnumerator ExecuteCoroutine(ReFitRequest request, ReFitProgress progress,
            Action<ReFitResult> onComplete, CancellationToken cancellationToken)
        {
            return ExecuteCoroutineCore(request?.Clone(), progress, onComplete, cancellationToken);
        }

        private static IEnumerator ExecuteCoroutineCore(ReFitRequest request, ReFitProgress progress,
            Action<ReFitResult> onComplete, CancellationToken cancellationToken)
        {
            var preflight = PrepareSceneAsset(request);
            if (preflight.HasErrors)
            {
                onComplete?.Invoke(new ReFitResult { report = preflight });
                yield break;
            }
            ReFitInputState inputs = null;
            try { inputs = new ReFitInputState(request); }
            catch (Exception e) { preflight.Error("input-capture", e.Message); }
            if (preflight.HasErrors)
            {
                onComplete?.Invoke(new ReFitResult { report = preflight });
                yield break;
            }
            ReFitComputation computation = null;
            var worker = new ReFitEngine().RunCoroutine(request, progress, c => computation = c, cancellationToken);
            using (worker as IDisposable)
                while (worker.MoveNext()) yield return worker.Current;

            if (computation == null)
            {
                onComplete?.Invoke(new ReFitResult { report = preflight });
                yield break;
            }
            computation.report.messages.InsertRange(0, preflight.messages);
            bool unchanged = false;
            try { unchanged = inputs.Unchanged(); }
            catch (Exception e) { computation.report.Error("input-validation", e.Message); }
            if (!unchanged)
            {
                computation.success = false;
                computation.report.Error("input-changed", "An input mesh, pose or renderer changed during computation. Run ReFit again.");
            }
            var result = Complete(request, computation, progress, cancellationToken);
            onComplete?.Invoke(result);
        }

        private static ReFitResult Complete(ReFitRequest request, ReFitComputation computation,
            ReFitProgress progress, CancellationToken cancellationToken)
        {
            var result = new ReFitResult { report = computation.report };
            if (!computation.success || result.report.HasErrors || cancellationToken.IsCancellationRequested)
            {
                if (computation.mesh != null) Object.DestroyImmediate(computation.mesh);
                if (cancellationToken.IsCancellationRequested)
                    result.report.Warn("refit-cancelled", "ReFit was cancelled before application.");
                return result;
            }

            ReFitDebugSession debug = null;
            int undoGroup = -1;
            try
            {
                progress?.Invoke(0.94f, "Applying and saving the result");
                cancellationToken.ThrowIfCancellationRequested();
                // No yielding or client callbacks while this undo group is open.
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("ReFit");
                debug = ReFitDebugService.BeginSession(request, result.report);
                debug?.Capture("00_input_asset", request.assetRenderer);
                debug?.CaptureScenePoseAsDefault("01_scene_pose_as_default", request.assetRenderer);
                result.sceneRenderer = ReFitAssetPipeline.ApplyToScene(request, computation, result.report,
                    out var originalState, debug);
                if (result.sceneRenderer == null || result.report.HasErrors)
                    throw new InvalidOperationException("Scene application did not produce a valid result.");

                var subfolder = request.assetRenderer != null ? request.assetRenderer.name : "ReFit";
                result.mesh = computation.mesh;
                result.originalMesh = computation.appliedOriginalMesh;
                result.originalRendererState = originalState;
                result.primaryShapeName = computation.primaryShapeName;
                result.secondaryShapeNames = computation.secondaryShapeNames;
                result.secondarySourceShapeNames = computation.secondarySourceShapeNames;
                result.meshAssetPath = ReFitAssetPipeline.SaveMesh(computation.mesh, subfolder, result.report);
                if (request.settings.savePrefab)
                    result.prefabAssetPath = ReFitAssetPipeline.TrySavePrefab(computation, result.sceneRenderer, subfolder, result.report);
                debug?.Finish();
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
                result.success = true;
            }
            catch (Exception e)
            {
                RecordException(result, e);
                if (undoGroup >= 0)
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                if (!string.IsNullOrEmpty(result.prefabAssetPath)) AssetDatabase.DeleteAsset(result.prefabAssetPath);
                if (!string.IsNullOrEmpty(result.meshAssetPath)) AssetDatabase.DeleteAsset(result.meshAssetPath);
                if (computation.mesh != null && !AssetDatabase.Contains(computation.mesh))
                    Object.DestroyImmediate(computation.mesh);
                result.mesh = null;
                result.sceneRenderer = null;
                result.meshAssetPath = null;
                result.prefabAssetPath = null;
                result.originalRendererState = null;
            }
            // A caller's progress handler is not part of committing geometry.
            if (result.success)
            {
                try { ReFitBlendshapeHistory.Record(result.secondarySourceShapeNames); }
                catch (Exception e) { result.report.Warn("recent-blendshapes", e.Message); }
                try { progress?.Invoke(1f, "Done"); }
                catch (Exception e) { result.report.Warn("progress-callback", e.Message); }
            }
            return result;
        }

        public static ReFitReport Validate(ReFitRequest request)
        {
            try { return new ReFitEngine().Validate(request); }
            catch (Exception e)
            {
                var report = new ReFitReport();
                report.Error("validate-exception", e.Message);
                return report;
            }
        }

        private static ReFitReport PrepareSceneAsset(ReFitRequest request)
        {
            var report = new ReFitReport();
            try { ReFitAssetPipeline.ValidateSceneAssetArmature(request, report); }
            catch (Exception e) { report.Error("preflight-exception", e.Message); }
            return report;
        }

        private static void RecordException(ReFitResult result, Exception e)
        {
            result.success = false;
            if (e is OperationCanceledException) result.report.Warn("refit-cancelled", "ReFit was cancelled before application.");
            else result.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
        }
    }
}
