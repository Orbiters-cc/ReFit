using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>Content identities for projection provenance. Call on the main thread while capturing inputs.</summary>
    public static class ReFitCacheIdentity
    {
        /// <summary>
        /// The algorithm's revision: saved transfer data and results cached by the Orbiters tools from an earlier one are
        /// rebuilt (8: the target fitted with the followed shapes it shows, their clothing shapes taking them away; clothing
        /// lined up with the source keeps the source unscaled; 9: fabric sunk into the body draped out the nearer way, a
        /// shoulder's fabric not a sleeve's; 10: clothing the caller says was made for another body covered as such, its
        /// separate pieces carried with the fabric, closed rings following the coverage repair part by part; 11: after the cloth
        /// fit, only what clips pushed out, to the settings' clearance and spread little, over the body and the layers worn
        /// under it; made for another body also when the cloth fit falls back, and in shapes added later; the registration
        /// gate measuring every clothing point near the skin; the cloth fit's counterpart the nearer of the skin beneath and
        /// above; coverage supports on millimetre triangles; 12: after the cloth fit the legs keep the centimetre of room, by
        /// their share of the skin's skinning).
        /// </summary>
        public const int Revision = 12;

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
            var hash = Hash128.Compute("ReFit-binding-" + Revision);
            hash.Append(settings.coverDifferentBaseBody ? 1 : 0);
            hash.Append(settings.preserveClosedTubes ? 1 : 0);
            hash.Append(settings.preserveLowerBodyCoverage ? 1 : 0);
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
            // Algorithm revisions must not reuse fields generated before them.
            return Hash128.Compute("ReFit-transfer-" + Revision + ":" + JsonUtility.ToJson(geometry)).ToString();
        }
    }
}
