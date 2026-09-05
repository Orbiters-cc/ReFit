using Orbiters.XRayGizmos.Editor;
using UnityEditor;

namespace Orbiters.ReFit.Editor
{
    [InitializeOnLoad]
    public static class ReFitXRayIntegration
    {
        static ReFitXRayIntegration()
        {
            XRayExternalGizmoRegistry.Register(
                "orbiters.refit.projection-rays",
                "ReFit projection rays",
                () => ReFitGizmoIntegration.ProjectionEnabled,
                value => ReFitGizmoIntegration.ProjectionEnabled = value,
                "Asset/source/target projection diagnostics");
            XRayExternalGizmoRegistry.Register(
                "orbiters.refit.welded-groups",
                "ReFit welded vertex groups",
                () => ReFitGizmoIntegration.WeldedGroupsEnabled,
                value => ReFitGizmoIntegration.WeldedGroupsEnabled = value,
                "Welded vertex group markers on ReFit debug mesh-edge snapshots");
            ReFitGizmoIntegration.Changed += XRayExternalGizmoRegistry.NotifyChanged;
            ReFitGizmoIntegration.MeshEdgesEnabledProvider = () => XRayMeshEdgeService.Enabled;
            ReFitGizmoIntegration.ActiveRenderersProvider = () => XRayMeshEdgeService.ActiveRenderers;
        }

        public static void VerifyRegistry()
        {
            const string id = "orbiters.refit.tests.fake-extra-gizmo";
            bool enabled = false;
            XRayExternalGizmoRegistry.Register(id, "Fake ReFit gizmo", () => enabled, value => enabled = value);
            try
            {
                foreach (var entry in XRayExternalGizmoRegistry.Entries)
                {
                    if (entry.Id != id) continue;
                    entry.SetEnabled(true);
                    if (!enabled) throw new System.Exception("External gizmo did not receive its enabled toggle.");
                    entry.SetEnabled(false);
                    if (enabled) throw new System.Exception("External gizmo did not receive its disabled toggle.");
                    return;
                }
                throw new System.Exception("External gizmo was not registered.");
            }
            finally { XRayExternalGizmoRegistry.Unregister(id); }
        }
    }
}
