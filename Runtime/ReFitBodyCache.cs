using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// What ReFit keeps from one refit to the next: the bodies' derived data (enclosed skin, hierarchies) in memory, and the
    /// bodies laid onto each other in memory and in <see cref="LibraryDirectory"/> for later sessions.
    /// </summary>
    public static class ReFitCaches
    {
        /// <summary>Where the bodies laid onto each other are saved (the editor sets the project's Library/ReFit), or null.</summary>
        public static string LibraryDirectory { get; set; }

        /// <summary>
        /// Keeps more bodies until disposed: the refits of a batch (an outfit's pieces) find each other's bodies. Dispose it
        /// once the batch is done.
        /// </summary>
        public static IDisposable Batch() => ReFitBodyCache.Batch();

        /// <summary>Forgets every kept body and laid registration, in memory and on disk (tests timing a full run).</summary>
        public static void Clear()
        {
            ReFitBodyCache.Clear();
            ReFitBodyRegistration.ClearCache();
        }
    }

    /// <summary>
    /// What ReFit derives from a body surface, kept by the surface's content from one refit to the next (the pieces of one
    /// outfit, a refit run again): the skin enclosed in another part of it, its hierarchies (with and without that skin) and its
    /// main surface. Each staged run captures its bodies anew; a captured body with the same content (positions, normals,
    /// triangles) finds what an earlier run derived from it instead of deriving it again. Thread safe; the entries live until a
    /// domain reload or <see cref="Clear"/>.
    /// </summary>
    internal static class ReFitBodyCache
    {
        // Bodies kept: a refit reads a source, a target and the target without the shapes it shows. A batch of refits keeps
        // more (outfits fitted onto two avatars in turn).
        private const int Kept = 3, KeptInBatch = 6;

        /// <summary>The derived data of one body content.</summary>
        internal sealed class Entry
        {
            public readonly ulong key;
            public readonly int vertexCount, triangleCount;
            internal bool enclosedKnown;
            internal bool[] enclosed;
            internal bool[] main;
            // Hierarchies by the content of the hidden triangles they leave out (0: none).
            private readonly Dictionary<ulong, SurfaceBvh> indices = new Dictionary<ulong, SurfaceBvh>();

            public Entry(ulong key, int vertexCount, int triangleCount)
            {
                this.key = key;
                this.vertexCount = vertexCount;
                this.triangleCount = triangleCount;
            }

            /// <summary>The hierarchy of <paramref name="body"/> (this entry's content) leaving out its current hidden triangles.</summary>
            public SurfaceBvh Index(MeshSnapshot body) => Index(body, body.hiddenTriangles);

            /// <summary>The hierarchy of <paramref name="body"/> (this entry's content) leaving out <paramref name="hiddenTriangles"/>.</summary>
            public SurfaceBvh Index(MeshSnapshot body, bool[] hiddenTriangles)
            {
                ulong hidden = FlagsKey(hiddenTriangles);
                lock (indices)
                {
                    if (indices.TryGetValue(hidden, out var index)) { System.Threading.Interlocked.Increment(ref hits); return index; }
                }
                // Built outside the lock (it takes a while); two threads asking at once build the same tree.
                var built = SurfaceBvh.Build(body.worldVertices, body.triangles, hiddenTriangles);
                lock (indices)
                {
                    if (indices.TryGetValue(hidden, out var index)) return index;
                    indices[hidden] = built;
                    return built;
                }
            }
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static int batches;
        private static int hits;

        /// <summary>How many hierarchies were found instead of built since the last <see cref="Clear"/> (tests, diagnostics).</summary>
        internal static int IndexHits => hits;

        /// <summary>
        /// Links <paramref name="body"/> to the entry of its content (made when none exists), so its enclosed skin, hierarchies
        /// and main surface are shared with every equal body. Call once its vertices, normals and triangles are final.
        /// </summary>
        public static void Attach(MeshSnapshot body)
        {
            if (body == null || body.worldVertices == null || body.triangles == null || body.shared != null) return;
            ulong key = ContentKey(body);
            lock (Entries)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    var entry = Entries[i];
                    if (entry.key != key || entry.vertexCount != body.worldVertices.Length || entry.triangleCount != body.triangles.Length / 3) continue;
                    Entries.RemoveAt(i);
                    Entries.Insert(0, entry);
                    body.shared = entry;
                    return;
                }
                var made = new Entry(key, body.worldVertices.Length, body.triangles.Length / 3);
                Entries.Insert(0, made);
                int keep = batches > 0 ? KeptInBatch : Kept;
                while (Entries.Count > keep) Entries.RemoveAt(Entries.Count - 1);
                body.shared = made;
            }
        }

        /// <summary>
        /// Keeps more bodies until disposed: the refits of a batch (an outfit's pieces, on one avatar or several) find each
        /// other's bodies.
        /// </summary>
        public static IDisposable Batch()
        {
            lock (Entries) batches++;
            return new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool done;

            public void Dispose()
            {
                if (done) return;
                done = true;
                lock (Entries)
                {
                    batches = Math.Max(0, batches - 1);
                    int keep = batches > 0 ? KeptInBatch : Kept;
                    while (Entries.Count > keep) Entries.RemoveAt(Entries.Count - 1);
                }
            }
        }

        /// <summary>Forgets every body (tests timing a full run).</summary>
        public static void Clear()
        {
            lock (Entries) Entries.Clear();
            hits = 0;
        }

        // FNV-1a over the bits of what the derived data reads: positions, normals (a degenerate triangle's face normal) and triangles.
        internal static ulong ContentKey(MeshSnapshot body)
        {
            ulong hash = 14695981039346656037UL;
            void Add(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
            void Vectors(Vector3[] values)
            {
                Add(values?.Length ?? -1);
                if (values == null) return;
                for (int i = 0; i < values.Length; i++)
                {
                    Add(BitConverter.SingleToInt32Bits(values[i].x));
                    Add(BitConverter.SingleToInt32Bits(values[i].y));
                    Add(BitConverter.SingleToInt32Bits(values[i].z));
                }
            }
            Vectors(body.worldVertices);
            Vectors(body.worldNormals);
            var triangles = body.triangles;
            Add(triangles.Length);
            for (int i = 0; i < triangles.Length; i++) Add(triangles[i]);
            return hash;
        }

        // FNV-1a over the indices of the set flags; 0 for none set (or no array).
        internal static ulong FlagsKey(bool[] flags)
        {
            if (flags == null) return 0;
            ulong hash = 14695981039346656037UL;
            bool any = false;
            for (int i = 0; i < flags.Length; i++)
                if (flags[i])
                {
                    any = true;
                    unchecked { hash = (hash ^ (uint)i) * 1099511628211UL; }
                }
            return any ? hash : 0;
        }
    }
}
