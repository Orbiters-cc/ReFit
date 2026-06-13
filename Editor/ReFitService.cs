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

                progress?.Invoke(0.96f, "Applying to the scene");
                result.sceneRenderer = ReFitAssetPipeline.ApplyToScene(request, computation, result.report);
                if (result.sceneRenderer != null)
                    result.prefabAssetPath = ReFitAssetPipeline.TrySavePrefab(computation, result.sceneRenderer, subfolder, result.report);

                result.success = result.sceneRenderer != null && !result.report.HasErrors;
                progress?.Invoke(1f, "Done");
            }
            catch (System.Exception e)
            {
                result.report.Error("refit-exception", $"Unexpected error: {e.Message}\n{e.StackTrace}");
                result.success = false;
            }
            return result;
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
