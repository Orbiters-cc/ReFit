using System;
using System.Threading.Tasks;
using UnityEngine;

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
        /// <summary>
        /// Binds every welding group of <paramref name="asset"/> to the surface of <paramref name="body"/>.
        /// <paramref name="assetGroupRegions"/> / <paramref name="bodyTriRegions"/> may be null to skip region filtering.
        /// </summary>
        public static SurfaceBinding[] ComputeGroupBindings(
            MeshSnapshot asset, MeshSnapshot body, SurfaceBvh bvh, ReFitSettings settings,
            BodyRegion[] assetGroupRegions, BodyRegion[] bodyTriRegions, ReFitReport report)
        {
            int groupCount = asset.GroupCount;
            var bindings = new SurfaceBinding[groupCount];
            float queryRange = Mathf.Max(settings.maxProjectionDistance * 2f, 0.01f);
            float cosMaxAngle = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
            bool useNormal = settings.filterByNormal;
            bool useRegion = settings.filterByBoneRegion && assetGroupRegions != null && bodyTriRegions != null;

            int invalid = 0;
            Parallel.For(0, groupCount, g =>
            {
                int rep = asset.groupRep[g];
                var p = asset.worldVertices[rep];
                var n = asset.worldNormals[rep];
                var region = useRegion ? assetGroupRegions[g] : BodyRegion.Unknown;

                Func<int, bool> filter = null;
                if (useNormal || useRegion)
                {
                    filter = t =>
                    {
                        if (useRegion && !HumanoidBoneMapper.RegionsCompatible(region, bodyTriRegions[t])) return false;
                        if (useNormal && Vector3.Dot(n, body.FaceNormal(t)) < cosMaxAngle) return false;
                        return true;
                    };
                }

                var hit = bvh.ClosestPoint(p, queryRange, filter);
                bool usedRelaxedFallback = false;
                if (!hit.found && filter != null)
                {
                    // Nothing acceptable nearby; relax the filters rather than leaving a hole.
                    hit = bvh.ClosestPoint(p, queryRange, null);
                    usedRelaxedFallback = hit.found;
                }

                if (hit.found)
                {
                    bindings[g] = new SurfaceBinding
                    {
                        valid = true,
                        triangle = hit.triangle,
                        bary = hit.bary,
                        point = hit.position,
                        distance = hit.distance,
                        requestedRegion = region,
                        hitRegion = TriangleRegion(hit.triangle, bodyTriRegions),
                        usedRelaxedFallback = usedRelaxedFallback,
                        normalDot = Vector3.Dot(n, body.FaceNormal(hit.triangle))
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
        public static SurfaceBinding BindPoint(Vector3 point, MeshSnapshot body, SurfaceBvh bvh, float maxDistance,
            BodyRegion region, BodyRegion[] bodyTriRegions, Vector3 referenceNormal, float cosMaxAngle, bool useNormal)
        {
            Func<int, bool> filter = null;
            if (useNormal || (bodyTriRegions != null && region != BodyRegion.Unknown))
            {
                filter = t =>
                {
                    if (bodyTriRegions != null && !HumanoidBoneMapper.RegionsCompatible(region, bodyTriRegions[t])) return false;
                    if (useNormal && Vector3.Dot(referenceNormal, body.FaceNormal(t)) < cosMaxAngle) return false;
                    return true;
                };
            }
            var hit = bvh.ClosestPoint(point, maxDistance, filter);
            bool usedRelaxedFallback = false;
            if (!hit.found && filter != null)
            {
                hit = bvh.ClosestPoint(point, maxDistance, null);
                usedRelaxedFallback = hit.found;
            }
            return new SurfaceBinding
            {
                valid = hit.found,
                triangle = hit.triangle,
                bary = hit.bary,
                point = hit.position,
                distance = hit.distance,
                requestedRegion = region,
                hitRegion = TriangleRegion(hit.triangle, bodyTriRegions),
                usedRelaxedFallback = usedRelaxedFallback,
                normalDot = hit.found ? Vector3.Dot(referenceNormal, body.FaceNormal(hit.triangle)) : 0f
            };
        }

        private static BodyRegion TriangleRegion(int triangle, BodyRegion[] bodyTriRegions)
        {
            if (bodyTriRegions == null || triangle < 0 || triangle >= bodyTriRegions.Length)
                return BodyRegion.Unknown;
            return bodyTriRegions[triangle];
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
            for (int it = 0; it < iterations; it++)
            {
                Parallel.For(0, n, g =>
                {
                    var neighbors = adjacency[g];
                    if (neighbors.Count == 0) { buffer[g] = deltas[g]; return; }
                    Vector3 avg = Vector3.zero;
                    foreach (var nb in neighbors) avg += deltas[nb];
                    avg /= neighbors.Count;
                    buffer[g] = Vector3.Lerp(deltas[g], avg, strength);
                });
                Array.Copy(buffer, deltas, n);
            }
        }

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
