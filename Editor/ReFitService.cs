using System.Collections;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// Public entry point of ReFit for tools (MCB, scripts) and the wizard.
    ///
    /// <code>
    /// var result = ReFitService.Execute(new ReFitRequest {
    ///     mode = ReFitMode.MeshToMesh,
    ///     assetRenderer = myClothing,
    ///     sourceAvatar = baseAvatarPrefab,
    ///     targetAvatar = mySceneAvatar
    /// });
    /// if (result.success) Debug.Log(result.meshAssetPath);
    /// </code>
    /// </summary>
    public static class ReFitService
    {
        /// <summary>
        /// Runs the full re-fit: computes the deformation, saves the mesh asset, applies everything to the scene
        /// (instantiating prefabs when needed) and saves a prefab when possible. Never throws; inspect
        /// <see cref="ReFitResult.report"/> for diagnostics.
        /// </summary>
        public static ReFitResult Execute(ReFitRequest request, ReFitProgress progress = null)
        {
            var result = new ReFitResult();
            ReFitDebugSession debug = null;
            try
            {
                var computation = new ReFitEngine().Run(request, progress);
                result.report = computation.report;
                if (!computation.success)
                {
                    if (computation.mesh != null) Object.DestroyImmediate(computation.mesh);
                    return result;
                }

                progress?.Invoke(0.92f, "Saving assets");
                var subfolder = request.assetRenderer != null ? request.assetRenderer.name : "ReFit";
                result.mesh = computation.mesh;
                result.meshAssetPath = ReFitAssetPipeline.SaveMesh(computation.mesh, subfolder, result.report);

                debug = ReFitDebugService.BeginSession(request, result.report);
                debug?.Capture("00_input_asset", request.assetRenderer);

                progress?.Invoke(0.96f, "Applying to the scene");
                result.sceneRenderer = ReFitAssetPipeline.ApplyToScene(request, computation, result.report, debug);
                result.originalMesh = computation.appliedOriginalMesh;
                if (result.sceneRenderer != null && (request.settings == null || request.settings.savePrefab))
                    result.prefabAssetPath = ReFitAssetPipeline.TrySavePrefab(computation, result.sceneRenderer, subfolder, result.report);

                result.success = result.sceneRenderer != null && !result.report.HasErrors;
                progress?.Invoke(1f, "Done");
            }
            catch (System.Exception e)
            {
                result.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
                result.success = false;
            }
            finally
            {
                debug?.Finish();
            }
            return result;
        }

        /// <summary>
        /// Asynchronous variant of <see cref="Execute"/> as an editor coroutine: staging and mesh baking happen
        /// on the main thread, the heavy geometry runs on a background thread so the editor stays responsive.
        /// Drive it with any editor coroutine runner (or <c>EditorApplication.update</c>).
        /// <paramref name="onComplete"/> is invoked on the main thread.
        /// </summary>
        public static IEnumerator ExecuteCoroutine(ReFitRequest request, ReFitProgress progress, System.Action<ReFitResult> onComplete)
        {
            var result = new ReFitResult();
            ReFitComputation computation = null;
            ReFitDebugSession debug = null;
            yield return new ReFitEngine().RunCoroutine(request, progress, c => computation = c);

            try
            {
                result.report = computation.report;
                if (computation.success)
                {
                    progress?.Invoke(0.94f, "Saving assets");
                    var subfolder = request.assetRenderer != null ? request.assetRenderer.name : "ReFit";
                    result.mesh = computation.mesh;
                    result.meshAssetPath = ReFitAssetPipeline.SaveMesh(computation.mesh, subfolder, result.report);

                    debug = ReFitDebugService.BeginSession(request, result.report);
                    debug?.Capture("00_input_asset", request.assetRenderer);

                    progress?.Invoke(0.97f, "Applying to the scene");
                    result.sceneRenderer = ReFitAssetPipeline.ApplyToScene(request, computation, result.report, debug);
                    result.originalMesh = computation.appliedOriginalMesh;
                    if (result.sceneRenderer != null && (request.settings == null || request.settings.savePrefab))
                        result.prefabAssetPath = ReFitAssetPipeline.TrySavePrefab(computation, result.sceneRenderer, subfolder, result.report);

                    result.success = result.sceneRenderer != null && !result.report.HasErrors;
                }
                else if (computation.mesh != null)
                {
                    Object.DestroyImmediate(computation.mesh);
                }
            }
            catch (System.Exception e)
            {
                result.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
                result.success = false;
            }
            finally
            {
                debug?.Finish();
            }
            progress?.Invoke(1f, "Done");
            onComplete?.Invoke(result);
        }

        /// <summary>
        /// Dry run that stages the avatars, checks armature matching and proportions, and returns diagnostics
        /// without modifying anything. Surface these to the user before committing — warnings (including proportion
        /// mismatches) are informative only and never block <see cref="Execute"/>.
        /// </summary>
        public static ReFitReport Validate(ReFitRequest request)
        {
            try
            {
                return new ReFitEngine().Validate(request);
            }
            catch (System.Exception e)
            {
                var report = new ReFitReport();
                report.Error("validate-exception", $"Unexpected error: {e.Message}");
                return report;
            }
        }
    }
}
