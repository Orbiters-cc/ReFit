using System.Text.RegularExpressions;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Whether an asset is clothing worn on the body (fitted snugly by default) rather than an accessory: body clothing the
    /// gravity detection recognises, and by name underwear, swimwear and form-fitting one-pieces, which are small or sit on
    /// the hips and legs where that detection does not look. Loose one-pieces (onesies, jumpsuits) are not named here: a
    /// snug fit made the reported onesie eight times slower to fit for about a tenth less clipping.
    /// </summary>
    public static class ReFitClothingDetection
    {
        // Distinctive enough to be found inside longer names ("classicJockstrap", "SportBodysuit").
        private static readonly string[] Tokens =
        {
            "jockstrap", "underwear", "undies", "lingerie", "bikini", "swimsuit", "swimwear", "leotard", "bodysuit",
            "panties", "boxers", "boxerbrief", "briefs", "thong", "speedo"
        };

        // Short words that are clothing only on their own ("Bra", not "Bracelet").
        private static readonly Regex Words = new Regex(@"(?<![a-z])(bra|bras|jock|trunks)(?![a-z])", RegexOptions.CultureInvariant);

        /// <summary>True for clothing: by name (underwear, swimwear, form-fitting one-pieces) or as detected body clothing.</summary>
        public static bool IsClothing(SkinnedMeshRenderer renderer, GameObject targetAvatar)
        {
            if (renderer == null || renderer.sharedMesh == null) return false;
            if (NamedClothing(renderer)) return true;
            var candidate = ReFitGravityRelaxation.DetectCandidate(renderer, targetAvatar);
            return candidate != null && candidate.isCandidate;
        }

        /// <summary>The renderer, its object or its mesh is named like underwear, swimwear or a form-fitting one-piece.</summary>
        public static bool NamedClothing(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) return false;
            string text = renderer.name + " " + renderer.gameObject.name + " " + (renderer.sharedMesh != null ? renderer.sharedMesh.name : "");
            string normalized = ReFitUtility.NormalizeName(text);
            foreach (string token in Tokens)
                if (normalized.Contains(token)) return true;
            // Word boundaries for the short ones, camel case split first ("SportBra" -> "sport bra").
            string spaced = Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            return Words.IsMatch(spaced);
        }
    }
}
