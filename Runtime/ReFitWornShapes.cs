using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The body as the clothing is worn: every body shape at zero except those the clothing sets while it is worn (see
    /// <see cref="ReFitRequest.wornBodyShapes"/>), by name. Clothing made with a toggle that hides fluff or shrinks the
    /// skin under it was fitted on that body, and is worn on it.
    /// </summary>
    public static class ReFitWornShapes
    {
        /// <summary>Shape index to weight (0..1) for <see cref="MeshSnapshot.Capture"/>, or null for a body without shapes.</summary>
        public static Dictionary<int, float> Overrides(SkinnedMeshRenderer body, IDictionary<string, float> worn)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0) return null;
            var overrides = new Dictionary<int, float>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++) overrides[i] = 0f;
            if (worn != null)
                foreach (var pair in worn)
                {
                    int index = mesh.GetBlendShapeIndex(pair.Key);
                    if (index >= 0) overrides[index] = Mathf.Clamp(pair.Value, 0f, 100f) / 100f;
                }
            return overrides;
        }

        /// <summary>The worn shapes a body has, as "name weight" pairs for messages and identities (sorted, non-zero).</summary>
        public static string Describe(SkinnedMeshRenderer body, IDictionary<string, float> worn)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null || worn == null) return "";
            return string.Join(", ", worn.Where(p => p.Value > 0f && mesh.GetBlendShapeIndex(p.Key) >= 0)
                .OrderBy(p => p.Key, System.StringComparer.Ordinal).Select(p => p.Key + " " + p.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
