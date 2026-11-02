using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Fabric pushed out of the body along its distance field (<see cref="ReFitBodyField"/>, inside and outside from the air
    /// around the body, not from the nearest triangle's normal): the "Fix clipping" pass after a fit
    /// (<see cref="ReFitSettings.declipAfterFit"/>, <see cref="Apply"/>) and the push-out brush of the result page share the
    /// same push (<see cref="Need"/>) and the same sharing with the neighbours (<see cref="Share"/>).
    /// After a fit, each welded group lying inside the body or closer to it than its room moves out along the field by what it
    /// lacks; the correction is then smoothed over the fabric a few times, a group never moving less than it needs and a group
    /// with its room never moving at all; the field is read again after each smoothing and the pass stops once nothing lacks
    /// room. Small pieces (a chain's links, a button) move as one. A group whose move would stretch an edge to more than
    /// <see cref="StretchRatio"/> times its authored length (or less than its inverse) moves with its neighbours instead, and
    /// is counted. Thread safe: no Unity object is touched.
    /// The idea (intersections pushed outward, then Laplacian smoothing, over a body SDF) follows the MIT VRCloth-Declipper
    /// (dev.omelette_ak.vrcloth-declipper); no code of it is used.
    /// </summary>
    public static class ReFitDeclip
    {
        /// <summary>The room left between the skin and the fabric by default (metres).</summary>
        public const float DefaultRoom = .001f;
        /// <summary>The field: cells of this size (grown to stay under <see cref="FieldCells"/>) over the fabric and this margin.</summary>
        public const float FieldCell = .0025f, FieldMargin = .04f;
        public const int FieldCells = 4000000;
        /// <summary>Smoothing passes of the correction after the fit.</summary>
        public const int DefaultIterations = 3;
        /// <summary>The seam guard: edges the push would stretch past this ratio of their authored length (as the harness counts a stretched triangle).</summary>
        public const float StretchRatio = 2f;
        // A group is pushed at most this far (deeper, the field has no way out: beyond its band it only knows inside).
        private const float MaxPush = .03f;
        // Lacking less room than this is having it.
        private const float Tolerance = .00005f;
        private const float SmoothingStrength = .5f;
        // Groups farther than this from the skin (and outside by its normal) need no field.
        private const float CandidateReach = .005f;

        /// <summary>What a pass did (the engine's "declip" report line).</summary>
        public sealed class Result
        {
            /// <summary>Groups lacking room (inside the body or within their room) before the pass, and moved by it.</summary>
            public int lacking, moved;
            /// <summary>Groups inside the body (signed distance below zero) before and after.</summary>
            public int insideBefore, insideAfter;
            public float deepestBeforeMm, deepestAfterMm;
            /// <summary>Groups the seam guard moved with their neighbours instead of their own push.</summary>
            public int guarded;
            /// <summary>Small pieces moved as one.</summary>
            public int pieces;
            public int iterations;
            public long milliseconds;
            public string field;

            public override string ToString() =>
                lacking == 0
                    ? $"Fix clipping: nothing lies in the body or closer to it than its room ({milliseconds} ms)."
                    : $"Fix clipping: {lacking} fabric points lacked room ({insideBefore} in the body, {deepestBeforeMm:0.0} mm at most); {moved} moved out " +
                      $"({pieces} small pieces as one, {iterations} smoothing passes), {insideAfter} left in the body ({deepestAfterMm:0.0} mm at most), " +
                      $"{guarded} held by the seam guard; {milliseconds} ms, field {field}.";
        }

        /// <summary>
        /// The way out of the body at <paramref name="point"/> (unit, along the field's gradient) and how far it must go to
        /// lie <paramref name="room"/> out (<paramref name="need"/>; zero or less: it has its room). Zero vector where the
        /// field has no gradient (deeper than its band) though <paramref name="need"/> is positive.
        /// </summary>
        public static Vector3 Need(ReFitBodyField field, Vector3 point, float room, out float need)
        {
            float distance = field.Sample(point, out var gradient);
            need = room - distance;
            if (need <= 0f || gradient.sqrMagnitude < 1e-8f) return Vector3.zero;
            return gradient.normalized;
        }

        /// <summary>
        /// A group's move shared with its neighbours: <paramref name="strength"/> of the way to their mean move, but never less
        /// than <paramref name="floor"/> along <paramref name="outward"/> (a point in the body still leaves it).
        /// </summary>
        public static Vector3 Share(Vector3 own, Vector3 neighbours, float strength, Vector3 outward, float floor)
        {
            var blended = Vector3.Lerp(own, neighbours, strength);
            float along = Vector3.Dot(blended, outward);
            return along < floor ? blended + outward * (floor - along) : blended;
        }

        /// <summary>The body's field over <paramref name="fabric"/> (world bounds of the fabric) and a margin, unsealed.</summary>
        public static ReFitBodyField Field(MeshSnapshot body, SurfaceBvh index, Bounds fabric, CancellationToken cancellation)
        {
            fabric.Expand(2f * FieldMargin);
            // Unsealed: tight trousers wrap each thigh, and a sealed gap between them would push the inner-leg fabric out of it.
            return ReFitBodyField.Build(body, index, fabric, FieldCell, FieldCells, cancellation, 0);
        }

        /// <summary>
        /// The room each group keeps from the skin: <paramref name="room"/>, or, with <paramref name="keepAuthored"/>, no more
        /// than the clothing had over the body it was made on (its welded group's gap over its binding there; negative under
        /// it): a strap pressing in, a collar under fluff, stays as made, as the cloth fit keeps it.
        /// </summary>
        public static float[] Rooms(MeshSnapshot asset, SurfaceBinding[] bindings, MeshSnapshot madeOn, float room, bool keepAuthored)
        {
            var rooms = new float[asset.GroupCount];
            for (int g = 0; g < rooms.Length; g++)
            {
                rooms[g] = room;
                if (!keepAuthored || bindings == null || madeOn == null || g >= bindings.Length || !bindings[g].valid) continue;
                var p = asset.worldVertices[asset.groupRep[g]];
                float gap = Vector3.Dot(p - bindings[g].point, madeOn.BaryNormal(bindings[g].triangle, bindings[g].bary));
                rooms[g] = Mathf.Min(room, gap);
            }
            return rooms;
        }

        /// <summary>
        /// Pushes the fitted fabric (<paramref name="asset"/>'s welded groups at their authored world position plus
        /// <paramref name="groupDeltas"/>) out of <paramref name="body"/> to each group's <paramref name="rooms"/>, in place.
        /// </summary>
        /// <param name="index">The body's hierarchy (reused from the fit).</param>
        public static Result Apply(MeshSnapshot asset, Vector3[] groupDeltas, MeshSnapshot body, SurfaceBvh index, float[] rooms,
            int iterations, CancellationToken cancellation)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new Result();
            int count = asset.GroupCount;
            var adjacency = asset.groupAdjacency;
            var position = new Vector3[count];
            for (int g = 0; g < count; g++) position[g] = asset.worldVertices[asset.groupRep[g]] + groupDeltas[g];

            // Groups near the skin (either side) or inside it by its normal: only they need the field, over their bounds.
            var candidate = new bool[count];
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, g =>
            {
                var hit = index.ClosestPoint(position[g], MaxPush);
                if (!hit.found) return;
                candidate[g] = hit.distance < Mathf.Max(0f, rooms[g]) + CandidateReach ||
                               Vector3.Dot(position[g] - hit.position, body.BaryNormal(hit.triangle, hit.bary)) < 0f;
            });
            Bounds bounds = default;
            bool any = false;
            for (int g = 0; g < count; g++)
            {
                if (!candidate[g]) continue;
                if (!any) { bounds = new Bounds(position[g], Vector3.zero); any = true; }
                else bounds.Encapsulate(position[g]);
            }
            if (!any) { result.milliseconds = watch.ElapsedMilliseconds; return result; }
            var field = Field(body, index, bounds, cancellation);
            result.field = field.Timing;

            // The push: each group lacking room moves out by what it lacks. Groups with their room stay (pinned).
            var correction = new Vector3[count];
            var outward = new Vector3[count];
            var floor = new float[count];
            var pinned = new bool[count];
            var deepest = new float[count];
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, g =>
            {
                if (!candidate[g]) { pinned[g] = true; return; }
                var way = Need(field, position[g], rooms[g], out float need);
                deepest[g] = Mathf.Max(0f, need - rooms[g]);
                if (need <= Tolerance) { pinned[g] = true; return; }
                outward[g] = way;
                floor[g] = way == Vector3.zero ? 0f : Mathf.Min(need, MaxPush);
                correction[g] = way * floor[g];
            });
            float deepestBefore = 0f;
            for (int g = 0; g < count; g++)
            {
                if (pinned[g]) continue;
                result.lacking++;
                if (field.Sample(position[g]) < 0f) { result.insideBefore++; deepestBefore = Mathf.Max(deepestBefore, deepest[g]); }
            }
            result.deepestBeforeMm = deepestBefore * 1000f;
            if (result.lacking == 0) { result.milliseconds = watch.ElapsedMilliseconds; return result; }

            // Small pieces move as one; pieces with nothing lacking room stay.
            var pieces = RigidFit.Pieces(asset, ReFitClothFit.DetailExtent, out var pieceOf);
            var movingPiece = new bool[pieces.Length];
            for (int p = 0; p < pieces.Length; p++)
                foreach (int g in pieces[p])
                    if (!pinned[g]) { movingPiece[p] = true; break; }

            var buffer = new Vector3[count];
            int lacking = result.lacking;
            for (int it = 0; it < Mathf.Max(1, iterations) && lacking > 0; it++)
            {
                cancellation.ThrowIfCancellationRequested();
                result.iterations++;
                // Smoothed over the fabric: a pinned group never moves, a group never less than it needs.
                Parallel.For(0, count, g =>
                {
                    if (pinned[g] || pieceOf[g] >= 0) { buffer[g] = correction[g]; return; }
                    var neighbours = adjacency[g];
                    if (neighbours == null || neighbours.Count == 0) { buffer[g] = correction[g]; return; }
                    var mean = Vector3.zero;
                    foreach (int n in neighbours) mean += correction[n];
                    buffer[g] = Share(correction[g], mean / neighbours.Count, SmoothingStrength, outward[g], floor[g]);
                });
                Array.Copy(buffer, correction, count);
                // Read again where the fabric is now: what still lacks room is pushed on (the gradient is a line, the skin curves).
                lacking = 0;
                var lackingNow = new int[count];
                Parallel.For(0, count, g =>
                {
                    if (pinned[g] || pieceOf[g] >= 0) return;
                    var way = Need(field, position[g] + correction[g], rooms[g], out float need);
                    if (need <= Tolerance) return;
                    lackingNow[g] = 1;
                    if (way == Vector3.zero) return;
                    correction[g] += way * Mathf.Min(need, MaxPush);
                    if (correction[g].magnitude > MaxPush) correction[g] = correction[g].normalized * MaxPush;
                    float along = correction[g].magnitude;
                    if (along > floor[g]) { floor[g] = along; outward[g] = correction[g] / along; }
                });
                for (int g = 0; g < count; g++) lacking += lackingNow[g];
                for (int p = 0; p < pieces.Length; p++)
                    if (movingPiece[p] && MovePiece(pieces[p], field, position, rooms, correction)) lacking++;
            }
            for (int p = 0; p < pieces.Length; p++) if (movingPiece[p]) result.pieces++;

            // Seam guard: a group whose move stretches an edge past the ratio (and more than the fit did) moves with its
            // neighbours instead.
            var authored = new Vector3[count];
            for (int g = 0; g < count; g++) authored[g] = asset.worldVertices[asset.groupRep[g]];
            var guarded = new bool[count];
            for (int pass = 0; pass < 2; pass++)
            {
                var flagged = new List<int>();
                for (int g = 0; g < count; g++)
                {
                    if (correction[g] == Vector3.zero || pieceOf[g] >= 0 || adjacency[g] == null) continue;
                    foreach (int n in adjacency[g])
                        if (Stretched(authored[g], authored[n], position[g], position[n], position[g] + correction[g], position[n] + correction[n]))
                        {
                            flagged.Add(g);
                            break;
                        }
                }
                if (flagged.Count == 0) break;
                foreach (int g in flagged)
                {
                    var mean = Vector3.zero;
                    foreach (int n in adjacency[g]) mean += correction[n];
                    buffer[g] = mean / adjacency[g].Count;
                }
                foreach (int g in flagged) { correction[g] = buffer[g]; guarded[g] = true; }
            }

            float deepestAfter = 0f;
            for (int g = 0; g < count; g++)
            {
                if (guarded[g]) result.guarded++;
                if (correction[g] == Vector3.zero) continue;
                result.moved++;
                groupDeltas[g] += correction[g];
                float distance = field.Sample(position[g] + correction[g]);
                if (distance < 0f) { result.insideAfter++; deepestAfter = Mathf.Max(deepestAfter, -distance); }
            }
            result.deepestAfterMm = deepestAfter * 1000f;
            result.milliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        // A piece moves as one: by the move of its group lacking the most, then on by what its groups still lack, read again.
        // True when some of it still lacks room.
        private static bool MovePiece(List<int> piece, ReFitBodyField field, Vector3[] position, float[] rooms, Vector3[] correction)
        {
            var move = Vector3.zero;
            foreach (int g in piece) if (correction[g].sqrMagnitude > move.sqrMagnitude) move = correction[g];
            for (int round = 0; round < 4; round++)
            {
                float worst = Tolerance;
                var way = Vector3.zero;
                foreach (int g in piece)
                {
                    var w = Need(field, position[g] + move, rooms[g], out float need);
                    if (need > worst && w != Vector3.zero) { worst = need; way = w; }
                }
                if (way == Vector3.zero) break;
                move += way * worst;
                if (move.magnitude > MaxPush) { move = move.normalized * MaxPush; break; }
            }
            bool lacking = false;
            foreach (int g in piece)
            {
                correction[g] = move;
                if (!lacking) { Need(field, position[g] + move, rooms[g], out float need); lacking = need > Tolerance; }
            }
            return lacking;
        }

        // The edge from a to b, authored, fitted and pushed: stretched past the ratio by the push, more than the fit had it.
        private static bool Stretched(Vector3 authoredA, Vector3 authoredB, Vector3 fittedA, Vector3 fittedB, Vector3 pushedA, Vector3 pushedB)
        {
            float length = (authoredA - authoredB).magnitude;
            if (length < 1e-6f) return false;
            float before = (fittedA - fittedB).magnitude / length, after = (pushedA - pushedB).magnitude / length;
            return (after > StretchRatio && after > before * 1.05f) || (after < 1f / StretchRatio && after < before / 1.05f);
        }
    }
}
