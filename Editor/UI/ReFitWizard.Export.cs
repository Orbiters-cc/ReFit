using System.Collections.Generic;
using System.Linq;
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

        /// <summary>The successful result's re-fitted renderers (every part of an outfit) and the shape carrying each fit.</summary>
        private Dictionary<SkinnedMeshRenderer, string> ResultFits()
        {
            var fits = new Dictionary<SkinnedMeshRenderer, string>();
            var results = outfitResults.Count > 0 ? outfitResults.Select(r => r.result) : new[] { lastResult };
            foreach (var result in results)
                if (result != null && result.success && result.sceneRenderer != null && !fits.ContainsKey(result.sceneRenderer))
                    fits.Add(result.sceneRenderer, result.primaryShapeName);
            return fits;
        }

        private void ExportResultFbx()
        {
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
    }
}
