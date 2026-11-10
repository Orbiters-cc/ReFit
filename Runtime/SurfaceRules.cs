using Orbiters.Toolkit.Meshes;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The query rules ReFit's stages use, as Toolkit <see cref="TriangleRule"/>s: the hierarchy (<see cref="MeshBvh"/>) runs them in
    /// Burst, so a filtered answer has the same bits on every machine as an unfiltered one.
    /// </summary>
    internal static class SurfaceRules
    {
        /// <summary>
        /// Triangles facing <paramref name="direction"/> (the dot product of the unit face normal at least
        /// <paramref name="minimum"/>, above it when <paramref name="strict"/>), of a region <paramref name="mask"/> accepts when
        /// <paramref name="triangleMasks"/> is given (a mask of 0 on either side accepts), among <paramref name="allowed"/> when
        /// given.
        /// </summary>
        /// <param name="faceNormals">Unit face normals by triangle (<see cref="MeshSnapshot.FaceNormals"/>).</param>
        public static TriangleRule Facing(Vector3[] faceNormals, Vector3 direction, float minimum, int mask = 0, int[] triangleMasks = null,
            bool[] allowed = null, bool strict = false)
        {
            var rule = TriangleRule.All.Facing(faceNormals, direction, minimum, strict ? FacingTest.Above : FacingTest.AtLeast);
            if (triangleMasks != null && mask != 0) rule = rule.InMasks(triangleMasks, mask);
            if (allowed != null) rule = rule.Among(allowed);
            return rule;
        }

        /// <summary>Faces turned against <paramref name="direction"/> (a negative dot product with it).</summary>
        public static TriangleRule Against(Vector3[] faceNormals, Vector3 direction) => TriangleRule.All.Against(faceNormals, direction);

        /// <summary>Whether triangle <paramref name="t"/> of the face normals faces as <see cref="Facing"/> asks (managed, for code deciding outside a query).</summary>
        public static bool Faces(Vector3[] faceNormals, int t, Vector3 direction, float minimum)
        {
            var n = faceNormals[t];
            return n.x * direction.x + n.y * direction.y + n.z * direction.z >= minimum;
        }

        private static bool[][] compatibleRegions;

        /// <summary>
        /// Triangles of a region compatible with <paramref name="region"/> (<see cref="HumanoidBoneMapper.RegionsCompatible"/>):
        /// <paramref name="triangleRegions"/> as labels against the region's row of compatible regions, a triangle of no region
        /// (Unknown, -1: outside the row) always compatible.
        /// </summary>
        public static TriangleRule CompatibleRegion(this TriangleRule rule, BodyRegion[] triangleRegions, BodyRegion region, bool second = false) =>
            second
                ? rule.AlsoLabelled(Labels(triangleRegions), CompatibleWith(region), outsideAccepted: true)
                : rule.Labelled(Labels(triangleRegions), CompatibleWith(region), outsideAccepted: true);

        // An enum array is an array of its underlying type to the runtime: the regions are read as labels in place.
        private static int[] Labels(BodyRegion[] regions) => (int[])(object)regions;

        // The regions compatible with region, by region value (0 up); Unknown is compatible with all.
        private static bool[] CompatibleWith(BodyRegion region)
        {
            var table = compatibleRegions;
            if (table == null)
            {
                int count = 0;
                foreach (BodyRegion v in System.Enum.GetValues(typeof(BodyRegion))) count = Mathf.Max(count, (int)v + 1);
                // Row 0 is Unknown's, row r + 1 region r's.
                table = new bool[count + 1][];
                for (int a = -1; a < count; a++)
                {
                    table[a + 1] = new bool[count];
                    for (int b = 0; b < count; b++) table[a + 1][b] = HumanoidBoneMapper.RegionsCompatible((BodyRegion)a, (BodyRegion)b);
                }
                compatibleRegions = table;
            }
            int index = (int)region + 1;
            return index >= 0 && index < table.Length ? table[index] : table[0];
        }
    }
}
