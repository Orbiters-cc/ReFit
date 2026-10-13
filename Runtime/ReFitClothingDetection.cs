using System.Collections.Generic;
using System.Text;
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
        // Distinctive enough to be found inside longer names ("classicJockstrap", "SportBodysuit", "Swim_Suit"), as long as the
        // name's word ends with them, with a plural or a garment word ("bikinitop", "Boxer Shorts"): "Speedometer" is no speedo.
        private static readonly string[] Tokens =
        {
            "jockstrap", "underwear", "undies", "lingerie", "bikini", "swimsuit", "swimwear", "leotard", "bodysuit",
            "panties", "boxers", "boxershort", "boxerbrief", "briefs", "thong", "speedo"
        };

        // What may follow a token within a word: a plural, or a garment word of a compound name.
        private static readonly string[] Endings =
        {
            "s", "es", "top", "tops", "set", "sets", "short", "shorts", "bottom", "bottoms", "brief", "briefs", "piece", "pieces"
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
            // Word boundaries, camel case split first ("SportBra" -> "sport bra").
            string spaced = Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            if (Words.IsMatch(spaced)) return true;
            // The words joined ("boxer_shorts" -> "boxershorts"), remembering where each ends.
            var joined = new StringBuilder();
            var ends = new HashSet<int>();
            foreach (Match word in Regex.Matches(spaced, "[a-z]+"))
            {
                joined.Append(word.Value);
                ends.Add(joined.Length);
            }
            string letters = joined.ToString();
            foreach (string token in Tokens)
                for (int at = letters.IndexOf(token, System.StringComparison.Ordinal); at >= 0;
                     at = letters.IndexOf(token, at + 1, System.StringComparison.Ordinal))
                    if (EndsWord(letters, at + token.Length, ends)) return true;
            return false;
        }

        // A word ends there, or after a plural or a garment word that ends one.
        private static bool EndsWord(string letters, int end, HashSet<int> ends)
        {
            if (ends.Contains(end)) return true;
            foreach (string ending in Endings)
                if (end + ending.Length <= letters.Length && string.CompareOrdinal(letters, end, ending, 0, ending.Length) == 0 &&
                    ends.Contains(end + ending.Length)) return true;
            return false;
        }
    }
}
