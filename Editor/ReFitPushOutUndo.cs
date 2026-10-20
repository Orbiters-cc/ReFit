using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The positions and normals of a mesh the push-out brush (<see cref="ReFitClipFixTool"/>) changed, recorded for Undo in
    /// place of the mesh: a stroke's undo step keeps only them, not the whole mesh with its blendshapes (hundreds of MB on a
    /// body-sized piece). Undo and Redo put them back on the mesh, also after the brush ended. One per mesh, never saved.
    /// </summary>
    internal sealed class ReFitPushOutUndo : ScriptableObject
    {
        public Mesh mesh;
        public Vector3[] vertices;
        public Vector3[] normals;

        /// <summary>The record of <paramref name="mesh"/>, made the first time.</summary>
        public static ReFitPushOutUndo For(Mesh mesh)
        {
            foreach (var existing in Resources.FindObjectsOfTypeAll<ReFitPushOutUndo>())
                if (existing != null && existing.mesh == mesh) return existing;
            var record = CreateInstance<ReFitPushOutUndo>();
            record.hideFlags = HideFlags.HideAndDontSave;
            record.mesh = mesh;
            return record;
        }

        /// <summary>One stroke, as an undo step: the mesh went from <paramref name="before"/> to its positions and normals now.</summary>
        public void Record(Vector3[] before, Vector3[] normalsBefore, string undoName)
        {
            vertices = before;
            normals = normalsBefore;
            Undo.RecordObject(this, undoName);
            vertices = mesh.vertices;
            normals = mesh.normals;
        }

        [InitializeOnLoadMethod]
        private static void FollowUndo() => Undo.undoRedoPerformed += Apply;

        /// <summary>Puts each record's positions and normals back on its mesh where an Undo or Redo changed them.</summary>
        internal static void Apply()
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<ReFitPushOutUndo>())
            {
                var mesh = record != null ? record.mesh : null;
                if (mesh == null || record.vertices == null || record.vertices.Length != mesh.vertexCount) continue;
                var current = mesh.vertices;
                bool changed = false;
                for (int i = 0; i < current.Length && !changed; i++)
                {
                    // Exactly: Vector3's own != lets moves under 0.01 mm through.
                    Vector3 a = current[i], b = record.vertices[i];
                    changed = a.x != b.x || a.y != b.y || a.z != b.z;
                }
                if (!changed) continue;
                mesh.vertices = record.vertices;
                if (record.normals != null && record.normals.Length == record.vertices.Length) mesh.normals = record.normals;
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
            }
        }
    }
}
