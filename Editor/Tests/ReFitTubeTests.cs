using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitTubeTests
    {
        public static void RunOrThrow()
        {
            ReFitMeshIntersectionChecks.SelfTest();
            CheckFixture(Matrix4x4.identity);
            CheckFixture(Matrix4x4.TRS(new Vector3(1.3f, -0.4f, 2.1f), Quaternion.Euler(31, 42, 19), Vector3.one * 3.57f));
            CheckEngine();
            Debug.Log("[ReFit Tube Tests] PASS: analytic expansion, equal-size components, thickness, zero field, scaled/rotated pose and legacy reproduction.");
        }

        private static void CheckEngine()
        {
            var objects = new List<Object>(); ReFitComputation computation = null;
            try
            {
                Cylinder(.1f, Matrix4x4.identity, objects);
                var body = ((GameObject)objects[1]).GetComponent<SkinnedMeshRenderer>();
                var asset = Tubes(.112f, .004f, Matrix4x4.identity, objects);
                var renderer = ((GameObject)objects[3]).GetComponent<SkinnedMeshRenderer>();
                renderer.transform.SetParent(body.transform, false);
                var movement = body.sharedMesh.vertices;
                for (int i = 0; i < movement.Length; i++) { movement[i].y = 0; movement[i] = movement[i].normalized * .025f; }
                body.sharedMesh.AddBlendShapeFrame("expand", 100, movement, null, null);
                body.sharedMesh.AddBlendShapeFrame("expand copy", 100, movement, null, null);
                var request = new ReFitRequest
                {
                    mode = ReFitMode.MeshAndBlendshape, assetRenderer = renderer,
                    sourceAvatar = body.gameObject, sourceBodyRenderer = body,
                    targetAvatar = body.gameObject, targetBodyRenderer = body,
                    targetBlendshapes = new List<string> { "expand", "expand copy" },
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, prefixTransferredShapes = false }
                };
                computation = new ReFitEngine().Run(request);
                Require(computation.success, "Full tube engine fixture failed: " + string.Join("\n", computation.report.messages.ConvertAll(m => m.code + ": " + m.text)));
                request.settings.coverDifferentBaseBody = true;
                var covered = new ReFitEngine().Run(request);
                try
                {
                    Require(covered.success, "Tube control with coverage enabled failed.");
                    var a = new Vector3[computation.mesh.vertexCount]; var b = new Vector3[a.Length];
                    for (int s = 0; s < computation.mesh.blendShapeCount; s++)
                    {
                        computation.mesh.GetBlendShapeFrameVertices(s, 0, a, null, null);
                        covered.mesh.GetBlendShapeFrameVertices(s, 0, b, null, null);
                        for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], "Clothing coverage changed an excluded closed tube.");
                    }
                }
                finally { if (covered.mesh != null) Object.DestroyImmediate(covered.mesh); }
                var mesh = computation.mesh; var original = renderer.sharedMesh.vertices;
                var primary = new Vector3[mesh.vertexCount]; var delta = new Vector3[mesh.vertexCount]; var second = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("refit"), 0, primary, null, null);
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("expand"), 0, delta, null, null);
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("expand copy"), 0, second, null, null);
                foreach (float weight in new[] { 0f, .25f, .5f, .75f, 1f })
                {
                    var output = mesh.vertices;
                    for (int i = 0; i < output.Length; i++)
                    {
                        Require(primary[i].magnitude < .00001f, "Identity primary moved a tube in the full pipeline.");
                        Require((delta[i] - second[i]).magnitude < .000001f, "Batch transfer depends on shape order.");
                        output[i] += primary[i] + delta[i] * weight;
                        var expected = new Vector3(original[i].x, 0, original[i].z).normalized * (.025f * weight);
                        Require((output[i] - original[i] - expected).magnitude < .001f, "Full pipeline lost analytic expansion accuracy.");
                    }
                    Require(ReFitMeshIntersectionChecks.Intersections(original, output, mesh.triangles).Count == 0, "Final tube geometry self-intersects.");
                }
                // A and B now have different default surfaces, in addition to B's transferred shape.
                Cylinder(.1f, Matrix4x4.identity, objects);
                var source = ((GameObject)objects[objects.Count - 1]).GetComponent<SkinnedMeshRenderer>();
                var larger = body.sharedMesh.vertices;
                for (int i = 0; i < larger.Length; i++) larger[i] += movement[i];
                body.sharedMesh.vertices = larger; body.sharedMesh.RecalculateBounds();
                Object.DestroyImmediate(computation.mesh);
                computation = new ReFitEngine().Run(new ReFitRequest
                {
                    mode = ReFitMode.MeshAndBlendshape, assetRenderer = renderer,
                    sourceAvatar = source.gameObject, sourceBodyRenderer = source,
                    targetAvatar = body.gameObject, targetBodyRenderer = body,
                    targetBlendshapes = new List<string> { "expand" },
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, prefixTransferredShapes = false }
                });
                Require(computation.success, "Different-default-surface tube fixture failed.");
                mesh = computation.mesh;
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("refit"), 0, primary, null, null);
                mesh.GetBlendShapeFrameVertices(mesh.GetBlendShapeIndex("expand"), 0, delta, null, null);
                for (int i = 0; i < original.Length; i++)
                {
                    var expected = new Vector3(original[i].x, 0, original[i].z).normalized * .025f;
                    Require((primary[i] - expected).magnitude < .001f, "Default shape transfer lost analytic radius accuracy.");
                    Require((primary[i] + delta[i] - expected * 2).magnitude < .001f, "Primary plus transferred tube shape is inaccurate.");
                }
            }
            finally
            {
                if (computation?.mesh != null) Object.DestroyImmediate(computation.mesh);
                for (int i = objects.Count - 1; i >= 0; i--) if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            }
        }

        private static void CheckFixture(Matrix4x4 transform)
        {
            var objects = new List<Object>();
            try
            {
                var body = Cylinder(0.1f, transform, objects);
                var asset = Tubes(0.112f, 0.004f, transform, objects);
                var index = SurfaceBvh.Build(body);
                var tubes = ReFitTubeField.Build(asset, true);
                Require(tubes.Count == 2, "Equal-sized closed rings were not both detected.");
                Require(ReFitTubeField.Build(asset, false).Count == 0, "Disable setting was ignored.");
                Require(ReFitTubeField.Build(body, true).Count == 0, "Open sleeve was classified as a closed tube.");
                var settings = new ReFitSettings { maxProjectionDistance = 1f, falloffStartDistance = 1f };
                var bindings = SurfaceBindingSolver.ComputeGroupBindings(asset, body, index, settings, null, null, null, tubes.groups);
                var zero = new Vector3[asset.GroupCount];
                tubes.Apply(asset, body, index, null, null, zero, settings, null, "identity");
                foreach (var d in zero) Require(d.magnitude < 0.00001f, "Zero input changed the tube rest shape.");
                var bodyDelta = new Vector3[body.worldVertices.Length];
                var inverse = transform.inverse;
                for (int i = 0; i < bodyDelta.Length; i++)
                {
                    var p = inverse.MultiplyPoint3x4(body.worldVertices[i]); p.y = 0;
                    bodyDelta[i] = transform.MultiplyVector(p.normalized * 0.025f);
                }
                var legacy = SurfaceBindingSolver.ComputeGroupBindings(asset, body, index, settings, null, null, null);
                var bad = Sample(body, legacy, bodyDelta);
                var raw = Sample(body, bindings, bodyDelta);
                tubes.Apply(asset, body, index, bodyDelta, null, raw, settings, null, "muscle");
                float maxError = 0, badError = 0, thicknessError = 0;
                for (int g = 0; g < asset.GroupCount; g++)
                {
                    var p = inverse.MultiplyPoint3x4(asset.worldVertices[asset.groupRep[g]]);
                    var radial = new Vector3(p.x, 0, p.z).normalized;
                    var expected = transform.MultiplyVector(radial * 0.025f);
                    maxError = Mathf.Max(maxError, (raw[g] - expected).magnitude);
                    badError = Mathf.Max(badError, (bad[g] - expected).magnitude);
                    var q = inverse.MultiplyPoint3x4(asset.worldVertices[asset.groupRep[g]] + raw[g]);
                    float cy = p.y < 0 ? -0.065f : 0.065f;
                    float r = new Vector2(q.x, q.z).magnitude;
                    thicknessError = Mathf.Max(thicknessError, Mathf.Abs(new Vector2(r - 0.137f, q.y - cy).magnitude - 0.004f));
                    foreach (float w in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                    {
                        var mid = inverse.MultiplyPoint3x4(asset.worldVertices[asset.groupRep[g]] + raw[g] * w);
                        Require(new Vector2(mid.x, mid.z).magnitude > 0.1f + 0.025f * w, "Interpolated tube entered the analytic body.");
                    }
                }
                Require(badError > 0.02f, "Fixture did not reproduce the hard-normal correspondence failure.");
                Require(maxError < 0.001f, $"Analytic expansion error {maxError * 1000:F3}mm exceeds 1mm.");
                Require(thicknessError < 0.0004f, "Tube thickness changed by more than 10%.");
                var unsafeField = new Vector3[asset.GroupCount];
                for (int g = 0; g < unsafeField.Length; g++)
                {
                    var p = inverse.MultiplyPoint3x4(asset.worldVertices[asset.groupRep[g]]);
                    unsafeField[g] = transform.MultiplyVector(new Vector3(p.x, 0, p.z).normalized * 2);
                }
                var rejected = new ReFitReport();
                tubes.Apply(asset, body, index, null, null, unsafeField, settings, rejected, "unsafe expansion");
                Require(rejected.HasErrors && rejected.messages.Exists(m => m.code == "tube-geometry-invalid"), "Severe tube strain was silently accepted.");
                Debug.Log($"[ReFit Tube Tests] scale={transform.lossyScale.x:F2}, maxError={maxError * 1000:F4}mm, thicknessError={thicknessError * 1000:F4}mm.");
            }
            finally { for (int i = objects.Count - 1; i >= 0; i--) Object.DestroyImmediate(objects[i]); }
        }

        private static Vector3[] Sample(MeshSnapshot body, SurfaceBinding[] bindings, Vector3[] delta)
        {
            var result = new Vector3[bindings.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var b = bindings[i]; if (!b.valid) continue;
                int t = b.triangle * 3;
                result[i] = delta[body.triangles[t]] * b.bary.x + delta[body.triangles[t + 1]] * b.bary.y + delta[body.triangles[t + 2]] * b.bary.z;
            }
            return result;
        }

        private static MeshSnapshot Tubes(float radius, float thickness, Matrix4x4 transform, List<Object> objects)
        {
            const int around = 64, section = 8;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int ring = 0; ring < 2; ring++)
            {
                int start = vertices.Count;
                for (int i = 0; i < around; i++) for (int j = 0; j < section; j++)
                {
                    float a = i * 2 * Mathf.PI / around, b = j * 2 * Mathf.PI / section;
                    vertices.Add(new Vector3((radius + thickness * Mathf.Cos(b)) * Mathf.Cos(a), (ring == 0 ? -0.065f : 0.065f) + thickness * Mathf.Sin(b), (radius + thickness * Mathf.Cos(b)) * Mathf.Sin(a)));
                    int p = start + i * section + j, q = start + ((i + 1) % around) * section + j;
                    int r = start + i * section + (j + 1) % section, s = start + ((i + 1) % around) * section + (j + 1) % section;
                    triangles.AddRange(new[] { p, r, q, q, r, s });
                }
            }
            return Snapshot(vertices, triangles, transform, objects);
        }

        private static MeshSnapshot Cylinder(float radius, Matrix4x4 transform, List<Object> objects)
        {
            const int count = 256;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < count; i++)
            {
                float a = i * 2 * Mathf.PI / count;
                vertices.Add(new Vector3(radius * Mathf.Cos(a), -0.25f, radius * Mathf.Sin(a)));
                vertices.Add(new Vector3(radius * Mathf.Cos(a), 0.25f, radius * Mathf.Sin(a)));
                int p = i * 2, q = (i + 1) % count * 2;
                triangles.AddRange(new[] { p, p + 1, q, q, p + 1, q + 1 });
            }
            return Snapshot(vertices, triangles, transform, objects);
        }

        private static MeshSnapshot Snapshot(List<Vector3> vertices, List<int> triangles, Matrix4x4 transform, List<Object> objects)
        {
            for (int i = 0; i < vertices.Count; i++) vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
            var mesh = new Mesh { vertices = vertices.ToArray(), triangles = triangles.ToArray() }; objects.Add(mesh);
            mesh.RecalculateNormals();
            var go = new GameObject("__ReFitTubeTest") { hideFlags = HideFlags.HideAndDontSave }; objects.Add(go);
            var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            return MeshSnapshot.Capture(renderer, true, null, null);
        }

        private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    }
}
