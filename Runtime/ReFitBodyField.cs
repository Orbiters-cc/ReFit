using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// A body as a signed distance grid over a region (metres, negative inside). Inside and outside come from a flood of the
    /// air from the region's border, not from the nearest triangle's normal alone, so skin sheets inside the body (a mouth,
    /// overlapping parts, a collapsed fold) do not turn the air around them inside. Optionally sealed: air the body closes in
    /// on all sides (between thighs pressed together, under an arm against the side) counts as inside, its distance the way
    /// out of the crease, so a point there is pushed out of the whole crease instead of towards whichever skin sheet is
    /// nearest. Built in a narrow band around the skin (exact distances to the triangles there); beyond, the grid only knows
    /// inside or outside. Sampled trilinearly with its gradient; read-only once built (thread safe).
    /// </summary>
    public sealed class ReFitBodyField
    {
        // Distances are exact this far from the skin (cells); beyond, the field is ±Band.
        private const int BandCells = 4;
        /// <summary>The usual seal: gaps in the skin narrower than about this many cells (a slit, a crease) do not let the air in.</summary>
        public const int SealCells = 2;
        // How far a border cell looks for the skin to tell whether it lies inside a limb the region cuts through: no part of a
        // body is thicker (farther, it is air).
        private const float BorderReach = .25f;

        public readonly Vector3 origin;
        public readonly float cell;
        public readonly int nx, ny, nz;
        private readonly float[] values;
        private readonly float band;

        private ReFitBodyField(Vector3 origin, float cell, int nx, int ny, int nz)
        {
            this.origin = origin;
            this.cell = cell;
            this.nx = nx; this.ny = ny; this.nz = nz;
            values = new float[nx * ny * nz];
            band = BandCells * cell;
        }

        /// <summary>Cells of the grid (diagnostics).</summary>
        public int Cells => values.Length;

        /// <summary>Where the build's time went (diagnostics).</summary>
        public string Timing { get; private set; }

        /// <param name="region">World bounds to cover (clothing bounds and some margin); the body outside it is ignored.</param>
        /// <param name="cell">Cell size (metres): a few millimetres.</param>
        /// <param name="maxCells">Cell count the grid may not exceed: the cell grows to fit.</param>
        /// <param name="seal">Gaps narrower than about this many cells stay shut (0: none, the air goes everywhere it can).</param>
        public static ReFitBodyField Build(MeshSnapshot body, SurfaceBvh index, Bounds region, float cell, int maxCells, CancellationToken cancellation,
            int seal = SealCells)
        {
            var size = region.size;
            float volume = Mathf.Max(1e-9f, size.x * size.y * size.z);
            cell = Mathf.Max(cell, Mathf.Pow(volume / Mathf.Max(1, maxCells), 1f / 3f));
            int nx = Mathf.Max(2, Mathf.CeilToInt(size.x / cell) + 1), ny = Mathf.Max(2, Mathf.CeilToInt(size.y / cell) + 1),
                nz = Mathf.Max(2, Mathf.CeilToInt(size.z / cell) + 1);
            var field = new ReFitBodyField(region.min, cell, nx, ny, nz);
            field.Fill(body, index, Mathf.Max(0, seal), cancellation);
            return field;
        }

        private int Index(int x, int y, int z) => (z * ny + y) * nx + x;

        private Vector3 Center(int x, int y, int z) => origin + new Vector3(x * cell, y * cell, z * cell);

        private void Fill(MeshSnapshot body, SurfaceBvh index, int sealCells, CancellationToken cancellation)
        {
            int count = values.Length;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var laps = new List<string>();
            void Lap(string step) { laps.Add(step + " " + watch.ElapsedMilliseconds + " ms"); watch.Restart(); }
            // 1. The band: cells near a triangle's bounds get their exact distance, signed by the skin's normal there.
            var near = new bool[count];
            var faceNormals = body.FaceNormals ?? body.CacheFaceNormals();
            int triangles = body.triangles.Length / 3;
            for (int t = 0; t < triangles; t++)
            {
                if (body.hiddenTriangles != null && body.hiddenTriangles[t]) continue;
                var a = body.worldVertices[body.triangles[t * 3]];
                var b = body.worldVertices[body.triangles[t * 3 + 1]];
                var c = body.worldVertices[body.triangles[t * 3 + 2]];
                var min = Vector3.Min(a, Vector3.Min(b, c)) - Vector3.one * band;
                var max = Vector3.Max(a, Vector3.Max(b, c)) + Vector3.one * band;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((min.x - origin.x) / cell)), x1 = Mathf.Min(nx - 1, Mathf.CeilToInt((max.x - origin.x) / cell));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((min.y - origin.y) / cell)), y1 = Mathf.Min(ny - 1, Mathf.CeilToInt((max.y - origin.y) / cell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((min.z - origin.z) / cell)), z1 = Mathf.Min(nz - 1, Mathf.CeilToInt((max.z - origin.z) / cell));
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++) near[Index(x, y, z)] = true;
            }
            cancellation.ThrowIfCancellationRequested();
            Lap("band");
            var unsigned = new float[count];
            var outsideByNormal = new bool[count];
            Parallel.For(0, nz, new ParallelOptions { CancellationToken = cancellation }, z =>
            {
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int i = Index(x, y, z);
                        if (!near[i]) { unsigned[i] = band; continue; }
                        var p = Center(x, y, z);
                        var hit = index.ClosestPoint(p, band);
                        if (!hit.found) { unsigned[i] = band; near[i] = false; continue; }
                        unsigned[i] = hit.distance;
                        // Inside or out by the smooth normal where the closest point is (the face normal on a degenerate one).
                        outsideByNormal[i] = Vector3.Dot(p - hit.position, body.BaryNormal(hit.triangle, hit.bary)) >= 0f;
                    }
            });

            Lap("distances");
            // 2. The air: flooded from the border through cells farther than a seal from the skin, then grown back by the seal
            // (a closing), so gaps narrower than the seal stay shut.
            float seal = sealCells * cell;
            var air = new bool[count];
            var queue = new Queue<int>();
            void Seed(int x, int y, int z)
            {
                int i = Index(x, y, z);
                if (air[i] || unsigned[i] <= seal || (near[i] && !outsideByNormal[i])) return;
                air[i] = true;
                queue.Enqueue(i);
            }
            // The region may cut through a limb: a border cell starts the flood only where the skin nearest it faces it. Told
            // apart beforehand, in parallel; a cell outside the body's bounds is air without looking.
            var bodyBounds = new Bounds(body.worldVertices.Length > 0 ? body.worldVertices[0] : Vector3.zero, Vector3.zero);
            foreach (var v in body.worldVertices) bodyBounds.Encapsulate(v);
            var borderCells = new List<int>();
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        if (x == 0 || y == 0 || z == 0 || x == nx - 1 || y == ny - 1 || z == nz - 1) borderCells.Add(Index(x, y, z));
            var inLimb = new bool[count];
            Parallel.For(0, borderCells.Count, new ParallelOptions { CancellationToken = cancellation }, b =>
            {
                int i = borderCells[b];
                if (near[i] || unsigned[i] <= seal) return;
                var p = Center(i % nx, i / nx % ny, i / (nx * ny));
                if (!bodyBounds.Contains(p)) return;
                var hit = index.ClosestPoint(p, BorderReach);
                inLimb[i] = hit.found && Vector3.Dot(p - hit.position, body.BaryNormal(hit.triangle, hit.bary)) < 0f;
            });
            void Border(int x, int y, int z)
            {
                int i = Index(x, y, z);
                if (air[i] || unsigned[i] <= seal || inLimb[i]) return;
                Seed(x, y, z);
            }
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++) { Border(0, y, z); Border(nx - 1, y, z); }
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++) { Border(x, 0, z); Border(x, ny - 1, z); }
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++) { Border(x, y, 0); Border(x, y, nz - 1); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                if (x > 0) Seed(x - 1, y, z);
                if (x < nx - 1) Seed(x + 1, y, z);
                if (y > 0) Seed(x, y - 1, z);
                if (y < ny - 1) Seed(x, y + 1, z);
                if (z > 0) Seed(x, y, z - 1);
                if (z < nz - 1) Seed(x, y, z + 1);
            }
            cancellation.ThrowIfCancellationRequested();
            // Grown back by the seal: the air near the skin outside it, met from the flooded air.
            var outside = (bool[])air.Clone();
            var grow = new Queue<int>();
            for (int i = 0; i < count; i++) if (air[i]) grow.Enqueue(i);
            for (int step = 0; step < sealCells + 1 && grow.Count > 0; step++)
            {
                int layer = grow.Count;
                for (int k = 0; k < layer; k++)
                {
                    int i = grow.Dequeue();
                    int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                    for (int d = 0; d < 6; d++)
                    {
                        int xx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), yy = y + (d == 2 ? -1 : d == 3 ? 1 : 0), zz = z + (d == 4 ? -1 : d == 5 ? 1 : 0);
                        if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz) continue;
                        int j = Index(xx, yy, zz);
                        if (outside[j] || (near[j] && !outsideByNormal[j])) continue;
                        outside[j] = true;
                        grow.Enqueue(j);
                    }
                }
            }
            // Inside the skin: minus the distance to it. Sealed air (outside by the skin's normal, shut in): minus the way out to
            // the open air, so its gradient leads out of the crease, not towards the skin around it.
            Lap("air");
            var exit = SealedExit(outside, near, outsideByNormal);
            for (int i = 0; i < count; i++)
                values[i] = outside[i] ? unsigned[i] : exit != null && exit[i] >= 0f ? -exit[i] : -unsigned[i];
            Lap("sealed");
            Timing = $"{nx}x{ny}x{nz} cells of {cell * 1000f:0.0} mm: " + string.Join(", ", laps);
        }

        // Sealed air: cells near the skin, outside it by its normal, that the air did not reach.
        private static bool SealedAir(int i, bool[] outside, bool[] near, bool[] outsideByNormal) => !outside[i] && near[i] && outsideByNormal[i];

        // For each sealed air cell, the distance to the nearest outside cell through sealed air (26 neighbours, Dijkstra), at
        // most the band; -1 elsewhere. Null when there is none.
        private float[] SealedExit(bool[] outside, bool[] near, bool[] outsideByNormal)
        {
            int count = values.Length;
            bool any = false;
            for (int i = 0; i < count && !any; i++) any = SealedAir(i, outside, near, outsideByNormal);
            if (!any) return null;
            var exit = new float[count];
            for (int i = 0; i < count; i++) exit[i] = -1f;
            var heap = new SortedSet<(float distance, int cell)>();
            // Seeds: outside cells next to sealed air, at distance zero.
            for (int i = 0; i < count; i++)
            {
                if (!outside[i]) continue;
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                for (int d = 0; d < 6; d++)
                {
                    int xx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), yy = y + (d == 2 ? -1 : d == 3 ? 1 : 0), zz = z + (d == 4 ? -1 : d == 5 ? 1 : 0);
                    if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz) continue;
                    if (!SealedAir(Index(xx, yy, zz), outside, near, outsideByNormal)) continue;
                    exit[i] = 0f;
                    heap.Add((0f, i));
                    break;
                }
            }
            while (heap.Count > 0)
            {
                var top = heap.Min;
                heap.Remove(top);
                int i = top.cell;
                float distance = top.distance;
                int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0) continue;
                            int xx = x + dx, yy = y + dy, zz = z + dz;
                            if (xx < 0 || yy < 0 || zz < 0 || xx >= nx || yy >= ny || zz >= nz) continue;
                            int j = Index(xx, yy, zz);
                            if (!SealedAir(j, outside, near, outsideByNormal)) continue;
                            float next = distance + cell * Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (next > band || (exit[j] >= 0f && next >= exit[j])) continue;
                            if (exit[j] >= 0f) heap.Remove((exit[j], j));
                            exit[j] = next;
                            heap.Add((next, j));
                        }
            }
            // The seeds are outside: they keep their distance to the skin.
            for (int i = 0; i < count; i++) if (outside[i]) exit[i] = -1f;
            return exit;
        }

        /// <summary>Signed distance at <paramref name="p"/> (trilinear); +band outside the grid.</summary>
        public float Sample(Vector3 p) => Sample(p, out _, false);

        /// <summary>Signed distance and its gradient (unnormalized: about unit length near the skin).</summary>
        public float Sample(Vector3 p, out Vector3 gradient) => Sample(p, out gradient, true);

        private float Sample(Vector3 p, out Vector3 gradient, bool wantGradient)
        {
            gradient = Vector3.zero;
            float fx = (p.x - origin.x) / cell, fy = (p.y - origin.y) / cell, fz = (p.z - origin.z) / cell;
            if (fx < 0f || fy < 0f || fz < 0f || fx > nx - 1 || fy > ny - 1 || fz > nz - 1) return band;
            int x = Mathf.Min(nx - 2, (int)fx), y = Mathf.Min(ny - 2, (int)fy), z = Mathf.Min(nz - 2, (int)fz);
            float u = fx - x, v = fy - y, w = fz - z;
            float c000 = values[Index(x, y, z)], c100 = values[Index(x + 1, y, z)], c010 = values[Index(x, y + 1, z)], c110 = values[Index(x + 1, y + 1, z)];
            float c001 = values[Index(x, y, z + 1)], c101 = values[Index(x + 1, y, z + 1)], c011 = values[Index(x, y + 1, z + 1)], c111 = values[Index(x + 1, y + 1, z + 1)];
            float c00 = c000 + (c100 - c000) * u, c10 = c010 + (c110 - c010) * u, c01 = c001 + (c101 - c001) * u, c11 = c011 + (c111 - c011) * u;
            float c0 = c00 + (c10 - c00) * v, c1 = c01 + (c11 - c01) * v;
            if (wantGradient)
            {
                float dx = ((c100 - c000) * (1 - v) + (c110 - c010) * v) * (1 - w) + ((c101 - c001) * (1 - v) + (c111 - c011) * v) * w;
                float dy = (c10 - c00) * (1 - w) + (c11 - c01) * w;
                float dz = c1 - c0;
                gradient = new Vector3(dx, dy, dz) / cell;
            }
            return c0 + (c1 - c0) * w;
        }
    }
}
