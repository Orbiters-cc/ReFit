using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The ReFit logo (five light bars) built from plain VisualElements so it needs no texture import.
    /// Geometry mirrors the official SVG (viewBox 39x35).
    /// </summary>
    public static class ReFitLogo
    {
        /// <summary>Creates the logo at the given scale (1 = 39x35 px).</summary>
        public static VisualElement Create(float scale = 1f)
        {
            var root = new VisualElement
            {
                style =
                {
                    width = 39f * scale,
                    height = 35f * scale,
                    flexShrink = 0
                }
            };
            // x, y, width, height in SVG units
            AddBar(root, 0f, 0f, 39f, 3.72f, scale);        // top bar
            AddBar(root, 0f, 12.69f, 39f, 9.87f, scale);     // middle bar
            AddBar(root, 0f, 31.92f, 9.27f, 3.08f, scale);   // bottom left
            AddBar(root, 14.99f, 31.92f, 9.27f, 3.08f, scale); // bottom middle
            AddBar(root, 29.73f, 31.92f, 9.27f, 3.08f, scale); // bottom right
            return root;
        }

        private static void AddBar(VisualElement parent, float x, float y, float w, float h, float scale)
        {
            var bar = new VisualElement();
            bar.AddToClassList("refit-logo-bar");
            bar.style.left = x * scale;
            bar.style.top = y * scale;
            bar.style.width = w * scale;
            bar.style.height = h * scale;
            parent.Add(bar);
        }
    }
}
