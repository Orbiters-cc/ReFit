using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// Garments worn together, innermost first. A part is inside another where it is closer to the body over the body area
    /// both cover, whatever their names (a vest mesh holding the shirt, "FashionSuitJacket"...).
    /// </summary>
    internal static class ReFitOutfit
    {
        // Body area grid and the gap that decides which part is outside where both cover it.
        private const float Cell = .02f, Margin = .001f, Reach = .15f;

        public static List<SkinnedMeshRenderer> InnerFirst(SkinnedMeshRenderer body, IEnumerable<SkinnedMeshRenderer> parts)
        {
            var list = parts.Where(p => p != null && p.sharedMesh != null && p != body).Distinct().ToList();
            if (list.Count < 2 || body == null || body.sharedMesh == null) return list;
            var surface = MeshSnapshot.Capture(body, false, null, null);
            var index = SurfaceBvh.Build(surface);
            var gaps = list.Select(p => Gaps(p, surface, index)).ToList();
            // How many other parts each one covers: innermost covers none.
            var outside = new int[list.Count];
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    int aOut = 0, bOut = 0;
                    foreach (var pair in gaps[a])
                    {
                        if (!gaps[b].TryGetValue(pair.Key, out float other)) continue;
                        if (pair.Value > other + Margin) aOut++;
                        else if (other > pair.Value + Margin) bOut++;
                    }
                    if (aOut > bOut) outside[a]++;
                    else if (bOut > aOut) outside[b]++;
                }
            return list.Select((part, i) => (part, i)).OrderBy(x => outside[x.i]).ThenBy(x => x.i).Select(x => x.part).ToList();
        }

        // Median signed distance to the body of the part's vertices, per body area cell they are closest to.
        private static Dictionary<Vector3Int, float> Gaps(SkinnedMeshRenderer part, MeshSnapshot body, SurfaceBvh index)
        {
            var snapshot = MeshSnapshot.Capture(part, false, null, null);
            var cells = new Dictionary<Vector3Int, List<float>>();
            foreach (var point in snapshot.worldVertices)
            {
                var hit = index.ClosestPoint(point, Reach);
                if (!hit.found) continue;
                var cell = Vector3Int.FloorToInt(hit.position / Cell);
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<float>();
                list.Add(Vector3.Dot(point - hit.position, body.BaryNormal(hit.triangle, hit.bary)));
            }
            return cells.ToDictionary(pair => pair.Key, pair =>
            {
                pair.Value.Sort();
                return pair.Value[pair.Value.Count / 2];
            });
        }
    }
}
