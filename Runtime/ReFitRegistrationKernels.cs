using Unity.Burst;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The registration solve's inner loops over a range of nodes (<see cref="ReFitBodyRegistration"/>): the product with the
    /// system's matrix and the conjugate gradients' vector updates, three axes interleaved ([3i + axis]). Burst compiles them
    /// where it is installed (called directly, from any thread); elsewhere, or until Burst has compiled them, they run as they
    /// are written. Every operation is in double precision in a fixed order (Strict: nothing reassociated, no fused
    /// multiply-add), so both give the same bits, on every machine. The caller sums the per-range partial dot products in
    /// range order.
    /// </summary>
    [BurstCompile]
    internal static unsafe class ReFitRegistrationKernels
    {
        /// <summary>
        /// output = A input over nodes [begin, end), A = diag(weight + anchor + stiffness degree) - stiffness adjacency; with
        /// <paramref name="dot"/>, partial[0..2] = the range's input . output per axis.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void Multiply(double* input, double* output, int* start, int* next, float* weights, double anchor, double stiffness,
            int begin, int end, int dot, double* partial)
        {
            double d0 = 0, d1 = 0, d2 = 0;
            for (int i = begin; i < end; i++)
            {
                double s0 = 0, s1 = 0, s2 = 0;
                int last = start[i + 1];
                for (int e = start[i]; e < last; e++)
                {
                    int j = 3 * next[e];
                    s0 += input[j]; s1 += input[j + 1]; s2 += input[j + 2];
                }
                double self = (double)weights[i] + anchor + stiffness * (double)(last - start[i]);
                int k = 3 * i;
                double o0 = self * input[k] - stiffness * s0;
                double o1 = self * input[k + 1] - stiffness * s1;
                double o2 = self * input[k + 2] - stiffness * s2;
                output[k] = o0; output[k + 1] = o1; output[k + 2] = o2;
                if (dot != 0) { d0 += input[k] * o0; d1 += input[k + 1] * o1; d2 += input[k + 2] * o2; }
            }
            partial[0] = d0; partial[1] = d1; partial[2] = d2;
        }

        /// <summary>
        /// r = b - q, z = r / diagonal, p = z over nodes [begin, end); partial[0..2] = r . z, partial[3..5] = b . b per axis.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void Start(double* b, double* q, double* r, double* z, double* p, double* diagonal, int begin, int end, double* partial)
        {
            double rz0 = 0, rz1 = 0, rz2 = 0, b0 = 0, b1 = 0, b2 = 0;
            for (int i = begin; i < end; i++)
            {
                double inverse = 1.0 / diagonal[i];
                int k = 3 * i;
                for (int axis = 0; axis < 3; axis++)
                {
                    r[k + axis] = b[k + axis] - q[k + axis];
                    z[k + axis] = r[k + axis] * inverse;
                    p[k + axis] = z[k + axis];
                }
                rz0 += r[k] * z[k]; rz1 += r[k + 1] * z[k + 1]; rz2 += r[k + 2] * z[k + 2];
                b0 += b[k] * b[k]; b1 += b[k + 1] * b[k + 1]; b2 += b[k + 2] * b[k + 2];
            }
            partial[0] = rz0; partial[1] = rz1; partial[2] = rz2;
            partial[3] = b0; partial[4] = b1; partial[5] = b2;
        }

        /// <summary>
        /// x += alpha p, r -= alpha q, z = r / diagonal over nodes [begin, end) (alpha per axis); partial[0..2] = r . r,
        /// partial[3..5] = r . z per axis.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void Step(double* x, double* r, double* z, double* p, double* q, double* diagonal, double alpha0, double alpha1, double alpha2,
            int begin, int end, double* partial)
        {
            double res0 = 0, res1 = 0, res2 = 0, next0 = 0, next1 = 0, next2 = 0;
            for (int i = begin; i < end; i++)
            {
                double inverse = 1.0 / diagonal[i];
                int k = 3 * i;
                x[k] += alpha0 * p[k]; r[k] -= alpha0 * q[k]; z[k] = r[k] * inverse;
                x[k + 1] += alpha1 * p[k + 1]; r[k + 1] -= alpha1 * q[k + 1]; z[k + 1] = r[k + 1] * inverse;
                x[k + 2] += alpha2 * p[k + 2]; r[k + 2] -= alpha2 * q[k + 2]; z[k + 2] = r[k + 2] * inverse;
                res0 += r[k] * r[k]; res1 += r[k + 1] * r[k + 1]; res2 += r[k + 2] * r[k + 2];
                next0 += r[k] * z[k]; next1 += r[k + 1] * z[k + 1]; next2 += r[k + 2] * z[k + 2];
            }
            partial[0] = res0; partial[1] = res1; partial[2] = res2;
            partial[3] = next0; partial[4] = next1; partial[5] = next2;
        }

        /// <summary>p = z + beta p over nodes [begin, end), for the axes still active (bit axis of <paramref name="active"/>).</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public static void Direction(double* p, double* z, double beta0, double beta1, double beta2, int active, int begin, int end)
        {
            for (int i = begin; i < end; i++)
            {
                int k = 3 * i;
                if ((active & 1) != 0) p[k] = z[k] + beta0 * p[k];
                if ((active & 2) != 0) p[k + 1] = z[k + 1] + beta1 * p[k + 1];
                if ((active & 4) != 0) p[k + 2] = z[k + 2] + beta2 * p[k + 2];
            }
        }
    }
}
