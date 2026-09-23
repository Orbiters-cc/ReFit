using System;
using UnityEngine;

namespace Orbiters.ReFit.Editor.Tests
{
    public static partial class ReFitDeterministicTestRunner
    {
        // An authored expansion near the bottom of a top must remain a full body-shape transfer.
        // Exercise the engine and Unity's actual skinning, including an FBX-style rotated mesh basis.
        public static void TransferredBelly_FollowsLowerTorsoInAllAxes()
        {
            foreach (var mode in new[] { ReFitMode.Blendshape, ReFitMode.MeshAndBlendshape })
            foreach (bool rotatedMesh in new[] { false, true })
            using (var fixture = ReFitTestFixture.Create())
            {
                var clothing = fixture.targetSpaceAccessory;
                clothing.root.transform.SetParent(fixture.target.root.transform, true);
                clothing.renderer.name = "Tanktop";
                var input = clothing.mesh.vertices;
                var bodyVertices = fixture.target.mesh.vertices;
                // Rotate the surface itself so its outward normal has nonzero world X/Y/Z components.
                var surfaceRotation = Quaternion.Euler(15f, 30f, 0f);
                foreach (var mesh in new[] { fixture.source.mesh, fixture.target.mesh, clothing.mesh })
                {
                    var vertices = mesh.vertices;
                    var normals = mesh.normals;
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        vertices[i] = surfaceRotation * vertices[i];
                        normals[i] = surfaceRotation * normals[i];
                    }
                    mesh.vertices = vertices;
                    mesh.normals = normals;
                    mesh.RecalculateBounds();
                }
                var bodyDeltas = new Vector3[bodyVertices.Length];
                for (int i = 0; i < bodyDeltas.Length; i++)
                    bodyDeltas[i] = surfaceRotation * LowerTorsoExpansion(bodyVertices[i]);
                fixture.target.mesh.AddBlendShapeFrame("Belly", 100f, bodyDeltas, null, null);

                if (rotatedMesh)
                {
                    var rotation = Quaternion.Euler(-90f, 0f, 0f);
                    clothing.renderer.transform.localRotation = rotation;
                    var local = clothing.mesh.vertices;
                    var normals = clothing.mesh.normals;
                    for (int i = 0; i < local.Length; i++)
                    {
                        local[i] = Quaternion.Inverse(rotation) * local[i];
                        normals[i] = Quaternion.Inverse(rotation) * normals[i];
                    }
                    clothing.mesh.vertices = local;
                    clothing.mesh.normals = normals;
                    clothing.mesh.bindposes = BuildBindposes(clothing.renderer.transform, clothing.bones);
                }

                var request = mode == ReFitMode.Blendshape
                    ? BuildBlendshapeOnlyRequest(fixture, clothing.renderer)
                    : BuildMeshAndBlendshapeRequest(fixture, clothing.renderer, false);
                // Both entry points can transfer a shape to clothing already fitted to this avatar.
                // Source-to-target proportion conversion retains its existing hem restraint.
                request.sourceAvatar = fixture.target.root;
                request.sourceBodyRenderer = fixture.target.renderer;
                request.targetBlendshape = "Belly";
                request.settings.enableClearanceCorrection = false;
                request.settings.upperBodyGarmentHemFollowScale = 0.18f;
                var comp = new ReFitEngine().Run(request);
                var baked = new Mesh();
                try
                {
                    AssertComputationSucceeded(comp);
                    clothing.renderer.sharedMesh = comp.mesh;
                    int shape = comp.mesh.GetBlendShapeIndex(SingleSecondaryShape(comp));
                    foreach (float weight in new[] { 0f, 25f, 50f, 75f, 100f })
                    {
                        clothing.renderer.SetBlendShapeWeight(shape, weight);
                        clothing.renderer.BakeMesh(baked);
                        var actual = baked.vertices;
                        int lowerTorsoSamples = 0;
                        for (int i = 0; i < actual.Length; i++)
                        {
                            if (Mathf.Abs(input[i].x) > 0.61f || input[i].y > 0.54f) continue;
                            var expected = surfaceRotation *
                                (input[i] + LowerTorsoExpansion(input[i]) * (weight / 100f));
                            var world = clothing.renderer.transform.TransformPoint(actual[i]);
                            AssertLessOrEqual(Vector3.Distance(world, expected), 0.0001f,
                                $"{mode}, rotated={rotatedMesh}, Belly={weight}, vertex={i}: " +
                                $"expected {expected:F5}, rendered {world:F5}. Hem protection suppressed authored body motion.");
                            lowerTorsoSamples++;
                        }
                        AssertGreater(lowerTorsoSamples, 8, "Belly check did not cover the lower torso and open hem.");
                    }
                }
                finally
                {
                    clothing.renderer.sharedMesh = clothing.mesh;
                    UnityEngine.Object.DestroyImmediate(baked);
                    DestroyComputationMesh(comp);
                }
            }
        }

        private static Vector3 LowerTorsoExpansion(Vector3 point)
        {
            float vertical = Mathf.Max(0f, 1f - point.y / 0.8f);
            float center = Mathf.Max(0f, 1f - Mathf.Abs(point.x) / 1.2f);
            // Belly expansion also moves the surface down and sideways; normal-only recovery is insufficient.
            return new Vector3(0.035f, -0.02f, 0.10f) * (center * vertical);
        }
    }
}
