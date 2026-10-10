using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    internal sealed class ReFitCommissionPhoto
    {
        public string name;
        public byte[] bytes;
    }

    internal static class ReFitCommissionCapture
    {
        private static readonly Vector3[] Directions = { Vector3.forward, new Vector3(1, 0, 1), Vector3.right, new Vector3(0, 1.5f, 1) };
        private static readonly string[] Names = { "front", "three-quarter", "side", "elevated" };
        private static readonly Color Background = new Color(0.18f, 0.19f, 0.21f, 1f);

        // Only renderer data is copied; avatar scripts, constraints and physics never run in the preview.
        internal static List<ReFitCommissionPhoto> Capture(GameObject avatar, SkinnedMeshRenderer accessory, string primaryShape)
        {
            var photos = new List<ReFitCommissionPhoto>();
            using (var scene = ReFitScenePicture.Collect(avatar, accessory, primaryShape))
                for (int view = 0; view < Directions.Length; view++)
                {
                    var image = ReFitScenePicture.Render(scene, Directions[view], false, 1024, 1024, 1.08f, Background, false);
                    try { photos.Add(new ReFitCommissionPhoto { name = "refit-" + Names[view] + ".jpg", bytes = image.EncodeToJPG(85) }); }
                    finally { Object.DestroyImmediate(image); }
                }
            return photos;
        }

        internal static Matrix4x4 CaptureMatrix(Renderer renderer) => ReFitScenePicture.CaptureMatrix(renderer);
    }
}
