using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Fixes from the 2026-10-28 audit: closest points on collapsed triangles, clearance fields spread over any number of
    /// rings, shown shapes turning the captured normals, inert staging copies and the four-influence notice.
    /// </summary>
    public sealed class ReFitAuditFixTests
    {
        [Test]
        public void CollapsedTrianglesGiveFiniteClosestPoints()
        {
            var cases = new[]
            {
                // two corners on one point, all three on a line, all three on one point
                (Vector3.zero, Vector3.zero, Vector3.right, new Vector3(.5f, 1f, 0f), new Vector3(.5f, 0f, 0f)),
                (Vector3.right, Vector3.zero, Vector3.zero, new Vector3(.5f, 1f, 0f), new Vector3(.5f, 0f, 0f)),
                (Vector3.zero, Vector3.right, Vector3.right * 2f, new Vector3(1.5f, 1f, 0f), new Vector3(1.5f, 0f, 0f)),
                (Vector3.zero, Vector3.right * 2f, Vector3.right, new Vector3(3f, 1f, 0f), new Vector3(2f, 0f, 0f)),
                (Vector3.one, Vector3.one, Vector3.one, Vector3.zero, Vector3.one),
            };
            foreach (var (a, b, c, p, expected) in cases)
            {
                var q = SurfaceBvh.ClosestPointOnTriangle(p, a, b, c, out var bary);
                Assert.That(Finite(q) && Finite(bary), Is.True, $"{a} {b} {c} from {p}: {q} {bary}");
                Assert.That((q - expected).magnitude, Is.LessThan(1e-5f), $"{a} {b} {c} from {p}: {q}");
                Assert.That(bary.x + bary.y + bary.z, Is.EqualTo(1f).Within(1e-5f));
                Assert.That((a * bary.x + b * bary.y + c * bary.z - q).magnitude, Is.LessThan(1e-5f), "Barycentric coordinates give the point.");
            }

            // A triangle with area answers as before.
            var flat = SurfaceBvh.ClosestPointOnTriangle(new Vector3(.25f, .25f, 1f), Vector3.zero, Vector3.right, Vector3.up, out var flatBary);
            Assert.That((flat - new Vector3(.25f, .25f, 0f)).magnitude, Is.LessThan(1e-6f));
            Assert.That((flatBary - new Vector3(.5f, .25f, .25f)).magnitude, Is.LessThan(1e-6f));
        }

        [Test]
        public void ClearanceCorrectionsReachEveryConnectedRing()
        {
            // A 100-group strip seeded at one end: every group is connected to the seed, none takes the fallback.
            const int n = 100;
            var (groups, localIndex, adjacency) = Chain(n);
            var field = Field(n);
            var valid = new bool[n];
            field.corrections[0] = new Vector3(0f, .01f, 0f);
            field.receiverWeights[0] = 1f;
            valid[0] = true;
            ReFitClearanceCorrection.FillIslandTargetCorrectionField(groups, localIndex, adjacency, field, valid, new Vector3(0f, 0f, .5f));
            Assert.That(valid.All(v => v), Is.True);
            for (int i = 0; i < n; i++)
                Assert.That((field.corrections[i] - new Vector3(0f, .01f, 0f)).magnitude, Is.LessThan(1e-7f), "group " + i);
        }

        [Test]
        public void ClearanceFrontierMatchesRoundByRoundFilling()
        {
            // Random islands within 48 rings, several seeds and an unreachable piece: the frontier gives exactly what the
            // former round-by-round scan of every group gave.
            var random = new System.Random(7);
            for (int trial = 0; trial < 20; trial++)
            {
                int n = 30 + random.Next(170);
                var adjacency = new List<int>[n + 5];
                for (int g = 0; g < adjacency.Length; g++) adjacency[g] = new List<int>();
                // Island groups are 5..n+4 (ids that are not indices), the last tenth stays unconnected to the rest.
                int reachable = n - n / 10;
                for (int i = 1; i < n; i++)
                {
                    int j = i < reachable ? random.Next(i) : reachable + random.Next(Mathf.Max(1, i - reachable));
                    if (j == i) continue;
                    adjacency[i + 5].Add(j + 5);
                    if (random.NextDouble() < .9) adjacency[j + 5].Add(i + 5); // mostly symmetric
                }
                var groups = Enumerable.Range(5, n).ToList();
                var localIndex = Enumerable.Repeat(-1, n + 5).ToArray();
                for (int i = 0; i < n; i++) localIndex[i + 5] = i;

                var a = Field(n);
                var valid = new bool[n];
                for (int s = 0; s < 4; s++)
                {
                    int i = random.Next(reachable);
                    a.corrections[i] = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * .02f;
                    a.receiverWeights[i] = (float)random.NextDouble();
                    a.supportComponents[i] = s;
                    a.supportGroups[i] = 100 + s;
                    a.supportDistances[i] = (float)random.NextDouble();
                    valid[i] = true;
                }
                var b = Copy(a);
                var validB = (bool[])valid.Clone();
                var fallback = new Vector3(1f, 2f, 3f);
                ReFitClearanceCorrection.FillIslandTargetCorrectionField(groups, localIndex, adjacency, a, valid, fallback);
                RoundByRound(groups, localIndex, adjacency, b, validB, fallback);
                for (int i = 0; i < n; i++)
                {
                    Assert.That(a.corrections[i], Is.EqualTo(b.corrections[i]), $"trial {trial} group {i}");
                    Assert.That(a.receiverWeights[i], Is.EqualTo(b.receiverWeights[i]));
                    Assert.That(a.supportComponents[i], Is.EqualTo(b.supportComponents[i]));
                    Assert.That(a.supportGroups[i], Is.EqualTo(b.supportGroups[i]));
                    Assert.That(a.supportDistances[i], Is.EqualTo(b.supportDistances[i]));
                }
            }
        }

        [Test]
        public void ShownShapesTurnTheCapturedNormals()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up },
                triangles = new[] { 0, 2, 1 },
            };
            // The shape lifts the triangle and tilts its normals towards +x.
            var tilt = (new Vector3(1f, 1f, 0f).normalized - Vector3.up);
            mesh.AddBlendShapeFrame("Tilt", 100f, new[] { Vector3.up, Vector3.up, Vector3.up }, new[] { tilt, tilt, tilt }, null);
            try
            {
                var go = new GameObject("Shaped");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                var renderer = go.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = mesh;
                renderer.SetBlendShapeWeight(0, 100f);

                var shown = MeshSnapshot.Capture(renderer, false, null, null);
                Assert.That(shown.localVertices[0], Is.EqualTo(Vector3.up));
                Assert.That(Vector3.Angle(shown.worldNormals[0], new Vector3(1f, 1f, 0f)), Is.LessThan(.01f), "Normals follow the shown shape.");
                Assert.That(shown.baseNormals[0], Is.EqualTo(Vector3.up), "The authored normals stay the rest normals.");

                var rest = MeshSnapshot.Capture(renderer, false, new Dictionary<int, float> { [0] = 0f }, null);
                Assert.That(Vector3.Angle(rest.worldNormals[0], Vector3.up), Is.LessThan(.01f));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void StagingCopiesNeverEnableTheirScripts()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var original = new GameObject("Original");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(original, scene);
                var child = new GameObject("Child").transform;
                child.SetParent(original.transform, false);
                child.gameObject.AddComponent<EnableCounter>();
                var parent = new GameObject("Stage").transform;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(parent.gameObject, scene);
                original.transform.position = new Vector3(1f, 2f, 3f);

                EnableCounter.enables = 0;
                var clone = PoseNormalizer.Clone(original, parent);
                try
                {
                    Assert.That(EnableCounter.enables, Is.Zero, "OnEnable ran on the staging copy.");
                    Assert.That(clone.activeInHierarchy, Is.True);
                    Assert.That(clone.transform.parent, Is.SameAs(parent));
                    Assert.That(clone.transform.position, Is.EqualTo(original.transform.position));
                    Assert.That(clone.GetComponentInChildren<EnableCounter>(true).enabled, Is.False);
                }
                finally
                {
                    Object.DestroyImmediate(clone);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ReducedSkinInfluencesAreReported()
        {
            var six = SkinnedQuad(6);
            var four = SkinnedQuad(4);
            try
            {
                var report = new ReFitReport();
                ReFitEngine.ReportReducedInfluences(six, four, report);
                Assert.That(report.messages.Select(m => m.code), Is.EqualTo(new[] { "skin-influences-reduced" }));
                Assert.That(report.messages[0].severity, Is.EqualTo(ReFitSeverity.Warning));

                var kept = new ReFitReport();
                ReFitEngine.ReportReducedInfluences(six, six, kept);
                ReFitEngine.ReportReducedInfluences(four, four, kept);
                Assert.That(kept.messages, Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(six);
                Object.DestroyImmediate(four);
            }
        }

        private static Mesh SkinnedQuad(int influences)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, influences).ToArray();
            var perVertex = new Unity.Collections.NativeArray<byte>(3, Unity.Collections.Allocator.Temp);
            var weights = new Unity.Collections.NativeArray<BoneWeight1>(3 * influences, Unity.Collections.Allocator.Temp);
            for (int v = 0; v < 3; v++)
            {
                perVertex[v] = (byte)influences;
                for (int k = 0; k < influences; k++)
                    weights[v * influences + k] = new BoneWeight1 { boneIndex = k, weight = (influences - k) / (influences * (influences + 1) / 2f) };
            }
            mesh.SetBoneWeights(perVertex, weights);
            perVertex.Dispose();
            weights.Dispose();
            return mesh;
        }

        /// <summary>Counts OnEnable calls, in edit mode too (a staging copy must never enable it).</summary>
        [ExecuteAlways]
        private sealed class EnableCounter : MonoBehaviour
        {
            public static int enables;
            private void OnEnable() => enables++;
        }

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

        private static (List<int>, int[], List<int>[]) Chain(int n)
        {
            var adjacency = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                adjacency[i] = new List<int>();
                if (i > 0) adjacency[i].Add(i - 1);
                if (i < n - 1) adjacency[i].Add(i + 1);
            }
            return (Enumerable.Range(0, n).ToList(), Enumerable.Range(0, n).ToArray(), adjacency);
        }

        private static ReFitClearanceCorrection.IslandTargetCorrectionField Field(int n) => new ReFitClearanceCorrection.IslandTargetCorrectionField
        {
            corrections = new Vector3[n],
            receiverWeights = Enumerable.Repeat(.4f, n).ToArray(),
            supportComponents = Enumerable.Repeat(-1, n).ToArray(),
            supportGroups = Enumerable.Repeat(-1, n).ToArray(),
            supportDistances = Enumerable.Repeat(.3f, n).ToArray(),
            averageWeight = .4f,
            maxSupportDistance = .3f,
            dominantSupportComponent = -1,
            dominantSupportGroup = -1,
        };

        private static ReFitClearanceCorrection.IslandTargetCorrectionField Copy(ReFitClearanceCorrection.IslandTargetCorrectionField f)
        {
            var copy = f;
            copy.corrections = (Vector3[])f.corrections.Clone();
            copy.receiverWeights = (float[])f.receiverWeights.Clone();
            copy.supportComponents = (int[])f.supportComponents.Clone();
            copy.supportGroups = (int[])f.supportGroups.Clone();
            copy.supportDistances = (float[])f.supportDistances.Clone();
            return copy;
        }

        // The filling as it was before the frontier (48 rounds over every group), the reference for short islands.
        private static void RoundByRound(List<int> groups, int[] localIndex, List<int>[] adjacency,
            ReFitClearanceCorrection.IslandTargetCorrectionField field, bool[] valid, Vector3 fallback)
        {
            var fill = new Vector3[field.corrections.Length];
            var fillWeight = new float[field.corrections.Length];
            var fillSupportComponent = new int[field.corrections.Length];
            var fillSupportGroup = new int[field.corrections.Length];
            var fillSupportDistance = new float[field.corrections.Length];
            var fillValid = new bool[field.corrections.Length];
            for (int iteration = 0; iteration < 48; iteration++)
            {
                bool changed = false;
                System.Array.Clear(fillValid, 0, fillValid.Length);
                for (int i = 0; i < groups.Count; i++)
                {
                    if (valid[i]) continue;
                    int g = groups[i];
                    if (adjacency == null || g < 0 || g >= adjacency.Length || adjacency[g] == null) continue;
                    Vector3 total = Vector3.zero;
                    float weightTotal = 0f;
                    int count = 0;
                    int bestSupportComponent = field.dominantSupportComponent;
                    int bestSupportGroup = field.dominantSupportGroup;
                    float bestWeight = -1f;
                    float maxDistance = 0f;
                    var neighbors = adjacency[g];
                    for (int n = 0; n < neighbors.Count; n++)
                    {
                        int nb = neighbors[n];
                        if (nb < 0 || nb >= localIndex.Length) continue;
                        int local = localIndex[nb];
                        if (local < 0 || !valid[local]) continue;
                        float weight = Mathf.Max(0.05f, field.receiverWeights[local]);
                        total += field.corrections[local] * weight;
                        weightTotal += weight;
                        count++;
                        maxDistance = Mathf.Max(maxDistance, field.supportDistances[local]);
                        if (weight > bestWeight)
                        {
                            bestWeight = weight;
                            bestSupportComponent = field.supportComponents[local];
                            bestSupportGroup = field.supportGroups[local];
                        }
                    }
                    if (count <= 0 || weightTotal <= 1e-6f) continue;
                    fill[i] = total / weightTotal;
                    fillWeight[i] = Mathf.Clamp01(weightTotal / count);
                    fillSupportComponent[i] = bestSupportComponent;
                    fillSupportGroup[i] = bestSupportGroup;
                    fillSupportDistance[i] = maxDistance;
                    fillValid[i] = true;
                    changed = true;
                }
                if (!changed) break;
                for (int i = 0; i < fillValid.Length; i++)
                {
                    if (!fillValid[i]) continue;
                    field.corrections[i] = fill[i];
                    field.receiverWeights[i] = fillWeight[i];
                    field.supportComponents[i] = fillSupportComponent[i];
                    field.supportGroups[i] = fillSupportGroup[i];
                    field.supportDistances[i] = fillSupportDistance[i];
                    valid[i] = true;
                }
            }
            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i]) continue;
                field.corrections[i] = fallback;
                field.receiverWeights[i] = field.averageWeight;
                field.supportComponents[i] = field.dominantSupportComponent;
                field.supportGroups[i] = field.dominantSupportGroup;
                field.supportDistances[i] = field.maxSupportDistance;
                valid[i] = true;
            }
        }
    }

}
