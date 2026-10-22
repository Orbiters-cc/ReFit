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
        public static Dictionary<int, float> AsShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> zeroed,
            IDictionary<string, float> made = null)
        {
            var overrides = Overrides(body, worn);
            if (overrides == null) return null;
            var mesh = body.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (worn != null && worn.ContainsKey(name) || zeroed != null && zeroed.Contains(name)) continue;
                overrides[i] = Mathf.Clamp(Weight(body, i, made), 0f, 100f) / 100f;
            }
            return overrides;
        }

        // The weight a shape counts at: what the clothing was made with (<see cref="ReFitRequest.madeWithShapes"/>), else
        // what the body shows.
        private static float Weight(SkinnedMeshRenderer body, int index, IDictionary<string, float> made) =>
            made != null && made.TryGetValue(body.sharedMesh.GetBlendShapeName(index), out float weight) ? weight : body.GetBlendShapeWeight(index);

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
        public static float Shown(SkinnedMeshRenderer body, string name, IDictionary<string, float> worn, IDictionary<string, float> made = null)
        {
            var mesh = body != null ? body.sharedMesh : null;
            int index = mesh != null ? mesh.GetBlendShapeIndex(name) : -1;
            if (index < 0 || mesh.GetBlendShapeFrameCount(index) != 1 || worn != null && worn.ContainsKey(name)) return 0f;
            var frame = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(index, 0, frame, null, null);
            var applied = new Vector3[frame.Length];
            BlendShapeEvaluation.Add(mesh, index, Mathf.Clamp(Weight(body, index, made), 0f, 100f), BlendShapeEvaluation.ClampWeights, applied, null,
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
        /// The followed shapes fitted at zero (see <see cref="AsShown"/>): all of them, unless the clothing counts as made on the
        /// target as it shows (<paramref name="asShown"/>: fitted from another body, whose source is the body it was made for
        /// without the target's shapes; or a shapes-only transfer, the clothing already worn on the target), then those it
        /// does not show (<see cref="Shown"/>, at the weights it was made with when <paramref name="made"/> says).
        /// </summary>
        public static List<string> FittedAtZero(SkinnedMeshRenderer body, IDictionary<string, float> worn, IEnumerable<string> followed, bool asShown,
            IDictionary<string, float> made = null)
        {
            var zeroed = new List<string>();
            foreach (var name in followed)
                if (!asShown || Shown(body, name, worn, made) <= 0f) zeroed.Add(name);
            return zeroed;
        }

        /// <summary>
        /// Whether clothing given shapes alone was made over a followed shape the target shows, or over the body without it
        /// (made on the base, the body shaped afterwards). Where the shape moves the skin under the clothing (within
        /// <paramref name="reach"/>), clothing is never made into the body it was made on: when clearly more of it lies in one
        /// of the two bodies than in the other, it was made on the other. When neither tells (loose clothing, or clipping as
        /// much either way), it was made on the body it lies nearer: taken for made with the shape, it stays as authored at
        /// the body's weight. <paramref name="motion"/> is what the shape at its shown weight moves each vertex of
        /// <paramref name="body"/> (the body as it shows, in world space). Null when the shape moves too little of the skin
        /// under the clothing to tell.
        /// </summary>
        public static bool? MadeWithShown(Vector3[] cloth, MeshSnapshot body, SurfaceBvh index, Vector3[] motion, float reach = .05f)
        {
            var shown = new List<float>();
            var without = new List<float>();
            var triangles = body.triangles;
            foreach (var point in cloth)
            {
                var hit = index.ClosestPoint(point, reach);
                if (!hit.found) continue;
                int t = hit.triangle * 3;
                var shift = motion[triangles[t]] * hit.bary.x + motion[triangles[t + 1]] * hit.bary.y + motion[triangles[t + 2]] * hit.bary.z;
                var normal = body.FaceNormal(hit.triangle);
                float along = Vector3.Dot(shift, normal);
                if (Mathf.Abs(along) < MovedToTell) continue;
                float gap = Vector3.Dot(point - hit.position, normal);
                shown.Add(gap);
                without.Add(gap + along);
            }
            if (shown.Count < MinMovedToTell) return null;
            int inShown = shown.Count(gap => gap < -InBody), inWithout = without.Count(gap => gap < -InBody);
            if (inShown - inWithout > ClearlyMore * shown.Count) return false;
            if (inWithout - inShown > ClearlyMore * shown.Count) return true;
            shown.Sort();
            without.Sort();
            return shown[shown.Count / 2] <= without[without.Count / 2];
        }

        // A clothing vertex the shape moves (along the skin's normal) by less than this tells nothing.
        private const float MovedToTell = .001f;
        private const int MinMovedToTell = 8;
        // Deeper than this in the body, a clothing vertex lies in it; a share of the moved ones this much larger is clear.
        private const float InBody = .002f, ClearlyMore = .05f;

        /// <summary>The shapes <see cref="AsShown"/> leaves on, as "name weight" pairs (sorted), for messages and identities.</summary>
        public static string DescribeShown(SkinnedMeshRenderer body, IDictionary<string, float> worn, ICollection<string> zeroed,
            IDictionary<string, float> made = null)
        {
            var mesh = body != null ? body.sharedMesh : null;
            if (mesh == null) return "";
            var shown = new List<string>();
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                float weight = Mathf.Clamp(Weight(body, i, made), 0f, 100f);
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
