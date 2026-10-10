using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        private const float MinimumWidth = 620f;
        private const float MinimumHeight = 560f;
        // A first window: room for the stage beside the page.
        private const float DefaultWidth = 1080f;
        private const float DefaultHeight = 720f;
        // Narrower than this, the stage tops the page instead of standing beside it.
        internal const float NarrowWidth = 760f;

        /// <summary>
        /// The ReFit window, opened at its default size the first time. It keeps the size it is given: pages scroll inside
        /// it rather than growing it.
        /// </summary>
        private static ReFitWizard Summon()
        {
            bool fresh = !HasOpenInstances<ReFitWizard>();
            var window = GetWindow<ReFitWizard>();
            window.titleContent = new GUIContent("ReFit");
            window.minSize = new Vector2(MinimumWidth, MinimumHeight);
            if (fresh && !window.docked)
            {
                var position = window.position;
                window.position = new Rect(position.x, position.y, Mathf.Max(position.width, DefaultWidth), Mathf.Max(position.height, DefaultHeight));
            }
            return window;
        }
    }
}
