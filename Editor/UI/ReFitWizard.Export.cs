using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEngine;
#if REFIT_VRCHAT_AVATARS
using Orbiters.Toolkit.Editor.VRChat.Refit;
#endif

namespace Orbiters.ReFit.Editor
{
    // Export FBX: the re-fitted meshes as they look fitted, written by Orbiters Toolkit (VRChat avatar projects).
    public partial class ReFitWizard
    {
#if REFIT_VRCHAT_AVATARS
        private static bool CanExportFbx => true;
#else
        private static bool CanExportFbx => false;
#endif

        // The last run's successful results with their renderer (every part of an outfit).
        private List<ReFitResult> FittedResults()
        {
            var results = outfitResults.Count > 0 ? outfitResults.Select(r => r.result) : new[] { lastResult };
            return results.Where(r => r != null && r.success && r.sceneRenderer != null).ToList();
        }

        /// <summary>The successful result's re-fitted renderers that still wear their fit, and the shape carrying each fit.</summary>
        private Dictionary<SkinnedMeshRenderer, string> ResultFits()
        {
            var fits = new Dictionary<SkinnedMeshRenderer, string>();
            foreach (var result in FittedResults())
                if (!fits.ContainsKey(result.sceneRenderer) && WearsFit(result.sceneRenderer, result.primaryShapeName, result.mesh))
                    fits.Add(result.sceneRenderer, result.primaryShapeName);
            return fits;
        }

        private void ExportResultFbx()
        {
            // The page can show a result undone or reset since it was built: said, never written unfitted.
            var undone = FittedResults().Where(r => !WearsFit(r.sceneRenderer, r.primaryShapeName, r.mesh))
                .Select(r => r.sceneRenderer.name).Distinct().ToList();
            if (undone.Count > 0)
            {
                RefuseUnfitted(undone);
                Render();
                return;
            }
            var fits = ResultFits();
            string name = asset != null ? asset.name : fits.Keys.Select(r => r.name).FirstOrDefault();
            ExportFbx(fits, name);
        }

        private static void ExportFbx(IReadOnlyDictionary<SkinnedMeshRenderer, string> fits, string name)
        {
            if (fits == null || fits.Count == 0) return;
#if REFIT_VRCHAT_AVATARS
            RefitFbxExport.Save(fits.Keys, name, fits);
#endif
        }

        // The renderer still wears its fit: the shape carrying it is on its mesh, or (no shape named) it wears the mesh it was
        // given or its refit record is applied. An Undo or a Reset puts the original, unfitted mesh back.
        private static bool WearsFit(SkinnedMeshRenderer renderer, string fitShape, Mesh fitted)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null) return false;
            if (!string.IsNullOrEmpty(fitShape)) return mesh.GetBlendShapeIndex(fitShape) >= 0;
            if (fitted != null && mesh == fitted) return true;
#if REFIT_VRCHAT_AVATARS
            var record = RefitRecords.Find(renderer);
            return record != null && record.Applied;
#else
            return false;
#endif
        }

        private static void RefuseUnfitted(IReadOnlyList<string> names) =>
            FbxExportDialog.Show(new FbxExportResult
            {
                Error = names.Count == 1
                    ? $"{names[0]} no longer wears its refit (it was undone or reset)."
                    : $"{string.Join(", ", names)} no longer wear their refit (it was undone or reset)."
            });
    }
}
