using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Content identities for projection provenance. Call on the main thread while capturing inputs.</summary>
    public static class ReFitCacheIdentity
    {
        public static string Renderer(SkinnedMeshRenderer renderer, bool includeShapes)
        {
            if (renderer == null || renderer.sharedMesh == null) return null;
            var hash = Hash128.Compute("ReFit-cache-1");
            var mesh = renderer.sharedMesh;
            hash.Append(mesh.vertices);
            hash.Append(mesh.normals);
            hash.Append(mesh.triangles);
            hash.Append(mesh.boneWeights);
            hash.Append(mesh.bindposes);
            var matrix = renderer.transform.localToWorldMatrix;
            hash.Append(ref matrix);
            var bones = renderer.bones;
            hash.Append(bones.Length);
            foreach (var bone in bones)
            {
                hash.Append(bone != null ? 1 : 0);
                if (bone == null) continue;
                hash.Append(bone.name);
                matrix = bone.localToWorldMatrix;
                hash.Append(ref matrix);
            }
            if (includeShapes)
            {
                var deltas = new Vector3[mesh.vertexCount];
                var transfers = renderer.GetComponent<ReFitGeneratedAssetMetadata>()?.data?.transferredShapes;
                hash.Append(mesh.blendShapeCount);
                for (int s = 0; s < mesh.blendShapeCount; s++)
                {
                    string name = mesh.GetBlendShapeName(s);
                    hash.Append(name);
                    bool transferred = false;
                    if (transfers != null)
                        foreach (var shape in transfers)
                            if (shape != null && shape.generatedName == name) { transferred = true; break; }
                    // Transferred sliders do not contribute to the base surface; primary and garment sliders do.
                    if (!transferred) hash.Append(renderer.GetBlendShapeWeight(s));
                    int frames = mesh.GetBlendShapeFrameCount(s);
                    hash.Append(frames);
                    for (int f = 0; f < frames; f++)
                    {
                        hash.Append(mesh.GetBlendShapeFrameWeight(s, f));
                        mesh.GetBlendShapeFrameVertices(s, f, deltas, null, null);
                        hash.Append(deltas);
                    }
                }
            }
            return hash.ToString();
        }

        public static string Shape(Vector3[] deltas) => Hash128.Compute(deltas).ToString();

        public static string BindingSettings(ReFitSettings settings)
        {
            var hash = Hash128.Compute("ReFit-binding-1");
            hash.Append(settings.maxProjectionDistance);
            hash.Append(settings.falloffStartDistance);
            hash.Append(settings.filterByNormal ? 1 : 0);
            hash.Append(settings.maxNormalAngle);
            hash.Append(settings.filterByBoneRegion ? 1 : 0);
            return hash.ToString();
        }

        public static string Settings(ReFitSettings settings)
        {
            var geometry = settings.Clone();
            // Output choices and an already completed skeleton transfer do not change a shape's deltas.
            geometry.savePrefab = false;
            geometry.captureProjectionDebug = false;
            geometry.maxProjectionDebugGroups = 0;
            geometry.prefixTransferredShapes = true;
            geometry.blendshapeName = "refit";
            geometry.replaceArmature = false;
            geometry.transferWeights = false;
            geometry.proportionWarningThreshold = 0f;
            return Hash128.Compute(JsonUtility.ToJson(geometry)).ToString();
        }
    }
}
