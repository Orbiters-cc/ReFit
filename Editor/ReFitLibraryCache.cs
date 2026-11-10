using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>Points ReFit's saved registrations at the project's Library/ReFit when the editor loads.</summary>
    internal static class ReFitLibraryCache
    {
        [InitializeOnLoadMethod]
        private static void Locate() =>
            ReFitCaches.LibraryDirectory = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "ReFit");
    }
}
