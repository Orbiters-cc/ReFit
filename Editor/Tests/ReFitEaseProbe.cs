using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// How much room clothing leaves over the skin, by body region (what "bulky" means): for each clothing vertex with skin
    /// under it within <see cref="Reach"/>, the gap along the skin's normal over the target as fitted, and for the same vertex
    /// the gap it had as authored over the source. The region is the skin's main bone (neck, chest, torso, shoulder, upper
    /// arm, forearm, hips, thigh, shin, foot); "armpit" is the skin below and within 8 cm of either upper arm's joint.
    /// </summary>
    internal static class ReFitEaseProbe
    {
        private const float Reach = .06f;

        internal static string Measure(Mesh sourceBody, Mesh original, Mesh body, Mesh fitted, SkinnedMeshRenderer target)
        {
            var a = new MeshSnapshot { worldVertices = sourceBody.vertices, triangles = sourceBody.triangles };
            var b = new MeshSnapshot { worldVertices = body.vertices, triangles = body.triangles };
            var ia = SurfaceBvh.Build(a); var ib = SurfaceBvh.Build(b);
            var regions = Regions(target, out var arms);
            var p = original.vertices; var q = fitted.vertices; var bodyTriangles = body.triangles;
            var gaps = new Dictionary<string, (List<float> now, List<float> authored, List<float> added)>();
            for (int v = 0; v < q.Length; v++)
            {
                var hb = ib.ClosestPoint(q[v], Reach, null);
                if (!hb.found) continue;
                float now = Vector3.Dot(q[v] - hb.position, b.FaceNormal(hb.triangle));
                if (now < 0) continue;
                var ha = ia.ClosestPoint(p[v], Reach, null);
                float authored = ha.found ? Vector3.Dot(p[v] - ha.position, a.FaceNormal(ha.triangle)) : float.NaN;
                string region = regions[bodyTriangles[hb.triangle * 3]];
                foreach (var arm in arms)
                    if (hb.position.y < arm.y + .01f && (hb.position - arm).magnitude < .08f) region = "armpit";
                if (!gaps.TryGetValue(region, out var lists)) gaps[region] = lists = (new List<float>(), new List<float>(), new List<float>());
                lists.now.Add(now);
                if (!float.IsNaN(authored) && authored >= 0) { lists.authored.Add(authored); lists.added.Add(now - authored); }
            }
            string P(List<float> values, float at) => values.Count == 0 ? "-" : (values.OrderBy(x => x).ElementAt((int)((values.Count - 1) * at)) * 1000).ToString("F1");
            var order = new[] { "neck", "chest", "armpit", "shoulder", "upper arm", "forearm", "hand", "torso", "hips", "thigh", "shin", "foot", "other" };
            return "ease mm (gap over the skin now p50/p90 | authored p50/p90 | added p50/p90, n): " + string.Join("; ", order.Where(gaps.ContainsKey)
                .Select(r => $"{r} {P(gaps[r].now, .5f)}/{P(gaps[r].now, .9f)} | {P(gaps[r].authored, .5f)}/{P(gaps[r].authored, .9f)} | " +
                             $"{P(gaps[r].added, .5f)}/{P(gaps[r].added, .9f)} n={gaps[r].now.Count}"));
        }

        /// <summary>
        /// The gap over the skin by region (p50/p90 mm) of clothing at its group deltas on the body it is fitted onto, as the
        /// engine sees them between its steps (<see cref="ReFitEngine.CoverStepCaptured"/>), with how much of it is under the skin.
        /// </summary>
        internal static string MeasureStep(MeshSnapshot asset, MeshSnapshot body, Vector3[] groupDeltas, string[] regions, List<Vector3> arms)
        {
            if (regions == null || regions.Length != body.worldVertices.Length) return "regions do not match the body";
            var index = SurfaceBvh.Build(body);
            var gaps = new Dictionary<string, (List<float> over, int under)>();
            for (int v = 0; v < asset.worldVertices.Length; v++)
            {
                var q = asset.worldVertices[v] + groupDeltas[asset.groupOfVertex[v]];
                var hit = index.ClosestPoint(q, Reach, null);
                if (!hit.found) continue;
                float now = Vector3.Dot(q - hit.position, body.FaceNormal(hit.triangle));
                string region = regions[body.triangles[hit.triangle * 3]];
                foreach (var arm in arms)
                    if (hit.position.y < arm.y + .01f && (hit.position - arm).magnitude < .08f) region = "armpit";
                if (!gaps.TryGetValue(region, out var entry)) entry = (new List<float>(), 0);
                if (now >= 0) entry.over.Add(now); else if (now < -.001f) entry.under++;
                gaps[region] = entry;
            }
            string P(List<float> values, float at) => values.Count == 0 ? "-" : (values.OrderBy(x => x).ElementAt((int)((values.Count - 1) * at)) * 1000).ToString("F1");
            var order = new[] { "neck", "chest", "armpit", "shoulder", "upper arm", "forearm", "hand", "torso", "hips", "thigh", "shin", "foot", "other" };
            return string.Join("; ", order.Where(gaps.ContainsKey).Select(r => $"{r} {P(gaps[r].over, .5f)}/{P(gaps[r].over, .9f)} under {gaps[r].under}"));
        }

        // Each body vertex's region by its main bone's name; the upper arms' joints (world, as baked).
        internal static string[] Regions(SkinnedMeshRenderer target, out List<Vector3> arms)
        {
            var mesh = target.sharedMesh; var bones = target.bones; var weights = mesh.boneWeights;
            var names = bones.Select(bone => Region(bone != null ? bone.name : "")).ToArray();
            arms = bones.Where(bone => bone != null && Region(bone.name) == "upper arm" && !bones.Any(o => o != null && o != bone && bone.IsChildOf(o) && Region(o.name) == "upper arm"))
                .Select(bone => bone.position).ToList();
            var result = new string[mesh.vertexCount];
            for (int v = 0; v < result.Length; v++)
                result[v] = v < weights.Length && weights[v].boneIndex0 < names.Length ? names[weights[v].boneIndex0] : "other";
            return result;
        }

        private static string Region(string boneName)
        {
            string n = new string(boneName.ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (n.Contains("neck") || n.Contains("head")) return "neck";
            if (n.Contains("hand") || n.Contains("finger") || n.Contains("thumb") || n.Contains("index") || n.Contains("pinky")) return "hand";
            if (n.Contains("forearm") || n.Contains("lowerarm") || n.Contains("elbow")) return "forearm";
            if (n.Contains("upperarm") || n.Contains("arm")) return "upper arm";
            if (n.Contains("shoulder") || n.Contains("clavicle")) return "shoulder";
            if (n.Contains("chest") || n.Contains("pec") || n.Contains("breast")) return "chest";
            if (n.Contains("spine") || n.Contains("belly") || n.Contains("stomach") || n.Contains("abdomen")) return "torso";
            if (n.Contains("hip") || n.Contains("pelvis") || n.Contains("butt")) return "hips";
            // The feet apart from the shins: shoes and socks keep no leg room over them.
            if (n.Contains("foot") || n.Contains("toe") || n.Contains("ankle")) return "foot";
            if (n.Contains("shin") || n.Contains("calf") || n.Contains("knee") || n.Contains("lowerleg")) return "shin";
            // The Rexouium's and the Ultipaw's thighs are "Left leg" and "Right leg".
            if (n.Contains("thigh") || n.Contains("upperleg") || n.EndsWith("leg")) return "thigh";
            return "other";
        }
    }
}
