using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Scene component carrying projection diagnostics for editor gizmo rendering.</summary>
    public sealed class ReFitProjectionDebugComponent : MonoBehaviour
    {
        public SkinnedMeshRenderer targetRenderer;
        public ReFitProjectionDebugData data;
        public bool visible = true;
    }

    /// <summary>Scene component carrying welded group markers for editor mesh-edge diagnostics.</summary>
    public sealed class ReFitWeldedGroupDebugComponent : MonoBehaviour
    {
        public SkinnedMeshRenderer targetRenderer;
        public ReFitWeldedGroupDebugData data;
        public bool visible = true;
    }
}
