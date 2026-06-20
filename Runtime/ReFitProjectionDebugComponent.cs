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
}
