using System;
using Orbiters.Toolkit.Meshes;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>A test's own acceptance test as a hierarchy rule: decided once per triangle (managed), then queried in Burst.</summary>
    internal static class ReFitTestRules
    {
        public static TriangleRule Where(MeshBvh index, Func<int, bool> accept)
        {
            var flags = new bool[index.TriangleCount];
            for (int t = 0; t < flags.Length; t++) flags[t] = accept(t);
            return TriangleRule.All.Among(flags);
        }
    }
}
