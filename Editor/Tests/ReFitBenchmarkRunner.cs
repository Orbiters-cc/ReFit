using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        /// <summary>Runs an independently supplied reference and the current engine back-to-back, alternating order.</summary>
        public static void CompareHoodieEngines(Func<ReFitRequest, ReFitComputation> reference,
            ReFitSettings settings, string label)
        {
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            if (string.IsNullOrEmpty(label) || label != Path.GetFileName(label))
                throw new ArgumentException("Benchmark label must be a file name.", nameof(label));
            string path = Path.GetFullPath("Temp/ReFitBenchmarks/" + label + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string> { JsonUtility.ToJson(settings) };
            try
            {
                for (int repeat = 0; repeat < 2; repeat++)
                for (int count = 1; count <= 4; count *= 4)
                using (var fixture = RealHoodieArtifactFixture.Create())
                {
                    var request = BuildRealHoodieSourceRequest(fixture);
                    request.settings = settings.Clone();
                    request.settings.savePrefab = false;
                    request.targetBlendshapes = new List<string> { RealTargetShapeName };
                    for (int s = 0; s < fixture.targetBody.sharedMesh.blendShapeCount && request.targetBlendshapes.Count < count; s++)
                    {
                        string name = fixture.targetBody.sharedMesh.GetBlendShapeName(s);
                        if (!request.targetBlendshapes.Contains(name)) request.targetBlendshapes.Add(name);
                    }
                    ReFitComputation before = null, after = null;
                    try
                    {
                        for (int order = 0; order < 2; order++)
                        {
                            bool isReference = (order + repeat) % 2 == 0;
                            var timer = Stopwatch.StartNew();
                            if (isReference) before = reference(request);
                            else after = new ReFitEngine().Run(request);
                            lines.Add($"repeat={repeat} shapes={count} engine={(isReference ? "reference" : "current")} ms={timer.Elapsed.TotalMilliseconds:F1}");
                        }
                        AssertEquivalentShapes(before, after, fixture.hoodie.transform.localToWorldMatrix, 0.00001f);
                        lines.Add("PASS geometry parity <= 0.01mm: " + string.Join(", ", request.targetBlendshapes));
                    }
                    finally { DestroyComputationMesh(before); DestroyComputationMesh(after); }
                }
            }
            catch (Exception e) { lines.Add("FAIL: " + e); throw; }
            finally { File.WriteAllLines(path, lines); }
        }

        // Local artifacts contain licensed geometry: never write these into the package or Assets.
        public static void BenchmarkHoodie(bool baseline)
        {
            string directory = Path.GetFullPath("Temp/ReFitBenchmarks");
            Directory.CreateDirectory(directory);
            var lines = new List<string>();
            try
            {
                for (int count = 1; count <= 4; count *= 4)
                using (var fixture = RealHoodieArtifactFixture.Create())
                {
                    var request = BuildRealHoodieSourceRequest(fixture);
                    request.settings = new ReFitSettings { savePrefab = false };
                    request.targetBlendshapes = new List<string> { RealTargetShapeName };
                    for (int s = 0; s < fixture.targetBody.sharedMesh.blendShapeCount && request.targetBlendshapes.Count < count; s++)
                    {
                        string name = fixture.targetBody.sharedMesh.GetBlendShapeName(s);
                        if (!request.targetBlendshapes.Contains(name)) request.targetBlendshapes.Add(name);
                    }
                    var timer = Stopwatch.StartNew();
                    double previous = 0;
                    string label = "start";
                    var comp = new ReFitEngine().Run(request, (progress, next) =>
                    {
                        double elapsed = timer.Elapsed.TotalMilliseconds;
                        lines.Add(count + " shapes: " + label + " = " + (elapsed - previous).ToString("F1") + " ms");
                        previous = elapsed; label = next;
                    });
                    try
                    {
                        AssertComputationSucceeded(comp);
                        lines.Add(count + " shapes total=" + timer.Elapsed.TotalMilliseconds.ToString("F1") + "ms");
                        string path = Path.Combine(directory, "hoodie-" + count + ".bin");
                        if (baseline) SaveBenchmarkGeometry(path, comp.mesh);
                        else lines.Add(count + " shapes max output drift=" + CompareBenchmarkGeometry(path, comp.mesh));
                    }
                    finally { DestroyComputationMesh(comp); }
                }
            }
            catch (Exception e) { lines.Add("FAIL: " + e); throw; }
            finally
            {
                File.WriteAllLines(Path.Combine(directory, baseline ? "baseline.txt" : "current.txt"), lines);
                Debug.Log("[ReFit Benchmark] " + string.Join("\n", lines));
            }
        }

        private static void SaveBenchmarkGeometry(string path, Mesh mesh)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(mesh.vertexCount); writer.Write(mesh.blendShapeCount);
                WriteVectors(writer, mesh.vertices);
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    writer.Write(mesh.GetBlendShapeName(s));
                    var deltas = new Vector3[mesh.vertexCount];
                    mesh.GetBlendShapeFrameVertices(s, mesh.GetBlendShapeFrameCount(s) - 1, deltas, null, null);
                    WriteVectors(writer, deltas);
                }
            }
        }

        private static void WriteVectors(BinaryWriter writer, Vector3[] values)
        {
            foreach (var v in values) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
        }

        private static float CompareBenchmarkGeometry(string path, Mesh mesh)
        {
            float maximum = 0;
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                AssertTrue(reader.ReadInt32() == mesh.vertexCount && reader.ReadInt32() == mesh.blendShapeCount, "Benchmark mesh layout changed.");
                for (int s = -1; s < mesh.blendShapeCount; s++)
                {
                    var deltas = mesh.vertices;
                    if (s >= 0)
                    {
                        AssertTrue(reader.ReadString() == mesh.GetBlendShapeName(s), "Benchmark shape order changed.");
                        mesh.GetBlendShapeFrameVertices(s, mesh.GetBlendShapeFrameCount(s) - 1, deltas, null, null);
                    }
                    foreach (var v in deltas)
                        maximum = Mathf.Max(maximum, (v - new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())).magnitude);
                }
            }
            AssertLessOrEqual(maximum, 0.00001f, "Optimization changed geometry by more than 0.01mm.");
            return maximum;
        }
    }
}
