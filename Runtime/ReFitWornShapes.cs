using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The body as the clothing is worn: every body shape at zero except those the clothing sets while it is worn (see
    /// <see cref="ReFitRequest.wornBodyShapes"/>), by name. Clothing made with a toggle that hides fluff or shrinks the
    /// skin under it was fitted on that body, and is worn on it. The target is fitted as it shows (<see cref="AsShown"/>).
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

        /// <summary>
        /// The target as it shows: each shape at its weight on the body, except the shapes the clothing follows (at zero: they
        /// become the clothing's own shapes) and those it sets while it is worn (<paramref name="worn"/>). A shape the clothing
        /// does not follow never moves it: fitted over collar fluff the avatar keeps hidden, it would stand out over nothing.
        /// </summary>
        public static Dictionary<int, float> AsShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> followed)
        {
            var overrides = Overrides(body, worn);
            if (overrides == null) return null;
            var mesh = body.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (worn != null && worn.ContainsKey(name) || followed != null && followed.Contains(name)) continue;
                overrides[i] = Mathf.Clamp(body.GetBlendShapeWeight(i), 0f, 100f) / 100f;
            }
            return overrides;
        }

        /// <summary>The shapes <see cref="AsShown"/> leaves on, as "name weight" pairs (sorted), for messages and identities.</summary>
        public static string DescribeShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> followed)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null) return "";
            var shown = new List<string>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float weight = Mathf.Clamp(body.GetBlendShapeWeight(i), 0f, 100f);
                if (weight <= 0f || worn != null && worn.ContainsKey(name) || followed != null && followed.Contains(name)) continue;
                shown.Add(name + " " + weight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            }
            shown.Sort(System.StringComparer.Ordinal);
            return string.Join(", ", shown);
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
