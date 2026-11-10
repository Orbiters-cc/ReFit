using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The last step of the different-base coverage: skin of the target showing through the clothing (the fabric lies under
    /// it, turned the same way, straight beneath, inside the same part of the body) pushes the fabric out over it. The push
    /// spreads over the fabric around it like cloth draped over the body, so fabric spans breasts, a belly or buttocks much
    /// larger than the base's instead of tenting at single points. Fabric authored under the skin (a collar under fluff)
    /// stays under it, and openings (head, cuffs closed over a tube) and closed details are not moved by the skin: a closed
    /// ring (a belt, a collar) follows the draped fabric under each part of it, a small closed detail (a button) rides with it
    /// whole. Built for shapes added to a fit later, the fit is where the clothing was made, on the target itself.
    /// </summary>
    internal sealed class ReFitDrape
    {
        // How deep under the skin fabric is looked for.
        private const float Reach = .2f;
        // The room left between the skin and the fabric draped over it.
        private const float Clearance = .004f;
        private const float Facing = .35f;
        private const int Iterations = 64;
        // Iterations spreading the push over the fabric around it; the last ones only push.
        private const int SpreadIterations = 48;
        // Exposed skin left below this depth is noise of coarse fabric triangles over a curved body.
        private const float ExposedTolerance = .001f;
        // How far around the fabric the skin needed at first, the drape may lift it as much (rings of neighbours).
        private const int NeedRings = 8;
        // The fabric's hierarchy follows it every iteration and is built again this often: its bounds loosen as it moves.
        private const int RebuildEvery = 16;

        private readonly MeshSnapshot asset;
        // Of clothing made for another body: its sleeve and leg points away from the hems, and the skin found through them.
        private readonly int limbGroups;
        private int limbCovered;
        private readonly bool[] authoredUnder, fixedGroups, details, rings;
        // Of clothing made for another body: its separate pieces (a belt, its loops, a buckle), carried with the fabric.
        private readonly List<List<int>> pieces;
        // Where each group was made, on the body it was made for (as authored, or as fitted for shapes added to a fit).
        private readonly Vector3[] made;
        // The target body's vertex neighbours (same topology for its shapes).
        private readonly List<int>[] neighbours;
        // The clothing groups' body regions, or null.
        private readonly BodyRegion[] groupRegions;
        // Of clothing made for another body: the limb of the source each group lay over as made (see LimbFabric), and each target
        // vertex's part of the body, or null. The fabric of one leg or arm is never pushed out over the other's skin: thighs
        // that meet squeeze the inner panel of one leg of a pair of shorts into the other thigh, and pushed out over that thigh
        // it would wrap around it.
        private readonly BodyRegion[] fabricLimbs, skinParts;
        // The fabric's edges between welded groups, each once.
        private readonly List<(int a, int b)> edges;
        // Per target vertex: its counterpart on the source was under the clothing as authored. Other skin (fluff through a
        // collar, a mane larger than the base's, hands and feet) never moves the fabric.
        private readonly bool[] covered;
        // The source's counterpart of target skin: the nearest skin turned the same way within Overlap where the bodies
        // coincide, otherwise the skin met along the normal, inward up to CounterpartReach (the target is larger: breasts,
        // buttocks) or outward up to Shrink (it is smaller). The authored fabric may lie up to AuthoredCover over it.
        private const float Overlap = .01f, CounterpartReach = .3f, Shrink = .05f, AuthoredCover = .05f;
        // Skin in a fold of the source is covered when the authored fabric bridging the fold is this close.
        private const float FoldCover = .08f;
        // A source part out of the clothing this far inside the target has no counterpart there; up to this many are passed.
        private const float Unmatched = .02f;
        private const int CounterpartHops = 3;
        // Normals averaged over this many rings of neighbours: the direction a fold of the body opens to.
        private const int MacroPasses = 10;
        // The averaged normal is used too where it differs this much from the skin's own (dot below).
        private const float MacroDifference = .9f;


        /// <param name="rest">
        /// Per group, from the asset as captured to where it was made on <paramref name="source"/>: null when it was made as
        /// captured; the fit, for shapes added to a fitted clothing (then <paramref name="source"/> is the target).
        /// </param>
        /// <param name="source">The body the clothing was made for, in the staged pose the asset was captured in.</param>
        /// <param name="target">The target body at rest: the skin the drape keeps covered.</param>
        /// <param name="hidden">Clipped groups covered by the clothing's own fabric: left where they are.</param>
        /// <param name="targetRegions">The target body's triangle regions, or null.</param>
        /// <param name="tubes">Closed rings (<see cref="ReFitTubeField"/>), or null.</param>
        /// <param name="madeForAnother">
        /// The source's triangle regions when the clothing was made for another body than <paramref name="source"/> and resized
        /// onto it, else null. An arm or a leg larger than its sleeve or leg came through it there, never meant to, and is
        /// covered by it (away from its hem): fabric lying over a limb of the source, whatever bone moves it (a pair of shorts is
        /// often skinned to the hips down to the knees). The torso keeps the authored rule: skin through a panel there is as
        /// often a hip below a hem or a neck in a collar. Nor is fabric of one limb pushed out over the other's skin. The regions
        /// put the shoulders in the torso: fabric over them is a vest's strap or a sleeve's cap, not a sleeve.
        /// </param>
        public ReFitDrape(MeshSnapshot asset, Vector3[] rest, MeshSnapshot source, MeshBvh sourceIndex, MeshSnapshot target, BodyRegion[] targetRegions,
            BodyRegion[] regions, bool[] tubes, bool[] hidden, BodyRegion[] madeForAnother = null)
        {
            this.asset = asset;
            groupRegions = regions;
            made = new Vector3[asset.GroupCount];
            for (int g = 0; g < made.Length; g++) made[g] = asset.worldVertices[asset.groupRep[g]] + (rest != null ? rest[g] : Vector3.zero);
            var unique = new HashSet<(int, int)>();
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = asset.groupOfVertex[asset.triangles[t + k]], b = asset.groupOfVertex[asset.triangles[t + (k + 1) % 3]];
                    if (a != b) unique.Add((Math.Min(a, b), Math.Max(a, b)));
                }
            edges = new List<(int, int)>(unique);
            details = ReFitSurfaceCoverage.DetailGroups(asset, tubes);
            rings = tubes != null ? (bool[])tubes.Clone() : new bool[asset.GroupCount];
            pieces = madeForAnother != null ? ReFitSurfaceCoverage.Pieces(asset, details) : new List<List<int>>();
            fixedGroups = ReFitSurfaceCoverage.ProtectOpenings(asset, regions, details);
            if (hidden != null)
                for (int g = 0; g < hidden.Length && g < fixedGroups.Length; g++) fixedGroups[g] |= hidden[g];
            authoredUnder = new bool[asset.GroupCount];
            covered = new bool[target.worldVertices.Length];
            neighbours = Neighbours(target);
            if (source == null || sourceIndex == null) return;
            var madeVertices = asset.worldVertices;
            var madeNormals = asset.worldNormals;
            if (rest != null)
            {
                madeVertices = new Vector3[asset.worldVertices.Length];
                for (int i = 0; i < madeVertices.Length; i++) madeVertices[i] = asset.worldVertices[i] + rest[asset.groupOfVertex[i]];
                madeNormals = VertexNormals(madeVertices, asset.triangles);
            }
            var under = authoredUnder;
            var targetIndex = target.Index();
            // Of clothing made for another body, a sleeve or a leg away from its hem never stays under the skin.
            var fabricPart = madeForAnother != null ? LimbFabric(asset, made, source, sourceIndex, madeForAnother) : null;
            bool[] limbFabric = null;
            if (fabricPart != null)
            {
                limbFabric = new bool[fabricPart.Length];
                for (int g = 0; g < fabricPart.Length; g++) limbFabric[g] = IsLimb(fabricPart[g]);
            }
            if (limbFabric != null) foreach (bool limb in limbFabric) if (limb) limbGroups++;
            fabricLimbs = fabricPart;
            Parallel.For(0, asset.GroupCount, g =>
            {
                if (limbFabric != null && limbFabric[g]) return;
                int rep = asset.groupRep[g];
                var normal = madeNormals[rep];
                if (normal.sqrMagnitude < 1e-12f) return;
                // Out of the fabric, the first skin met is the way out of the body: the fabric was made under it.
                var hit = sourceIndex.Raycast(madeVertices[rep], normal, Reach);
                // Skin the target lacks there (the base's tail lying over the back) does not keep the fabric under it.
                under[g] = hit.Found && Vector3.Dot(source.FaceNormal(hit.TriangleOrNone), normal) > 0 &&
                           targetIndex.ClosestPoint(hit.Position, Unmatched).Found;
            });
            var authored = new MeshSnapshot { worldVertices = madeVertices, triangles = asset.triangles };
            var authoredNormals = authored.CacheFaceNormals();
            var sourceNormals = source.FaceNormals ?? source.CacheFaceNormals();
            var authoredIndex = MeshSnapshot.BuildIndex(authored);
            // Source skin at a point, turned up: under the fabric as authored, and not through it (fluff, a tail). Of clothing
            // made for another body, skin of a limb through the fabric of that limb is covered too.
            bool Covered(Vector3 position, Vector3 up, BodyRegion region)
            {
                var facing = SurfaceRules.Facing(authoredNormals, up, Facing);
                bool under = authoredIndex.Raycast(position + up * .0005f, up, AuthoredCover, true, facing).Found;
                // In a fold of the body (the gluteal crease) the skin faces other skin, and the fabric bridges the fold.
                if (!under && sourceIndex.Raycast(position + up * .001f, up, AuthoredCover, true, SurfaceRules.Against(sourceNormals, up)).Found)
                    under = authoredIndex.ClosestPoint(position, FoldCover).Found;
                if (!under && limbFabric != null && IsLimb(region) && Through(position, up, facing, region))
                {
                    Interlocked.Increment(ref limbCovered);
                    return true;
                }
                if (!under || !authoredIndex.Raycast(position - up * .0005f, -up, Reach, true, facing).Found) return under;
                // Through the fabric where another part of the source hides it, one the target lacks (the base's tail
                // over the gluteal crease): the target shows that skin, so it is covered.
                var over = sourceIndex.Raycast(position + up * .001f, up, AuthoredCover, true, SurfaceRules.Against(sourceNormals, up));
                return over.Found && !targetIndex.ClosestPoint(over.Position, Unmatched).Found;
            }
            // Through the fabric: fabric over the same limb, away from its hem, lies beneath the skin turned its way, inside the limb.
            bool Through(Vector3 position, Vector3 up, in TriangleRule facing, BodyRegion region)
            {
                var origin = position - up * .0005f;
                var fabric = authoredIndex.Raycast(origin, -up, LimbReach, true, facing);
                if (!fabric.Found || sourceIndex.Raycast(origin, -up, fabric.Distance, true, SurfaceRules.Against(sourceNormals, up)).Found) return false;
                int t3 = fabric.TriangleOrNone * 3;
                for (int k = 0; k < 3; k++)
                    if (fabricPart[asset.groupOfVertex[asset.triangles[t3 + k]]] != region) return false;
                return true;
            }
            var cover = covered;
            var enclosed = target.HiddenVertices();
            var macro = MacroNormals(target.worldNormals, neighbours);
            var decided = new bool[cover.Length];
            var head = new bool[cover.Length];
            // Each target vertex's part of the body (one of its triangles'), for the limbs' fabric.
            var part = skinParts = new BodyRegion[cover.Length];
            for (int v = 0; v < part.Length; v++) part[v] = BodyRegion.Unknown;
            if (targetRegions != null)
                for (int t = 0; t < targetRegions.Length; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int v = target.triangles[t * 3 + k];
                        if (targetRegions[t] == BodyRegion.Head) head[v] = true;
                        if (part[v] == BodyRegion.Unknown) part[v] = targetRegions[t];
                    }
            Parallel.For(0, cover.Length, i =>
            {
                if (enclosed != null && enclosed[i]) return;
                var point = target.worldVertices[i];
                var normal = target.worldNormals[i];
                var own = CounterpartCovered(point, normal, part[i]);
                // A wall of a fold (the gluteal crease) is seen along the fold's opening: the normal of the skin around it.
                var opening = own != true && Vector3.Dot(macro[i], normal) < MacroDifference && FoldWall(target, targetIndex, i)
                    ? CounterpartCovered(point, macro[i], part[i]) : null;
                cover[i] = own == true || opening == true;
                // Without a counterpart, skin of the head (a mane larger than the base's) is out of the clothing; other skin
                // (buttocks reaching far behind the base's) takes the decision of the skin around it.
                decided[i] = own.HasValue || opening.HasValue || head[i];
            });
            Inherit(cover, decided, enclosed, neighbours);

            // Whether the source's counterpart of target skin (at point, turned normal) was covered, null without one.
            bool? CounterpartCovered(Vector3 point, Vector3 normal, BodyRegion region)
            {
                var same = TriangleRule.All.Facing(source.KnownFaceNormals(), normal, .5f);
                var counterpart = sourceIndex.ClosestPoint(point, Overlap, same);
                if (counterpart.Found) return Covered(counterpart.Position, source.FaceNormal(counterpart.TriangleOrNone), region);
                // The target is smaller here: the source skin just outside it.
                var outward = sourceIndex.Raycast(point, normal, Shrink, true, same);
                var inward = sourceIndex.Raycast(point, -normal, CounterpartReach, true, same);
                if (outward.Found && (!inward.Found || outward.Distance < inward.Distance))
                    return Covered(outward.Position, source.FaceNormal(outward.TriangleOrNone), region);
                if (!inward.Found)
                {
                    // Skin turned unlike any source skin around it: the nearest, where the bodies nearly meet.
                    var nearest = sourceIndex.ClosestPoint(point, 2 * Overlap);
                    return nearest.Found ? Covered(nearest.Position, source.FaceNormal(nearest.TriangleOrNone), region) : (bool?)null;
                }
                for (int hop = 0; hop < CounterpartHops && inward.Found; hop++)
                {
                    bool covers = Covered(inward.Position, source.FaceNormal(inward.TriangleOrNone), region);
                    // A part of the source out of the clothing deep inside the target (the base's tail inside larger
                    // buttocks) has no counterpart on the target: look past it.
                    if (covers || targetIndex.ClosestPoint(inward.Position, Unmatched).Found) return covers;
                    float travelled = (inward.Position - point).magnitude + .001f;
                    inward = sourceIndex.Raycast(point - normal * travelled, -normal, CounterpartReach - travelled, true, same);
                }
                return null;
            }
            FillHoles(cover, enclosed, neighbours);
        }

        // How far along the fabric from a hem (an open edge) a sleeve or a leg keeps what lay under the skin as made: a cuff sunk
        // into a larger arm looks like a tight cuff, and pushed out the hem flares. How deep under a limb's skin its fabric is
        // looked for.
        private const float HemBand = .03f, LimbReach = .06f;

        private static bool IsLimb(BodyRegion region) =>
            region == BodyRegion.LeftArm || region == BodyRegion.RightArm || region == BodyRegion.LeftLeg || region == BodyRegion.RightLeg;

        // The limb of the source each group lay over as made (its nearest skin's region), for groups farther than HemBand along
        // the fabric from an open edge (an edge of a single triangle); Unknown for the others.
        private static BodyRegion[] LimbFabric(MeshSnapshot asset, Vector3[] made, MeshSnapshot source, MeshBvh sourceIndex, BodyRegion[] sourceRegions)
        {
            var counts = new Dictionary<(int, int), int>();
            for (int t = 0; t + 2 < asset.triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = asset.groupOfVertex[asset.triangles[t + k]], b = asset.groupOfVertex[asset.triangles[t + (k + 1) % 3]];
                    if (a == b) continue;
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
            var distance = new float[asset.GroupCount];
            for (int g = 0; g < distance.Length; g++) distance[g] = float.MaxValue;
            var queue = new Queue<int>();
            foreach (var pair in counts)
            {
                if (pair.Value != 1) continue;
                foreach (int g in new[] { pair.Key.Item1, pair.Key.Item2 })
                    if (distance[g] > 0f) { distance[g] = 0f; queue.Enqueue(g); }
            }
            while (queue.Count > 0)
            {
                int g = queue.Dequeue();
                foreach (int n in asset.groupAdjacency[g])
                {
                    float d = distance[g] + (made[g] - made[n]).magnitude;
                    if (d < distance[n] && d <= HemBand) { distance[n] = d; queue.Enqueue(n); }
                }
            }
            var part = new BodyRegion[distance.Length];
            Parallel.For(0, part.Length, g =>
            {
                part[g] = BodyRegion.Unknown;
                if (distance[g] <= HemBand) return;
                var nearest = sourceIndex.ClosestPoint(made[g], LimbReach);
                if (nearest.Found && nearest.TriangleOrNone < sourceRegions.Length && IsLimb(sourceRegions[nearest.TriangleOrNone])) part[g] = sourceRegions[nearest.TriangleOrNone];
            });
            return part;
        }

        // Counterparts are noisy where the bodies differ most (around a grown breast, over a source fold): skin mostly
        // surrounded by covered skin is covered too.
        private const int HolePasses = 4;

        // A wall of a fold faces other skin this close.
        private const float FoldWidth = .06f;

        private static bool FoldWall(MeshSnapshot body, MeshBvh index, int vertex)
        {
            var normal = body.worldNormals[vertex];
            return index.Raycast(body.worldVertices[vertex] + normal * .001f, normal, FoldWidth,
                true, SurfaceRules.Against(body.FaceNormals ?? body.CacheFaceNormals(), normal)).Found;
        }

        private static List<int>[] Neighbours(MeshSnapshot body)
        {
            var neighbours = new List<int>[body.worldVertices.Length];
            for (int t = 0; t + 2 < body.triangles.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = body.triangles[t + e], b = body.triangles[t + (e + 1) % 3];
                    (neighbours[a] ??= new List<int>()).Add(b);
                    (neighbours[b] ??= new List<int>()).Add(a);
                }
            return neighbours;
        }

        private static Vector3[] MacroNormals(Vector3[] normals, List<int>[] neighbours)
        {
            var current = (Vector3[])normals.Clone();
            var next = new Vector3[current.Length];
            for (int pass = 0; pass < MacroPasses; pass++)
            {
                Parallel.For(0, current.Length, i =>
                {
                    var sum = current[i];
                    if (neighbours[i] != null) foreach (int n in neighbours[i]) sum += current[n];
                    next[i] = sum;
                });
                for (int i = 0; i < current.Length; i++) current[i] = next[i].normalized;
            }
            return current;
        }

        // Skin without a counterpart on the source (buttocks reaching far behind the base's) takes the decision of the
        // nearest skin across the body's surface that has one.
        private static void Inherit(bool[] cover, bool[] decided, bool[] enclosed, List<int>[] neighbours)
        {
            var queue = new Queue<int>();
            for (int i = 0; i < decided.Length; i++) if (decided[i]) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (neighbours[i] == null) continue;
                foreach (int n in neighbours[i])
                {
                    if (decided[n] || (enclosed != null && enclosed[n])) continue;
                    decided[n] = true;
                    cover[n] = cover[i];
                    queue.Enqueue(n);
                }
            }
        }

        private static void FillHoles(bool[] cover, bool[] enclosed, List<int>[] neighbours)
        {
            for (int pass = 0; pass < HolePasses; pass++)
            {
                var next = (bool[])cover.Clone();
                for (int i = 0; i < cover.Length; i++)
                {
                    if (cover[i] || neighbours[i] == null || (enclosed != null && enclosed[i])) continue;
                    int on = 0;
                    foreach (int n in neighbours[i]) if (cover[n]) on++;
                    next[i] = on * 2 > neighbours[i].Count;
                }
                Array.Copy(next, cover, cover.Length);
            }
        }

        /// <param name="primary">For a shape frame: the refit the frame adds to, otherwise null.</param>
        /// <param name="deltas">The fabric's displacement (primary refit, or a frame's), corrected in place.</param>
        /// <param name="bodyDelta">For a shape frame: the body's world displacement at the frame, otherwise null.</param>
        /// <param name="progress">0..1 through the drape, from the worker thread.</param>
        public void Apply(MeshSnapshot body, Vector3[] bodyDelta, Vector3[] primary, Vector3[] deltas, ReFitReport report, string label,
            CancellationToken cancellation = default, Action<float> progress = null)
        {
            int groups = asset.GroupCount;
            var skin = body;
            if (bodyDelta != null)
            {
                var moved = new Vector3[body.worldVertices.Length];
                for (int i = 0; i < moved.Length; i++) moved[i] = body.worldVertices[i] + bodyDelta[i];
                // Its hierarchies are the body's refitted where the frame moves it.
                skin = MeshSnapshot.MovedFrom(body, moved, bodyDelta);
                skin.worldNormals = VertexNormals(moved, body.triangles);
            }
            var skinIndex = skin.Index();
            // The way out of a part of the body crosses its skin, the skin enclosed in another part too (under the muscles and
            // the mane of a custom base, the top of the shoulder): left out, the skin under the arm found the fabric over the
            // shoulder past it, and pushed it down the arm.
            var wholeSkin = skin.WholeIndex();
            // Where the fabric started: the pieces on it follow what untangling and bridging do to it.
            var start = (Vector3[])deltas.Clone();
            var startBasis = new Vector3[groups];
            for (int g = 0; g < groups; g++) startBasis[g] = asset.worldVertices[asset.groupRep[g]] + (primary != null ? primary[g] : Vector3.zero) + deltas[g];
            var (tangled, untangled) = Untangle(primary, deltas, skin, skinIndex, cancellation);
            if (tangled > 0)
                report?.Info("drape-untangle", $"{label}: {tangled} fabric edges crossed the fabric where they did not as authored; {untangled} left after relaxing the fit around them.");
            // A shape frame bridges only folds of the skin: elsewhere a dip of its displacement is the shape (skin shrinking).
            Bridge(primary, deltas, skin, skinIndex, bodyDelta != null);
            var groupTriangles = new int[asset.triangles.Length];
            for (int i = 0; i < groupTriangles.Length; i++) groupTriangles[i] = asset.groupOfVertex[asset.triangles[i]];
            FollowPieces(groupTriangles, start, startBasis, deltas);
            var enclosed = skin.HiddenVertices();
            var skinNormals = skin.FaceNormals ?? skin.CacheFaceNormals();
            // Whether each skin vertex is a wall of a fold (the skin does not move during the drape): 0 not known yet, 1 yes, 2 no.
            var foldWall = new sbyte[skin.worldVertices.Length];
            var basis = new Vector3[groups];
            for (int g = 0; g < groups; g++) basis[g] = asset.worldVertices[asset.groupRep[g]] + (primary != null ? primary[g] : Vector3.zero) + deltas[g];
            var push = new Vector3[groups];
            var vertices = new Vector3[asset.worldVertices.Length];
            var cloth = new MeshSnapshot { worldVertices = vertices, triangles = asset.triangles };
            var hits = new (int triangle, Vector3 bary, float missing, Vector3 direction)[skin.worldVertices.Length];
            var macro = MacroNormals(skin.worldNormals, neighbours);
            // Only skin showing through before the drape drives it: fabric lifted into another part of the body (a collar
            // into the jaw) does not push itself further.
            var wanted = new bool[hits.Length];
            var corrections = new Vector3[groups];
            var weights = new float[groups];
            var need = new float[groups];
            int exposed = 0, first = -1;
            float deepest = 0, firstDeepest = 0;
            MeshBvh index = null;
            // One more pass than pushes: it counts what the last push left.
            for (int iteration = 0; iteration <= Iterations; iteration++)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke((float)iteration / (Iterations + 1));
                for (int i = 0; i < vertices.Length; i++) vertices[i] = basis[asset.groupOfVertex[i]] + push[asset.groupOfVertex[i]];
                var clothNormals = cloth.CacheFaceNormals();
                if (index == null || iteration % RebuildEvery == 0) index = MeshSnapshot.BuildIndex(cloth);
                else index.Refit(cloth.worldVertices);
                Parallel.For(0, hits.Length, i =>
                {
                    hits[i] = (-1, Vector3.zero, 0, Vector3.zero);
                    if (!covered[i] || (iteration > 0 && !wanted[i])) return;
                    if (enclosed != null && enclosed[i]) return;
                    var normal = skin.worldNormals[i];
                    if (normal.sqrMagnitude < 1e-12f) return;
                    // Along the skin's normal, and for a wall of a fold along the fold's opening (the fabric deeper in the
                    // gluteal crease than the skin of its walls).
                    if (!Exposed(i, normal) && Vector3.Dot(macro[i], normal) < MacroDifference && IsFoldWall(i)) Exposed(i, macro[i]);
                    if (iteration == 0 && hits[i].triangle >= 0) wanted[i] = true;
                });
                bool IsFoldWall(int i)
                {
                    if (foldWall[i] == 0) foldWall[i] = FoldWall(skin, skinIndex, i) ? (sbyte)1 : (sbyte)2;
                    return foldWall[i] == 1;
                }
                bool Exposed(int i, Vector3 normal)
                {
                    var origin = skin.worldVertices[i] - normal * .0005f;
                    // Fabric turned the skin's way, or across it (fabric bridging a fold deeper than the fold's walls); not
                    // fabric facing the skin (a lining, the inside of the same tube).
                    var hit = index.Raycast(origin, -normal, Reach, true, SurfaceRules.Facing(clothNormals, normal, -Facing, strict: true));
                    if (!hit.Found) return false;
                    // The fabric must be inside the same part of the body, and nearer this side of it than the other: not past the
                    // other side of a limb, nor past its middle (the skin over there pushes it out the short way: the top of the
                    // shoulder, not the armpit, for a vest's strap inside a muscled shoulder).
                    var exit = wholeSkin.Raycast(origin, -normal, 2f * hit.Distance, true, SurfaceRules.Against(skinNormals, normal));
                    if (exit.Found) return false;
                    int t3 = hit.TriangleOrNone * 3;
                    int a = asset.groupOfVertex[asset.triangles[t3]], b = asset.groupOfVertex[asset.triangles[t3 + 1]], c = asset.groupOfVertex[asset.triangles[t3 + 2]];
                    if (fixedGroups[a] && fixedGroups[b] && fixedGroups[c]) return false;
                    if (fabricLimbs != null && IsLimb(skinParts[i]))
                    {
                        // The other leg's (or arm's) fabric is not this limb's to push out.
                        int most = hit.Bary.x >= hit.Bary.y && hit.Bary.x >= hit.Bary.z ? a : hit.Bary.y >= hit.Bary.z ? b : c;
                        if (IsLimb(fabricLimbs[most]) && fabricLimbs[most] != skinParts[i]) return false;
                    }
                    float madeUnder = (authoredUnder[a] ? hit.Bary.x : 0) + (authoredUnder[b] ? hit.Bary.y : 0) + (authoredUnder[c] ? hit.Bary.z : 0);
                    if (madeUnder >= .5f) return false;
                    var facing = cloth.FaceNormal(hit.TriangleOrNone);
                    if (Vector3.Dot(facing, normal) >= Facing)
                    {
                        float depth = hit.Distance + .0005f;
                        if (depth < ExposedTolerance) return false;
                        hits[i] = (hit.TriangleOrNone, hit.Bary, depth + Clearance, normal);
                        return true;
                    }
                    // Fabric across the skin: out its own way until the skin is behind it.
                    float behind = Vector3.Dot(skin.worldVertices[i] - hit.Position, facing);
                    if (behind < ExposedTolerance) return false;
                    hits[i] = (hit.TriangleOrNone, hit.Bary, behind + Clearance, facing);
                    return true;
                }
                System.Array.Clear(corrections, 0, groups);
                System.Array.Clear(weights, 0, groups);
                exposed = 0; deepest = 0;
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].triangle < 0) continue;
                    float missing = hits[i].missing;
                    exposed++; deepest = Mathf.Max(deepest, missing - Clearance);
                    // Out along the skin, the way the fabric leaves it.
                    var normal = hits[i].direction;
                    var bary = hits[i].bary;
                    var amount = normal * (missing / Mathf.Max(bary.sqrMagnitude, .34f));
                    int t3 = hits[i].triangle * 3;
                    for (int k = 0; k < 3; k++)
                    {
                        int g = asset.groupOfVertex[asset.triangles[t3 + k]];
                        if (fixedGroups[g] || details[g]) continue;
                        corrections[g] += amount * bary[k];
                        weights[g] += bary[k];
                    }
                }
                if (first < 0)
                {
                    first = exposed; firstDeepest = deepest;
                    // No fabric is lifted farther than the skin around it needed at first.
                    for (int i = 0; i < hits.Length; i++)
                    {
                        if (hits[i].triangle < 0) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int g = asset.groupOfVertex[asset.triangles[hits[i].triangle * 3 + k]];
                            need[g] = Mathf.Max(need[g], hits[i].missing);
                        }
                    }
                    for (int ring = 0; ring < NeedRings; ring++)
                    {
                        var wider = (float[])need.Clone();
                        for (int g = 0; g < groups; g++)
                            foreach (int n in asset.groupAdjacency[g]) wider[g] = Mathf.Max(wider[g], need[n]);
                        need = wider;
                    }
                }
                if (iteration == Iterations) break;
                bool any = false;
                for (int g = 0; g < groups; g++)
                    if (weights[g] > 1e-6f) { push[g] = Vector3.ClampMagnitude(push[g] + corrections[g] / Mathf.Max(weights[g], 1f), need[g]); any = true; }
                if (!any) break;
                if (iteration < SpreadIterations)
                {
                    // Spread the push over the fabric around it; the push only ever lifts fabric out of the body.
                    DeltaField.Smooth(push, asset.groupAdjacency, 3, .5f);
                    for (int g = 0; g < groups; g++)
                        if (fixedGroups[g] || details[g]) push[g] = Vector3.zero;
                }
            }
            CarryDetails(groupTriangles, asset.groupAdjacency, basis, push, details, rings, t => !details[groupTriangles[t * 3]]);
            progress?.Invoke(1f);

            float largest = 0;
            for (int g = 0; g < groups; g++) { deltas[g] += push[g]; largest = Mathf.Max(largest, push[g].magnitude); }
            if (first > 0 || largest > 0)
                report?.Info("drape", $"{label}: {first} skin vertices showed through the clothing (deepest {firstDeepest * 1000:F1}mm); " +
                                      $"draped the fabric out up to {largest * 1000:F1}mm; {exposed} left (deepest {deepest * 1000:F1}mm)" +
                                      (limbGroups > 0 ? $"; {limbCovered} skin points covered through {limbGroups} sleeve and leg points." : "."));
            if (exposed > 0 && deepest > .003f)
                report?.Warn("drape-limited", $"{label}: {exposed} skin vertices still show through the clothing, up to {deepest * 1000:F1}mm. " +
                                              "Inspect the garment at individual and combined shape weights.");
        }

        // Fabric crossing its own fabric where it did not as authored (a pleat where the fit follows a fold of the body, at
        // the back of a thigh overhanging the knee): the displacement around it is smoothed, a round at a time. A shape frame
        // relaxes only the crossings it adds: those the refit left at rest are the refit's.
        private const int UntangleRounds = 12, UntangleRings = 2, UntanglePasses = 4;
        // How far from the skin relaxed fabric is kept out of it.
        private const float GuardReach = .05f;

        private (int first, int last) Untangle(Vector3[] primary, Vector3[] deltas, MeshSnapshot skin, MeshBvh skinIndex, CancellationToken cancellation)
        {
            int groups = asset.GroupCount;
            var positions = new Vector3[groups];
            var vertices = new Vector3[asset.worldVertices.Length];
            var cloth = new MeshSnapshot { worldVertices = vertices, triangles = asset.triangles };
            MeshBvh index = null;
            int first = -1, count = 0;
            Vector3 Rest(int g) => Authored(g) + primary[g];
            for (int round = 0; round <= UntangleRounds; round++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int g = 0; g < groups; g++) positions[g] = Authored(g) + (primary != null ? primary[g] : Vector3.zero) + deltas[g];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = positions[asset.groupOfVertex[i]];
                if (index == null) index = MeshSnapshot.BuildIndex(cloth);
                else index.Refit(cloth.worldVertices);
                var tangled = new bool[groups];
                int found = 0;
                Parallel.For(0, edges.Count, e =>
                {
                    var (ga, gb) = edges[e];
                    var a = positions[ga]; var direction = positions[gb] - a;
                    float length = direction.magnitude;
                    if (length < 1e-6f) return;
                    var hit = index.Raycast(a, direction / length, length, true, TriangleRule.All.AwayFrom(asset.triangles, asset.groupOfVertex, ga, gb));
                    if (!hit.Found) return;
                    int t3 = hit.TriangleOrNone * 3;
                    int x = asset.groupOfVertex[asset.triangles[t3]], y = asset.groupOfVertex[asset.triangles[t3 + 1]], z = asset.groupOfVertex[asset.triangles[t3 + 2]];
                    // Layers made crossing (a collar through its fold) stay as they are, and so do the two legs pressed
                    // into each other: neither can give way.
                    if (SegmentCrosses(made[ga], made[gb], made[x], made[y], made[z]) || Opposite(ga, x)) return;
                    if (primary != null && SegmentCrosses(Rest(ga), Rest(gb), Rest(x), Rest(y), Rest(z))) return;
                    Interlocked.Increment(ref found);
                    tangled[ga] = tangled[gb] = tangled[x] = tangled[y] = tangled[z] = true;
                });
                count = found;
                if (first < 0) first = found;
                if (found == 0 || round == UntangleRounds) break;
                for (int ring = 0; ring < UntangleRings; ring++)
                {
                    var wider = (bool[])tangled.Clone();
                    for (int g = 0; g < groups; g++)
                        if (tangled[g]) foreach (int n in asset.groupAdjacency[g]) wider[n] = true;
                    tangled = wider;
                }
                var relaxed = new bool[groups];
                for (int g = 0; g < groups; g++)
                    relaxed[g] = tangled[g] && !fixedGroups[g] && !details[g] && asset.groupAdjacency[g].Count > 0;
                for (int pass = 0; pass < UntanglePasses; pass++)
                {
                    var next = (Vector3[])deltas.Clone();
                    for (int g = 0; g < groups; g++)
                    {
                        if (!relaxed[g]) continue;
                        var average = Vector3.zero;
                        foreach (int n in asset.groupAdjacency[g]) average += deltas[n];
                        next[g] = Vector3.Lerp(deltas[g], average / asset.groupAdjacency[g].Count, .5f);
                    }
                    KeepOutOfSkin(primary, deltas, next, relaxed, skin, skinIndex);
                    Array.Copy(next, deltas, groups);
                }
            }
            return (first, count);
        }

        // Relaxing a crossing never takes fabric into the skin, nor deeper into it than it was: smoothed toward fabric lying
        // lower (an inner layer's side of a fold, the skin side of a pleat), it stops at the skin.
        private void KeepOutOfSkin(Vector3[] primary, Vector3[] before, Vector3[] after, bool[] moved, MeshSnapshot skin, MeshBvh skinIndex)
        {
            Parallel.For(0, after.Length, g =>
            {
                if (!moved[g]) return;
                var basePoint = Authored(g) + (primary != null ? primary[g] : Vector3.zero);
                var position = basePoint + after[g];
                var nearest = skinIndex.ClosestPoint(position, GuardReach);
                if (!nearest.Found) return;
                var normal = skin.BaryNormal(nearest.TriangleOrNone, nearest.Bary);
                float height = Vector3.Dot(position - nearest.Position, normal);
                float floor = Mathf.Min(Vector3.Dot(basePoint + before[g] - nearest.Position, normal), Clearance);
                if (height < floor) after[g] += normal * (floor - height);
            });
        }

        // Fabric pulled into a fold of the body more than the fabric around it (the gluteal crease, under a breast) spans
        // it instead, as cloth does: the fit is smoothed only where that lifts the fabric out.
        private const int BridgeIterations = 24;
        // Fabric farther than this from the skin bridges nothing.
        private const float BridgeReach = .1f;

        /// <param name="foldsOnly">Only fabric over a fold of the skin (a wall facing other skin) is lifted.</param>
        private void Bridge(Vector3[] primary, Vector3[] deltas, MeshSnapshot skin, MeshBvh skinIndex, bool foldsOnly)
        {
            int groups = asset.GroupCount;
            // Out of the body at the fabric: the skin nearest to it turned out. Lifting along the fabric's own normal would
            // slide a tilted hem along the body.
            var normals = new Vector3[groups];
            Parallel.For(0, groups, g =>
            {
                var position = Authored(g) + (primary != null ? primary[g] : Vector3.zero) + deltas[g];
                var nearest = skinIndex.ClosestPoint(position, BridgeReach);
                if (!nearest.Found) return;
                var normal = skin.BaryNormal(nearest.TriangleOrNone, nearest.Bary);
                if (foldsOnly && !skinIndex.Raycast(nearest.Position + normal * .001f, normal, FoldWidth, true,
                        SurfaceRules.Against(skin.KnownFaceNormals(), normal)).Found) return;
                normals[g] = normal;
            });
            var next = new Vector3[groups];
            for (int iteration = 0; iteration < BridgeIterations; iteration++)
            {
                Array.Copy(deltas, next, groups);
                for (int g = 0; g < groups; g++)
                {
                    var neighbours = asset.groupAdjacency[g];
                    if (fixedGroups[g] || details[g] || neighbours.Count == 0 || normals[g] == Vector3.zero) continue;
                    var average = Vector3.zero;
                    foreach (int n in neighbours) average += deltas[n];
                    float lift = Vector3.Dot(average / neighbours.Count - deltas[g], normals[g]);
                    if (lift > 0) next[g] = deltas[g] + normals[g] * (lift * .5f);
                }
                Array.Copy(next, deltas, groups);
            }
        }

        private bool Opposite(int a, int b)
        {
            if (groupRegions == null) return false;
            var ra = groupRegions[a]; var rb = groupRegions[b];
            return (ra == BodyRegion.LeftLeg && rb == BodyRegion.RightLeg) || (ra == BodyRegion.RightLeg && rb == BodyRegion.LeftLeg) ||
                   (ra == BodyRegion.LeftArm && rb == BodyRegion.RightArm) || (ra == BodyRegion.RightArm && rb == BodyRegion.LeftArm);
        }

        private Vector3 Authored(int group) => asset.worldVertices[asset.groupRep[group]];

        // The segment p-q passes through the triangle a-b-c (Moller-Trumbore).
        private static bool SegmentCrosses(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
        {
            var direction = q - p;
            var e1 = b - a; var e2 = c - a;
            var h = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float f = 1 / det;
            var s = p - a;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1) return false;
            var r = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(direction, r);
            if (v < 0 || u + v > 1) return false;
            float t = f * Vector3.Dot(e2, r);
            return t > 0 && t < 1;
        }

        // Of clothing made for another body, the separate pieces follow what relaxing its tangles and bridging the folds did to
        // the fabric under each part of them (a belt left where it was sank under the waistband relaxed out), unless they moved
        // more themselves. The drape then pushes them out of the skin like the rest. Clothing placed on its own target can be
        // a tangle of its own (the onesie, 4882 crossings), and a zipper carried by the collar beside it went astray.
        private void FollowPieces(int[] groupTriangles, Vector3[] start, Vector3[] startBasis, Vector3[] deltas)
        {
            if (pieces.Count == 0) return;
            var inPiece = new bool[deltas.Length];
            foreach (var piece in pieces) foreach (int g in piece) inPiece[g] = true;
            var change = new Vector3[deltas.Length];
            for (int g = 0; g < change.Length; g++) change[g] = deltas[g] - start[g];
            var own = (Vector3[])change.Clone();
            CarryDetails(groupTriangles, asset.groupAdjacency, startBasis, change, inPiece, inPiece,
                t => !inPiece[groupTriangles[t * 3]] && !details[groupTriangles[t * 3]]);
            foreach (var piece in pieces)
            {
                float moved = 0, carry = 0;
                foreach (int g in piece) { moved += own[g].magnitude; carry += change[g].magnitude; }
                if (carry <= moved) continue;
                foreach (int g in piece) deltas[g] = start[g] + change[g];
            }
        }

        // A closed detail (a zipper pull, a button) rides with the moved fabric nearest to it as one rigid piece. A closed
        // ring (a belt, a collar, a bracelet) goes around the body: each part of it follows the fabric under that part,
        // smoothed along the ring so it stays whole. Carried whole by the largest push, a belt slid forward over a grown belly
        // and its back half sank into the body.
        private const int RingPasses = 4;

        /// <summary>
        /// Moves the details (<paramref name="details"/>) with the fabric nearest to them: by <paramref name="push"/>, the
        /// fabric's displacement per group, set in place for the details' groups.
        /// </summary>
        /// <param name="triangles">The fabric's triangles by group, over <paramref name="basis"/> (positions by group).</param>
        /// <param name="rings">Groups of closed rings (<see cref="ReFitTubeField"/>): a detail with one follows along itself.</param>
        /// <param name="fabric">The triangles a detail may follow.</param>
        internal static void CarryDetails(int[] triangles, List<int>[] adjacency, Vector3[] basis, Vector3[] push, bool[] details, bool[] rings,
            Func<int, bool> fabric)
        {
            var visited = new bool[details.Length];
            MeshBvh index = null;
            for (int start = 0; start < details.Length; start++)
            {
                if (!details[start] || visited[start]) continue;
                if (index == null)
                {
                    // A tree of the triangles the details may follow only: what filtering every query by them would find.
                    var include = new bool[triangles.Length / 3];
                    for (int t = 0; t < include.Length; t++) include[t] = fabric(t);
                    index = MeshBvh.Build(basis, triangles, include);
                }
                var members = new List<int>();
                var queue = new Queue<int>(); queue.Enqueue(start); visited[start] = true;
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue(); members.Add(g);
                    foreach (int n in adjacency[g]) if (details[n] && !visited[n]) { visited[n] = true; queue.Enqueue(n); }
                }
                var carried = new Vector3[members.Count];
                var found = new bool[members.Count];
                var carry = Vector3.zero;
                bool ring = false;
                for (int m = 0; m < members.Count; m++)
                {
                    int g = members[m];
                    ring |= rings != null && rings[g];
                    var hit = index.ClosestPoint(basis[g], .04f);
                    if (!hit.Found) continue;
                    int t3 = hit.TriangleOrNone * 3;
                    var d = push[triangles[t3]] * hit.Bary.x + push[triangles[t3 + 1]] * hit.Bary.y + push[triangles[t3 + 2]] * hit.Bary.z;
                    carried[m] = d;
                    found[m] = true;
                    if (d.sqrMagnitude > carry.sqrMagnitude) carry = d;
                }
                if (!ring)
                {
                    foreach (int g in members) push[g] = carry;
                    continue;
                }
                FollowAlong(adjacency, members, carried, found);
                for (int m = 0; m < members.Count; m++) push[members[m]] = carried[m];
            }
        }

        // A ring's carry: parts away from the fabric take it from their neighbours along the ring, then the whole is smoothed
        // over the ring (its cross-sections stay round, its path unkinked).
        private static void FollowAlong(List<int>[] adjacency, List<int> members, Vector3[] carried, bool[] found)
        {
            var member = new Dictionary<int, int>(members.Count);
            for (int m = 0; m < members.Count; m++) member[members[m]] = m;
            var queue = new Queue<int>();
            for (int m = 0; m < members.Count; m++) if (found[m]) queue.Enqueue(m);
            while (queue.Count > 0)
            {
                int m = queue.Dequeue();
                foreach (int n in adjacency[members[m]])
                {
                    if (!member.TryGetValue(n, out int other) || found[other]) continue;
                    found[other] = true;
                    carried[other] = carried[m];
                    queue.Enqueue(other);
                }
            }
            var next = new Vector3[carried.Length];
            for (int pass = 0; pass < RingPasses; pass++)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    var sum = Vector3.zero;
                    int count = 0;
                    foreach (int n in adjacency[members[m]])
                        if (member.TryGetValue(n, out int other)) { sum += carried[other]; count++; }
                    next[m] = count > 0 ? Vector3.Lerp(carried[m], sum / count, .5f) : carried[m];
                }
                Array.Copy(next, carried, carried.Length);
            }
        }

        private static Vector3[] VertexNormals(Vector3[] vertices, int[] triangles)
        {
            var normals = new Vector3[vertices.Length];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = MeshSnapshot.Unit(normals[i]);
            return normals;
        }
    }
}
