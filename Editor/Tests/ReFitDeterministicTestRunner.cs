using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    /// <summary>
    /// Deterministic in-memory ReFit checks. These intentionally avoid Unity Test Framework dependencies so they
    /// can be run from the menu, batchmode, CI, or by agents with only an editor executeMethod call.
    /// </summary>
    public static class ReFitDeterministicTestRunner
    {
        private const string BodyShapeName = "TestMuscle";
        private const float PrimaryRefitDriftTolerance = 0.002f;
        private const float ChestForwardDeltaMinimum = 0.06f;
        private const float ArmOutwardDeltaMinimum = 0.02f;
        private const float WaistDeltaMaximum = 0.012f;

        [MenuItem("Tools/Orbiters/ReFit/Run Deterministic Tests")]
        public static void RunFromMenu()
        {
            try
            {
                RunOrThrow();
                EditorUtility.DisplayDialog("ReFit deterministic tests", "All deterministic ReFit tests passed.", "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("ReFit deterministic tests failed", e.Message, "OK");
            }
        }

        public static void RunBatchMode()
        {
            try
            {
                RunOrThrow();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void RunOrThrow()
        {
            var previousSelection = Selection.activeObject;
            var failures = new List<string>();
            try
            {
                RunCase(failures,
                    "MeshAndBlendshape equal body surfaces do not create primary refit drift",
                    MeshAndBlendshape_EqualSurfaces_NoPrimaryDrift_TransfersMuscle);
                RunCase(failures,
                    "Blendshape-only transfer works on target-space clothing and preserves root bone",
                    BlendshapeOnly_TargetSpaceAccessory_TransfersMuscle_PreservesRootBone);
                RunCase(failures,
                    "Mesh refit with armature replacement disabled preserves clothing root bone",
                    MeshAndBlendshape_ArmatureReplacementDisabled_PreservesRootBone);

                if (failures.Count > 0)
                    throw new Exception("[ReFit Tests] Failed deterministic checks:\n" + string.Join("\n", failures));

                Debug.Log("[ReFit Tests] All deterministic ReFit tests passed.");
            }
            finally
            {
                Selection.activeObject = previousSelection;
            }
        }

        private static void RunCase(List<string> failures, string name, Action test)
        {
            try
            {
                test();
                Debug.Log($"[ReFit Tests] PASS: {name}");
            }
            catch (Exception e)
            {
                failures.Add($"- {name}: {e.Message}");
                Debug.LogError($"[ReFit Tests] FAIL: {name}\n{e}");
            }
        }

        private static void MeshAndBlendshape_EqualSurfaces_NoPrimaryDrift_TransfersMuscle()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                AssertFixtureActuallyDiffers(fixture);

                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(!string.IsNullOrEmpty(comp.primaryShapeName),
                        "MeshAndBlendshape mode did not produce the primary refit blendshape.");

                    float maxPrimaryDrift = MaxBlendShapeMagnitude(comp.mesh, comp.primaryShapeName);
                    Debug.Log($"[ReFit Tests] Primary refit max drift: {maxPrimaryDrift * 1000f:0.###} mm");
                    AssertLessOrEqual(maxPrimaryDrift, PrimaryRefitDriftTolerance,
                        $"Expected no visible primary refit drift when source and target body surfaces are identical. " +
                        $"Max drift was {maxPrimaryDrift * 1000f:0.###} mm.");

                    AssertTransferredMuscleShape(comp.mesh, SingleSecondaryShape(comp));
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void BlendshapeOnly_TargetSpaceAccessory_TransfersMuscle_PreservesRootBone()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var originalRootBone = fixture.targetSpaceAccessory.renderer.rootBone;
                var request = BuildBlendshapeOnlyRequest(fixture, fixture.targetSpaceAccessory.renderer);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(string.IsNullOrEmpty(comp.primaryShapeName),
                        "Blendshape-only mode should not produce a primary refit blendshape.");

                    AssertTransferredMuscleShape(comp.mesh, SingleSecondaryShape(comp));

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertSame(applied.rootBone, originalRootBone,
                        $"Blendshape-only apply changed the clothing root bone from {PathOf(originalRootBone)} to {PathOf(applied.rootBone)}.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void MeshAndBlendshape_ArmatureReplacementDisabled_PreservesRootBone()
        {
            using (var fixture = ReFitTestFixture.Create())
            {
                var originalRootBone = fixture.sourceSpaceAccessory.renderer.rootBone;
                var request = BuildMeshAndBlendshapeRequest(fixture, fixture.sourceSpaceAccessory.renderer, false);
                var comp = new ReFitEngine().Run(request);
                try
                {
                    AssertComputationSucceeded(comp);
                    AssertTrue(!comp.armatureReplaced,
                        "The computation replaced the armature even though settings.replaceArmature was false.");

                    var applied = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report);
                    AssertTrue(applied != null, "ApplyToScene returned no renderer.");
                    AssertSame(applied.rootBone, originalRootBone,
                        $"Apply with armature replacement disabled changed the clothing root bone from {PathOf(originalRootBone)} to {PathOf(applied.rootBone)}.");
                    AssertTrue(applied.rootBone != fixture.target.hips,
                        "Apply with armature replacement disabled rebound the clothing root bone to the target avatar hips.");
                }
                finally
                {
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static ReFitRequest BuildMeshAndBlendshapeRequest(
            ReFitTestFixture fixture, SkinnedMeshRenderer accessory, bool replaceArmature)
        {
            return new ReFitRequest
            {
                mode = ReFitMode.MeshAndBlendshape,
                assetRenderer = accessory,
                sourceAvatar = fixture.source.root,
                targetAvatar = fixture.target.root,
                sourceBodyRenderer = fixture.source.renderer,
                targetBodyRenderer = fixture.target.renderer,
                targetBlendshape = BodyShapeName,
                settings = CreateDeterministicSettings(replaceArmature)
            };
        }

        private static ReFitRequest BuildBlendshapeOnlyRequest(ReFitTestFixture fixture, SkinnedMeshRenderer accessory)
        {
            return new ReFitRequest
            {
                mode = ReFitMode.Blendshape,
                assetRenderer = accessory,
                targetAvatar = fixture.target.root,
                targetBodyRenderer = fixture.target.renderer,
                targetBlendshape = BodyShapeName,
                settings = CreateDeterministicSettings(false)
            };
        }

        private static ReFitSettings CreateDeterministicSettings(bool replaceArmature)
        {
            return new ReFitSettings
            {
                maxProjectionDistance = 0.3f,
                falloffStartDistance = 0.08f,
                smoothingIterations = 0,
                smoothingStrength = 0f,
                filterByNormal = false,
                filterByBoneRegion = false,
                transferWeights = false,
                replaceArmature = replaceArmature,
                keepExtraBoneVertices = true,
                blendshapeName = "refit",
                prefixTransferredShapes = false,
                offsetMode = OffsetMode.Translate,
                recalculateNormalDeltas = false,
                savePrefab = false,
                proportionWarningThreshold = 1f
            };
        }

        private static void AssertFixtureActuallyDiffers(ReFitTestFixture fixture)
        {
            float chestBoneOffset = Vector3.Distance(fixture.source.chest.position, fixture.target.chest.position);
            AssertGreater(chestBoneOffset, 0.05f,
                "The deterministic fixture is invalid: source and target chest bones are not meaningfully different.");

            var sourceWeight = WeightAtClosestVertex(fixture.source.renderer.sharedMesh, new Vector2(0f, 1.05f));
            var targetWeight = WeightAtClosestVertex(fixture.target.renderer.sharedMesh, new Vector2(0f, 1.05f));
            float weightDifference = WeightDifference(sourceWeight, targetWeight);
            AssertGreater(weightDifference, 0.2f,
                "The deterministic fixture is invalid: source and target body skin weights are too similar.");
        }

        private static void AssertTransferredMuscleShape(Mesh mesh, string shapeName)
        {
            var chest = MeasureRegion(mesh, shapeName, v => Mathf.Abs(v.x) <= 0.35f && v.y >= 0.85f && v.y <= 1.3f);
            var leftArm = MeasureRegion(mesh, shapeName, v => v.x <= -0.75f && v.y >= 1f && v.y <= 1.5f);
            var rightArm = MeasureRegion(mesh, shapeName, v => v.x >= 0.75f && v.y >= 1f && v.y <= 1.5f);
            var waist = MeasureRegion(mesh, shapeName, v => Mathf.Abs(v.x) <= 0.45f && v.y <= 0.45f);

            Debug.Log(
                $"[ReFit Tests] {shapeName}: chest avg z {chest.average.z * 1000f:0.###} mm, " +
                $"left arm outward {leftArm.averageOutward * 1000f:0.###} mm, " +
                $"right arm outward {rightArm.averageOutward * 1000f:0.###} mm, " +
                $"waist max {waist.maxMagnitude * 1000f:0.###} mm");

            AssertGreater(chest.average.z, ChestForwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the chest far enough.");
            AssertGreater(leftArm.averageOutward, ArmOutwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the left upper arm far enough.");
            AssertGreater(rightArm.averageOutward, ArmOutwardDeltaMinimum,
                $"Transferred shape '{shapeName}' did not expand the right upper arm far enough.");
            AssertLessOrEqual(waist.maxMagnitude, WaistDeltaMaximum,
                $"Transferred shape '{shapeName}' moved the waist even though the target body shape has no waist delta.");
        }

        private static string SingleSecondaryShape(ReFitComputation comp)
        {
            AssertTrue(comp.secondaryShapeNames != null && comp.secondaryShapeNames.Length == 1,
                "Expected exactly one transferred blendshape.");
            AssertTrue(!string.IsNullOrEmpty(comp.secondaryShapeNames[0]),
                "Transferred blendshape name was empty.");
            return comp.secondaryShapeNames[0];
        }

        private static void AssertComputationSucceeded(ReFitComputation comp)
        {
            AssertTrue(comp != null, "ReFit returned no computation.");
            AssertTrue(comp.success, "ReFit computation failed.\n" + FormatReport(comp.report));
            AssertTrue(comp.mesh != null, "ReFit computation succeeded without producing a mesh.");
        }

        private static string FormatReport(ReFitReport report)
        {
            if (report == null || report.messages.Count == 0) return "(no report messages)";
            var lines = new List<string>();
            foreach (var message in report.messages)
                lines.Add(message.ToString());
            return string.Join("\n", lines);
        }

        private static float MaxBlendShapeMagnitude(Mesh mesh, string shapeName)
        {
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            float max = 0f;
            for (int i = 0; i < deltas.Length; i++)
                max = Mathf.Max(max, deltas[i].magnitude);
            return max;
        }

        private static RegionMetrics MeasureRegion(Mesh mesh, string shapeName, Func<Vector3, bool> contains)
        {
            var vertices = mesh.vertices;
            var deltas = GetBlendShapeDeltas(mesh, shapeName);
            var metrics = new RegionMetrics();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!contains(vertices[i])) continue;

                metrics.count++;
                metrics.average += deltas[i];
                metrics.maxMagnitude = Mathf.Max(metrics.maxMagnitude, deltas[i].magnitude);
                if (Mathf.Abs(vertices[i].x) > 1e-4f)
                    metrics.averageOutward += Mathf.Sign(vertices[i].x) * deltas[i].x;
            }

            AssertTrue(metrics.count > 0, $"No vertices matched the requested region for shape '{shapeName}'.");
            metrics.average /= metrics.count;
            metrics.averageOutward /= metrics.count;
            return metrics;
        }

        private static Vector3[] GetBlendShapeDeltas(Mesh mesh, string shapeName)
        {
            AssertTrue(mesh != null, "Cannot read blendshape deltas from a null mesh.");
            int shapeIndex = mesh.GetBlendShapeIndex(shapeName);
            AssertTrue(shapeIndex >= 0, $"Mesh '{mesh.name}' does not contain blendshape '{shapeName}'.");
            int frame = mesh.GetBlendShapeFrameCount(shapeIndex) - 1;
            AssertTrue(frame >= 0, $"Blendshape '{shapeName}' has no frames.");
            var deltas = new Vector3[mesh.vertexCount];
            mesh.GetBlendShapeFrameVertices(shapeIndex, frame, deltas, null, null);
            return deltas;
        }

        private static BoneWeight WeightAtClosestVertex(Mesh mesh, Vector2 target)
        {
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            AssertTrue(weights != null && weights.Length == vertices.Length,
                $"Mesh '{mesh.name}' has no usable bone weights.");

            int bestIndex = -1;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < vertices.Length; i++)
            {
                float sqr = (new Vector2(vertices[i].x, vertices[i].y) - target).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                bestIndex = i;
            }

            AssertTrue(bestIndex >= 0, $"Mesh '{mesh.name}' has no vertices.");
            return weights[bestIndex];
        }

        private static float WeightDifference(BoneWeight a, BoneWeight b)
        {
            float diff = 0f;
            for (int bone = 0; bone < 8; bone++)
                diff += Mathf.Abs(WeightOf(a, bone) - WeightOf(b, bone));
            return diff;
        }

        private static float WeightOf(BoneWeight weight, int bone)
        {
            float value = 0f;
            if (weight.boneIndex0 == bone) value += weight.weight0;
            if (weight.boneIndex1 == bone) value += weight.weight1;
            if (weight.boneIndex2 == bone) value += weight.weight2;
            if (weight.boneIndex3 == bone) value += weight.weight3;
            return value;
        }

        private static void DestroyComputationMesh(ReFitComputation comp)
        {
            if (comp != null && comp.mesh != null)
            {
                Object.DestroyImmediate(comp.mesh);
                comp.mesh = null;
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertSame(Object actual, Object expected, string message)
        {
            if (actual != expected) throw new Exception(message);
        }

        private static void AssertGreater(float actual, float expectedMinimum, string message)
        {
            if (!(actual > expectedMinimum))
                throw new Exception($"{message} Expected > {expectedMinimum:0.####}, got {actual:0.####}.");
        }

        private static void AssertLessOrEqual(float actual, float expectedMaximum, string message)
        {
            if (!(actual <= expectedMaximum))
                throw new Exception($"{message} Expected <= {expectedMaximum:0.####}, got {actual:0.####}.");
        }

        private static string PathOf(Transform transform)
        {
            if (transform == null) return "<null>";
            var parts = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private struct RegionMetrics
        {
            public int count;
            public Vector3 average;
            public float averageOutward;
            public float maxMagnitude;
        }

        private sealed class ReFitTestFixture : IDisposable
        {
            public SkinnedSample source;
            public SkinnedSample target;
            public SkinnedSample sourceSpaceAccessory;
            public SkinnedSample targetSpaceAccessory;

            public static ReFitTestFixture Create()
            {
                var sourcePose = RigPose.Source();
                var targetPose = RigPose.Target();

                return new ReFitTestFixture
                {
                    source = SkinnedSample.CreateAvatar(
                        "__ReFitTest_SourceA", "Body", 9, 7, 0f, sourcePose, SourceBodyWeight, false),
                    target = SkinnedSample.CreateAvatar(
                        "__ReFitTest_TargetB", "Body", 17, 13, 0f, targetPose, TargetBodyWeight, true),
                    sourceSpaceAccessory = SkinnedSample.CreateAvatar(
                        "__ReFitTest_SourceSpaceHoodie", "Hoodie", 9, 7, 0.035f, sourcePose, SourceAccessoryWeight, false),
                    targetSpaceAccessory = SkinnedSample.CreateAvatar(
                        "__ReFitTest_TargetSpaceHoodie", "Hoodie", 9, 7, 0.035f, targetPose, TargetAccessoryWeight, false)
                };
            }

            public void Dispose()
            {
                sourceSpaceAccessory?.Dispose();
                targetSpaceAccessory?.Dispose();
                source?.Dispose();
                target?.Dispose();
            }
        }

        private sealed class SkinnedSample : IDisposable
        {
            public GameObject root;
            public SkinnedMeshRenderer renderer;
            public Mesh mesh;
            public Transform[] bones;
            public Transform hips => bones[(int)RigBone.Hips];
            public Transform chest => bones[(int)RigBone.Chest];

            public static SkinnedSample CreateAvatar(
                string rootName,
                string rendererName,
                int columns,
                int rows,
                float zOffset,
                RigPose pose,
                Func<Vector3, BoneWeight> weightFactory,
                bool includeMuscleShape)
            {
                var root = new GameObject(rootName);
                root.hideFlags = HideFlags.HideAndDontSave;
                var bones = CreateBones(root.transform, pose);

                var rendererObject = new GameObject(rendererName);
                rendererObject.hideFlags = HideFlags.HideAndDontSave;
                rendererObject.transform.SetParent(root.transform, false);
                var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();

                var mesh = CreateGridMesh($"{rendererName}_{columns}x{rows}", columns, rows, zOffset, weightFactory, includeMuscleShape);
                renderer.sharedMesh = mesh;
                renderer.bones = bones;
                renderer.rootBone = bones[(int)RigBone.Hips];
                mesh.bindposes = BuildBindposes(renderer.transform, bones);
                renderer.localBounds = mesh.bounds;
                renderer.updateWhenOffscreen = true;

                return new SkinnedSample
                {
                    root = root,
                    renderer = renderer,
                    mesh = mesh,
                    bones = bones
                };
            }

            public void Dispose()
            {
                if (root != null) Object.DestroyImmediate(root);
                if (mesh != null) Object.DestroyImmediate(mesh);
                root = null;
                renderer = null;
                mesh = null;
                bones = null;
            }
        }

        private enum RigBone
        {
            Hips = 0,
            Spine = 1,
            Chest = 2,
            Head = 3,
            LeftUpperArm = 4,
            RightUpperArm = 5
        }

        private struct RigPose
        {
            public Vector3 hips;
            public Vector3 spine;
            public Vector3 chest;
            public Vector3 head;
            public Vector3 leftUpperArm;
            public Vector3 rightUpperArm;

            public static RigPose Source()
            {
                return new RigPose
                {
                    hips = new Vector3(0f, 0f, 0f),
                    spine = new Vector3(0f, 0.45f, 0f),
                    chest = new Vector3(0f, 1.0f, 0f),
                    head = new Vector3(0f, 1.65f, 0f),
                    leftUpperArm = new Vector3(-0.72f, 1.18f, 0f),
                    rightUpperArm = new Vector3(0.72f, 1.18f, 0f)
                };
            }

            public static RigPose Target()
            {
                return new RigPose
                {
                    hips = new Vector3(0f, 0f, 0f),
                    spine = new Vector3(0f, 0.53f, 0.025f),
                    chest = new Vector3(0.035f, 1.08f, -0.035f),
                    head = new Vector3(0f, 1.65f, 0f),
                    leftUpperArm = new Vector3(-0.86f, 1.29f, 0.015f),
                    rightUpperArm = new Vector3(0.82f, 1.12f, -0.02f)
                };
            }
        }

        private static Transform[] CreateBones(Transform root, RigPose pose)
        {
            var bones = new Transform[6];
            bones[(int)RigBone.Hips] = CreateBone("Hips", root, pose.hips);
            bones[(int)RigBone.Spine] = CreateBone("Spine", bones[(int)RigBone.Hips], pose.spine);
            bones[(int)RigBone.Chest] = CreateBone("Chest", bones[(int)RigBone.Spine], pose.chest);
            bones[(int)RigBone.Head] = CreateBone("Head", bones[(int)RigBone.Chest], pose.head);
            bones[(int)RigBone.LeftUpperArm] = CreateBone("LeftUpperArm", bones[(int)RigBone.Chest], pose.leftUpperArm);
            bones[(int)RigBone.RightUpperArm] = CreateBone("RightUpperArm", bones[(int)RigBone.Chest], pose.rightUpperArm);
            return bones;
        }

        private static Transform CreateBone(string name, Transform parent, Vector3 worldPosition)
        {
            var bone = new GameObject(name).transform;
            bone.gameObject.hideFlags = HideFlags.HideAndDontSave;
            bone.SetParent(parent, true);
            bone.position = worldPosition;
            bone.rotation = Quaternion.identity;
            bone.localScale = Vector3.one;
            return bone;
        }

        private static Matrix4x4[] BuildBindposes(Transform rendererTransform, Transform[] bones)
        {
            var bindposes = new Matrix4x4[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                bindposes[i] = bones[i].worldToLocalMatrix * rendererTransform.localToWorldMatrix;
            return bindposes;
        }

        private static Mesh CreateGridMesh(
            string name,
            int columns,
            int rows,
            float zOffset,
            Func<Vector3, BoneWeight> weightFactory,
            bool includeMuscleShape)
        {
            var vertices = new Vector3[columns * rows];
            var normals = new Vector3[vertices.Length];
            var uvs = new Vector2[vertices.Length];
            var weights = new BoneWeight[vertices.Length];

            int index = 0;
            for (int y = 0; y < rows; y++)
            {
                float fy = rows == 1 ? 0f : (float)y / (rows - 1);
                for (int x = 0; x < columns; x++)
                {
                    float fx = columns == 1 ? 0f : (float)x / (columns - 1);
                    var vertex = new Vector3(Mathf.Lerp(-1.2f, 1.2f, fx), Mathf.Lerp(0f, 1.6f, fy), zOffset);
                    vertices[index] = vertex;
                    normals[index] = Vector3.forward;
                    uvs[index] = new Vector2(fx, fy);
                    weights[index] = weightFactory(vertex);
                    index++;
                }
            }

            var triangles = new List<int>((columns - 1) * (rows - 1) * 6);
            for (int y = 0; y < rows - 1; y++)
            {
                for (int x = 0; x < columns - 1; x++)
                {
                    int a = y * columns + x;
                    int b = a + 1;
                    int c = (y + 1) * columns + x;
                    int d = c + 1;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(c);
                }
            }

            var mesh = new Mesh
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles.ToArray(),
                boneWeights = weights
            };

            if (includeMuscleShape)
            {
                var deltas = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                    deltas[i] = MuscleDelta(vertices[i]);
                mesh.AddBlendShapeFrame(BodyShapeName, 100f, deltas, null, null);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 MuscleDelta(Vector3 vertex)
        {
            var delta = Vector3.zero;

            if (Mathf.Abs(vertex.x) <= 0.5f && vertex.y >= 0.75f && vertex.y <= 1.35f)
                delta += new Vector3(Mathf.Sign(vertex.x) * 0.015f, 0f, 0.1f);

            if (Mathf.Abs(vertex.x) >= 0.68f && vertex.y >= 0.9f && vertex.y <= 1.55f)
                delta += new Vector3(Mathf.Sign(vertex.x) * 0.04f, 0f, 0.055f);

            return delta;
        }

        private static BoneWeight SourceBodyWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.85f, (int)RigBone.Chest, 0.15f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.8f, (int)RigBone.Spine, 0.2f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.7f, (int)RigBone.Hips, 0.3f);
            return MakeWeight((int)RigBone.Chest, 0.85f, (int)RigBone.Spine, 0.15f);
        }

        private static BoneWeight TargetBodyWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.55f, (int)RigBone.Chest, 0.35f, (int)RigBone.Spine, 0.1f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.55f, (int)RigBone.Spine, 0.45f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.5f, (int)RigBone.Chest, 0.3f, (int)RigBone.Hips, 0.2f);
            return MakeWeight((int)RigBone.Chest, 0.55f, (int)RigBone.Spine, 0.4f, (int)RigBone.Hips, 0.05f);
        }

        private static BoneWeight SourceAccessoryWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.7f, (int)RigBone.Chest, 0.3f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.6f, (int)RigBone.Spine, 0.4f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.6f, (int)RigBone.Chest, 0.3f, (int)RigBone.Hips, 0.1f);
            return MakeWeight((int)RigBone.Chest, 0.7f, (int)RigBone.Spine, 0.3f);
        }

        private static BoneWeight TargetAccessoryWeight(Vector3 vertex)
        {
            if (Mathf.Abs(vertex.x) > 0.65f && vertex.y > 0.85f)
                return MakeWeight(ArmBone(vertex), 0.65f, (int)RigBone.Chest, 0.25f, (int)RigBone.Spine, 0.1f);
            if (vertex.y < 0.45f)
                return MakeWeight((int)RigBone.Hips, 0.65f, (int)RigBone.Spine, 0.35f);
            if (vertex.y < 0.95f)
                return MakeWeight((int)RigBone.Spine, 0.55f, (int)RigBone.Chest, 0.35f, (int)RigBone.Hips, 0.1f);
            return MakeWeight((int)RigBone.Chest, 0.65f, (int)RigBone.Spine, 0.35f);
        }

        private static int ArmBone(Vector3 vertex)
        {
            return vertex.x < 0f ? (int)RigBone.LeftUpperArm : (int)RigBone.RightUpperArm;
        }

        private static BoneWeight MakeWeight(
            int bone0,
            float weight0,
            int bone1 = 0,
            float weight1 = 0f,
            int bone2 = 0,
            float weight2 = 0f,
            int bone3 = 0,
            float weight3 = 0f)
        {
            float total = weight0 + weight1 + weight2 + weight3;
            if (total <= 1e-6f)
                return new BoneWeight { boneIndex0 = (int)RigBone.Hips, weight0 = 1f };

            return new BoneWeight
            {
                boneIndex0 = bone0,
                weight0 = weight0 / total,
                boneIndex1 = bone1,
                weight1 = weight1 / total,
                boneIndex2 = bone2,
                weight2 = weight2 / total,
                boneIndex3 = bone3,
                weight3 = weight3 / total
            };
        }
    }
}
