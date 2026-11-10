using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit
{
    /// <summary>
    /// Fabric pushed out of the body along its distance field (<see cref="ReFitBodyField"/>, inside and outside from the air
    /// around the body, not from the nearest triangle's normal): the "Fix clipping" pass after a fit
    /// (<see cref="ReFitSettings.declipAfterFit"/>, <see cref="Apply"/>) and the push-out brush of the result page share the
    /// same push (<see cref="Need"/>) and the same sharing with the neighbours (<see cref="Share"/>).
    /// After a fit, each welded group and each triangle centre lacking at least half a millimetre of its room (inside the body
    /// or nearer it than the room) moves out along the field by all it lacks (a centre moves its three corners); the
    /// correction is then smoothed over the fabric a few times, a group never moving less than it needs and a group with its
    /// room never moving at all; the field is read again after each smoothing and the pass stops once nothing lacks room. The
    /// field covers only clusters of the fabric lacking room, at 2.5 mm cells, and its way out is checked against the nearest
    /// skin. Small pieces (a chain's links, a button) move as one. Last, a push leaving a point deeper by the nearest skin is
    /// dropped, and the quality guard shrinks every push that would leave the fabric worse than the fit did: a spike, a
    /// triangle turned over, an edge stretched past <see cref="StretchRatio"/> (see <see cref="Guard"/>). Thread safe: no
    /// Unity object is touched.
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
        /// <summary>The quality guard: edges outside this ratio of their authored length (as the harness counts a stretched triangle).</summary>
        public const float StretchRatio = 2f;
        // A group is pushed at most this far (deeper, the field has no way out: beyond its band it only knows inside).
        private const float MaxPush = .03f;
        // Lacking less room than this is having it.
        private const float Tolerance = .00005f;
        private const float SmoothingStrength = .5f;
        // Fabric starts moving when it lacks this much room (then it goes all the way): less is clearance, not clipping, and on
        // tight clothing that sits at its room everywhere it took fields over the whole garment (minutes).
        private const float MinNeed = .0005f;
        // Points nearer the skin than their room less this (nearest skin, exact), or inside it by its normal, need the field: the
        // trilinear field reads a fraction of a millimetre off the exact distance.
        private const float CandidateSlack = .0003f;
        // Each cluster's field reaches this far around it (more than the band and any push but the deepest).
        private const float ClusterMargin = .02f;
        // The nearest skin within this checks the field's way out.
        private const float TrustReach = .02f;
        // The fields: over clusters of the fabric near the skin (buckets this size joined when they touch), each split along its
        // longest side until it holds at most this many cells of FieldCell, so the cells stay fine over a whole catsuit (one field
        // over it grew them to 4 mm, whose trilinear distances are millimetres off).
        private const float ClusterBucket = .06f;
        private const int ClusterCells = 2000000;
        // The quality guard: a vertex moving this much unlike its neighbours is a spike (as the harness counts them); a push may
        // bring one no nearer than the limit.
        private const float SpikeCount = .005f, SpikeLimit = .0045f;
        // Rounds halving the pushes; then rounds dropping them until nothing is worse (at most this many more: each drops at least
        // one push, and none left is the fit as it was).
        private const int GuardRounds = 6, GuardDropRounds = 200;

        /// <summary>What a pass did (the engine's "declip" report line).</summary>
        public sealed class Result
        {
            /// <summary>Groups lacking room (inside the body or within their room) before the pass, and moved by it.</summary>
            public int lacking, moved;
            /// <summary>Triangle centres lacking room before the pass.</summary>
            public int triangles;
            /// <summary>Groups inside the body (signed distance below zero) before and after.</summary>
            public int insideBefore, insideAfter;
            public float deepestBeforeMm, deepestAfterMm;
            /// <summary>Groups whose push the quality guard shrank or dropped.</summary>
            public int guarded;
            /// <summary>Small pieces moved as one.</summary>
            public int pieces;
            /// <summary>Pushes dropped because the nearest skin said they went in.</summary>
            public int inward;
            public int iterations;
            public long milliseconds;
            public string field;

            public override string ToString() =>
                lacking == 0
                    ? $"Fix clipping: nothing lies in the body or closer to it than its room ({milliseconds} ms)."
                    : $"Fix clipping: {lacking} fabric points and {triangles} triangle centres lacked room ({insideBefore} points in the body, " +
                      $"{deepestBeforeMm:0.0} mm at most); {moved} moved out ({pieces} small pieces as one, {iterations} smoothing passes), " +
                      $"{insideAfter} left in the body ({deepestAfterMm:0.0} mm at most), {guarded} held back by the quality guard, " +
                      $"{inward} going in dropped; " +
                      $"{milliseconds} ms, {field}.";
        }

        /// <summary>
        /// The way out of the body at <paramref name="point"/> (unit, along the field's gradient) and how far it must go to
        /// lie <paramref name="room"/> out (<paramref name="need"/>; zero or less: it has its room). Zero vector where the
        /// field has no gradient (deeper than its band) though <paramref name="need"/> is positive.
        /// </summary>
        public static Vector3 Need(ReFitBodyField field, Vector3 point, float room, out float need)
        {
            float distance = field.Sample(point, out var gradient);
            return Way(distance, gradient, room, out need);
        }

        private static Vector3 Way(float distance, Vector3 gradient, float room, out float need)
        {
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
        public static ReFitBodyField Field(MeshSnapshot body, MeshBvh index, Bounds fabric, CancellationToken cancellation) =>
            Field(body, index, fabric, FieldMargin, cancellation);

        private static ReFitBodyField Field(MeshSnapshot body, MeshBvh index, Bounds fabric, float margin, CancellationToken cancellation)
        {
            fabric.Expand(2f * margin);
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
        /// The room of each triangle centre (the asset's triangles as welded groups, degenerate ones left out, in order):
        /// <paramref name="room"/>, or, with <paramref name="keepAuthored"/>, no more than the authored centre had over the body it
        /// was made on (nearest skin, signed by its normal): a coarse face sagged into a curved body as made keeps that sag.
        /// </summary>
        public static float[] CentreRooms(MeshSnapshot asset, MeshSnapshot madeOn, MeshBvh madeOnIndex, float room, bool keepAuthored)
        {
            var triangles = GroupTriangles(asset);
            var rooms = new float[triangles.Length / 3];
            Parallel.For(0, rooms.Length, t =>
            {
                rooms[t] = room;
                if (!keepAuthored || madeOn == null || madeOnIndex == null) return;
                var centre = (asset.worldVertices[asset.groupRep[triangles[t * 3]]] + asset.worldVertices[asset.groupRep[triangles[t * 3 + 1]]] +
                              asset.worldVertices[asset.groupRep[triangles[t * 3 + 2]]]) / 3f;
                var hit = madeOnIndex.ClosestPoint(centre, MaxPush);
                if (!hit.Found) return;
                bool under = Vector3.Dot(centre - hit.Position, madeOn.BaryNormal(hit.TriangleOrNone, hit.Bary)) < 0f;
                rooms[t] = Mathf.Min(room, under ? -hit.Distance : hit.Distance);
            });
            return rooms;
        }

        /// <summary>
        /// Pushes the fitted fabric (<paramref name="asset"/>'s welded groups at their authored world position plus
        /// <paramref name="groupDeltas"/>) out of <paramref name="body"/> to each group's <paramref name="rooms"/>, in place.
        /// Triangle centres count too: a centre lacking room pushes its three corners, so a face the skin comes through between
        /// its vertices is lifted whole, and a sunk vertex takes its faces along instead of standing out alone.
        /// </summary>
        /// <param name="index">The body's hierarchy (reused from the fit).</param>
        /// <param name="centreRooms">The room of each triangle centre (<see cref="CentreRooms"/>), or null: its corners' least room.</param>
        public static Result Apply(MeshSnapshot asset, Vector3[] groupDeltas, MeshSnapshot body, MeshBvh index, float[] rooms,
            float[] centreRooms, int iterations, CancellationToken cancellation)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new Result();
            int count = asset.GroupCount;
            var adjacency = asset.groupAdjacency;
            var position = new Vector3[count];
            for (int g = 0; g < count; g++) position[g] = asset.worldVertices[asset.groupRep[g]] + groupDeltas[g];
            var allTriangles = GroupTriangles(asset);
            var triangles = allTriangles;
            if (centreRooms == null || centreRooms.Length != allTriangles.Length / 3)
            {
                centreRooms = new float[allTriangles.Length / 3];
                for (int t = 0; t < centreRooms.Length; t++)
                    centreRooms[t] = Mathf.Min(rooms[allTriangles[t * 3]], Mathf.Min(rooms[allTriangles[t * 3 + 1]], rooms[allTriangles[t * 3 + 2]]));
            }
            int triangleCount = triangles.Length / 3;

            // Groups and triangle centres inside the skin by its normal, or about as near it as their room: only they need the
            // field, over their clusters.
            var candidate = new bool[count];
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, g =>
                candidate[g] = Near(body, index, position[g], rooms[g]));
            var activeTriangle = new bool[triangleCount];
            Parallel.For(0, triangleCount, new ParallelOptions { CancellationToken = cancellation }, t =>
                activeTriangle[t] = Near(body, index, Centre(triangles, position, null, t), centreRooms[t]));
            var near = new List<Vector3>();
            for (int g = 0; g < count; g++) if (candidate[g]) near.Add(position[g]);
            for (int t = 0; t < triangleCount; t++) if (activeTriangle[t]) near.Add(Centre(triangles, position, null, t));
            if (near.Count == 0) { result.milliseconds = watch.ElapsedMilliseconds; return result; }
            var fields = BuildFields(body, index, near, cancellation);
            fields.body = body;
            fields.index = index;
            result.field = fields.timing;

            // The push: each group lacking room moves out by what it lacks, and the corners of each triangle whose centre lacks
            // room by what the centre lacks. Groups with their room stay (pinned).
            var correction = new Vector3[count];
            var outward = new Vector3[count];
            var floor = new float[count];
            var pinned = new bool[count];
            var deepest = new float[count];
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, g =>
            {
                if (!candidate[g]) { pinned[g] = true; return; }
                var way = fields.Need(position[g], rooms[g], out float need);
                deepest[g] = Mathf.Max(0f, need - rooms[g]);
                if (need <= MinNeed) { pinned[g] = true; return; }
                outward[g] = way;
                floor[g] = way == Vector3.zero ? 0f : Mathf.Min(need, MaxPush);
                correction[g] = way * floor[g];
            });
            var triangleWay = new Vector3[triangleCount];
            var triangleNeed = new float[triangleCount];
            CentreNeeds(triangles, position, correction, centreRooms, activeTriangle, fields, triangleWay, triangleNeed);
            for (int t = 0; t < triangleCount; t++)
            {
                if (triangleNeed[t] <= MinNeed) continue;
                result.triangles++;
                if (triangleWay[t] == Vector3.zero) continue;
                float need = Mathf.Min(triangleNeed[t], MaxPush);
                for (int k = 0; k < 3; k++)
                {
                    int g = triangles[t * 3 + k];
                    if (need <= floor[g]) continue;
                    pinned[g] = false;
                    floor[g] = need;
                    outward[g] = triangleWay[t];
                    correction[g] = outward[g] * need;
                }
            }
            float deepestBefore = 0f;
            for (int g = 0; g < count; g++)
            {
                if (pinned[g]) continue;
                result.lacking++;
                if (fields.Sample(position[g], out _) < 0f) { result.insideBefore++; deepestBefore = Mathf.Max(deepestBefore, deepest[g]); }
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
            var extra = new float[count];
            var extraWay = new Vector3[count];
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
                Array.Clear(extra, 0, count);
                var lackingNow = new int[count];
                Parallel.For(0, count, g =>
                {
                    if (pinned[g] || pieceOf[g] >= 0) return;
                    var way = fields.Need(position[g] + correction[g], rooms[g], out float need);
                    if (need <= Tolerance) return;
                    lackingNow[g] = 1;
                    if (way == Vector3.zero) return;
                    extra[g] = need;
                    extraWay[g] = way;
                });
                lacking = 0;
                for (int g = 0; g < count; g++) lacking += lackingNow[g];
                CentreNeeds(triangles, position, correction, centreRooms, activeTriangle, fields, triangleWay, triangleNeed);
                for (int t = 0; t < triangleCount; t++)
                {
                    if (triangleNeed[t] <= Tolerance) continue;
                    lacking++;
                    if (triangleWay[t] == Vector3.zero) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int g = triangles[t * 3 + k];
                        if (pieceOf[g] >= 0 || triangleNeed[t] <= extra[g]) continue;
                        extra[g] = triangleNeed[t];
                        extraWay[g] = triangleWay[t];
                    }
                }
                for (int g = 0; g < count; g++)
                {
                    if (extra[g] <= 0f) continue;
                    pinned[g] = false;
                    correction[g] += extraWay[g] * Mathf.Min(extra[g], MaxPush);
                    if (correction[g].magnitude > MaxPush) correction[g] = correction[g].normalized * MaxPush;
                    float along = correction[g].magnitude;
                    if (along > floor[g]) { floor[g] = along; outward[g] = correction[g] / along; }
                }
                for (int p = 0; p < pieces.Length; p++)
                    if (movingPiece[p] && MovePiece(pieces[p], fields, position, rooms, correction)) lacking++;
            }
            for (int p = 0; p < pieces.Length; p++) if (movingPiece[p]) result.pieces++;

            // No push may leave a point deeper by the nearest skin than the fit did (a field read wrong; pieces move as one).
            var deeper = new bool[count];
            Parallel.For(0, count, g =>
            {
                if (correction[g] == Vector3.zero || pieceOf[g] >= 0) return;
                deeper[g] = fields.Nearest(position[g] + correction[g]) < fields.Nearest(position[g]) - Tolerance;
            });
            for (int g = 0; g < count; g++)
                if (deeper[g]) { correction[g] = Vector3.zero; result.inward++; }
            result.guarded = Guard(asset, groupDeltas, correction, pieceOf, allTriangles);

            float deepestAfter = 0f;
            for (int g = 0; g < count; g++)
            {
                if (correction[g] == Vector3.zero) continue;
                result.moved++;
                groupDeltas[g] += correction[g];
                float distance = fields.Sample(position[g] + correction[g], out _);
                if (distance < 0f) { result.insideAfter++; deepestAfter = Mathf.Max(deepestAfter, -distance); }
            }
            result.deepestAfterMm = deepestAfter * 1000f;
            result.milliseconds = watch.ElapsedMilliseconds;
            return result;
        }

        // The asset's triangles as welded groups (three per triangle), degenerate ones left out.
        private static int[] GroupTriangles(MeshSnapshot asset)
        {
            var list = new List<int>(asset.triangles.Length);
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
            {
                int a = asset.groupOfVertex[asset.triangles[t]], b = asset.groupOfVertex[asset.triangles[t + 1]], c = asset.groupOfVertex[asset.triangles[t + 2]];
                if (a == b || b == c || a == c) continue;
                list.Add(a); list.Add(b); list.Add(c);
            }
            return list.ToArray();
        }

        // Each active triangle's centre, where the fabric is now: how much room it lacks and the way out.
        private static void CentreNeeds(int[] triangles, Vector3[] position, Vector3[] correction, float[] centreRooms, bool[] active, Fields fields,
            Vector3[] way, float[] need)
        {
            Parallel.For(0, triangles.Length / 3, t =>
            {
                need[t] = 0f;
                way[t] = Vector3.zero;
                if (!active[t]) return;
                way[t] = fields.Need(Centre(triangles, position, correction, t), centreRooms[t], out need[t]);
            });
        }

        private static Vector3 Centre(int[] triangles, Vector3[] position, Vector3[] correction, int t)
        {
            int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
            var sum = position[a] + position[b] + position[c];
            if (correction != null) sum += correction[a] + correction[b] + correction[c];
            return sum / 3f;
        }

        // Inside the skin by the nearest skin's normal, or no farther from it than the room and a field cell.
        private static bool Near(MeshSnapshot body, MeshBvh index, Vector3 point, float room)
        {
            var hit = index.ClosestPoint(point, MaxPush);
            if (!hit.Found) return false;
            return hit.Distance < room - MinNeed + CandidateSlack ||
                   Vector3.Dot(point - hit.Position, body.BaryNormal(hit.TriangleOrNone, hit.Bary)) < 0f;
        }

        /// <summary>
        /// Shrinks pushes that would leave the fabric worse than the fit did, as the case harness measures it: a vertex moving
        /// <see cref="SpikeCount"/> unlike its neighbours (a spike), a triangle turned over against the authored clothing, an
        /// edge outside half to twice its authored length. Every push around such a place is halved, round after round, then
        /// dropped until no such place is left: no push at all is the fit as it was, so the guard always ends there at worst.
        /// Returns the groups held back.
        /// </summary>
        private static int Guard(MeshSnapshot asset, Vector3[] deltas, Vector3[] correction, int[] pieceOf, int[] triangles)
        {
            int count = correction.Length;
            var adjacency = asset.groupAdjacency;
            var authored = new Vector3[count];
            for (int g = 0; g < count; g++) authored[g] = asset.worldVertices[asset.groupRep[g]];
            // How far each vertex moved unlike its neighbours in the fit, and how far a push may take that: spikes already there
            // stay spikes whatever the push does.
            var limit = new float[count];
            for (int g = 0; g < count; g++)
            {
                var neighbours = adjacency[g];
                if (neighbours == null || neighbours.Count == 0) { limit[g] = float.MaxValue; continue; }
                var mean = Vector3.zero;
                foreach (int n in neighbours) mean += deltas[n];
                float deviation = (deltas[g] - mean / neighbours.Count).magnitude;
                limit[g] = deviation > SpikeCount ? float.MaxValue : Mathf.Max(SpikeLimit, deviation);
            }
            int triangleCount = triangles.Length / 3;
            var turnedBefore = new bool[triangleCount];
            var stretchedBefore = new bool[triangleCount];
            for (int t = 0; t < triangleCount; t++)
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                turnedBefore[t] = Turned(authored, deltas, null, a, b, c);
                stretchedBefore[t] = Stretched(authored, deltas, null, a, b, c);
            }
            var held = new bool[count];
            var flagged = new bool[count];
            for (int round = 0; round < GuardRounds + GuardDropRounds; round++)
            {
                Array.Clear(flagged, 0, count);
                bool any = false;
                for (int g = 0; g < count; g++)
                {
                    var neighbours = adjacency[g];
                    if (limit[g] == float.MaxValue || neighbours == null) continue;
                    bool moved = correction[g] != Vector3.zero;
                    var mean = Vector3.zero;
                    foreach (int n in neighbours) { mean += deltas[n] + correction[n]; moved |= correction[n] != Vector3.zero; }
                    if (!moved) continue;
                    if ((deltas[g] + correction[g] - mean / neighbours.Count).magnitude <= limit[g]) continue;
                    any = true;
                    flagged[g] = true;
                    foreach (int n in neighbours) flagged[n] = true;
                }
                for (int t = 0; t < triangleCount; t++)
                {
                    int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                    if (correction[a] == Vector3.zero && correction[b] == Vector3.zero && correction[c] == Vector3.zero) continue;
                    if ((!turnedBefore[t] && Turned(authored, deltas, correction, a, b, c)) ||
                        (!stretchedBefore[t] && Stretched(authored, deltas, correction, a, b, c)))
                    {
                        any = true;
                        flagged[a] = flagged[b] = flagged[c] = true;
                    }
                }
                if (!any) break;
                float scale = round < GuardRounds ? .5f : 0f;
                for (int g = 0; g < count; g++)
                {
                    if (!flagged[g] || pieceOf[g] >= 0 || correction[g] == Vector3.zero) continue;
                    correction[g] *= scale;
                    held[g] = true;
                }
            }
            int heldCount = 0;
            for (int g = 0; g < count; g++) if (held[g]) heldCount++;
            return heldCount;
        }

        // The triangle a, b, c faces against the authored clothing once moved by deltas (and correction).
        private static bool Turned(Vector3[] authored, Vector3[] deltas, Vector3[] correction, int a, int b, int c)
        {
            var normal = Vector3.Cross(authored[b] - authored[a], authored[c] - authored[a]);
            Vector3 pa = authored[a] + deltas[a], pb = authored[b] + deltas[b], pc = authored[c] + deltas[c];
            if (correction != null) { pa += correction[a]; pb += correction[b]; pc += correction[c]; }
            return Vector3.Dot(normal, Vector3.Cross(pb - pa, pc - pa)) < 0f;
        }

        // An edge of the triangle outside half to twice its authored length once moved (the harness's stretched triangle).
        private static bool Stretched(Vector3[] authored, Vector3[] deltas, Vector3[] correction, int a, int b, int c) =>
            EdgeStretched(authored, deltas, correction, a, b) || EdgeStretched(authored, deltas, correction, b, c) ||
            EdgeStretched(authored, deltas, correction, c, a);

        private static bool EdgeStretched(Vector3[] authored, Vector3[] deltas, Vector3[] correction, int u, int v)
        {
            float length = (authored[u] - authored[v]).magnitude;
            if (length < 1e-6f) return false;
            var pu = authored[u] + deltas[u];
            var pv = authored[v] + deltas[v];
            if (correction != null) { pu += correction[u]; pv += correction[v]; }
            float ratio = (pu - pv).magnitude / length;
            return ratio > StretchRatio || ratio < 1f / StretchRatio;
        }

        // A piece moves as one: by the move of its group lacking the most, then on by what its groups still lack, read again.
        // True when some of it still lacks room.
        private static bool MovePiece(List<int> piece, Fields fields, Vector3[] position, float[] rooms, Vector3[] correction)
        {
            var move = Vector3.zero;
            foreach (int g in piece) if (correction[g].sqrMagnitude > move.sqrMagnitude) move = correction[g];
            for (int round = 0; round < 4; round++)
            {
                float worst = Tolerance;
                var way = Vector3.zero;
                foreach (int g in piece)
                {
                    var w = fields.Need(position[g] + move, rooms[g], out float need);
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
                if (!lacking) { fields.Need(position[g] + move, rooms[g], out float need); lacking = need > Tolerance; }
            }
            return lacking;
        }

        // The body's fields over the fabric lacking room: one per cluster of it.
        private sealed class Fields
        {
            public readonly List<Vector3> min = new List<Vector3>(), max = new List<Vector3>();
            public readonly List<ReFitBodyField> fields = new List<ReFitBodyField>();
            public string timing;
            // The body, to check the field's way out against the nearest skin.
            public MeshSnapshot body;
            public MeshBvh index;

            // The field of the cluster nearest the point (a field covers its cluster and a margin larger than any push).
            public float Sample(Vector3 p, out Vector3 gradient)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < fields.Count && bestDistance > 0f; i++)
                {
                    var outside = Vector3.Max(min[i] - p, Vector3.Max(Vector3.zero, p - max[i]));
                    float distance = outside.sqrMagnitude;
                    if (distance < bestDistance) { bestDistance = distance; best = i; }
                }
                gradient = Vector3.zero;
                return best < 0 ? float.MaxValue : fields[best].Sample(p, out gradient);
            }

            // As the public Need, except that a way out turned against the nearest skin's normal (within TrustReach) is not taken:
            // a small field cut through a thick, layered body (the ThiccWiker, a pair of thighs) can read its inside wrong there,
            // and the push went into the body.
            public Vector3 Need(Vector3 point, float room, out float need)
            {
                float distance = Sample(point, out var gradient);
                var way = Way(distance, gradient, room, out need);
                if (way == Vector3.zero || index == null) return way;
                var hit = index.ClosestPoint(point, TrustReach);
                if (hit.Found && Vector3.Dot(way, body.BaryNormal(hit.TriangleOrNone, hit.Bary)) < 0f) return Vector3.zero;
                return way;
            }

            // Signed distance to the nearest skin (by its normal), up to MaxPush; MaxPush where none is that near.
            public float Nearest(Vector3 point)
            {
                var hit = index.ClosestPoint(point, MaxPush);
                if (!hit.Found) return MaxPush;
                return Vector3.Dot(point - hit.Position, body.BaryNormal(hit.TriangleOrNone, hit.Bary)) < 0f ? -hit.Distance : hit.Distance;
            }
        }

        private static Fields BuildFields(MeshSnapshot body, MeshBvh index, List<Vector3> points, CancellationToken cancellation)
        {
            var set = new Fields();
            var parts = new List<List<Vector3>>();
            foreach (var cluster in Clusters(points)) Split(cluster, parts);
            long cells = 0;
            float finest = float.MaxValue, coarsest = 0f;
            long slowest = -1;
            string slowestTiming = null;
            foreach (var part in parts)
            {
                cancellation.ThrowIfCancellationRequested();
                var bounds = new Bounds(part[0], Vector3.zero);
                foreach (var p in part) bounds.Encapsulate(p);
                var started = System.Diagnostics.Stopwatch.StartNew();
                var field = Field(body, index, bounds, ClusterMargin, cancellation);
                if (started.ElapsedMilliseconds > slowest) { slowest = started.ElapsedMilliseconds; slowestTiming = field.Timing; }
                set.min.Add(bounds.min);
                set.max.Add(bounds.max);
                set.fields.Add(field);
                cells += field.Cells;
                finest = Mathf.Min(finest, field.cell);
                coarsest = Mathf.Max(coarsest, field.cell);
            }
            set.timing = $"{parts.Count} field{(parts.Count == 1 ? "" : "s")} of {cells / 1000000f:0.0} M cells, {finest * 1000f:0.0}" +
                         (coarsest > finest + 1e-5f ? $" to {coarsest * 1000f:0.0}" : "") + $" mm (slowest {slowest} ms: {slowestTiming})";
            return set;
        }

        // Points in buckets of ClusterBucket, buckets touching (26 neighbours) joined.
        private static List<List<Vector3>> Clusters(List<Vector3> points)
        {
            var buckets = new Dictionary<Vector3Int, List<Vector3>>();
            foreach (var p in points)
            {
                var key = new Vector3Int(Mathf.FloorToInt(p.x / ClusterBucket), Mathf.FloorToInt(p.y / ClusterBucket), Mathf.FloorToInt(p.z / ClusterBucket));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Vector3>();
                list.Add(p);
            }
            var clusters = new List<List<Vector3>>();
            var seen = new HashSet<Vector3Int>();
            var open = new Stack<Vector3Int>();
            foreach (var start in buckets.Keys)
            {
                if (!seen.Add(start)) continue;
                var cluster = new List<Vector3>();
                open.Push(start);
                while (open.Count > 0)
                {
                    var key = open.Pop();
                    cluster.AddRange(buckets[key]);
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                var next = new Vector3Int(key.x + dx, key.y + dy, key.z + dz);
                                if (buckets.ContainsKey(next) && seen.Add(next)) open.Push(next);
                            }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        // A cluster split in two along its longest side (at its middle point) until its field holds at most ClusterCells cells.
        private static void Split(List<Vector3> points, List<List<Vector3>> parts)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var p in points) bounds.Encapsulate(p);
            var size = bounds.size + Vector3.one * (2f * ClusterMargin);
            double cells = (double)(size.x / FieldCell) * (size.y / FieldCell) * (size.z / FieldCell);
            if (cells <= ClusterCells || points.Count < 16) { parts.Add(points); return; }
            var extent = bounds.size;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            points.Sort((a, b) => a[axis].CompareTo(b[axis]));
            int half = points.Count / 2;
            Split(points.GetRange(0, half), parts);
            Split(points.GetRange(half, points.Count - half), parts);
        }
    }
}
