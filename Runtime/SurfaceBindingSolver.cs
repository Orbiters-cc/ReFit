using System;
using System.Threading.Tasks;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit
{
    /// <summary>A binding of one asset welding group to a point on a body surface.</summary>
    public struct SurfaceBinding
    {
        public bool valid;
        public int triangle;
        public Vector3 bary;
        /// <summary>World-space bound point on the body surface.</summary>
        public Vector3 point;
        /// <summary>World-space distance from the asset vertex to the bound point.</summary>
        public float distance;
        /// <summary>Region requested by the caller for this binding.</summary>
        public BodyRegion requestedRegion;
        /// <summary>Region of the triangle that was finally hit, when known.</summary>
        public BodyRegion hitRegion;
        /// <summary>True when the filtered query found nothing and the solver retried without filters.</summary>
        public bool usedRelaxedFallback;
        /// <summary>Normal agreement between the query normal and the hit triangle normal.</summary>
        public float normalDot;
    }

    /// <summary>
    /// Computes bindings between asset vertices (welding groups) and a body surface, with optional
    /// normal-agreement and body-region filtering (the ideas behind Auto Body Hider's "Smart" mode).
    /// Bindings are computed once and reused for every shape evaluation — this is what makes ReFit fast
    /// compared to per-shape closest-point transfers.
    /// </summary>
    public static class SurfaceBindingSolver
    {
        private const float NearEquivalentSurfaceEpsilon = 0.01f;
        // A clothing surface facing the skin it covers (a lining, the inner side of a strap or waistband) keeps that skin
        // unless a surface facing its own way is at most this much (or its own distance) farther.
        private const float SkinFacingSlack = 0.02f;

        /// <summary>
        /// Binds every welding group of <paramref name="asset"/> to the surface of <paramref name="body"/>.
        /// <paramref name="assetGroupRegions"/> / <paramref name="bodyTriRegions"/> may be null to skip region filtering.
        /// </summary>
        /// <param name="bodyTriRegionMasks">Optional: every region each triangle belongs to (see <see cref="RegionMask"/>),
        /// so a transition band such as the hips' side accepts clothing of both regions. Null filters by dominant region.</param>
        public static SurfaceBinding[] ComputeGroupBindings(
            MeshSnapshot asset, MeshSnapshot body, MeshBvh bvh, ReFitSettings settings,
            BodyRegion[] assetGroupRegions, BodyRegion[] bodyTriRegions, ReFitReport report, bool[] tubularGroups = null,
            int[] bodyTriRegionMasks = null)
        {
            int groupCount = asset.GroupCount;
            var bindings = new SurfaceBinding[groupCount];
            float queryRange = Mathf.Max(settings.maxProjectionDistance * 2f, 0.01f);
            float cosMaxAngle = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
            bool useNormal = settings.filterByNormal;
            // A lower-body garment can contain an inward-facing lining and hard-edged hems.
            // Their shading normals must not send them to the far side of the same leg.
            bool lowerBodyCloth = settings.preserveLowerBodyCoverage && ReFitSurfaceCoverage.IsLowerBodyCloth(asset, assetGroupRegions, tubularGroups);
            bool useRegion = settings.filterByBoneRegion && assetGroupRegions != null && bodyTriRegions != null;

            int invalid = 0;
            Parallel.For(0, groupCount, g =>
            {
                int rep = asset.groupRep[g];
                var p = asset.worldVertices[rep];
                var n = asset.worldNormals[rep];
                var region = useRegion ? assetGroupRegions[g] : BodyRegion.Unknown;

                var hit = ClosestPointWithFallback(
                    p, body, bvh, queryRange, region, bodyTriRegions, n, cosMaxAngle,
                    useNormal && !lowerBodyCloth && !(tubularGroups != null && tubularGroups[g]), useRegion,
                    out bool usedRelaxedFallback, true, bodyTriRegionMasks);

                if (hit.Found)
                {
                    bindings[g] = new SurfaceBinding
                    {
                        valid = true,
                        triangle = hit.TriangleOrNone,
                        bary = hit.Bary,
                        point = hit.Position,
                        distance = hit.Distance,
                        requestedRegion = region,
                        hitRegion = TriangleRegion(hit.TriangleOrNone, bodyTriRegions),
                        usedRelaxedFallback = usedRelaxedFallback,
                        normalDot = Vector3.Dot(n, body.FaceNormal(hit.TriangleOrNone))
                    };
                }
                else
                {
                    bindings[g].valid = false;
                    System.Threading.Interlocked.Increment(ref invalid);
                }
            });

            if (invalid > 0)
                report?.Info("unbound-vertices",
                    $"{invalid}/{groupCount} asset vertex groups are farther than {settings.maxProjectionDistance * 2f:0.###}m from the body surface and will not be deformed directly (smoothed neighbors take over).");
            return bindings;
        }

        /// <summary>
        /// Re-binds a world point to another surface (used to chain source-surface points onto the target surface).
        /// </summary>
        /// <param name="clothNormal">The reference normal is a clothing normal (not a body normal): a clothing surface facing
        /// the near skin keeps it (see <see cref="ComputeGroupBindings"/>).</param>
        /// <param name="bodyTriRegionMasks">Optional: every region each triangle belongs to (see <see cref="RegionMask"/>).</param>
        public static SurfaceBinding BindPoint(Vector3 point, MeshSnapshot body, MeshBvh bvh, float maxDistance,
            BodyRegion region, BodyRegion[] bodyTriRegions, Vector3 referenceNormal, float cosMaxAngle, bool useNormal,
            bool clothNormal = false, int[] bodyTriRegionMasks = null)
        {
            bool useRegion = bodyTriRegions != null && region != BodyRegion.Unknown;
            // One traversal answers the near probe and every fallback (see ClosestPointWithFallback).
            var classes = Classify(point, body, bvh, maxDistance, region, bodyTriRegions, referenceNormal, cosMaxAngle, useNormal, useRegion,
                bodyTriRegionMasks);
            // The nearest triangle within the probe's range, of any facing or region: the unfiltered class's answer when it lies
            // that close (the least distance of all), none otherwise. Its squared distance is recomputed as the query computed it.
            float probe = Mathf.Min(maxDistance, NearEquivalentSurfaceEpsilon);
            var nearEquivalent = classes.any.Found && (classes.any.Position - point).sqrMagnitude < probe * probe
                ? classes.any : default;
            if (nearEquivalent.Found)
            {
                return new SurfaceBinding
                {
                    valid = true,
                    triangle = nearEquivalent.TriangleOrNone,
                    bary = nearEquivalent.Bary,
                    point = nearEquivalent.Position,
                    distance = nearEquivalent.Distance,
                    requestedRegion = region,
                    hitRegion = TriangleRegion(nearEquivalent.TriangleOrNone, bodyTriRegions),
                    usedRelaxedFallback = useNormal || useRegion,
                    normalDot = Vector3.Dot(referenceNormal, body.FaceNormal(nearEquivalent.TriangleOrNone))
                };
            }

            var hit = Fallback(classes, body, region, referenceNormal, cosMaxAngle, useNormal, useRegion, out bool usedRelaxedFallback, clothNormal);
            return new SurfaceBinding
            {
                valid = hit.Found,
                triangle = hit.TriangleOrNone,
                bary = hit.Bary,
                point = hit.Position,
                distance = hit.Distance,
                requestedRegion = region,
                hitRegion = TriangleRegion(hit.TriangleOrNone, bodyTriRegions),
                usedRelaxedFallback = usedRelaxedFallback,
                normalDot = hit.Found ? Vector3.Dot(referenceNormal, body.FaceNormal(hit.TriangleOrNone)) : 0f
            };
        }

        /// <summary>The bit of a region in a triangle region mask (0 for <see cref="BodyRegion.Unknown"/>).</summary>
        public static int RegionMask(BodyRegion region) => region == BodyRegion.Unknown ? 0 : 1 << (int)region;

        // How much farther than the nearest compatible point the surface along the normal may be (factor, then slack).
        private const float ProjectionReach = 3f;
        private const float ProjectionSlack = 0.02f;

        /// <summary>
        /// The point of another body matching a body surface point (source to target). Where the bodies overlap (within
        /// a centimeter) it is the nearest point. Elsewhere it is the surface met along the normal, outward where the other
        /// body is larger and inward where it is smaller, on faces turned the same way (and of a compatible region),
        /// unless that is much farther than the nearest compatible point: a point on a grown buttock or thigh stays on it
        /// instead of jumping to the nearest crease.
        /// </summary>
        /// <param name="normal">Unit surface normal at <paramref name="point"/>.</param>
        public static SurfaceBinding ChainPoint(Vector3 point, Vector3 normal, MeshSnapshot body, MeshBvh bvh, float maxDistance,
            BodyRegion region, BodyRegion[] bodyTriRegions, float cosMaxAngle, bool useNormal, int[] bodyTriRegionMasks = null)
        {
            var nearest = BindPoint(point, body, bvh, maxDistance, region, bodyTriRegions, normal, cosMaxAngle, useNormal, false, bodyTriRegionMasks);
            if (nearest.valid && nearest.distance <= NearEquivalentSurfaceEpsilon) return nearest;
            bool useRegion = bodyTriRegions != null && region != BodyRegion.Unknown;
            var facing = Rule(body, normal, cosMaxAngle, useRegion, region, bodyTriRegions, bodyTriRegionMasks);
            float reach = nearest.valid ? Mathf.Min(maxDistance, nearest.distance * ProjectionReach + ProjectionSlack) : maxDistance;
            var outward = bvh.Raycast(point, normal, reach, true, facing);
            var inward = bvh.Raycast(point, -normal, outward.Found ? outward.Distance : reach, true, facing);
            var along = inward.Found ? inward : outward;
            if (!along.Found) return nearest;
            return new SurfaceBinding
            {
                valid = true,
                triangle = along.TriangleOrNone,
                bary = along.Bary,
                point = along.Position,
                distance = along.Distance,
                requestedRegion = region,
                hitRegion = TriangleRegion(along.TriangleOrNone, bodyTriRegions),
                normalDot = Vector3.Dot(normal, body.FaceNormal(along.TriangleOrNone))
            };
        }

        private static BodyRegion TriangleRegion(int triangle, BodyRegion[] bodyTriRegions)
        {
            if (bodyTriRegions == null || triangle < 0 || triangle >= bodyTriRegions.Length)
                return BodyRegion.Unknown;
            return bodyTriRegions[triangle];
        }

        private static MeshBvh.Hit ClosestPointWithFallback(Vector3 point, MeshSnapshot body, MeshBvh bvh, float maxDistance,
            BodyRegion region, BodyRegion[] bodyTriRegions, Vector3 referenceNormal, float cosMaxAngle,
            bool useNormal, bool useRegion, out bool usedRelaxedFallback, bool clothNormal, int[] masks) =>
            Fallback(Classify(point, body, bvh, maxDistance, region, bodyTriRegions, referenceNormal, cosMaxAngle, useNormal, useRegion, masks),
                body, region, referenceNormal, cosMaxAngle, useNormal, useRegion, out usedRelaxedFallback, clothNormal);

        // The nearest triangle of each acceptance class the fallbacks read: the filter asked for (its region and facing), the
        // region alone, and any triangle.
        private struct Classes
        {
            public MeshBvh.Hit full, regionOnly, any;
        }

        // The rule both the rays of ChainPoint and the classes of Classify read: the region test (Labels, or Masks with region
        // masks; only when filtering by region) and the facing test (at least cosMaxAngle).
        private static TriangleRule Rule(MeshSnapshot body, Vector3 normal, float cosMaxAngle, bool useRegion, BodyRegion region, BodyRegion[] regions,
            int[] masks)
        {
            var rule = TriangleRule.All.Facing(body.KnownFaceNormals(), normal, cosMaxAngle);
            if (!useRegion) return rule;
            return masks != null ? rule.InMasks(masks, RegionMask(region)) : rule.CompatibleRegion(regions, region);
        }

        // The region test the rule makes (see Rule).
        private static TriangleTests RegionTest(bool useRegion, int[] masks) =>
            !useRegion ? TriangleTests.None : masks != null ? TriangleTests.Masks : TriangleTests.Labels;

        // A triangle's classes: 0 any, 1 of the region (any without region filtering), 2 of the region and facing the way asked
        // (without normal filtering, of the region).
        private static Classes Classify(Vector3 point, MeshSnapshot body, MeshBvh bvh, float maxDistance, BodyRegion region,
            BodyRegion[] bodyTriRegions, Vector3 referenceNormal, float cosMaxAngle, bool useNormal, bool useRegion, int[] masks)
        {
            Span<MeshBvh.Hit> hits = stackalloc MeshBvh.Hit[3];
            var regionTest = RegionTest(useRegion, masks);
            var classes = new TriangleClasses(Rule(body, referenceNormal, cosMaxAngle, useRegion, region, bodyTriRegions, masks),
                TriangleTests.None, regionTest, regionTest | (useNormal ? TriangleTests.Facing : TriangleTests.None));
            bvh.ClosestPointsByClass(point, maxDistance, classes, hits);
            return new Classes { any = hits[0], regionOnly = hits[1], full = hits[2] };
        }

        // The precedence of the filtered query's fallbacks, over the classes' answers: the filter asked for; for a clothing
        // normal, the skin it faces when nothing turned its way is about as close; the region alone; then any triangle (for the
        // torso or no region).
        private static MeshBvh.Hit Fallback(Classes classes, MeshSnapshot body, BodyRegion region, Vector3 referenceNormal, float cosMaxAngle,
            bool useNormal, bool useRegion, out bool usedRelaxedFallback, bool clothNormal)
        {
            usedRelaxedFallback = false;
            bool filtered = useNormal || useRegion;
            var hit = filtered ? classes.full : classes.any;
            var regionOnly = useRegion ? classes.regionOnly : classes.any;
            if (useNormal && clothNormal)
            {
                // The shading normal of a surface facing the skin points into that skin. Matching it to faces turned the
                // same way finds the far side of the limb or another body part; keep the skin it faces when nothing
                // turned its way is about as close.
                var near = regionOnly;
                if (near.Found && Vector3.Dot(-referenceNormal, body.FaceNormal(near.TriangleOrNone)) >= cosMaxAngle &&
                    (!hit.Found || hit.Distance > near.Distance + Mathf.Max(SkinFacingSlack, near.Distance)))
                    return near;
            }
            if (hit.Found || !filtered)
                return hit;

            if (useRegion && useNormal)
            {
                hit = regionOnly;
                if (hit.Found)
                {
                    usedRelaxedFallback = true;
                    return hit;
                }
            }

            if (!useRegion || region == BodyRegion.Unknown || region == BodyRegion.Torso)
            {
                hit = classes.any;
                usedRelaxedFallback = hit.Found;
            }

            return hit;
        }
    }

    /// <summary>Operations on per-group deformation fields: hole filling, smoothing and falloff.</summary>
    public static class DeltaField
    {
        /// <summary>
        /// Fills groups without a valid delta by diffusing from valid neighbors (Jacobi iterations).
        /// Groups that stay unreachable keep a zero delta.
        /// </summary>
        public static void FillInvalid(Vector3[] deltas, bool[] valid, System.Collections.Generic.List<int>[] adjacency, int iterations = 24)
        {
            int n = deltas.Length;
            var filled = (bool[])valid.Clone();
            var next = new Vector3[n];
            var nextFilled = new bool[n];
            for (int it = 0; it < iterations; it++)
            {
                bool changed = false;
                Array.Copy(deltas, next, n);
                Array.Copy(filled, nextFilled, n);
                for (int g = 0; g < n; g++)
                {
                    if (filled[g]) continue;
                    Vector3 sum = Vector3.zero;
                    int count = 0;
                    foreach (var nb in adjacency[g])
                    {
                        if (!filled[nb]) continue;
                        sum += deltas[nb];
                        count++;
                    }
                    if (count > 0)
                    {
                        next[g] = sum / count;
                        nextFilled[g] = true;
                        changed = true;
                    }
                }
                Array.Copy(next, deltas, n);
                Array.Copy(nextFilled, filled, n);
                if (!changed) break;
            }
        }

        /// <summary>Laplacian smoothing of the deformation field (not the geometry): keeps detail, removes projection noise.</summary>
        public static void Smooth(Vector3[] deltas, System.Collections.Generic.List<int>[] adjacency, int iterations, float strength)
        {
            if (iterations <= 0 || strength <= 0f) return;
            int n = deltas.Length;
            var buffer = new Vector3[n];
            // Ranges of groups per task, not a delegate call per group (a few additions each): every group's result is its own.
            int ranges = (n + SmoothRange - 1) / SmoothRange;
            for (int it = 0; it < iterations; it++)
            {
                Parallel.For(0, ranges, range =>
                {
                    int end = Math.Min(n, (range + 1) * SmoothRange);
                    for (int g = range * SmoothRange; g < end; g++)
                    {
                        var neighbors = adjacency[g];
                        if (neighbors.Count == 0) { buffer[g] = deltas[g]; continue; }
                        Vector3 avg = Vector3.zero;
                        foreach (var nb in neighbors) avg += deltas[nb];
                        avg /= neighbors.Count;
                        buffer[g] = Vector3.Lerp(deltas[g], avg, strength);
                    }
                });
                Array.Copy(buffer, deltas, n);
            }
        }

        private const int SmoothRange = 1024;

        /// <summary>1 below <paramref name="start"/>, 0 above <paramref name="max"/>, smoothstep in between.</summary>
        public static float Falloff(float distance, float start, float max)
        {
            if (distance <= start) return 1f;
            if (distance >= max) return 0f;
            float t = Mathf.Clamp01((distance - start) / Mathf.Max(max - start, 1e-6f));
            return 1f - t * t * (3f - 2f * t);
        }
    }
}
