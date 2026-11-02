using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The rigid motion (a turn and a move) that best takes a set of points to where they were asked to go, in the least
    /// squares sense (Horn's closed form with unit quaternions). A small piece (a button, a buckle, a chain's link) moved by it
    /// keeps its shape and turns with the skin under it, where the mean of the motions would only slide it. The turn is at
    /// most <see cref="MaxTurn"/>: a piece only tilts with the skin, and motions far from rigid (a link half in the skin pushed
    /// out at one end) would otherwise flip it over.
    /// </summary>
    internal static class RigidFit
    {
        // Spread of a piece (metres², per point, along its second axis) below which it is a line: its turn about that line is
        // unknown, and only the move is used.
        private const float FlatSpread = 1e-8f;
        /// <summary>The largest turn a piece takes (degrees).</summary>
        public const float MaxTurn = 30f;

        /// <summary>
        /// Replaces the motions of <paramref name="piece"/>'s points (positions <paramref name="basis"/>[i], asked to move by
        /// <paramref name="move"/>[i]) by the best rigid motion's. Pieces of fewer than three points, or along a line, take
        /// the mean motion.
        /// </summary>
        public static void Apply(List<int> piece, Vector3[] basis, Vector3[] move)
        {
            int count = piece.Count;
            if (count == 0) return;
            Vector3 from = Vector3.zero, to = Vector3.zero;
            foreach (int i in piece) { from += basis[i]; to += basis[i] + move[i]; }
            from /= count;
            to /= count;
            var mean = to - from;
            if (count < 3 || !Turn(piece, basis, move, from, to, out var rotation))
            {
                foreach (int i in piece) move[i] = mean;
                return;
            }
            rotation.ToAngleAxis(out float angle, out _);
            if (angle > 180f) angle = 360f - angle;
            if (angle > MaxTurn) rotation = Quaternion.Slerp(Quaternion.identity, rotation, MaxTurn / angle);
            foreach (int i in piece) move[i] = to + rotation * (basis[i] - from) - basis[i];
        }

        /// <summary>
        /// The pieces of <paramref name="asset"/> (welded groups connected by its edges) whose authored extent is at most
        /// <paramref name="extent"/>, the whole asset excepted; <paramref name="of"/>: each group's piece, or -1.
        /// </summary>
        public static List<int>[] Pieces(MeshSnapshot asset, float extent, out int[] of)
        {
            int groupCount = asset.GroupCount;
            var adjacency = asset.groupAdjacency;
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
                if ((max - min).magnitude > extent || piece.Count == groupCount) continue;
                foreach (int x in piece) of[x] = list.Count;
                list.Add(piece);
            }
            return list.ToArray();
        }

        // Horn: the cross-covariance of the centred point sets gives a symmetric 4x4 matrix whose eigenvector of the largest
        // eigenvalue is the best unit quaternion.
        private static bool Turn(List<int> piece, Vector3[] basis, Vector3[] move, Vector3 from, Vector3 to, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
            double cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (int i in piece)
            {
                var p = basis[i] - from;
                var q = basis[i] + move[i] - to;
                sxx += p.x * q.x; sxy += p.x * q.y; sxz += p.x * q.z;
                syx += p.y * q.x; syy += p.y * q.y; syz += p.y * q.z;
                szx += p.z * q.x; szy += p.z * q.y; szz += p.z * q.z;
                cxx += p.x * p.x; cyy += p.y * p.y; czz += p.z * p.z;
                cxy += p.x * p.y; cxz += p.x * p.z; cyz += p.y * p.z;
            }
            // A piece along a line (a single strand): its second spread is about zero.
            var spread = new double[3, 3] { { cxx, cxy, cxz }, { cxy, cyy, cyz }, { cxz, cyz, czz } };
            Eigen3(spread, out var values);
            Array.Sort(values);
            if (values[1] / piece.Count < FlatSpread) return false;

            var n = new double[4, 4]
            {
                { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
                { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
                { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
                { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
            };
            var vectors = Jacobi(n, 4, out var eigen);
            int best = 0;
            for (int k = 1; k < 4; k++) if (eigen[k] > eigen[best]) best = k;
            var w = vectors[0, best];
            var x = vectors[1, best];
            var y = vectors[2, best];
            var z = vectors[3, best];
            double length = Math.Sqrt(w * w + x * x + y * y + z * z);
            if (length < 1e-12) return false;
            rotation = new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length));
            return true;
        }

        private static void Eigen3(double[,] matrix, out double[] values) => Jacobi(matrix, 3, out values);

        // Cyclic Jacobi rotations of a small symmetric matrix: its eigenvalues, and the eigenvectors as columns.
        private static double[,] Jacobi(double[,] input, int size, out double[] values)
        {
            var a = (double[,])input.Clone();
            var v = new double[size, size];
            for (int i = 0; i < size; i++) v[i, i] = 1;
            for (int sweep = 0; sweep < 32; sweep++)
            {
                double off = 0;
                for (int p = 0; p < size; p++)
                    for (int q = p + 1; q < size; q++) off += a[p, q] * a[p, q];
                if (off < 1e-30) break;
                for (int p = 0; p < size; p++)
                    for (int q = p + 1; q < size; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-300) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < size; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < size; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < size; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            values = new double[size];
            for (int i = 0; i < size; i++) values[i] = a[i, i];
            return v;
        }
    }
}
