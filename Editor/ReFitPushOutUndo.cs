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
        // One more with each stroke; Undo and Redo bring it back with the positions.
        public int revision;

        // The revision the mesh shows, out of Undo's reach (and kept through domain reloads): only a record Undo changed is
        // put back on its mesh, so undoing anything else never overwrites what other tools did to the mesh since.
        private string ShownKey => "Orbiters.ReFit.PushOut.Shown." + GetInstanceID();

        private int Shown
        {
            get => SessionState.GetInt(ShownKey, 0);
            set => SessionState.SetInt(ShownKey, value);
        }

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
            revision++;
            Shown = revision;
        }

        private void OnDestroy() => SessionState.EraseInt(ShownKey);

        [InitializeOnLoadMethod]
        private static void FollowUndo() => Undo.undoRedoPerformed += Apply;

        /// <summary>
        /// Puts each record an Undo or Redo changed back on its mesh; the others are left alone. Called again for the same
        /// Undo (the brush asks first, to read the piece again), it finds nothing left to do.
        /// </summary>
        internal static void Apply()
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<ReFitPushOutUndo>())
            {
                if (record == null || record.revision == record.Shown) continue;
                record.Shown = record.revision;
                var mesh = record.mesh;
                if (mesh == null || record.vertices == null || record.vertices.Length != mesh.vertexCount) continue;
                mesh.vertices = record.vertices;
                if (record.normals != null && record.normals.Length == record.vertices.Length) mesh.normals = record.normals;
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
            }
        }
    }
}
