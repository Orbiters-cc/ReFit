using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Dense body support for low resolution lower-body cloth, including triangle interiors.</summary>
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
            BodyRegion[] regions, bool[] tubes, ReFitSettings settings)
        {
            if (!settings.enableClearanceCorrection || !settings.preserveLowerBodyCoverage || !IsLowerBodyCloth(asset, regions, tubes)) return null;
            var result = new ReFitSurfaceCoverage { basis = new Vector3[asset.GroupCount], adjacency = asset.groupAdjacency };
            var vertices = new Vector3[asset.worldVertices.Length];
            for (int g = 0; g < result.basis.Length; g++)
                result.basis[g] = asset.worldVertices[asset.groupRep[g]] + (primary != null ? primary[g] : Vector3.zero);
            for (int i = 0; i < vertices.Length; i++) vertices[i] = result.basis[asset.groupOfVertex[i]];
            var cloth = new MeshSnapshot { worldVertices = vertices, triangles = asset.triangles };
            var index = SurfaceBvh.Build(cloth);
            float range = Mathf.Min(settings.falloffStartDistance, .06f);
            for (int i = 0; i < body.worldVertices.Length; i++)
            {
                var bodyNormal = body.worldNormals[i];
                var hit = index.ClosestPoint(body.worldVertices[i], range, t => Vector3.Dot(cloth.FaceNormal(t), bodyNormal) >= .35f);
                if (!hit.found) continue;
                int t = hit.triangle * 3;
                int a = asset.groupOfVertex[asset.triangles[t]], b = asset.groupOfVertex[asset.triangles[t + 1]], c = asset.groupOfVertex[asset.triangles[t + 2]];
                if (tubes != null && (tubes[a] || tubes[b] || tubes[c])) continue;
                var normal = cloth.FaceNormal(hit.triangle);
                float gap = Vector3.Dot(hit.position - body.worldVertices[i], normal);
                // Reject skin beyond an opening instead of extending the hem to cover it.
                var tangent = hit.position - body.worldVertices[i] - normal * gap;
                if (tangent.sqrMagnitude > .001f * .001f || gap < -.001f || gap > range) continue;
                result.supports.Add(new Support { vertex = i, a = a, b = b, c = c, bary = hit.bary, gap = Mathf.Max(0, gap) });
            }
            return result.supports.Count == 0 ? null : result;
        }

        public void Apply(MeshSnapshot body, Vector3[] bodyDelta, Vector3[] deltas, Vector3[] rawDeltas, ReFitSettings settings, ReFitReport report, string label)
        {
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
                    if (bodyDelta[s.vertex].sqrMagnitude < 1e-10f) continue;
                    var skin = body.worldVertices[s.vertex] + bodyDelta[s.vertex];
                    var reference = body.worldNormals[s.vertex];
                    // Retain the original material correspondence. Rebinding to the nearest moving
                    // face can jump to the opposite leg or lining and progressively fold the seam.
                    int ga = s.a, gb = s.b, gc = s.c;
                    var n = Vector3.Cross(positions[gb] - positions[ga], positions[gc] - positions[ga]).normalized;
                    if (Vector3.Dot(n, reference) < .35f) continue;
                    var p = positions[ga] * s.bary.x + positions[gb] * s.bary.y + positions[gc] * s.bary.z;
                    // Do not spend the original gap independently in every additive shape.
                    float safety = Mathf.Max(settings.clearanceMinimumSafetyDistance, s.gap);
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
                        deltas[g] = settings.clearanceMaxTransferredTotalCorrection > 0
                            ? rawDeltas[g] + Vector3.ClampMagnitude(value - rawDeltas[g], settings.clearanceMaxTransferredTotalCorrection)
                            : value;
                    }
                // Spread support over the local fabric rather than stretching a short seam edge.
                // Leave the final passes unsmoothed so diffusion cannot be the last collision operation.
                if (iteration < 40 && iteration % 4 == 3)
                {
                    var field = new Vector3[deltas.Length];
                    for (int g = 0; g < field.Length; g++) field[g] = deltas[g] - original[g];
                    DeltaField.Smooth(field, adjacency, 1, .5f);
                    for (int g = 0; g < field.Length; g++)
                    {
                        var value = original[g] + field[g];
                        deltas[g] = settings.clearanceMaxTransferredTotalCorrection > 0
                            ? rawDeltas[g] + Vector3.ClampMagnitude(value - rawDeltas[g], settings.clearanceMaxTransferredTotalCorrection)
                            : value;
                    }
                }
                residual = worst;
                if (worst <= .0001f && iteration >= 40) break;
            }
            float max = 0;
            for (int g = 0; g < deltas.Length; g++) max = Mathf.Max(max, (deltas[g] - original[g]).magnitude);
            report?.Info("surface-coverage", $"{label}: {supports.Count} body supports, {corrected} constraint corrections, maximum coverage correction {max * 1000:F3}mm, last support residual {residual * 1000:F3}mm. Support residual is not a full mesh-intersection test.");
            if (residual > .0005f)
                report?.Warn("surface-coverage-limited", $"{label}: lower-body surface support remains {residual * 1000:F3}mm short after the bounded correction. Inspect the garment at individual and combined shape weights.");
        }
    }
}
