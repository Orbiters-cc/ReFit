using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Dense body support for cloth triangle interiors, with opt-in repair after fitting across bases.</summary>
    internal sealed class ReFitSurfaceCoverage
    {
        private struct Support
        {
            public int vertex, a, b, c;
            public Vector3 bary;
            public float gap;
        }
        private readonly List<Support> supports = new List<Support>();
        private Vector3[] basis;
        private List<int>[] adjacency;
        private bool repair;
        private bool[] protectedGroups;
        private int[] triangles;
        private Vector3[] outward;
        private bool[] tubes;

        public static bool IsLowerBodyCloth(MeshSnapshot asset, BodyRegion[] regions, bool[] tubes)
        {
            if (asset == null || regions == null || regions.Length != asset.GroupCount) return false;
            int legs = 0, other = 0, count = 0;
            for (int g = 0; g < asset.GroupCount; g++)
            {
                if (tubes != null && tubes[g]) continue;
                count++;
                if (regions[g] == BodyRegion.LeftLeg || regions[g] == BodyRegion.RightLeg) legs++;
                if (regions[g] == BodyRegion.LeftArm || regions[g] == BodyRegion.RightArm || regions[g] == BodyRegion.Head) other++;
            }
            return count >= 32 && legs >= count / 4 && other == 0;
        }

        public static ReFitSurfaceCoverage Build(MeshSnapshot asset, MeshSnapshot body, Vector3[] primary,
            BodyRegion[] regions, bool[] tubes, ReFitSettings settings, BodyRegion[] bodyRegions = null)
        {
            if (!settings.enableClearanceCorrection || (!settings.coverDifferentBaseBody &&
                (!settings.preserveLowerBodyCoverage || !IsLowerBodyCloth(asset, regions, tubes)))) return null;
            var result = new ReFitSurfaceCoverage { basis = new Vector3[asset.GroupCount], adjacency = asset.groupAdjacency, repair = settings.coverDifferentBaseBody };
            result.tubes = result.repair ? DetailGroups(asset, tubes) : tubes;
            result.protectedGroups = result.repair ? ProtectOpenings(asset, regions, result.tubes) : tubes;
            if (result.repair)
            {
                result.triangles = new int[asset.triangles.Length];
                for (int i = 0; i < result.triangles.Length; i++) result.triangles[i] = asset.groupOfVertex[asset.triangles[i]];
            }
            var vertices = new Vector3[asset.worldVertices.Length];
            for (int g = 0; g < result.basis.Length; g++)
                result.basis[g] = asset.worldVertices[asset.groupRep[g]] + (primary != null ? primary[g] : Vector3.zero);
            for (int i = 0; i < vertices.Length; i++) vertices[i] = result.basis[asset.groupOfVertex[i]];
            if (result.repair)
            {
                var bodyIndex = SurfaceBvh.Build(body);
                result.outward = new Vector3[result.basis.Length];
                for (int g = 0; g < result.basis.Length; g++)
                {
                    var nearest = bodyIndex.ClosestPoint(result.basis[g], .2f);
                    if (!nearest.found) continue;
                    result.outward[g] = body.FaceNormal(nearest.triangle);
                    if (bodyRegions != null && bodyRegions[nearest.triangle] == BodyRegion.Head) result.protectedGroups[g] = true;
                }
            }
            var cloth = new MeshSnapshot { worldVertices = vertices, triangles = asset.triangles };
            var index = SurfaceBvh.Build(cloth);
            float range = result.repair ? .04f : Mathf.Min(settings.falloffStartDistance, .06f);
            for (int i = 0; i < body.worldVertices.Length; i++)
            {
                var bodyNormal = body.worldNormals[i];
                // Never search past a nearby opening/lining for a distant outward-facing triangle.
                var hit = index.ClosestPoint(body.worldVertices[i], range, result.repair ? (Func<int, bool>)null : t => Vector3.Dot(cloth.FaceNormal(t), bodyNormal) >= .35f);
                if (!hit.found) continue;
                if (result.repair && Vector3.Dot(cloth.FaceNormal(hit.triangle), bodyNormal) < .35f) continue;
                int t = hit.triangle * 3;
                int a = asset.groupOfVertex[asset.triangles[t]], b = asset.groupOfVertex[asset.triangles[t + 1]], c = asset.groupOfVertex[asset.triangles[t + 2]];
                if (result.protectedGroups != null && (result.protectedGroups[a] || result.protectedGroups[b] || result.protectedGroups[c])) continue;
                var normal = cloth.FaceNormal(hit.triangle);
                float gap = Vector3.Dot(hit.position - body.worldVertices[i], normal);
                // Reject skin beyond an opening instead of extending the hem to cover it.
                var tangent = hit.position - body.worldVertices[i] - normal * gap;
                if (tangent.sqrMagnitude > .001f * .001f || (!result.repair && gap < -.001f) || gap > range) continue;
                result.supports.Add(new Support { vertex = i, a = a, b = b, c = c, bary = hit.bary, gap = Mathf.Max(0, gap) });
            }
            return result.supports.Count == 0 ? null : result;
        }

        private static bool[] ProtectOpenings(MeshSnapshot asset, BodyRegion[] regions, bool[] tubes)
        {
            var edges = new Dictionary<ulong, int>();
            for (int t = 0; t < asset.triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = asset.groupOfVertex[asset.triangles[t + e]], b = asset.groupOfVertex[asset.triangles[t + (e + 1) % 3]];
                    if (a == b) continue;
                    ulong key = ((ulong)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    edges.TryGetValue(key, out int count); edges[key] = count + 1;
                }
            var result = new bool[asset.GroupCount];
            foreach (var edge in edges) if (edge.Value == 1) { result[(int)(edge.Key >> 32)] = true; result[(int)(edge.Key & uint.MaxValue)] = true; }
            var boundary = (bool[])result.Clone();
            for (int g = 0; g < result.Length; g++)
            {
                if (boundary[g]) foreach (int n in asset.groupAdjacency[g]) result[n] = true;
                // A hoodie opening is not an invitation to cover the face or ears.
                if ((regions != null && regions[g] == BodyRegion.Head) || (tubes != null && tubes[g])) result[g] = true;
            }
            return result;
        }

        private static bool[] DetailGroups(MeshSnapshot asset, bool[] tubes)
        {
            var result = tubes != null ? (bool[])tubes.Clone() : new bool[asset.GroupCount];
            var visited = new bool[asset.GroupCount]; var components = new List<List<int>>(); int largest = 0;
            for (int start = 0; start < visited.Length; start++)
            {
                if (visited[start]) continue;
                var component = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true;
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue(); component.Add(g);
                    foreach (int n in asset.groupAdjacency[g]) if (!visited[n]) { visited[n] = true; queue.Enqueue(n); }
                }
                components.Add(component); largest = Math.Max(largest, component.Count);
            }
            foreach (var component in components)
                if (component.Count <= 64 && component.Count < largest / 8)
                    foreach (int g in component) result[g] = true;
            return result;
        }

        public static void RepairPrimary(MeshSnapshot asset, MeshSnapshot body, Vector3[] primary,
            BodyRegion[] regions, bool[] tubes, ReFitSettings settings, ReFitReport report, BodyRegion[] bodyRegions)
        {
            if (!settings.coverDifferentBaseBody) return;
            var coverage = Build(asset, body, primary, regions, tubes, settings, bodyRegions);
            if (coverage == null) return;
            var correction = new Vector3[primary.Length];
            coverage.Apply(body, null, correction, new Vector3[primary.Length], settings, report, "different-base coverage");
            for (int g = 0; g < primary.Length; g++) primary[g] += correction[g];
        }

        public void Apply(MeshSnapshot body, Vector3[] bodyDelta, Vector3[] deltas, Vector3[] rawDeltas, ReFitSettings settings, ReFitReport report, string label)
        {
            float limit = repair && bodyDelta == null ? .08f : settings.clearanceMaxTransferredTotalCorrection;
            var original = (Vector3[])deltas.Clone();
            int corrected = 0;
            var corrections = new Vector3[deltas.Length];
            var weights = new float[deltas.Length];
            float residual = 0;
            for (int iteration = 0; iteration < 48; iteration++)
            {
                Array.Clear(corrections, 0, corrections.Length);
                Array.Clear(weights, 0, weights.Length);
                var positions = new Vector3[deltas.Length];
                for (int g = 0; g < positions.Length; g++) positions[g] = basis[g] + deltas[g];
                float worst = 0;
                foreach (var s in supports)
                {
                    if (bodyDelta != null && bodyDelta[s.vertex].sqrMagnitude < 1e-10f) continue;
                    var skin = body.worldVertices[s.vertex] + (bodyDelta != null ? bodyDelta[s.vertex] : Vector3.zero);
                    var reference = body.worldNormals[s.vertex];
                    // Retain the original material correspondence. Rebinding to the nearest moving
                    // face can jump to the opposite leg or lining and progressively fold the seam.
                    int ga = s.a, gb = s.b, gc = s.c;
                    var n = Vector3.Cross(positions[gb] - positions[ga], positions[gc] - positions[ga]).normalized;
                    if (Vector3.Dot(n, reference) < .35f) continue;
                    var p = positions[ga] * s.bary.x + positions[gb] * s.bary.y + positions[gc] * s.bary.z;
                    // Do not spend the original gap independently in every additive shape.
                    float safety = Mathf.Max(repair ? Mathf.Max(.005f, settings.clearanceMinimumSafetyDistance) : settings.clearanceMinimumSafetyDistance, s.gap);
                    float missing = safety - Vector3.Dot(p - skin, n);
                    if (missing <= .00005f) continue;
                    worst = Mathf.Max(worst, missing); corrected++;
                    // Follow the body's outward direction. Pushing along a newly tilted fabric face
                    // introduces tangential drift that can reopen intersections at intermediate weights.
                    var push = reference * (missing / (Mathf.Max(.35f, Vector3.Dot(n, reference)) * s.bary.sqrMagnitude));
                    corrections[ga] += push * s.bary.x; weights[ga]++;
                    corrections[gb] += push * s.bary.y; weights[gb]++;
                    corrections[gc] += push * s.bary.z; weights[gc]++;
                }
                for (int g = 0; g < deltas.Length; g++)
                    if (weights[g] > 0)
                    {
                        var value = deltas[g] + corrections[g] / weights[g];
                        deltas[g] = limit > 0
                            ? rawDeltas[g] + Vector3.ClampMagnitude(value - rawDeltas[g], limit)
                            : value;
                    }
                // Spread support over the local fabric rather than stretching a short seam edge.
                // Leave the final passes unsmoothed so diffusion cannot be the last collision operation.
                if (iteration < 40 && iteration % 4 == 3)
                {
                    var field = new Vector3[deltas.Length];
                    for (int g = 0; g < field.Length; g++) field[g] = deltas[g] - original[g];
                    DeltaField.Smooth(field, adjacency, repair ? 5 : 1, repair ? .8f : .5f);
                    for (int g = 0; g < field.Length; g++)
                    {
                        if (repair && protectedGroups != null && protectedGroups[g]) { deltas[g] = original[g]; continue; }
                        var value = original[g] + field[g];
                        deltas[g] = limit > 0
                            ? rawDeltas[g] + Vector3.ClampMagnitude(value - rawDeltas[g], limit)
                            : value;
                    }
                }
                residual = worst;
                if (repair) Regularize(deltas, original, bodyDelta != null);
                if (worst <= .0001f && iteration >= 40) break;
            }
            float max = 0;
            if (repair)
            {
                FollowDetails(deltas, original);
                residual = 0;
                foreach (var s in supports)
                {
                    if (bodyDelta != null && bodyDelta[s.vertex].sqrMagnitude < 1e-10f) continue;
                    var a = basis[s.a] + deltas[s.a]; var b = basis[s.b] + deltas[s.b]; var c = basis[s.c] + deltas[s.c];
                    var n = Vector3.Cross(b - a, c - a).normalized;
                    if (Vector3.Dot(n, body.worldNormals[s.vertex]) < .35f) continue;
                    var skin = body.worldVertices[s.vertex] + (bodyDelta != null ? bodyDelta[s.vertex] : Vector3.zero);
                    float safety = Mathf.Max(Mathf.Max(.005f, settings.clearanceMinimumSafetyDistance), s.gap);
                    residual = Mathf.Max(residual, safety - Vector3.Dot(a*s.bary.x + b*s.bary.y + c*s.bary.z - skin, n));
                }
            }
            for (int g = 0; g < deltas.Length; g++) max = Mathf.Max(max, (deltas[g] - original[g]).magnitude);
            report?.Info("surface-coverage", $"{label}: {supports.Count} body supports, {corrected} constraint corrections, maximum coverage correction {max * 1000:F3}mm, last support residual {residual * 1000:F3}mm. Support residual is not a full mesh-intersection test.");
            if (residual > .0005f)
                report?.Warn("surface-coverage-limited", $"{label}: clothing surface support remains {residual * 1000:F3}mm short after the bounded correction. Inspect the garment at individual and combined shape weights.");
        }

        private void FollowDetails(Vector3[] deltas, Vector3[] original)
        {
            if (tubes == null) return;
            // Carry a closed detail (e.g. a drawstring) with the nearby expanded fabric as one
            // rigid piece. Its cross-section and authored folds must not be inflated vertex by vertex.
            var cloth = new MeshSnapshot { worldVertices = basis, triangles = triangles };
            var index = SurfaceBvh.Build(cloth);
            var visited = new bool[basis.Length];
            for (int start = 0; start < tubes.Length; start++)
            {
                if (!tubes[start] || visited[start]) continue;
                var members = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true;
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue(); members.Add(g);
                    foreach (int n in adjacency[g]) if (tubes[n] && !visited[n]) { visited[n] = true; queue.Enqueue(n); }
                }
                Vector3 carry = Vector3.zero;
                foreach (int g in members)
                {
                    var hit = index.ClosestPoint(basis[g], .04f, t => !protectedGroups[triangles[t*3]] && !protectedGroups[triangles[t*3+1]] && !protectedGroups[triangles[t*3+2]]);
                    if (!hit.found) continue;
                    int a = triangles[hit.triangle*3], b = triangles[hit.triangle*3+1], c = triangles[hit.triangle*3+2];
                    var d = (deltas[a]-original[a])*hit.bary.x + (deltas[b]-original[b])*hit.bary.y + (deltas[c]-original[c])*hit.bary.z;
                    if (d.sqrMagnitude > carry.sqrMagnitude) carry = d;
                }
                foreach (int g in members) deltas[g] = original[g] + carry;
            }
        }

        private void Regularize(Vector3[] deltas, Vector3[] original, bool transferred)
        {
            // Bound the spatial gradient of the extra displacement. Short cuff/lining edges must
            // share the motion of their neighbours instead of being stretched into sharp spikes.
            for (int pass = 0; pass < 12; pass++)
            {
                for (int a = 0; a < adjacency.Length; a++)
                    foreach (int b in adjacency[a])
                    {
                        if (b <= a) continue;
                        var da = deltas[a] - original[a]; var db = deltas[b] - original[b];
                        var difference = da - db;
                        float max = (basis[a] + original[a] - basis[b] - original[b]).magnitude * .35f;
                        if (difference.magnitude <= max) continue;
                        var excess = difference - Vector3.ClampMagnitude(difference, max);
                        bool fixedA = protectedGroups[a], fixedB = protectedGroups[b];
                        if (fixedA && fixedB) continue;
                        if (!fixedA) deltas[a] -= excess * (fixedB ? 1 : .5f);
                        if (!fixedB) deltas[b] += excess * (fixedA ? 1 : .5f);
                    }
                if (!transferred)
                    for (int g = 0; g < deltas.Length; g++)
                    {
                        var d = deltas[g] - original[g];
                        float inward = Vector3.Dot(d, outward[g]);
                        if (inward < 0) deltas[g] -= outward[g] * inward;
                    }
            }
            // Keep the orientation of even very narrow folded hems. A bounded edge gradient alone
            // is insufficient for a nearly collinear triangle.
            for (int pass = 0; pass < 24; pass++)
            {
                bool changed = false;
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (protectedGroups[a] || protectedGroups[b] || protectedGroups[c]) continue;
                    var da = transferred ? Vector3.zero : original[a];
                    var db = transferred ? Vector3.zero : original[b];
                    var dc = transferred ? Vector3.zero : original[c];
                    var pa = basis[a] + da; var pb = basis[b] + db; var pc = basis[c] + dc;
                    var n = Vector3.Cross(pb - pa, pc - pa);
                    if (n.sqrMagnitude < 1e-16f) continue;
                    var candidate = Vector3.Cross(basis[b] + deltas[b] - basis[a] - deltas[a], basis[c] + deltas[c] - basis[a] - deltas[a]);
                    if (Vector3.Dot(n, candidate) >= n.sqrMagnitude * .1f) continue;
                    deltas[a] = Vector3.Lerp(da, deltas[a], .5f);
                    deltas[b] = Vector3.Lerp(db, deltas[b], .5f);
                    deltas[c] = Vector3.Lerp(dc, deltas[c], .5f);
                    changed = true;
                }
                if (!changed) break;
            }
        }
    }
}
