using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Optional visualization host. Geometry and projection diagnostics do not depend on a host.</summary>
    public static class ReFitGizmoIntegration
    {
        public static bool ProjectionEnabled
        {
            get => ReFitProjectionGizmoService.Enabled;
            set => ReFitProjectionGizmoService.Enabled = value;
        }
        public static bool WeldedGroupsEnabled
        {
            get => ReFitProjectionGizmoService.WeldedGroupsEnabled;
            set => ReFitProjectionGizmoService.WeldedGroupsEnabled = value;
        }
        public static event Action Changed;
        public static Func<bool> MeshEdgesEnabledProvider { private get; set; }
        public static Func<IEnumerable<SkinnedMeshRenderer>> ActiveRenderersProvider { private get; set; }
        internal static bool MeshEdgesEnabled => MeshEdgesEnabledProvider?.Invoke() ?? false;
        internal static IEnumerable<SkinnedMeshRenderer> ActiveRenderers =>
            ActiveRenderersProvider?.Invoke() ?? Array.Empty<SkinnedMeshRenderer>();
        internal static void NotifyChanged() => Changed?.Invoke();
    }
}
