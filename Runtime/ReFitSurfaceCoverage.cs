using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Dense body support for cloth triangle interiors, with opt-in repair after fitting across bases.</summary>
    internal sealed class ReFitSurfaceCoverage
    {
        // A visible allowance for coarse triangles over a curved body, including in-between shape weights.
        private const float RepairSafety = .01f;
        // Clothing left short of its targeted clearance clips only inside the surface it covers or this close to it.
        // Farther out it only has less room than the correction aims for (RepairSafety): not worth a warning.
        internal const float ClipTolerance = .001f;
        // A remaining shortfall below this is solver noise.
        private const float ShortTolerance = .0005f;
        private struct Support
        {
            public int vertex, a, b, c;
            public Vector3 bary;
            public float gap, signedGap;
        }
        private struct VertexSupport
        {
            public int group, triangle;
            public Vector3 bary, normal;
            public float gap, signedGap;
        }
        private readonly List<VertexSupport> vertexSupports = new List<VertexSupport>();
        private readonly List<Support> supports = new List<Support>();
        private Vector3[] basis;
        private List<int>[] adjacency;
        private bool repair;
        // A clothing layer kept in its authored order: supports keep the authored gap (up to LayerClearance), not RepairSafety.
        private bool layer;
        // Small separate pieces (a pocket square, pins, buttons) carried with the fabric around them, when the order is kept.
        private List<List<int>> pieces;
        private bool[] protectedGroups;
        private int[] triangles;
        private Vector3[] outward;
        private bool[] tubes;
        // What the supports keep the clothing outside of, for diagnostics: the body or an inner garment.
        private string surfaceName;

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

        /// <param name="authored">A clothing layer's vertices as authored: support only where the asset was outside it.</param>
        /// <param name="innerGarment">The garment's name when <paramref name="body"/> is an inner garment, not the body.</param>
        public static ReFitSurfaceCoverage Build(MeshSnapshot asset, MeshSnapshot body, Vector3[] primary,
            BodyRegion[] regions, bool[] tubes, ReFitSettings settings, BodyRegion[] bodyRegions = null, Vector3[] authored = null,
            bool[] hidden = null, string innerGarment = null)
        {
            if (!settings.enableClearanceCorrection || (!settings.coverDifferentBaseBody &&
                (!settings.preserveLowerBodyCoverage || !IsLowerBodyCloth(asset, regions, tubes)))) return null;
            var result = new ReFitSurfaceCoverage { basis = new Vector3[asset.GroupCount], adjacency = asset.groupAdjacency, repair = settings.coverDifferentBaseBody,
                layer = authored != null, surfaceName = innerGarment != null ? $"inner garment '{innerGarment}'" : "the body surface" };
            result.tubes = result.repair ? DetailGroups(asset, tubes) : tubes;
            if (result.repair && settings.coverageKeepsLayerOrder) result.pieces = Pieces(asset, result.tubes);
            result.protectedGroups = result.repair ? ProtectOpenings(asset, regions, result.tubes) : tubes;
            if (result.repair && hidden != null)
                for (int g = 0; g < hidden.Length; g++) result.protectedGroups[g] |= hidden[g];
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
                var contacts = new VertexSupport?[result.basis.Length];
                Parallel.For(0, result.basis.Length, g =>
                {
                    var nearest = bodyIndex.ClosestPoint(result.basis[g], .2f);
                    if (!nearest.found) return;
                    result.outward[g] = body.BaryNormal(nearest.triangle, nearest.bary);
                    if (bodyRegions != null && bodyRegions[nearest.triangle] == BodyRegion.Head) result.protectedGroups[g] = true;
                    var offset = result.basis[g] - nearest.position;
                    var faceNormal = body.FaceNormal(nearest.triangle);
                    var tangent = offset - faceNormal * Vector3.Dot(offset, faceNormal);
                    float authoredGap = authored != null ? AuthoredGap(asset.worldVertices[asset.groupRep[g]], authored, body.triangles, nearest.triangle, nearest.bary) : 0;
                    if (authored != null && authoredGap < -AuthoredTolerance) return;
                    if (!result.protectedGroups[g] && nearest.distance <= .1f && tangent.sqrMagnitude <= .005f * .005f)
                        contacts[g] = new VertexSupport { group = g, triangle = nearest.triangle, bary = nearest.bary,
                            normal = result.outward[g], gap = authored != null ? Mathf.Clamp(authoredGap, 0, LayerClearance) : Mathf.Max(0, Vector3.Dot(result.basis[g] - nearest.position, result.outward[g])),
                            signedGap = Vector3.Dot(result.basis[g] - nearest.position, result.outward[g]) };
                });
                foreach (var contact in contacts) if (contact.HasValue) result.vertexSupports.Add(contact.Value);
            }
            var cloth = new MeshSnapshot { worldVertices = vertices, triangles = asset.triangles };
            var index = SurfaceBvh.Build(cloth);
            // Different bases can begin several centimetres inside the body (notably at the crotch).
            // Keep the nearest face and opening tests: a larger search must not bridge an open hem.
            float range = result.repair ? .08f : Mathf.Min(settings.falloffStartDistance, .06f);
            var samples = new Support?[body.worldVertices.Length];
            Parallel.For(0, body.worldVertices.Length, i =>
            {
                var bodyNormal = body.worldNormals[i];
                // Never search past a nearby opening/lining for a distant outward-facing triangle.
                var hit = index.ClosestPoint(body.worldVertices[i], range, result.repair ? (Func<int, bool>)null : t => Vector3.Dot(cloth.FaceNormal(t), bodyNormal) >= .35f);
                if (!hit.found) return;
                if (result.repair && Vector3.Dot(cloth.FaceNormal(hit.triangle), bodyNormal) < .35f)
                {
                    // Look past a thin inward lining only to its immediately adjacent outer fabric.
                    hit = index.ClosestPoint(body.worldVertices[i], Mathf.Min(range, hit.distance + .005f),
                        face => Vector3.Dot(cloth.FaceNormal(face), bodyNormal) >= .35f);
                    if (!hit.found) return;
                }
                int t = hit.triangle * 3;
                int a = asset.groupOfVertex[asset.triangles[t]], b = asset.groupOfVertex[asset.triangles[t + 1]], c = asset.groupOfVertex[asset.triangles[t + 2]];
                if (result.protectedGroups != null && (result.protectedGroups[a] || result.protectedGroups[b] || result.protectedGroups[c])) return;
                var normal = cloth.FaceNormal(hit.triangle);
                float gap = Vector3.Dot(hit.position - body.worldVertices[i], normal);
                // Reject skin beyond an opening instead of extending the hem to cover it.
                var tangent = hit.position - body.worldVertices[i] - normal * gap;
                if (tangent.sqrMagnitude > .001f * .001f || (!result.repair && gap < -.001f) || gap > range) return;
                if (authored != null)
                {
                    var p0 = asset.worldVertices[asset.triangles[t]]; var p1 = asset.worldVertices[asset.triangles[t + 1]]; var p2 = asset.worldVertices[asset.triangles[t + 2]];
                    var clothAuthored = p0 * hit.bary.x + p1 * hit.bary.y + p2 * hit.bary.z;
                    // The garment passed under this layer here as authored (a collar over a lapel): leave it there.
                    float authoredGap = Vector3.Dot(clothAuthored - authored[i], Vector3.Cross(p1 - p0, p2 - p0).normalized);
                    if (authoredGap < -AuthoredTolerance) return;
                    gap = Mathf.Min(gap, Mathf.Clamp(authoredGap, 0, LayerClearance));
                }
                samples[i] = new Support { vertex = i, a = a, b = b, c = c, bary = hit.bary, gap = Mathf.Max(0, gap), signedGap = gap };
            });
            // The solver accumulates constraints in vertex order, regardless of query completion order.
            foreach (var sample in samples) if (sample.HasValue) result.supports.Add(sample.Value);
            return result.supports.Count == 0 && result.vertexSupports.Count == 0 ? null : result;
        }

        // Hidden: a surface lies over the group as authored, between these heights above it and within this reach of the line
        // out of the body. Its own fabric counts once it is this many rings away (a fold, a lining, a vest over the shirt).
        private static readonly float[] CoverHeights = { .002f, .005f, .009f, .014f, .02f };
        private const float CoverReach = .003f, CoverFacing = .5f, CoverTilt = 30 * Mathf.Deg2Rad;
        private const int CoverRings = 4;

        /// <summary>
        /// Clipped groups another surface covers: the garment's own outer fabric (a collar
        /// fold, a vest over its shirt) or another part of the outfit (a bowtie band under the collar, a jacket collar under the
        /// shirt's). Their clipping was never visible; pushing them out of the body would bring them through what covers them.
        /// </summary>
        /// <param name="others">The outfit's other parts: refitted ones cover only from outside the body.</param>
        public static bool[] HiddenGroups(MeshSnapshot asset, MeshSnapshot body, SurfaceBvh bodyIndex, IList<(MeshSnapshot surface, bool refitted)> others) =>
            HiddenGroups(asset, body, bodyIndex, others, null, null);

        /// <summary>What lies over a group straight out of the body: a layer's triangle, or the asset's own (layer -1).</summary>
        public struct Cover
        {
            public int layer, triangle;
            public Vector3 bary, normal;
        }

        /// <param name="by">Diagnostics: -1 hidden by the asset's own fabric, otherwise the index of the covering garment.</param>
        internal static bool[] HiddenGroups(MeshSnapshot asset, MeshSnapshot body, SurfaceBvh bodyIndex, IList<(MeshSnapshot surface, bool refitted)> others, int[] byGroup,
            Cover?[] covers)
        {
            var hidden = new bool[asset.GroupCount];
            var self = SurfaceBvh.Build(asset);
            // A button or a pocket square does not hide the fabric it sits on.
            var details = DetailGroups(asset, null);
            var indices = others.Where(o => o.surface != null).Select(o => (snapshot: o.surface, index: SurfaceBvh.Build(o.surface), o.refitted)).ToList();
            Parallel.For(0, asset.GroupCount, g =>
            {
                var point = asset.worldVertices[asset.groupRep[g]];
                var nearest = bodyIndex.ClosestPoint(point, .1f);
                if (!nearest.found) return;
                var up = body.BaryNormal(nearest.triangle, nearest.bary);
                HashSet<int> near = null;
                // Whatever lies over it, clipped or not: in shapes it must not pass through that.
                if (covers != null) covers[g] = CoverOf(point, up);
                // Only clipping is left in place: a hidden group outside the body still moves with the fabric around it.
                if (Vector3.Dot(point - nearest.position, up) >= -AuthoredTolerance) return;
                // Covered from every side, not only straight out: a hem's edge near the line does not hide what is below it.
                var side = Vector3.Cross(up, Mathf.Abs(up.y) < .9f ? Vector3.up : Vector3.right).normalized;
                var other = Vector3.Cross(up, side);
                int cover = int.MinValue;
                foreach (var ray in new[] { up, Tilt(up, side), Tilt(up, -side), Tilt(up, other), Tilt(up, -other) })
                {
                    int by = Covered(point, ray, up);
                    if (by == int.MinValue) return;
                    cover = cover == int.MinValue || by >= 0 ? by : cover;
                }
                hidden[g] = true;
                if (byGroup != null) byGroup[g] = cover;

                Cover? CoverOf(Vector3 from, Vector3 outward)
                {
                    foreach (float height in CoverHeights)
                    {
                        var probe = from + outward * height;
                        for (int o = 0; o < indices.Count; o++)
                        {
                            if (!indices[o].refitted) continue;
                            var surface = indices[o].snapshot;
                            var hit = indices[o].index.ClosestPoint(probe, CoverReach, t => Vector3.Dot(surface.FaceNormal(t), outward) >= CoverFacing);
                            // A part not refitted yet does not move with the shapes: it will be refitted over this one later.
                            if (indices[o].refitted && hit.found && Outside(hit.position)) return new Cover { layer = o, triangle = hit.triangle, bary = hit.bary, normal = outward };
                        }
                        var own = self.ClosestPoint(probe, CoverReach, t => Vector3.Dot(asset.FaceNormal(t), outward) >= CoverFacing);
                        if (own.found && Far(own.triangle)) return new Cover { layer = -1, triangle = own.triangle, bary = own.bary, normal = outward };
                    }
                    return null;
                }

                bool Far(int triangle)
                {
                    near ??= Rings(asset, g, CoverRings);
                    int a = asset.groupOfVertex[asset.triangles[triangle * 3]], b = asset.groupOfVertex[asset.triangles[triangle * 3 + 1]],
                        c = asset.groupOfVertex[asset.triangles[triangle * 3 + 2]];
                    return !near.Contains(a) && !near.Contains(b) && !near.Contains(c) && !details[a] && !details[b] && !details[c];
                }

                // The covering garment's index, -1 for the asset's own outer fabric, int.MinValue when nothing covers.
                int Covered(Vector3 from, Vector3 ray, Vector3 outward)
                {
                    foreach (float height in CoverHeights)
                    {
                        var probe = from + ray * height;
                        for (int o = 0; o < indices.Count; o++)
                        {
                            // Past a lining or the inner side of a collar to the outer fabric facing out.
                            var surface = indices[o].snapshot;
                            var hit = indices[o].index.ClosestPoint(probe, CoverReach, t => Vector3.Dot(surface.FaceNormal(t), outward) >= CoverFacing);
                            if (hit.found && (!indices[o].refitted || Outside(hit.position))) return o;
                        }
                        var own = self.ClosestPoint(probe, CoverReach, t => Vector3.Dot(asset.FaceNormal(t), outward) >= CoverFacing);
                        if (own.found && Far(own.triangle)) return -1;
                    }
                    return int.MinValue;
                }

                // A refitted part still clipping into the body hides nothing (trousers left in the hips under the jacket's hem).
                bool Outside(Vector3 position)
                {
                    var skin = bodyIndex.ClosestPoint(position, .1f);
                    return !skin.found || Vector3.Dot(position - skin.position, body.BaryNormal(skin.triangle, skin.bary)) >= 0;
                }
            });
            return hidden;
        }

        /// <summary>
        /// In a shape, fabric stays beneath what lies over it (its own outer fabric or another part of the outfit): each surface
        /// follows the body on its own, and the one closer to the skin, which follows it more, would pass through the other
        /// (a jacket collar through the shirt's, a band through its fold). Only crossings are corrected.
        /// </summary>
        public static void KeepUnderCovers(MeshSnapshot asset, Vector3[] primary, Vector3[] deltas, Cover?[] covers,
            IList<MeshSnapshot> layers, IList<Vector3[]> layerDeltas)
        {
            if (covers == null) return;
            Vector3 Base(int vertex) => asset.worldVertices[vertex] + (primary != null ? primary[asset.groupOfVertex[vertex]] : Vector3.zero);
            // Covers can be covered too (a band under a fold under a lapel): settle chains over a few passes.
            for (int pass = 0; pass < 3; pass++)
                for (int g = 0; g < covers.Length; g++)
                {
                    if (!covers[g].HasValue) continue;
                    var c = covers[g].Value;
                    Vector3 coverBase, coverMotion;
                    if (c.layer >= 0)
                    {
                        var surface = layers[c.layer].worldVertices; var moved = layerDeltas[c.layer]; var t = layers[c.layer].triangles;
                        int a = t[c.triangle * 3], b = t[c.triangle * 3 + 1], d = t[c.triangle * 3 + 2];
                        coverBase = surface[a] * c.bary.x + surface[b] * c.bary.y + surface[d] * c.bary.z;
                        coverMotion = moved[a] * c.bary.x + moved[b] * c.bary.y + moved[d] * c.bary.z;
                    }
                    else
                    {
                        int a = asset.triangles[c.triangle * 3], b = asset.triangles[c.triangle * 3 + 1], d = asset.triangles[c.triangle * 3 + 2];
                        coverBase = Base(a) * c.bary.x + Base(b) * c.bary.y + Base(d) * c.bary.z;
                        coverMotion = deltas[asset.groupOfVertex[a]] * c.bary.x + deltas[asset.groupOfVertex[b]] * c.bary.y + deltas[asset.groupOfVertex[d]] * c.bary.z;
                    }
                    var groupBase = Base(asset.groupRep[g]);
                    float baseGap = Vector3.Dot(coverBase - groupBase, c.normal);
                    // Already through it at rest: nothing to keep.
                    if (baseGap < 0) continue;
                    float keep = Mathf.Min(baseGap, LayerClearance);
                    float gap = Vector3.Dot(coverBase + coverMotion - groupBase - deltas[g], c.normal);
                    if (gap < keep) deltas[g] -= c.normal * (keep - gap);
                }
        }

        private static Vector3 Tilt(Vector3 up, Vector3 toward) => (up * Mathf.Cos(CoverTilt) + toward * Mathf.Sin(CoverTilt)).normalized;

        private static HashSet<int> Rings(MeshSnapshot asset, int start, int rings)
        {
            var seen = new HashSet<int> { start };
            var frontier = new List<int> { start };
            for (int ring = 0; ring < rings; ring++)
            {
                var next = new List<int>();
                foreach (int g in frontier)
                    foreach (int n in asset.groupAdjacency[g])
                        if (seen.Add(n)) next.Add(n);
                frontier = next;
            }
            return seen;
        }

        // Authored interpenetration finer than this is noise, not a part made to pass under another.
        private const float AuthoredTolerance = .0005f;

        // A garment's height over a layer as authored: within LayerClearance, the gap it keeps when the layer moves.
        private const float LayerClearance = .002f;

        private static float AuthoredGap(Vector3 cloth, Vector3[] layer, int[] triangles, int triangle, Vector3 bary)
        {
            var a = layer[triangles[triangle * 3]]; var b = layer[triangles[triangle * 3 + 1]]; var c = layer[triangles[triangle * 3 + 2]];
            return Vector3.Dot(cloth - (a * bary.x + b * bary.y + c * bary.z), Vector3.Cross(b - a, c - a).normalized);
        }

        private static bool[] ProtectOpenings(MeshSnapshot asset, BodyRegion[] regions, bool[] tubes)
        {
            var result = new bool[asset.GroupCount];
            for (int g = 0; g < result.Length; g++)
            {
                // Cuffs and waistbands must expand with their neighbouring fabric. Pinning all
                // boundary vertices leaves a tight ring inside the body and stretches the next row.
                // Support's tangent rejection prevents extending openings over uncovered skin.
                // A hoodie opening is not an invitation to cover the face or ears.
                if ((regions != null && regions[g] == BodyRegion.Head) || (tubes != null && tubes[g])) result[g] = true;
            }
            return result;
        }

        internal static bool[] DetailGroups(MeshSnapshot asset, bool[] tubes)
        {
            var result = tubes != null ? (bool[])tubes.Clone() : new bool[asset.GroupCount];
            var edgeCounts = new Dictionary<ulong, int>();
            for (int t = 0; t < asset.triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = asset.groupOfVertex[asset.triangles[t + e]], b = asset.groupOfVertex[asset.triangles[t + (e + 1) % 3]];
                    if (a == b) continue;
                    ulong key = ((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    edgeCounts.TryGetValue(key, out var count); edgeCounts[key] = count + 1;
                }
            var open = new bool[asset.GroupCount];
            foreach (var edge in edgeCounts)
                if (edge.Value != 2) { open[(int)(edge.Key >> 32)] = true; open[(int)(edge.Key & uint.MaxValue)] = true; }
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
                // An open pocket flap or gusset is fabric, even if it is a small disconnected island.
                // Only closed detail shells receive rigid carry instead of collision deformation.
                if (component.Count <= 64 && component.Count < largest / 8 && !component.Exists(g => open[g]))
                    foreach (int g in component) result[g] = true;
            return result;
        }

        public static void RepairPrimary(MeshSnapshot asset, MeshSnapshot body, Vector3[] primary,
            BodyRegion[] regions, bool[] tubes, ReFitSettings settings, ReFitReport report, BodyRegion[] bodyRegions, Vector3[] authored = null,
            bool[] hidden = null, string innerGarment = null)
        {
            if (!settings.coverDifferentBaseBody) return;
            var coverage = Build(asset, body, primary, regions, tubes, settings, bodyRegions, authored, hidden, innerGarment);
            if (coverage == null) return;
            var correction = new Vector3[primary.Length];
            coverage.Apply(body, null, correction, new Vector3[primary.Length], settings, report, "different-base coverage");
            for (int g = 0; g < primary.Length; g++) primary[g] += correction[g];
        }

        public void Apply(MeshSnapshot body, Vector3[] bodyDelta, Vector3[] deltas, Vector3[] rawDeltas, ReFitSettings settings, ReFitReport report, string label)
        {
            // Cross-base body and shape repairs need the same travel budget. The tighter ordinary
            // shape-transfer cap can otherwise reintroduce the clipping just removed from the base fit.
            float limit = repair ? .08f : settings.clearanceMaxTransferredTotalCorrection;
            var original = (Vector3[])deltas.Clone();
            int corrected = 0;
            var corrections = new Vector3[deltas.Length];
            var weights = new float[deltas.Length];
            float residual = 0;
            // Clearance of the support most at risk of clipping (RisksClipping); infinite when none is.
            float clipping = float.PositiveInfinity;
            var limits = repair ? BuildLimits(original, bodyDelta != null) : null;
            var positions = new Vector3[deltas.Length];
            for (int iteration = 0; iteration < 48; iteration++)
            {
                Array.Clear(corrections, 0, corrections.Length);
                Array.Clear(weights, 0, weights.Length);
                for (int g = 0; g < positions.Length; g++) positions[g] = basis[g] + deltas[g];
                float worst = 0, closest = float.PositiveInfinity;
                // Dense skin samples constrain face interiors; cloth samples additionally catch deep
                // penetrations and folded cuffs whose face normals no longer point toward the skin.
                foreach (var s in vertexSupports)
                {
                    int a = body.triangles[s.triangle * 3], b = body.triangles[s.triangle * 3 + 1], c = body.triangles[s.triangle * 3 + 2];
                    var motion = bodyDelta == null ? Vector3.zero : bodyDelta[a] * s.bary.x + bodyDelta[b] * s.bary.y + bodyDelta[c] * s.bary.z;
                    if (bodyDelta != null && motion.sqrMagnitude < 1e-10f) continue;
                    var skin = body.worldVertices[a] * s.bary.x + body.worldVertices[b] * s.bary.y + body.worldVertices[c] * s.bary.z + motion;
                    // Shapes are additive. Each one preserves the achieved base clearance rather
                    // than applying the same unresolved base repair again (even for tiny skin motion).
                    float safety = bodyDelta != null ? s.signedGap : layer ? s.gap : Mathf.Max(RepairSafety, s.gap);
                    float missing = safety - Vector3.Dot(positions[s.group] - skin, s.normal);
                    if (missing <= .00005f) continue;
                    corrections[s.group] += s.normal * missing; weights[s.group]++;
                    worst = Mathf.Max(worst, missing); corrected++;
                }
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
                    float safety = repair && bodyDelta != null ? s.signedGap : repair && layer ? s.gap :
                        Mathf.Max(repair ? Mathf.Max(RepairSafety, settings.clearanceMinimumSafetyDistance) : settings.clearanceMinimumSafetyDistance, s.gap);
                    float missing = safety - Vector3.Dot(p - skin, n);
                    if (missing <= .00005f) continue;
                    worst = Mathf.Max(worst, missing); corrected++;
                    if (RisksClipping(missing, safety - missing)) closest = Mathf.Min(closest, safety - missing);
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
                clipping = closest;
                if (repair) Regularize(deltas, original, bodyDelta != null, limits);
                if (worst <= .0001f && iteration >= 40) break;
            }
            float max = 0;
            if (repair)
            {
                FollowDetails(deltas, original);
                FollowPieces(deltas, original);
                residual = 0;
                clipping = float.PositiveInfinity;
                foreach (var s in supports)
                {
                    if (bodyDelta != null && bodyDelta[s.vertex].sqrMagnitude < 1e-10f) continue;
                    var a = basis[s.a] + deltas[s.a]; var b = basis[s.b] + deltas[s.b]; var c = basis[s.c] + deltas[s.c];
                    var n = Vector3.Cross(b - a, c - a).normalized;
                    if (Vector3.Dot(n, body.worldNormals[s.vertex]) < .35f) continue;
                    var skin = body.worldVertices[s.vertex] + (bodyDelta != null ? bodyDelta[s.vertex] : Vector3.zero);
                    float safety = bodyDelta != null ? s.signedGap : layer ? s.gap : Mathf.Max(Mathf.Max(RepairSafety, settings.clearanceMinimumSafetyDistance), s.gap);
                    float shortfall = safety - Vector3.Dot(a*s.bary.x + b*s.bary.y + c*s.bary.z - skin, n);
                    residual = Mathf.Max(residual, shortfall);
                    if (RisksClipping(shortfall, safety - shortfall)) clipping = Mathf.Min(clipping, safety - shortfall);
                }
            }
            for (int g = 0; g < deltas.Length; g++) max = Mathf.Max(max, (deltas[g] - original[g]).magnitude);
            report?.Info("surface-coverage", $"{label}: {supports.Count} supports on {surfaceName}, {corrected} constraint corrections, maximum coverage correction {max * 1000:F3}mm, last support residual {residual * 1000:F3}mm short of the targeted clearance. Support residual is not a full mesh-intersection test.");
            if (clipping < ClipTolerance)
                report?.Warn("surface-coverage-limited", LimitedWarning(label, surfaceName, clipping));
        }

        /// <summary>
        /// A support the bounded correction left at risk of clipping: still short of its targeted clearance, and inside the
        /// surface it covers or within <see cref="ClipTolerance"/> of it. Short of the targeted room alone, cloth farther out
        /// only fits closer than intended.
        /// </summary>
        internal static bool RisksClipping(float shortfall, float clearance) => shortfall > ShortTolerance && clearance < ClipTolerance;

        /// <param name="clearance">The support most at risk: its signed distance out of <paramref name="surface"/>.</param>
        internal static string LimitedWarning(string label, string surface, float clearance) => clearance < 0
            ? $"{label}: clothing remains {-clearance * 1000:F2}mm under {surface} after the bounded correction. Inspect the garment at individual and combined shape weights."
            : $"{label}: clothing remains only {clearance * 1000:F2}mm from {surface} after the bounded correction and may clip. Inspect the garment at individual and combined shape weights.";

        // Separate pieces up to an eighth of the garment, other than the closed details already carried.
        private static List<List<int>> Pieces(MeshSnapshot asset, bool[] details)
        {
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
            return components.Where(c => c.Count < largest / 8 && !c.Exists(g => details != null && details[g])).ToList();
        }

        // A piece not connected to the fabric it sits on rides with it as one rigid part, unless its own correction is larger:
        // a pocket square or a pin must not sink into fabric pushed out of the body.
        private void FollowPieces(Vector3[] deltas, Vector3[] original)
        {
            if (pieces == null || pieces.Count == 0) return;
            var cloth = new MeshSnapshot { worldVertices = basis, triangles = triangles };
            var index = SurfaceBvh.Build(cloth);
            foreach (var piece in pieces)
            {
                var members = new HashSet<int>(piece);
                Vector3 carry = Vector3.zero, own = Vector3.zero;
                foreach (int g in piece)
                {
                    own += deltas[g] - original[g];
                    var hit = index.ClosestPoint(basis[g], .04f, t => !members.Contains(triangles[t * 3]) && !members.Contains(triangles[t * 3 + 1]) && !members.Contains(triangles[t * 3 + 2]));
                    if (!hit.found) continue;
                    int a = triangles[hit.triangle * 3], b = triangles[hit.triangle * 3 + 1], c = triangles[hit.triangle * 3 + 2];
                    var d = (deltas[a] - original[a]) * hit.bary.x + (deltas[b] - original[b]) * hit.bary.y + (deltas[c] - original[c]) * hit.bary.z;
                    if (d.sqrMagnitude > carry.sqrMagnitude) carry = d;
                }
                own /= piece.Count;
                if (carry.sqrMagnitude <= own.sqrMagnitude) continue;
                foreach (int g in piece) deltas[g] = original[g] + carry;
            }
        }

        private void FollowDetails(Vector3[] deltas, Vector3[] original)
        {
            if (tubes == null || Array.IndexOf(tubes, true) < 0) return;
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

        private sealed class RegularizationLimits
        {
            public readonly List<(int a, int b, float maximum)> edges = new List<(int, int, float)>();
            public readonly List<(int a, int b, int c, Vector3 da, Vector3 db, Vector3 dc, Vector3 normal, float minimum)> faces
                = new List<(int, int, int, Vector3, Vector3, Vector3, Vector3, float)>();
        }

        // Capture invariant rest geometry once per frame, preserving constraint order and arithmetic.
        private RegularizationLimits BuildLimits(Vector3[] original, bool transferred)
        {
            var limits = new RegularizationLimits();
            for (int a = 0; a < adjacency.Length; a++)
                foreach (int b in adjacency[a])
                    if (b > a && !(protectedGroups[a] && protectedGroups[b]))
                        limits.edges.Add((a, b, (basis[a] + original[a] - basis[b] - original[b]).magnitude * .35f));
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (protectedGroups[a] || protectedGroups[b] || protectedGroups[c]) continue;
                var da = transferred ? Vector3.zero : original[a];
                var db = transferred ? Vector3.zero : original[b];
                var dc = transferred ? Vector3.zero : original[c];
                var pa = basis[a] + da; var pb = basis[b] + db; var pc = basis[c] + dc;
                var normal = Vector3.Cross(pb - pa, pc - pa);
                if (normal.sqrMagnitude < 1e-16f) continue;
                limits.faces.Add((a, b, c, da, db, dc, normal, normal.sqrMagnitude * .1f));
            }
            return limits;
        }

        private void Regularize(Vector3[] deltas, Vector3[] original, bool transferred, RegularizationLimits limits)
        {
            // Bound the spatial gradient of the extra displacement. Short cuff/lining edges must
            // share the motion of their neighbours instead of being stretched into sharp spikes.
            for (int pass = 0; pass < 12; pass++)
            {
                bool adjusted = false;
                foreach (var edge in limits.edges)
                    {
                        int a = edge.a, b = edge.b;
                        var da = deltas[a] - original[a]; var db = deltas[b] - original[b];
                        var difference = da - db;
                        float max = edge.maximum;
                        if (difference.magnitude <= max) continue;
                        var excess = difference - Vector3.ClampMagnitude(difference, max);
                        bool fixedA = protectedGroups[a], fixedB = protectedGroups[b];
                        if (fixedA && fixedB) continue;
                        if (excess.sqrMagnitude > 0) adjusted = true;
                        if (!fixedA) deltas[a] -= excess * (fixedB ? 1 : .5f);
                        if (!fixedB) deltas[b] += excess * (fixedA ? 1 : .5f);
                    }
                if (!transferred)
                    for (int g = 0; g < deltas.Length; g++)
                    {
                        var d = deltas[g] - original[g];
                        float inward = Vector3.Dot(d, outward[g]);
                        if (inward < 0) { deltas[g] -= outward[g] * inward; adjusted = true; }
                    }
                // Once a complete pass changes nothing, the remaining identical passes cannot help.
                if (!adjusted) break;
            }
            // Keep the orientation of even very narrow folded hems. A bounded edge gradient alone
            // is insufficient for a nearly collinear triangle.
            for (int pass = 0; pass < 24; pass++)
            {
                bool changed = false;
                foreach (var face in limits.faces)
                {
                    int a = face.a, b = face.b, c = face.c;
                    var da = face.da; var db = face.db; var dc = face.dc;
                    var candidate = Vector3.Cross(basis[b] + deltas[b] - basis[a] - deltas[a], basis[c] + deltas[c] - basis[a] - deltas[a]);
                    if (Vector3.Dot(face.normal, candidate) >= face.minimum) continue;
                    // Relax differential motion around the triangle's translated centre. Resetting
                    // towards the old position undoes its escape from the body on every iteration.
                    var carry = (deltas[a] - da + deltas[b] - db + deltas[c] - dc) / 3f;
                    deltas[a] = Vector3.Lerp(da + carry, deltas[a], .5f);
                    deltas[b] = Vector3.Lerp(db + carry, deltas[b], .5f);
                    deltas[c] = Vector3.Lerp(dc + carry, deltas[c], .5f);
                    changed = true;
                }
                if (!changed) break;
            }
        }
    }
}
