using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Fits clothing from one body to another like cloth, in two steps. The clothing first moves with the body under it: each
    /// point takes the motion of the source skin around it (the source laid onto the target, <see cref="ReFitBodyRegistration"/>),
    /// averaged over a reach that grows with its distance from the skin, so the motion is smooth over the fabric and nothing
    /// jumps between body parts. Then the fabric is draped: where it covered the source body and the target skin comes
    /// through, the fabric is pushed out, and each push spreads over the fabric around it as cloth does, so it spans a larger
    /// muscle or fluff instead of tenting at single points. Skin that showed through the clothing on the source (fluff through
    /// a neckline, skin past a cuff) and target skin the source has no counterpart for never moves it. A body shape added to
    /// the fit is draped the same way over the shaped target, from the fitted clothing. Runs on captured arrays.
    /// </summary>
    internal sealed class ReFitClothFit
    {
        // Room kept between the fabric and the skin it covers: the authored room, within these bounds.
        private const float MinClearance = .0006f, MaxClearance = .003f;
        // The motion of a point is averaged over skin within this reach (at least), growing with its distance to the skin.
        private const float MinSpread = .015f, SpreadPerDistance = .3f, MaxReach = .15f;
        private const int MaxSamples = 48;
        // A target skin point has a counterpart on the source where the source laid onto it lies this close.
        private const float CounterpartReach = .012f;
        // How far out of the skin covering fabric is looked for, and how far apart the skin and the fabric may be compared.
        private const float CoverReach = .1f, NearReach = .06f;
        // Fabric turned this far from the skin (dot of normals) does not cover it.
        private const float Facing = .3f;
        private const int Iterations = 48, PushOnly = 8, SmoothPasses = 2;
        private const float SmoothStrength = .5f;
        // A push moves the fabric at most this much per pass, and the drape at most this far from where the body moved it.
        private const float MaxPush = .006f, MaxDrape = .12f, LatePush = .0015f, LateSmoothStrength = .2f;
        // Skin of the target's main surface the laid source does not reach (grown hips, a sculpted edit) takes the source skin
        // met along its normal: inward this far (the target is larger there) or outward (smaller).
        private const float GrownReach = .3f, ShrunkReach = .05f;
        // A push lifts the fabric around it at most this much less per metre away (the cone of fabric over a bump).
        private const float SpreadSlope = .4f;
        // Rays out of the skin looking for fabric: straight out, and tilted this much (radians) around it.
        private const float RayTilt = .45f;

        /// <summary>Debug: the target vertices the fit found covered and without a source counterpart (tests only).</summary>
        internal static Action<bool[], bool[]> CoverageCaptured;
        // Pieces this small (a button, a stud) move as one.
        private const float DetailExtent = .03f;

        private readonly MeshSnapshot asset;
        private readonly int groupCount;
        private readonly int[] groupTriangles;
        private readonly List<int>[] adjacency;
        private readonly float[] clearance;
        // How far each group lay over the skin it was made on (negative: under it).
        private readonly float[] authoredGap;
        private readonly BodyRegion[] regions;
        private readonly int[] detailOf;
        private readonly List<int>[] details;
        private readonly CancellationToken cancellation;

        // The fitted clothing and the target skin samples of each group, for shapes added to the fit.
        private Vector3[] fitted;
        private int[][] shapeSamples;
        private float[][] shapeWeights;
        private bool[] shapeCovered;
        private int[] shapeCandidates;
        // How far the fitted clothing still lets the skin through (per group, per candidate skin vertex): a shape's drape
        // corrects only what the shape adds. Shapes are summed when several are on, so each fixing what the fit left would
        // apply that correction once per shape.
        private float[] fabricAllowance, skinAllowance;

        // Above this share of the target's main skin under the clothing without a source counterpart or farther than
        // UnreachedGap from the source laid onto it, the bodies differ more than the registration laid them onto each other
        // (a sculpted edit, hips twice the base's): no cloth fit.
        private const float MaxUnmatchedShare = .02f, UnreachedGap = .02f;

        public ReFitClothFit(MeshSnapshot asset, SurfaceBinding[] sourceBindings, MeshSnapshot source, BodyRegion[] groupRegions,
            CancellationToken cancellation)
        {
            this.asset = asset;
            this.cancellation = cancellation;
            groupCount = asset.GroupCount;
            regions = groupRegions;
            adjacency = asset.groupAdjacency;
            var tris = new List<int>(asset.triangles.Length);
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                int a = asset.groupOfVertex[asset.triangles[t]], b = asset.groupOfVertex[asset.triangles[t + 1]], c = asset.groupOfVertex[asset.triangles[t + 2]];
                if (a == b || b == c || a == c) continue;
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            groupTriangles = tris.ToArray();

            // The room the fabric had over the skin it was made on.
            clearance = new float[groupCount];
            authoredGap = new float[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                float gap = MaxClearance;
                if (sourceBindings != null && sourceBindings[g].valid)
                {
                    var p = asset.worldVertices[asset.groupRep[g]];
                    gap = Vector3.Dot(p - sourceBindings[g].point, source.BaryNormal(sourceBindings[g].triangle, sourceBindings[g].bary));
                }
                authoredGap[g] = gap;
                clearance[g] = Mathf.Clamp(gap, MinClearance, MaxClearance);
            }
            Details(out detailOf, out details);
        }

        // ------------------------------------------------------------------
        // The fit
        // ------------------------------------------------------------------

        /// <summary>
        /// The world motion of each group from the source body onto the target, or null when too much of the target's main
        /// skin under the clothing has no counterpart on the source (the caller fits it another way). <paramref name="falloff"/>
        /// scales the motion along the body (accessories far from the skin), never the drape.
        /// </summary>
        public Vector3[] Fit(MeshSnapshot source, int[] sourceTriangleMasks, ReFitBodyRegistration registration, MeshSnapshot target,
            SurfaceBvh targetIndex, float[] falloff, ReFitReport report, Action<float> progress)
        {
            var authored = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) authored[g] = asset.worldVertices[asset.groupRep[g]];

            // 1. The motion of the skin under each group (the skin around it where none is under it).
            var authoredSurface = MeshSnapshot.FromWorld(authored, groupTriangles, false);
            var authoredIndex = SurfaceBvh.Build(authoredSurface);
            var sourceCovered = new bool[source.worldVertices.Length];
            Parallel.For(0, sourceCovered.Length, v =>
                sourceCovered[v] = Covered(source.worldVertices[v], source.worldNormals[v], authoredSurface, authoredIndex));
            var skin = BodyPoints.FromSource(source, sourceTriangleMasks, registration, sourceCovered);
            var sourceIndex = SurfaceBvh.Build(source);
            Samples(authored, skin, sourceIndex, out var samples, out var weights);
            var motions = new Vector3[groupCount];
            Parallel.For(0, groupCount, g =>
            {
                var motion = Vector3.zero;
                var s = samples[g];
                for (int k = 0; k < s.Length; k++) motion += skin.motion[s[k]] * weights[g][k];
                motions[g] = motion * (falloff != null ? falloff[g] : 1f);
            });
            MoveAsOne(motions);
            var start = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) start[g] = authored[g] + motions[g];
            progress?.Invoke(.3f);
            cancellation.ThrowIfCancellationRequested();
            float largest = 0f, total = 0f;
            for (int g = 0; g < groupCount; g++) { float m = (start[g] - authored[g]).magnitude; largest = Mathf.Max(largest, m); total += m; }
            report?.Info("cloth-fit-motion", $"The skin under the clothing moves it by {total / Mathf.Max(1, groupCount) * 1000f:0.0} mm on average, " +
                                             $"{largest * 1000f:0} mm at most.");

            // 2. Which target skin the fabric covers: where its counterpart on the source was under the authored fabric.
            var registered = MeshSnapshot.FromWorld(registration.registered, source.triangles, false);
            registered.hiddenTriangles = source.hiddenTriangles;
            var registeredIndex = SurfaceBvh.Build(registered);
            int targetCount = target.worldVertices.Length;
            var covered = new bool[targetCount];
            var unmatched = new bool[targetCount];
            var counterpartPoint = new Vector3[targetCount];
            var counterpartNormal = new Vector3[targetCount];
            var hidden = target.HiddenVertices();
            var main = target.MainSurface();
            Parallel.For(0, targetCount, t =>
            {
                if (hidden != null && hidden[t]) return;
                var p = target.worldVertices[t];
                var n = target.worldNormals[t];
                Func<int, bool> facing = tri => Vector3.Dot(registered.FaceNormal(tri), n) >= Facing;
                var counterpart = registeredIndex.ClosestPoint(p, CounterpartReach, facing);
                if (!counterpart.found && main[t])
                {
                    // Grown skin: the skin beneath it on the laid source (or above it, where the target is smaller).
                    counterpart = registeredIndex.Raycast(p, -n, GrownReach, facing);
                    if (!counterpart.found) counterpart = registeredIndex.Raycast(p, n, ShrunkReach, facing);
                }
                if (!counterpart.found) { unmatched[t] = true; return; }
                var sp = source.BaryPoint(counterpart.triangle, counterpart.bary);
                var sn = source.BaryNormal(counterpart.triangle, counterpart.bary);
                covered[t] = Covered(sp, sn, authoredSurface, authoredIndex);
                counterpartPoint[t] = sp;
                counterpartNormal[t] = sn;
            });
            progress?.Invoke(.4f);
            CoverageCaptured?.Invoke(covered, unmatched);

            // 3. The drape over the target.
            // What came through the fabric as authored stays allowed: coarse fabric sags into a curved body between its
            // vertices, straps press into it. The drape corrects only what the move to the target adds.
            var startSurface = MeshSnapshot.FromWorld(start, groupTriangles, false);
            var startIndex = SurfaceBvh.Build(startSurface);
            var under = new bool[targetCount];
            Parallel.For(0, targetCount, t => under[t] = (hidden == null || !hidden[t]) && startIndex.ClosestPoint(target.worldVertices[t], CoverReach).found);
            // Skin the source lacks on the target's main surface (not fluff, horns or hair: parts of their own) under the clothing.
            var lacks = new bool[targetCount];
            Parallel.For(0, targetCount, t =>
                lacks[t] = under[t] && main[t] && (unmatched[t] || !registeredIndex.ClosestPoint(target.worldVertices[t], UnreachedGap).found));
            int underCount = 0, lacking = 0;
            for (int t = 0; t < targetCount; t++)
            {
                if (!under[t] || !main[t]) continue;
                underCount++;
                if (lacks[t]) lacking++;
            }
            float lackingShare = underCount > 0 ? (float)lacking / underCount : 0f;
            if (lackingShare > MaxUnmatchedShare)
            {
                report?.Info("cloth-fit-skipped", $"{lackingShare * 100f:0} % of the target's skin under the clothing is not reached by the source " +
                                                 "laid onto it: the bodies differ more than they could be laid onto each other.");
                return null;
            }
            report?.Info("cloth-fit-coverage", $"{lackingShare * 100f:0.0} % of the target's skin under the clothing is not reached by the source laid onto it.");
            var near = new bool[targetCount];
            for (int t = 0; t < targetCount; t++) near[t] = covered[t] && under[t];
            var candidateList = new List<int>();
            for (int t = 0; t < targetCount; t++) if (near[t]) candidateList.Add(t);
            var candidates = candidateList.ToArray();
            fabricAllowance = new float[groupCount];
            for (int g = 0; g < groupCount; g++) fabricAllowance[g] = Mathf.Max(0f, clearance[g] - authoredGap[g]);
            skinAllowance = new float[candidates.Length];
            Parallel.For(0, candidates.Length, c =>
            {
                int t = candidates[c];
                skinAllowance[c] = Mathf.Max(0f, SkinNeed(counterpartPoint[t], counterpartNormal[t], authoredSurface, authoredIndex));
            });
            var result = Relax(start, target, targetIndex, covered, report, "the fit", p => progress?.Invoke(.4f + .6f * p), candidates, allowed: true);
            var through = Through(result, target, unmatched);
            if (through > 0)
                report?.Info("cloth-fit-unmatched", $"{through} target skin vertices the source body has no counterpart for (fluff, horns) come " +
                                                    "through the clothing; the avatar's own shapes can hide them.");
            var motionResult = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) motionResult[g] = result[g] - authored[g];
            return motionResult;
        }

        // ------------------------------------------------------------------
        // Shapes added to the fit
        // ------------------------------------------------------------------

        /// <summary>The fitted clothing (authored plus <paramref name="fit"/>) on the target, for <see cref="Shape"/>.</summary>
        public void PrepareShapes(Vector3[] fit, MeshSnapshot target, int[] targetTriangleMasks)
        {
            fitted = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) fitted[g] = asset.worldVertices[asset.groupRep[g]] + (fit != null ? fit[g] : Vector3.zero);
            // The target skin the fitted fabric covers keeps it covered while the shape moves it.
            var surface = MeshSnapshot.FromWorld(fitted, groupTriangles, false);
            var index = SurfaceBvh.Build(surface);
            int count = target.worldVertices.Length;
            shapeCovered = new bool[count];
            var hidden = target.HiddenVertices();
            var near = new bool[count];
            Parallel.For(0, count, t =>
            {
                if (hidden != null && hidden[t]) return;
                var p = target.worldVertices[t];
                shapeCovered[t] = Covered(p, target.worldNormals[t], surface, index);
                near[t] = index.ClosestPoint(p, CoverReach).found;
            });
            var candidates = new List<int>();
            for (int t = 0; t < count; t++) if (shapeCovered[t] && near[t]) candidates.Add(t);
            shapeCandidates = candidates.ToArray();
            var skin = BodyPoints.FromTarget(target, targetTriangleMasks, shapeCovered);
            var targetIndex = SurfaceBvh.Build(target);
            Samples(fitted, skin, targetIndex, out shapeSamples, out shapeWeights);
            fabricAllowance = new float[groupCount];
            skinAllowance = new float[shapeCandidates.Length];
            Relax(fitted, target, targetIndex, shapeCovered, null, null, null, shapeCandidates, measure: true);
        }

        /// <summary>
        /// The world motion of each group for a body shape moving the target's vertices by <paramref name="shapeMotion"/>, from the
        /// fitted clothing (<see cref="PrepareShapes"/>). Thread safe: shapes may be draped concurrently.
        /// </summary>
        public Vector3[] Shape(MeshSnapshot target, Vector3[] shapeMotion, float[] falloff, ReFitReport report, string shapeName,
            int smoothingIterations = 0, float smoothingStrength = 0f)
        {
            if (fitted == null) throw new InvalidOperationException("PrepareShapes first.");
            var motions = new Vector3[groupCount];
            Parallel.For(0, groupCount, g =>
            {
                var motion = Vector3.zero;
                var s = shapeSamples[g];
                for (int k = 0; k < s.Length; k++) motion += shapeMotion[s[k]] * shapeWeights[g][k];
                motions[g] = motion * (falloff != null ? falloff[g] : 1f);
            });
            DeltaField.Smooth(motions, adjacency, smoothingIterations, smoothingStrength);
            MoveAsOne(motions);
            var start = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) start[g] = fitted[g] + motions[g];
            var shaped = new Vector3[target.worldVertices.Length];
            for (int i = 0; i < shaped.Length; i++) shaped[i] = target.worldVertices[i] + shapeMotion[i];
            var body = MeshSnapshot.FromWorld(shaped, target.triangles, false);
            body.hiddenTriangles = target.hiddenTriangles;
            var index = SurfaceBvh.Build(body);
            var result = Relax(start, body, index, shapeCovered, report, "'" + shapeName + "'", null, shapeCandidates, allowed: true);
            var motionResult = new Vector3[groupCount];
            for (int g = 0; g < groupCount; g++) motionResult[g] = result[g] - fitted[g];
            return motionResult;
        }

        // ------------------------------------------------------------------
        // Drape
        // ------------------------------------------------------------------

        /// <summary>
        /// Pushes the fabric out of the covered skin of <paramref name="body"/>: each fabric point out of the skin under it, and the
        /// fabric over each covered skin point coming through it; the pushes spread over the fabric, the last passes only push.
        /// </summary>
        /// <param name="measure">Only measure how far the skin comes through <paramref name="start"/> into the allowances.</param>
        /// <param name="allowed">Correct only what goes beyond the allowances (a shape added to the fit).</param>
        private Vector3[] Relax(Vector3[] start, MeshSnapshot body, SurfaceBvh bodyIndex, bool[] coveredVertex, ReFitReport report,
            string label, Action<float> progress, int[] candidates = null, bool measure = false, bool allowed = false)
        {
            int triangleCount = body.triangles.Length / 3;
            var coveredTriangle = new bool[triangleCount];
            for (int t = 0; t < triangleCount; t++)
                coveredTriangle[t] = coveredVertex[body.triangles[t * 3]] && coveredVertex[body.triangles[t * 3 + 1]] && coveredVertex[body.triangles[t * 3 + 2]];
            var surface = MeshSnapshot.FromWorld(start, groupTriangles, false);
            if (candidates == null)
            {
                var index0 = SurfaceBvh.Build(surface);
                var list = new List<int>();
                var near = new bool[body.worldVertices.Length];
                Parallel.For(0, near.Length, t => near[t] = coveredVertex[t] && index0.ClosestPoint(body.worldVertices[t], CoverReach).found);
                for (int t = 0; t < near.Length; t++) if (near[t]) list.Add(t);
                candidates = list.ToArray();
            }

            var correction = new Vector3[groupCount];
            var position = (Vector3[])start.Clone();
            var push = new Vector3[groupCount];
            var pushLength = new float[groupCount];
            var skinPush = new Vector3[candidates.Length * 3];
            var skinPushGroup = new int[candidates.Length * 3];
            var buffer = new Vector3[groupCount];
            // The largest push each group asked for so far, and the cone the pushes may lift the fabric around them.
            var needed = new float[groupCount];
            var cap = new float[groupCount];
            SurfaceBvh index = null;
            int pushedLast = 0;
            float deepest = 0f;
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int g = 0; g < groupCount; g++) position[g] = start[g] + correction[g];
                surface.worldVertices = position;
                surface.worldNormals = Normals(position, groupTriangles, groupCount);
                if (index == null) index = SurfaceBvh.Build(surface); else index.Refit(surface);
                Array.Clear(push, 0, groupCount);
                Array.Clear(pushLength, 0, groupCount);

                // Fabric points out of the covered skin under them.
                var normals = surface.worldNormals;
                Parallel.For(0, groupCount, g =>
                {
                    var p = position[g];
                    var n = normals[g];
                    var hit = bodyIndex.ClosestPoint(p, NearReach, t => coveredTriangle[t] && Vector3.Dot(body.FaceNormal(t), n) > 0f);
                    if (!hit.found) return;
                    var skinNormal = body.BaryNormal(hit.triangle, hit.bary);
                    float height = Vector3.Dot(p - hit.position, skinNormal);
                    float need = clearance[g] - height;
                    if (measure) { fabricAllowance[g] = Mathf.Max(0f, need); return; }
                    if (allowed) need -= fabricAllowance[g];
                    if (need <= 0f) return;
                    push[g] = skinNormal * need;
                    pushLength[g] = need;
                });

                // Covered skin coming through the fabric over it.
                Parallel.For(0, candidates.Length, c =>
                {
                    int t = candidates[c];
                    for (int k = 0; k < 3; k++) skinPushGroup[c * 3 + k] = -1;
                    var p = body.worldVertices[t];
                    var n = body.worldNormals[t];
                    var hit = index.ClosestPoint(p, NearReach, tri => Vector3.Dot(surface.FaceNormal(tri), n) >= Facing);
                    if (!hit.found) return;
                    var faceNormal = surface.FaceNormal(hit.triangle);
                    int i0 = groupTriangles[hit.triangle * 3], i1 = groupTriangles[hit.triangle * 3 + 1], i2 = groupTriangles[hit.triangle * 3 + 2];
                    float room = clearance[i0] * hit.bary.x + clearance[i1] * hit.bary.y + clearance[i2] * hit.bary.z;
                    float need = Vector3.Dot(p - hit.position, faceNormal) + room;
                    if (measure) { skinAllowance[c] = Mathf.Max(0f, need); return; }
                    if (allowed) need -= skinAllowance[c];
                    if (need <= 0f) return;
                    float most = Mathf.Max(hit.bary.x, Mathf.Max(hit.bary.y, hit.bary.z));
                    skinPushGroup[c * 3] = i0; skinPush[c * 3] = faceNormal * (need * hit.bary.x / most);
                    skinPushGroup[c * 3 + 1] = i1; skinPush[c * 3 + 1] = faceNormal * (need * hit.bary.y / most);
                    skinPushGroup[c * 3 + 2] = i2; skinPush[c * 3 + 2] = faceNormal * (need * hit.bary.z / most);
                });
                if (measure) return start;
                for (int k = 0; k < skinPushGroup.Length; k++)
                {
                    int g = skinPushGroup[k];
                    if (g < 0) continue;
                    float length = skinPush[k].magnitude;
                    if (length > pushLength[g]) { pushLength[g] = length; push[g] = skinPush[k]; }
                }

                int pushed = 0;
                float worst = 0f;
                for (int g = 0; g < groupCount; g++)
                {
                    if (pushLength[g] <= 0f) continue;
                    worst = Mathf.Max(worst, pushLength[g]);
                    needed[g] = Mathf.Max(needed[g], correction[g].magnitude + pushLength[g]);
                    var moved = correction[g] + push[g] * Mathf.Min(1f, (iteration < Iterations - PushOnly ? MaxPush : LatePush) / pushLength[g]);
                    if (moved.sqrMagnitude > MaxDrape * MaxDrape) moved = moved.normalized * MaxDrape;
                    if ((moved - correction[g]).sqrMagnitude < 1e-10f) continue;
                    correction[g] = moved;
                    pushed++;
                }
                if (iteration == 0) deepest = worst;
                pushedLast = pushed;
                // The last passes push less and smooth lightly: a lone vertex pushed alone would stand out as a spike.
                {
                    Smooth(correction, buffer, iteration < Iterations - PushOnly ? SmoothStrength : LateSmoothStrength);
                    Cone(position, needed, cap);
                    for (int g = 0; g < groupCount; g++)
                    {
                        float length = correction[g].magnitude;
                        if (length > cap[g]) correction[g] *= cap[g] / length;
                    }
                }
                progress?.Invoke((iteration + 1f) / Iterations);
                if (pushed == 0 && iteration >= Iterations - PushOnly) break;
            }

            MoveAsOne(correction);
            for (int g = 0; g < groupCount; g++) position[g] = start[g] + correction[g];
            report?.Info("cloth-fit-drape", $"Draped {label}: the skin came {deepest * 1000f:0.0} mm through at most; {pushedLast} fabric points were still pushed at the end.");
            return position;
        }

        // Small pieces (a chain's links, a button) move as one: each group of a piece takes the mean move of the piece. Groups
        // of one link on either side of a body boundary (neck and chest) would otherwise move apart and tear it.
        private void MoveAsOne(Vector3[] move)
        {
            foreach (var piece in details)
            {
                var mean = Vector3.zero;
                foreach (int g in piece) mean += move[g];
                mean /= piece.Count;
                foreach (int g in piece) move[g] = mean;
            }
        }

        // cap = the largest need around each group, less SpreadSlope per metre of fabric between them.
        private void Cone(Vector3[] position, float[] needed, float[] cap)
        {
            Array.Copy(needed, cap, groupCount);
            for (int pass = 0; pass < 64; pass++)
            {
                bool changed = false;
                for (int g = 0; g < groupCount; g++)
                    foreach (int other in adjacency[g])
                    {
                        float reach = cap[other] - (position[g] - position[other]).magnitude * SpreadSlope;
                        if (reach > cap[g] + 1e-6f) { cap[g] = reach; changed = true; }
                    }
                if (!changed) break;
            }
        }

        private void Smooth(Vector3[] field, Vector3[] buffer, float strength)
        {
            for (int pass = 0; pass < SmoothPasses; pass++)
            {
                Parallel.For(0, groupCount, g =>
                {
                    var around = adjacency[g];
                    if (around.Count == 0) { buffer[g] = field[g]; return; }
                    var mean = Vector3.zero;
                    foreach (int other in around) mean += field[other];
                    mean /= around.Count;
                    buffer[g] = Vector3.Lerp(field[g], mean, strength);
                });
                Array.Copy(buffer, field, groupCount);
            }
        }

        // Target skin with no counterpart on the source, in front of the fabric covering the skin around it.
        private int Through(Vector3[] positions, MeshSnapshot target, bool[] unmatched)
        {
            var surface = MeshSnapshot.FromWorld(positions, groupTriangles, false);
            var index = SurfaceBvh.Build(surface);
            int count = 0;
            for (int t = 0; t < unmatched.Length; t++)
            {
                if (!unmatched[t]) continue;
                var p = target.worldVertices[t];
                var n = target.worldNormals[t];
                var hit = index.ClosestPoint(p, .03f, tri => Vector3.Dot(surface.FaceNormal(tri), n) >= Facing);
                if (hit.found && Vector3.Dot(p - hit.position, surface.FaceNormal(hit.triangle)) > .001f) count++;
            }
            return count;
        }

        // How far skin at a point (turned up) comes through the fabric over it, plus the room the fabric keeps there; at most
        // zero where it does not come through or no fabric is over it.
        private float SkinNeed(Vector3 point, Vector3 up, MeshSnapshot fabric, SurfaceBvh index)
        {
            var hit = index.ClosestPoint(point, NearReach, tri => Vector3.Dot(fabric.FaceNormal(tri), up) >= Facing);
            if (!hit.found) return 0f;
            int i0 = groupTriangles[hit.triangle * 3], i1 = groupTriangles[hit.triangle * 3 + 1], i2 = groupTriangles[hit.triangle * 3 + 2];
            float room = clearance[i0] * hit.bary.x + clearance[i1] * hit.bary.y + clearance[i2] * hit.bary.z;
            return Vector3.Dot(point - hit.position, fabric.FaceNormal(hit.triangle)) + room;
        }

        /// <summary>
        /// Skin at point (turned up) is under the fabric: fabric turned the same way is met straight out of the skin, or along
        /// two of four rays tilted around it (skin in a fold of the body, where the fabric bridges the fold). Skin past a hem
        /// is not under it, however close.
        /// </summary>
        private static bool Covered(Vector3 point, Vector3 up, MeshSnapshot fabric, SurfaceBvh index)
        {
            Func<int, bool> facing = tri => Vector3.Dot(fabric.FaceNormal(tri), up) >= Facing;
            var origin = point + up * .0005f;
            if (index.Raycast(origin, up, CoverReach, facing).found) return true;
            var side = MeshSnapshot.Unit(Vector3.Cross(up, Mathf.Abs(up.y) < .9f ? Vector3.up : Vector3.right));
            var other = Vector3.Cross(up, side);
            float c = Mathf.Cos(RayTilt), s = Mathf.Sin(RayTilt);
            int hits = 0;
            foreach (var around in new[] { side, -side, other, -other })
                if (index.Raycast(origin, up * c + around * s, CoverReach, facing).found && ++hits >= 2) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // Motion samples
        // ------------------------------------------------------------------

        /// <summary>
        /// Per group, the body points whose motion it takes and their weights: points turned towards it, of a compatible region,
        /// within a reach growing with its distance to the body, weighted down from the nearest.
        /// </summary>
        private void Samples(Vector3[] points, BodyPoints body, SurfaceBvh bodyIndex, out int[][] samples, out float[][] weights)
        {
            var sampleList = new int[groupCount][];
            var weightList = new float[groupCount][];
            Parallel.For(0, groupCount, g =>
            {
                var p = points[g];
                var nearest = bodyIndex.ClosestPoint(p, MaxReach);
                float distance = nearest.found ? nearest.distance : MaxReach;
                float spread = Mathf.Max(MinSpread, distance * SpreadPerDistance);
                float reach = Mathf.Min(MaxReach, distance + spread * 2.5f);
                var region = regions != null ? regions[g] : BodyRegion.Unknown;
                int regionMask = SurfaceBindingSolver.RegionMask(region);
                var found = new List<(int index, float weight)>();
                var under = new List<(int index, float weight)>();
                body.Near(p, reach, k =>
                {
                    var offset = p - body.position[k];
                    float d = offset.magnitude;
                    if (d > reach) return;
                    if (Vector3.Dot(body.normal[k], offset) < -.25f * d) return;
                    int mask = body.mask[k];
                    if (regionMask != 0 && mask != 0 && (mask & regionMask) == 0) return;
                    float x = (d - distance) / spread;
                    found.Add((k, Mathf.Exp(-x * x)));
                    if (body.under[k]) under.Add(found[found.Count - 1]);
                });
                // Skin the fabric covers moves it; fluff through it or skin past a hem only where none is under it.
                if (under.Count > 0) found = under;
                if (found.Count == 0 && nearest.found)
                {
                    // Nothing turned its way: the nearest body point.
                    int corner = body.NearestCorner(nearest.triangle, nearest.bary);
                    if (corner >= 0) found.Add((corner, 1f));
                }
                if (found.Count > MaxSamples)
                {
                    found.Sort((a, b) => b.weight.CompareTo(a.weight));
                    found.RemoveRange(MaxSamples, found.Count - MaxSamples);
                }
                float total = 0f;
                foreach (var f in found) total += f.weight;
                var s = new int[found.Count];
                var w = new float[found.Count];
                for (int i = 0; i < found.Count; i++) { s[i] = found[i].index; w[i] = total > 0f ? found[i].weight / total : 0f; }
                sampleList[g] = s;
                weightList[g] = w;
            });
            samples = sampleList;
            weights = weightList;
        }

        // Pieces of the clothing (connected groups) small enough to move as one.
        private void Details(out int[] of, out List<int>[] pieces)
        {
            of = new int[groupCount];
            for (int g = 0; g < groupCount; g++) of[g] = -1;
            var list = new List<List<int>>();
            var seen = new bool[groupCount];
            var stack = new Stack<int>();
            for (int g = 0; g < groupCount; g++)
            {
                if (seen[g]) continue;
                var piece = new List<int>();
                seen[g] = true;
                stack.Push(g);
                var min = asset.worldVertices[asset.groupRep[g]];
                var max = min;
                while (stack.Count > 0)
                {
                    int x = stack.Pop();
                    piece.Add(x);
                    var p = asset.worldVertices[asset.groupRep[x]];
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    foreach (int y in adjacency[x])
                        if (!seen[y]) { seen[y] = true; stack.Push(y); }
                }
                if ((max - min).magnitude > DetailExtent || piece.Count == groupCount) continue;
                foreach (int x in piece) of[x] = list.Count;
                list.Add(piece);
            }
            pieces = list.ToArray();
        }

        private static Vector3[] Normals(Vector3[] positions, int[] triangles, int count)
        {
            var normals = new Vector3[count];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < count; i++) normals[i] = MeshSnapshot.Unit(normals[i]);
            return normals;
        }

        /// <summary>Welded skin points of a body with their normal, regions and motion, in a grid for radius queries.</summary>
        private sealed class BodyPoints
        {
            private const float Cell = .02f;
            public Vector3[] position, normal, motion;
            public int[] mask;
            /// <summary>The point is under the fabric.</summary>
            public bool[] under;
            // Per body vertex, its point (welded); -1 for hidden skin.
            private int[] pointOf;
            private Dictionary<Vector3Int, List<int>> grid;
            private MeshSnapshot body;

            public static BodyPoints FromSource(MeshSnapshot source, int[] triangleMasks, ReFitBodyRegistration registration, bool[] covered)
            {
                var points = Build(source, triangleMasks, covered);
                points.motion = new Vector3[points.position.Length];
                for (int v = 0; v < source.worldVertices.Length; v++)
                {
                    int k = points.pointOf[v];
                    if (k >= 0) points.motion[k] = registration.registered[v] - source.worldVertices[v];
                }
                return points;
            }

            /// <summary>Points indexed by target vertex (a shape moves each vertex): no welding, every visible vertex.</summary>
            public static BodyPoints FromTarget(MeshSnapshot target, int[] triangleMasks, bool[] covered) => Build(target, triangleMasks, covered, false);

            private static BodyPoints Build(MeshSnapshot body, int[] triangleMasks, bool[] covered, bool weld = true)
            {
                int vertexCount = body.worldVertices.Length;
                var visible = new bool[vertexCount];
                var vertexMask = new int[vertexCount];
                for (int t = 0; t < body.triangles.Length / 3; t++)
                {
                    bool hidden = body.hiddenTriangles != null && body.hiddenTriangles[t];
                    for (int k = 0; k < 3; k++)
                    {
                        int v = body.triangles[t * 3 + k];
                        if (!hidden) visible[v] = true;
                        if (triangleMasks != null) vertexMask[v] |= triangleMasks[t];
                    }
                }
                var points = new BodyPoints { body = body, pointOf = new int[vertexCount], grid = new Dictionary<Vector3Int, List<int>>() };
                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                var masks = new List<int>();
                var unders = new List<bool>();
                var byPosition = weld ? new Dictionary<Vector3, int>() : null;
                for (int v = 0; v < vertexCount; v++)
                {
                    points.pointOf[v] = -1;
                    if (!visible[v]) continue;
                    var p = body.worldVertices[v];
                    bool isUnder = covered != null && covered[v];
                    if (weld && byPosition.TryGetValue(p, out int existing))
                    {
                        points.pointOf[v] = existing;
                        masks[existing] |= vertexMask[v];
                        unders[existing] |= isUnder;
                        continue;
                    }
                    int k = weld ? positions.Count : v;
                    if (!weld)
                    {
                        // Indexed by vertex: fill the gaps of hidden vertices.
                        while (positions.Count < v) { positions.Add(Vector3.zero); normals.Add(Vector3.zero); masks.Add(0); unders.Add(false); }
                    }
                    positions.Add(p);
                    normals.Add(body.worldNormals[v]);
                    masks.Add(vertexMask[v]);
                    unders.Add(isUnder);
                    if (weld) byPosition[p] = k;
                    points.pointOf[v] = k;
                    var cell = Vector3Int.FloorToInt(p / Cell);
                    if (!points.grid.TryGetValue(cell, out var list)) points.grid[cell] = list = new List<int>();
                    list.Add(k);
                }
                if (!weld) while (positions.Count < vertexCount) { positions.Add(Vector3.zero); normals.Add(Vector3.zero); masks.Add(0); unders.Add(false); }
                points.under = unders.ToArray();
                points.position = positions.ToArray();
                points.normal = normals.ToArray();
                points.mask = masks.ToArray();
                return points;
            }

            public void Near(Vector3 p, float radius, Action<int> visit)
            {
                var min = Vector3Int.FloorToInt((p - Vector3.one * radius) / Cell);
                var max = Vector3Int.FloorToInt((p + Vector3.one * radius) / Cell);
                for (int x = min.x; x <= max.x; x++)
                    for (int y = min.y; y <= max.y; y++)
                        for (int z = min.z; z <= max.z; z++)
                            if (grid.TryGetValue(new Vector3Int(x, y, z), out var list))
                                foreach (int k in list) visit(k);
            }

            public int NearestCorner(int triangle, Vector3 bary)
            {
                int corner = bary.x >= bary.y && bary.x >= bary.z ? 0 : bary.y >= bary.z ? 1 : 2;
                return pointOf[body.triangles[triangle * 3 + corner]];
            }
        }
    }
}
