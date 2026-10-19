using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Meshes;
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
        /// The target as it shows: each shape at its weight on the body, except those the clothing sets while it is worn
        /// (<paramref name="worn"/>) and the shapes it follows that are fitted at zero (<paramref name="zeroed"/>: the clothing's
        /// own shapes add them to the fit). A shape the clothing does not follow never moves it: fitted over collar fluff the
        /// avatar keeps hidden, it would stand out over nothing. A followed shape the body shows is fitted with it, and its
        /// clothing shape takes it away from the fit.
        /// </summary>
        public static Dictionary<int, float> AsShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> zeroed)
        {
            var overrides = Overrides(body, worn);
            if (overrides == null) return null;
            var mesh = body.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (worn != null && worn.ContainsKey(name) || zeroed != null && zeroed.Contains(name)) continue;
                overrides[i] = Mathf.Clamp(body.GetBlendShapeWeight(i), 0f, 100f) / 100f;
            }
            return overrides;
        }

        /// <summary>
        /// Below this share of its frame, a followed shape the target shows is fitted at zero: it hardly moves the body, and the
        /// drape taking it away, divided by that share, would multiply the drape's error by more than ten.
        /// </summary>
        public const float MinShown = .1f;

        /// <summary>
        /// How much of its frame the body shows of a followed shape (0..1, as Unity applies it at the body's weight), or 0 when
        /// it is fitted at zero: shown less than <see cref="MinShown"/>, set by the clothing while it is worn, missing, or of
        /// several frames (each clothing frame is draped on its own, from the fit at zero).
        /// </summary>
        public static float Shown(SkinnedMeshRenderer body, string name, IDictionary<string, float> worn)
        {
            var mesh = body != null ? body.sharedMesh : null;
            int index = mesh != null ? mesh.GetBlendShapeIndex(name) : -1;
            if (index < 0 || mesh.GetBlendShapeFrameCount(index) != 1 || worn != null && worn.ContainsKey(name)) return 0f;
            var frame = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(index, 0, frame, null, null);
            var applied = new Vector3[frame.Length];
            BlendShapeEvaluation.Add(mesh, index, Mathf.Clamp(body.GetBlendShapeWeight(index), 0f, 100f), BlendShapeEvaluation.ClampWeights, applied, null,
                new BlendShapeEvaluation.Scratch(frame.Length));
            double along = 0, length = 0;
            for (int i = 0; i < frame.Length; i++)
            {
                along += Vector3.Dot(applied[i], frame[i]);
                length += frame[i].sqrMagnitude;
            }
            float share = length > 0 ? (float)(along / length) : 0f;
            return share >= MinShown ? share : 0f;
        }

        /// <summary>
        /// The followed shapes fitted at zero (see <see cref="AsShown"/>): all of them, unless the clothing is fitted from another
        /// body (<paramref name="fromAnother"/>: the source is the body it was made for, without the target's shapes), then those
        /// it does not show (<see cref="Shown"/>).
        /// </summary>
        public static List<string> FittedAtZero(SkinnedMeshRenderer body, IDictionary<string, float> worn, IEnumerable<string> followed, bool fromAnother)
        {
            var zeroed = new List<string>();
            foreach (var name in followed)
                if (!fromAnother || Shown(body, name, worn) <= 0f) zeroed.Add(name);
            return zeroed;
        }

        /// <summary>The shapes <see cref="AsShown"/> leaves on, as "name weight" pairs (sorted), for messages and identities.</summary>
        public static string DescribeShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> zeroed)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null) return "";
            var shown = new List<string>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float weight = Mathf.Clamp(body.GetBlendShapeWeight(i), 0f, 100f);
                if (weight <= 0f || worn != null && worn.ContainsKey(name) || zeroed != null && zeroed.Contains(name)) continue;
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
