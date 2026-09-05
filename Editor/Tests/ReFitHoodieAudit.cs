using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        /// <summary>Private local audit of every debug stage. Never uploads or changes the user's avatar.</summary>
        public static void AuditHoodie() => AuditHoodie(new ReFitSettings(), "hoodie-audit");

        public static void AuditHoodie(ReFitSettings settings, string label)
        {
            if (string.IsNullOrEmpty(label) || label != Path.GetFileName(label))
                throw new ArgumentException("Audit label must be a directory name.", nameof(label));
            string directory = Path.GetFullPath(Path.Combine("Temp/ReFitBenchmarks", label));
            Directory.CreateDirectory(directory);
            var log = new StringBuilder();
            var snapshotMeshes = new HashSet<Mesh>();
            using (var fixture = RealHoodieArtifactFixture.Create())
            {
                var request = BuildRealHoodieSourceRequest(fixture);
                request.settings = settings.Clone();
                request.settings.savePrefab = false;
                request.settings.captureProjectionDebug = true;
                request.settings.maxProjectionDebugGroups = 0;
                log.AppendLine(JsonUtility.ToJson(request.settings));
                fixture.targetBody.SetBlendShapeWeight(fixture.targetBody.sharedMesh.GetBlendShapeIndex(RealTargetShapeName), 100f);
                var comp = new ReFitEngine().Run(request);
                ReFitDebugSession debug = null;
                try
                {
                    AssertComputationSucceeded(comp);
                    debug = new ReFitDebugSession(request, comp.report);
                    debug.Capture("00_input_asset", fixture.hoodie);
                    debug.CaptureScenePoseAsDefault("01_scene_pose_as_default", fixture.hoodie);
                    var renderer = ReFitAssetPipeline.ApplyToScene(request, comp, comp.report, debug);
                    AssertTrue(renderer != null && !comp.report.HasErrors, "Hoodie application failed.");
                    AssertRootBoneInRendererBones(renderer, "Hoodie audit");
                    log.AppendLine("Generated bone provenance:");
                    for (int i = 0; i < comp.bones.Length; i++)
                        log.AppendLine(i + " " + comp.bones[i].name + " <- " + comp.bones[i].origin);
                    foreach (Transform snapshot in debug.Root.transform)
                    {
                        log.AppendLine("\nSTEP " + snapshot.name);
                        foreach (var r in snapshot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            if (r.sharedMesh == null) continue;
                            snapshotMeshes.Add(r.sharedMesh);
                            AuditRenderer(r, snapshot, log);
                        }
                    }
                    log.AppendLine("\nTARGET BODY WEIGHTS AND HIERARCHY");
                    AuditRenderer(fixture.targetBody, fixture.targetAvatar.transform, log);
                    foreach (var name in comp.secondaryShapeNames)
                        renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(name), 100f);
                    fixture.targetBody.SetBlendShapeWeight(fixture.targetBody.sharedMesh.GetBlendShapeIndex(RealTargetShapeName), 100f);
                    foreach (var photo in ReFitCommissionCapture.Capture(fixture.targetAvatar, renderer, comp.primaryShapeName))
                        File.WriteAllBytes(Path.Combine(directory, photo.name), photo.bytes);
                }
                finally
                {
                    File.WriteAllText(Path.Combine(directory, "hierarchy-and-weights.txt"), log.ToString());
                    if (debug?.Root != null)
                    {
                        foreach (var r in debug.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            if (r.sharedMesh != null) snapshotMeshes.Add(r.sharedMesh);
                        Object.DestroyImmediate(debug.Root);
                    }
                    foreach (var mesh in snapshotMeshes)
                        if (mesh != null && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static void AuditRenderer(SkinnedMeshRenderer renderer, Transform root, StringBuilder log)
        {
            var bones = renderer.bones;
            var boneSet = new HashSet<Transform>(bones);
            var snap = MeshSnapshot.Capture(renderer, false, null, null);
            var weights = renderer.sharedMesh.boneWeights;
            log.AppendLine("Renderer=" + renderer.name + " bones=" + bones.Length + " root=" + renderer.rootBone?.name);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var names = new HashSet<string>();
                foreach (Transform child in transform)
                {
                    if (!names.Add(child.name) && boneSet.Contains(child))
                        throw new Exception("Duplicate skinned sibling: " + child.name);
                }
                log.AppendLine("  " + AnimationUtility.CalculateTransformPath(transform, root) +
                    " p=" + root.InverseTransformPoint(transform.position).ToString("F4") +
                    " rotation=" + transform.localEulerAngles.ToString("F2") +
                    " parentLength=" + (transform.parent != null ? Vector3.Distance(transform.position, transform.parent.position) : 0).ToString("F4") +
                    " skinned=" + boneSet.Contains(transform));
            }
            for (int i = 0; i < bones.Length; i++)
            {
                AssertTrue(bones[i] != null && (bones[i] == root || bones[i].IsChildOf(root)), "Snapshot has an external bone.");
                float total = 0;
                int count = 0;
                Bounds bounds = new Bounds();
                for (int v = 0; v < weights.Length; v++)
                {
                    var w = weights[v];
                    float weight = (w.boneIndex0 == i ? w.weight0 : 0) + (w.boneIndex1 == i ? w.weight1 : 0) +
                        (w.boneIndex2 == i ? w.weight2 : 0) + (w.boneIndex3 == i ? w.weight3 : 0);
                    if (weight <= 0.0001f) continue;
                    var point = root.InverseTransformPoint(snap.worldVertices[v]);
                    if (count++ == 0) bounds = new Bounds(point, Vector3.zero); else bounds.Encapsulate(point);
                    total += weight;
                }
                log.AppendLine($"WEIGHT {i} {bones[i].name}: total={total:F3} vertices={count} center={bounds.center:F4} size={bounds.size:F4}");
            }
        }
    }
}
