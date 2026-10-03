using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor.Tests
{
    public static class ReFitSurfaceCoverageTests
    {
        public static void RunOrThrow()
        {
            foreach (bool rotated in new[] { false, true }) CheckDensePeak(rotated);
            CheckDifferentBasePeak();
            CheckDeepPenetrationAndLayers();
            CheckAdditiveRepairDoesNotRepeat();
            CheckCoveragePoseIsTemporary();
            Debug.Log("[ReFit Coverage Tests] PASS: dense body peaks between cloth vertices, combined shapes, zero shape, rotated/scaled surfaces.");
        }

        private static void CheckCoveragePoseIsTemporary()
        {
            var root = new GameObject("__CoveragePose") { hideFlags = HideFlags.HideAndDontSave };
            var meshes = new List<Mesh>();
            try
            {
                var hips = new GameObject("Hips").transform; hips.SetParent(root.transform, false);
                var left = new GameObject("LeftUpperLeg").transform; left.SetParent(hips, false); left.localPosition = Vector3.left * .08f;
                var right = new GameObject("RightUpperLeg").transform; right.SetParent(hips, false); right.localPosition = Vector3.right * .08f;
                var body = Grid(root.transform, left, 7, 0, meshes);
                var cloth = Grid(root.transform, left, 7, .01f, meshes);
                foreach (var renderer in new[] { body, cloth })
                {
                    renderer.bones = new[] { left, right };
                    renderer.sharedMesh.bindposes = new[] { left.worldToLocalMatrix, right.worldToLocalMatrix };
                    renderer.sharedMesh.boneWeights = renderer.sharedMesh.vertices.Select(p => new BoneWeight { boneIndex0 = p.x < 0 ? 0 : 1, weight0 = 1 }).ToArray();
                }
                var request = new ReFitRequest { mode = ReFitMode.MeshToMesh, assetRenderer = cloth,
                    sourceAvatar = root, sourceBodyRenderer = body, targetAvatar = root, targetBodyRenderer = body,
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, coverDifferentBaseBody = true } };
                foreach (bool enabled in new[] { false, true })
                {
                    request.settings.coverDifferentBaseBody = enabled;
                    using (var stage = PoseNormalizer.CreateStage(request, new ReFitReport()))
                    {
                        if (stage == null) throw new Exception("Coverage pose fixture failed to stage.");
                        foreach (var kind in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
                        {
                            float angle = Quaternion.Angle(Quaternion.identity, stage.targetHumanMap[kind].localRotation);
                            if (Mathf.Abs(angle - (enabled ? 20f : 0f)) > .01f) throw new Exception("Coverage stance was not scoped to the staged lower-body fit.");
                        }
                    }
                    if (left.localRotation != Quaternion.identity || right.localRotation != Quaternion.identity)
                        throw new Exception("Coverage posing changed the original avatar.");
                }
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        private static void CheckAdditiveRepairDoesNotRepeat()
        {
            var root = new GameObject("__CoverageAdditive") { hideFlags = HideFlags.HideAndDontSave };
            var meshes = new List<Mesh>();
            try
            {
                var bone = new GameObject("LeftUpperArm").transform; bone.SetParent(root.transform, false);
                var body = Grid(root.transform, bone, 17, 0, meshes);
                var cloth = Grid(root.transform, bone, 7, .005f, meshes);
                var delta = Enumerable.Repeat(Vector3.forward * .00002f, body.sharedMesh.vertexCount).ToArray();
                body.sharedMesh.AddBlendShapeFrame("Tiny", 100, delta, null, null);
                var result = new ReFitEngine().Run(new ReFitRequest { mode = ReFitMode.Blendshape,
                    assetRenderer = cloth, targetAvatar = root, targetBodyRenderer = body,
                    targetBlendshapes = new List<string> { "Tiny" }, settings = new ReFitSettings {
                        replaceArmature = false, transferWeights = false, savePrefab = false, coverDifferentBaseBody = true,
                        prefixTransferredShapes = false } });
                try
                {
                    if (!result.success) throw new Exception("Additive fixture failed.");
                    var generated = new Vector3[cloth.sharedMesh.vertexCount];
                    result.mesh.GetBlendShapeFrameVertices(result.mesh.GetBlendShapeIndex("Tiny"), 0, generated, null, null);
                    if (generated.Any(d => d.magnitude > .0001f))
                        throw new Exception("A tiny body shape repeated the base coverage repair.");
                }
                finally { if (result.mesh != null) Object.DestroyImmediate(result.mesh); }
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        private static void CheckDeepPenetrationAndLayers()
        {
            var root = new GameObject("__CoverageLayers") { hideFlags = HideFlags.HideAndDontSave };
            var meshes = new List<Mesh>();
            try
            {
                var bone = new GameObject("LeftUpperArm").transform; bone.SetParent(root.transform, false);
                var body = Grid(root.transform, bone, 17, 0, meshes);
                var inner = Grid(root.transform, bone, 17, .06f, meshes);
                var outer = Grid(root.transform, bone, 7, .005f, meshes);
                inner.name = "Shirt"; outer.name = "Jacket";
                var bodyDelta = Enumerable.Repeat(Vector3.forward * .01f, body.sharedMesh.vertexCount).ToArray();
                var innerDelta = Enumerable.Repeat(Vector3.forward * .02f, inner.sharedMesh.vertexCount).ToArray();
                body.sharedMesh.AddBlendShapeFrame("Flex", 100, bodyDelta, null, null);
                inner.sharedMesh.AddBlendShapeFrame("Flex", 100, innerDelta, null, null);
                var request = new ReFitRequest { mode = ReFitMode.MeshAndBlendshape, assetRenderer = outer, sourceAvatar = root,
                    sourceBodyRenderer = body, targetAvatar = root, targetBodyRenderer = body, coverageLayers = new List<SkinnedMeshRenderer> { inner },
                    targetBlendshapes = new List<string> { "Flex" }, settings = new ReFitSettings { replaceArmature = false,
                        transferWeights = false, savePrefab = false, coverDifferentBaseBody = true, prefixTransferredShapes = false } };
                var result = new ReFitEngine().Run(request);
                try
                {
                    if (!result.success) throw new Exception("Layer fixture failed.");
                    var primary = new Vector3[outer.sharedMesh.vertexCount]; var flex = new Vector3[primary.Length];
                    result.mesh.GetBlendShapeFrameVertices(result.mesh.GetBlendShapeIndex("refit"), 0, primary, null, null);
                    result.mesh.GetBlendShapeFrameVertices(result.mesh.GetBlendShapeIndex("Flex"), 0, flex, null, null);
                    foreach (float weight in new[] { 0f, .5f, 1f })
                        for (int v = 0; v < primary.Length; v++)
                        {
                            var point = result.mesh.vertices[v] + primary[v] + flex[v] * weight;
                            if (point.z < .06f + .02f * weight + .004f)
                                throw new Exception($"Outer layer clips at weight {weight}, vertex {v}: {point.z}.");
                            if (Mathf.Abs(point.x - outer.sharedMesh.vertices[v].x) > .00001f || Mathf.Abs(point.y - outer.sharedMesh.vertices[v].y) > .00001f)
                                throw new Exception("Coverage extended the open hem tangentially.");
                        }
                    if (!result.mesh.boneWeights.SequenceEqual(outer.sharedMesh.boneWeights)) throw new Exception("Layer coverage changed skin weights.");
                }
                finally { if (result.mesh != null) Object.DestroyImmediate(result.mesh); }
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        private static void CheckDifferentBasePeak()
        {
            var root = new GameObject("__CrossBaseCoverage") { hideFlags = HideFlags.HideAndDontSave };
            var meshes = new List<Mesh>();
            try
            {
                var bone = new GameObject("LeftUpperArm").transform; bone.SetParent(root.transform, false);
                var body = Grid(root.transform, bone, 33, 0, meshes);
                var cloth = Grid(root.transform, bone, 7, .005f, meshes);
                var vertices = body.sharedMesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    var p = vertices[i];
                    vertices[i].z += .025f * Mathf.Exp(-((p.x-.025f)*(p.x-.025f)+(p.y-.425f)*(p.y-.425f))/.00018f);
                }
                body.sharedMesh.vertices = vertices; body.sharedMesh.RecalculateNormals(); body.sharedMesh.RecalculateBounds();
                var request = new ReFitRequest { mode = ReFitMode.MeshToMesh, sourceAvatar = root, sourceBodyRenderer = body,
                    targetAvatar = root, targetBodyRenderer = body, assetRenderer = cloth,
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false, coverDifferentBaseBody = true } };
                var result = new ReFitEngine().Run(request);
                try
                {
                    if (!result.success) throw new Exception("Different-base fit failed.");
                    var delta = new Vector3[cloth.sharedMesh.vertexCount];
                    result.mesh.GetBlendShapeFrameVertices(result.mesh.GetBlendShapeIndex("refit"), 0, delta, null, null);
                    var surface = new MeshSnapshot { worldVertices = cloth.sharedMesh.vertices.Select((p,i)=>p+delta[i]).ToArray(), triangles = cloth.sharedMesh.triangles };
                    var index = SurfaceBvh.Build(surface);
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        var hit = index.ClosestPoint(vertices[i], .1f);
                        float gap = Vector3.Dot(hit.position-vertices[i], surface.FaceNormal(hit.triangle));
                        if (gap < -.0001f) throw new Exception($"Initial cross-base peak remains inside cloth: {gap*1000:F3}mm at {i}.");
                    }
                    if (!cloth.sharedMesh.boneWeights.SequenceEqual(result.mesh.boneWeights)) throw new Exception("Coverage changed skinning.");
                }
                finally { if (result.mesh != null) Object.DestroyImmediate(result.mesh); }
            }
            finally { Object.DestroyImmediate(root); foreach (var mesh in meshes) Object.DestroyImmediate(mesh); }
        }

        private static void CheckDensePeak(bool rotated)
        {
            var root = new GameObject("__ReFitCoverageTest") { hideFlags = HideFlags.HideAndDontSave };
            var meshes = new List<Mesh>();
            ReFitComputation computation = null;
            try
            {
                var bone = new GameObject("LeftUpperLeg").transform; bone.SetParent(root.transform, false);
                var body = Grid(root.transform, bone, 33, 0, meshes);
                body.name = "Body";
                var garment = new GameObject("ShortsRoot").transform; garment.SetParent(root.transform, false);
                var clothBone = new GameObject("LeftUpperLeg").transform; clothBone.SetParent(garment, false);
                var cloth = Grid(garment, clothBone, 7, .005f, meshes); cloth.name = "Shorts";
                var vertices = body.sharedMesh.vertices;
                var delta = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    var p = vertices[i];
                    delta[i] = Vector3.forward * (.028f * Mathf.Exp(-((p.x - .025f) * (p.x - .025f) + (p.y - .425f) * (p.y - .425f)) / .00018f));
                }
                if (rotated)
                {
                    var matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(19, 37, 13), Vector3.one * 1.7f);
                    vertices = vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                    delta = delta.Select(matrix.MultiplyVector).ToArray();
                    body.sharedMesh.vertices = vertices; body.sharedMesh.RecalculateNormals(); body.sharedMesh.RecalculateBounds();
                    cloth.sharedMesh.vertices = cloth.sharedMesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                    cloth.sharedMesh.RecalculateNormals(); cloth.sharedMesh.RecalculateBounds();
                }
                body.sharedMesh.AddBlendShapeFrame("peak", 100, delta, null, null);
                body.sharedMesh.AddBlendShapeFrame("second", 100, delta, null, null);
                body.sharedMesh.AddBlendShapeFrame("zero", 100, new Vector3[vertices.Length], null, null);
                var request = new ReFitRequest
                {
                    mode = ReFitMode.Blendshape, assetRenderer = cloth, targetAvatar = root, targetBodyRenderer = body,
                    targetBlendshapes = new List<string> { "peak", "second", "zero" },
                    settings = new ReFitSettings { replaceArmature = false, transferWeights = false, savePrefab = false, captureProjectionDebug = true }
                };
                computation = new ReFitEngine().Run(request);
                if (!computation.success) throw new Exception(string.Join("\n", computation.report.messages));
                if (!computation.report.messages.Any(m => m.code == "surface-coverage")) throw new Exception("Lower-body coverage was not exercised. " + string.Join("\n", computation.report.messages));
                var first = new Vector3[cloth.sharedMesh.vertexCount]; var second = new Vector3[first.Length]; var zero = new Vector3[first.Length];
                computation.mesh.GetBlendShapeFrameVertices(computation.mesh.GetBlendShapeIndex("refit_peak"), 0, first, null, null);
                computation.mesh.GetBlendShapeFrameVertices(computation.mesh.GetBlendShapeIndex("refit_second"), 0, second, null, null);
                computation.mesh.GetBlendShapeFrameVertices(computation.mesh.GetBlendShapeIndex("refit_zero"), 0, zero, null, null);
                if (zero.Any(d => d.magnitude > .000001f)) throw new Exception("Zero shape moved the clothing.");
                if (first.Zip(second, (a, b) => (a - b).magnitude).Max() > .000001f) throw new Exception("Identical shape transfer is not deterministic.");
                var baseCloth = cloth.sharedMesh.vertices;
                var baseSurface = new MeshSnapshot { worldVertices = baseCloth, triangles = cloth.sharedMesh.triangles };
                foreach (float weight in new[] { .25f, .5f, .75f, 1f, 2f })
                {
                    var at = baseCloth.Select((p, i) => p + first[i] * weight).ToArray();
                    var surface = new MeshSnapshot { worldVertices = at, triangles = baseSurface.triangles };
                    var atIndex = SurfaceBvh.Build(surface);
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        var point = vertices[i] + delta[i] * weight;
                        var hit = atIndex.ClosestPoint(point, .1f, null);
                        float gap = Vector3.Dot(hit.position - point, surface.FaceNormal(hit.triangle));
                        if (gap < .0005f) throw new Exception($"Dense peak passes through fabric at rotated={rotated}, weight={weight}, vertex={i}, gap={gap * 1000:F3}mm. " + string.Join("\n", computation.report.messages.Where(m => m.code == "surface-coverage")));
                    }
                }
                if (!rotated)
                {
                    request.settings.preserveLowerBodyCoverage = false;
                    var previous = new ReFitEngine().Run(request);
                    try
                    {
                        if (!previous.success) throw new Exception("Coverage baseline failed to compute.");
                        var priorDelta = new Vector3[first.Length];
                        previous.mesh.GetBlendShapeFrameVertices(previous.mesh.GetBlendShapeIndex("refit_peak"), 0, priorDelta, null, null);
                        var surface = new MeshSnapshot { worldVertices = baseCloth.Select((p, i) => p + priorDelta[i]).ToArray(), triangles = baseSurface.triangles };
                        var index = SurfaceBvh.Build(surface);
                        float worst = 0;
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            var point = vertices[i] + delta[i];
                            var hit = index.ClosestPoint(point, .1f, null);
                            worst = Mathf.Min(worst, Vector3.Dot(hit.position - point, surface.FaceNormal(hit.triangle)));
                        }
                        if (worst >= -.001f) throw new Exception("Dense-peak fixture no longer reproduces the original >1mm clipping.");
                    }
                    finally { if (previous.mesh != null) Object.DestroyImmediate(previous.mesh); }
                }
            }
            finally
            {
                if (computation?.mesh != null) Object.DestroyImmediate(computation.mesh);
                Object.DestroyImmediate(root);
                foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            }
        }

        private static SkinnedMeshRenderer Grid(Transform parent, Transform bone, int count, float z, List<Mesh> owned)
        {
            var vertices = new Vector3[count * count]; var triangles = new List<int>();
            for (int y = 0; y < count; y++) for (int x = 0; x < count; x++)
            {
                int i = y * count + x;
                vertices[i] = new Vector3(-.15f + .3f * x / (count - 1), .3f + .3f * y / (count - 1), z);
                if (x + 1 < count && y + 1 < count) triangles.AddRange(new[] { i, i + 1, i + count, i + 1, i + count + 1, i + count });
            }
            var mesh = new Mesh { name = "Coverage grid", vertices = vertices, triangles = triangles.ToArray(),
                bindposes = new[] { Matrix4x4.identity }, boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, vertices.Length).ToArray() };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); owned.Add(mesh);
            var go = new GameObject("Coverage grid"); go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.bones = new[] { bone }; renderer.rootBone = bone;
            return renderer;
        }
    }
}
