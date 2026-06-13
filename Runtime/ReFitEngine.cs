using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Orbiters.ReFit
{
    /// <summary>
    /// The ReFit geometry engine. Produces a <see cref="ReFitComputation"/> (new mesh with a "refit"
    /// blendshape, optional new skinning) from a <see cref="ReFitRequest"/> without saving assets or touching
    /// the scene — the editor layer applies the result. Safe to call from editor code at any time.
    /// </summary>
    public class ReFitEngine
    {
        /// <summary>Runs the full computation.</summary>
        public ReFitComputation Run(ReFitRequest request, ReFitProgress progress = null)
        {
            var comp = new ReFitComputation();
            var report = comp.report;
            if (!ValidateRequest(request, report)) return comp;
            var settings = request.settings ?? new ReFitSettings();

            progress?.Invoke(0.02f, "Staging avatars");
            using (var stage = PoseNormalizer.CreateStage(request, report))
            {
                if (stage == null) return comp;
                comp.assetRendererPath = stage.assetRendererPath;

                ProportionChecker.Check(stage, settings, report);

                bool wantMesh = request.mode != ReFitMode.Blendshape;
                bool wantShape = request.mode != ReFitMode.MeshToMesh;

                int shapeIndex = -1;
                if (wantShape)
                {
                    shapeIndex = stage.targetBody.sharedMesh.GetBlendShapeIndex(request.targetBlendshape ?? string.Empty);
                    if (shapeIndex < 0)
                    {
                        report.Error("blendshape-not-found",
                            $"Blendshape '{request.targetBlendshape}' was not found on the target body '{stage.targetBody.name}'.");
                        return comp;
                    }
                }

                // ----------------------------------------------------------
                // Snapshots
                // ----------------------------------------------------------
                progress?.Invoke(0.12f, "Capturing meshes");
                var assetSnap = MeshSnapshot.Capture(stage.assetRenderer, true, null, report);
                var basisOverride = wantShape ? new Dictionary<int, float> { { shapeIndex, 0f } } : null;
                var targetBasis = MeshSnapshot.Capture(stage.targetBody, false, basisOverride, report);
                MeshSnapshot targetShaped = wantShape
                    ? MeshSnapshot.Capture(stage.targetBody, false, new Dictionary<int, float> { { shapeIndex, 1f } }, report)
                    : null;
                MeshSnapshot sourceSnap = null;
                if (wantMesh)
                    sourceSnap = stage.sourceBody == stage.targetBody ? targetBasis : MeshSnapshot.Capture(stage.sourceBody, false, null, report);

                // ----------------------------------------------------------
                // Spatial indices & body regions
                // ----------------------------------------------------------
                progress?.Invoke(0.25f, "Building spatial indices");
                var bvhTarget = SurfaceBvh.Build(targetBasis);
                var bvhSource = wantMesh && sourceSnap != targetBasis ? SurfaceBvh.Build(sourceSnap) : bvhTarget;

                var sourceRegions = HumanoidBoneMapper.ClassifyBones(stage.sourceRoot, stage.sourceHumanMap);
                var targetRegions = stage.sourceIsTarget ? sourceRegions : HumanoidBoneMapper.ClassifyBones(stage.targetRoot, stage.targetHumanMap);
                var targetTriRegions = TriangleRegions(targetBasis, targetRegions);
                var sourceTriRegions = wantMesh && sourceSnap != targetBasis ? TriangleRegions(sourceSnap, sourceRegions) : targetTriRegions;
                var assetGroupRegions = AssetGroupRegions(assetSnap, stage, sourceRegions);

                // ----------------------------------------------------------
                // Bindings
                // ----------------------------------------------------------
                progress?.Invoke(0.35f, "Binding the asset to the body");
                var firstSnap = wantMesh ? sourceSnap : targetBasis;
                var firstBvh = wantMesh ? bvhSource : bvhTarget;
                var firstTriRegions = wantMesh ? sourceTriRegions : targetTriRegions;
                var bindings = SurfaceBindingSolver.ComputeGroupBindings(
                    assetSnap, firstSnap, firstBvh, settings, assetGroupRegions, firstTriRegions, report);

                int groupCount = assetSnap.GroupCount;
                var targetBindings = new SurfaceBinding[groupCount];
                if (wantMesh)
                {
                    progress?.Invoke(0.45f, "Projecting onto the target body");
                    float cosMax = Mathf.Cos(settings.maxNormalAngle * Mathf.Deg2Rad);
                    float chainRange = Mathf.Max(settings.maxProjectionDistance * 2f, 0.05f);
                    Parallel.For(0, groupCount, g =>
                    {
                        if (!bindings[g].valid) { targetBindings[g].valid = false; return; }
                        var nA = firstSnap.BaryNormal(bindings[g].triangle, bindings[g].bary);
                        var region = assetGroupRegions != null ? assetGroupRegions[g] : BodyRegion.Unknown;
                        targetBindings[g] = SurfaceBindingSolver.BindPoint(
                            bindings[g].point, targetBasis, bvhTarget, chainRange,
                            region, settings.filterByBoneRegion ? targetTriRegions : null,
                            nA, cosMax, settings.filterByNormal);
                    });
                }
                else
                {
                    targetBindings = bindings;
                }

                // ----------------------------------------------------------
                // Deformation fields (per welding group, world space)
                // ----------------------------------------------------------
                progress?.Invoke(0.55f, "Computing the deformation");
                Vector3[] meshDeltas = null;
                if (wantMesh)
                {
                    meshDeltas = new Vector3[groupCount];
                    Parallel.For(0, groupCount, g =>
                    {
                        if (!bindings[g].valid || !targetBindings[g].valid) { meshDeltas[g] = Vector3.zero; return; }
                        var cpA = bindings[g].point;
                        var cpB = targetBindings[g].point;
                        Vector3 d;
                        if (settings.offsetMode == OffsetMode.RotateWithNormal)
                        {
                            var p = assetSnap.worldVertices[assetSnap.groupRep[g]];
                            var nA = firstSnap.FaceNormal(bindings[g].triangle);
                            var nB = targetBasis.FaceNormal(targetBindings[g].triangle);
                            d = cpB + Quaternion.FromToRotation(nA, nB) * (p - cpA) - p;
                        }
                        else
                        {
                            d = cpB - cpA;
                        }
                        meshDeltas[g] = d * DeltaField.Falloff(bindings[g].distance, settings.falloffStartDistance, settings.maxProjectionDistance);
                    });
                    DeltaField.Smooth(meshDeltas, assetSnap.groupAdjacency, settings.smoothingIterations, settings.smoothingStrength);
                }

                Vector3[] shapeDeltas = null;
                if (wantShape)
                {
                    shapeDeltas = new Vector3[groupCount];
                    Parallel.For(0, groupCount, g =>
                    {
                        if (!targetBindings[g].valid) { shapeDeltas[g] = Vector3.zero; return; }
                        var d = targetShaped.BaryPoint(targetBindings[g].triangle, targetBindings[g].bary)
                              - targetBasis.BaryPoint(targetBindings[g].triangle, targetBindings[g].bary);
                        shapeDeltas[g] = d * DeltaField.Falloff(bindings[g].valid ? bindings[g].distance : float.MaxValue,
                            settings.falloffStartDistance, settings.maxProjectionDistance);
                    });
                    DeltaField.Smooth(shapeDeltas, assetSnap.groupAdjacency, settings.smoothingIterations, settings.smoothingStrength);
                }

                // ----------------------------------------------------------
                // New skinning (bones, bindposes, weights)
                // ----------------------------------------------------------
                bool replace = settings.replaceArmature && wantMesh && !stage.sourceIsTarget && !assetSnap.rigid;
                Matrix4x4[] newBindposes = null;
                BoneWeight[] newWeights = null;
                if (replace)
                {
                    progress?.Invoke(0.7f, "Rebinding to the target armature");
                    replace = BuildBonePlan(stage, assetSnap, targetBasis, settings, comp, report,
                        targetBindings, out newBindposes, out newWeights);
                    if (!replace)
                        report.Warn("armature-replace-skipped", "Could not build the target bone plan; keeping the asset's original armature.");
                }
                comp.armatureReplaced = replace;

                // ----------------------------------------------------------
                // Blendshape deltas in mesh space
                // ----------------------------------------------------------
                progress?.Invoke(0.8f, "Baking blendshapes");
                int vertexCount = assetSnap.localVertices.Length;
                Vector3[] primaryLocal = null, secondaryLocal = null;
                if (wantMesh)
                {
                    primaryLocal = new Vector3[vertexCount];
                    var w2l = assetSnap.rendererWorldToLocal;
                    Parallel.For(0, vertexCount, i =>
                    {
                        var dWorld = meshDeltas[assetSnap.groupOfVertex[i]];
                        if (replace)
                        {
                            // New bindposes are captured in the staged pose: mesh space == staged renderer space.
                            primaryLocal[i] = w2l.MultiplyPoint3x4(assetSnap.worldVertices[i] + dWorld) - assetSnap.localVertices[i];
                        }
                        else
                        {
                            // Original skinning kept: un-skin the world delta through the inverse skinning matrix.
                            primaryLocal[i] = assetSnap.skinMatrices[i].inverse.MultiplyVector(dWorld);
                        }
                    });
                }
                if (wantShape)
                {
                    secondaryLocal = new Vector3[vertexCount];
                    var w2l = assetSnap.rendererWorldToLocal;
                    Parallel.For(0, vertexCount, i =>
                    {
                        var dWorld = shapeDeltas[assetSnap.groupOfVertex[i]];
                        secondaryLocal[i] = replace
                            ? w2l.MultiplyVector(dWorld)
                            : assetSnap.skinMatrices[i].inverse.MultiplyVector(dWorld);
                    });
                }

                // ----------------------------------------------------------
                // Output mesh
                // ----------------------------------------------------------
                var newMesh = Object.Instantiate(assetSnap.mesh);
                newMesh.name = assetSnap.mesh.name.Replace("(Clone)", "") + "_ReFit";

                if (primaryLocal != null)
                {
                    comp.primaryShapeName = UniqueShapeName(newMesh, settings.blendshapeName);
                    var normalDeltas = settings.recalculateNormalDeltas
                        ? NormalDeltas(assetSnap, primaryLocal, null, newMesh)
                        : null;
                    newMesh.AddBlendShapeFrame(comp.primaryShapeName, 100f, primaryLocal, normalDeltas, null);
                }
                if (secondaryLocal != null)
                {
                    comp.secondaryShapeName = UniqueShapeName(newMesh,
                        wantMesh ? settings.blendshapeName + "_" + request.targetBlendshape : request.targetBlendshape + "_refit");
                    var normalDeltas = settings.recalculateNormalDeltas
                        ? NormalDeltas(assetSnap, secondaryLocal, primaryLocal, newMesh)
                        : null;
                    newMesh.AddBlendShapeFrame(comp.secondaryShapeName, 100f, secondaryLocal, normalDeltas, null);
                }

                if (replace)
                {
                    newMesh.boneWeights = newWeights;
                    newMesh.bindposes = newBindposes;
                }

                comp.mesh = newMesh;
                comp.success = !report.HasErrors;
                progress?.Invoke(1f, "Done");
                return comp;
            }
        }

        /// <summary>
        /// Dry run: stages the avatars, checks armature matching and proportions, and returns the diagnostics
        /// without computing any geometry. Used by UIs to surface warnings before the user commits.
        /// </summary>
        public ReFitReport Validate(ReFitRequest request)
        {
            var report = new ReFitReport();
            if (!ValidateRequest(request, report)) return report;
            using (var stage = PoseNormalizer.CreateStage(request, report))
            {
                if (stage == null) return report;
                ProportionChecker.Check(stage, request.settings ?? new ReFitSettings(), report);
                if (request.mode != ReFitMode.MeshToMesh && !string.IsNullOrEmpty(request.targetBlendshape) &&
                    stage.targetBody != null && stage.targetBody.sharedMesh != null &&
                    stage.targetBody.sharedMesh.GetBlendShapeIndex(request.targetBlendshape) < 0)
                {
                    report.Error("blendshape-not-found",
                        $"Blendshape '{request.targetBlendshape}' was not found on the target body '{stage.targetBody.name}'.");
                }
            }
            return report;
        }

        private static bool ValidateRequest(ReFitRequest request, ReFitReport report)
        {
            if (request == null) { report.Error("bad-request", "Request is null."); return false; }
            if (request.assetRenderer == null) { report.Error("missing-asset", "No asset renderer set."); return false; }
            if (request.targetAvatar == null) { report.Error("missing-target", "No target avatar set."); return false; }
            if (request.mode != ReFitMode.Blendshape && request.sourceAvatar == null)
            {
                report.Error("missing-source", "Mesh re-fit needs the avatar the asset was made for (source avatar).");
                return false;
            }
            if (request.mode != ReFitMode.MeshToMesh && string.IsNullOrEmpty(request.targetBlendshape))
            {
                report.Error("missing-blendshape", "Blendshape transfer needs the name of the target body blendshape.");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Regions
        // ------------------------------------------------------------------

        private static BodyRegion[] TriangleRegions(MeshSnapshot body, Dictionary<Transform, BodyRegion> boneRegions)
        {
            var perBone = new BodyRegion[body.bones != null ? body.bones.Length : 0];
            for (int k = 0; k < perBone.Length; k++)
            {
                perBone[k] = BodyRegion.Unknown;
                if (body.bones[k] != null && boneRegions.TryGetValue(body.bones[k], out var r)) perBone[k] = r;
            }
            var perVertex = VertexRegions(body, perBone);

            int triCount = body.triangles.Length / 3;
            var perTri = new BodyRegion[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var ra = perVertex[body.triangles[t * 3]];
                var rb = perVertex[body.triangles[t * 3 + 1]];
                var rc = perVertex[body.triangles[t * 3 + 2]];
                perTri[t] = ra == rb || ra == rc ? ra : (rb == rc ? rb : ra);
            }
            return perTri;
        }

        private static BodyRegion[] VertexRegions(MeshSnapshot snap, BodyRegion[] perBone)
        {
            int n = snap.localVertices.Length;
            var regions = new BodyRegion[n];
            bool hasWeights = !snap.rigid && snap.boneWeights != null && snap.boneWeights.Length == n;
            for (int i = 0; i < n; i++)
            {
                regions[i] = BodyRegion.Unknown;
                if (!hasWeights) continue;
                var bw = snap.boneWeights[i];
                // strongest influence with a known region wins
                float best = 0f;
                PickRegion(bw.boneIndex0, bw.weight0, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex1, bw.weight1, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex2, bw.weight2, perBone, ref best, ref regions[i]);
                PickRegion(bw.boneIndex3, bw.weight3, perBone, ref best, ref regions[i]);
            }
            return regions;
        }

        private static void PickRegion(int idx, float w, BodyRegion[] perBone, ref float best, ref BodyRegion region)
        {
            if (w <= best || idx < 0 || idx >= perBone.Length) return;
            if (perBone[idx] == BodyRegion.Unknown) return;
            best = w;
            region = perBone[idx];
        }

        private static BodyRegion[] AssetGroupRegions(MeshSnapshot asset, NormalizedStage stage, Dictionary<Transform, BodyRegion> sourceRegions)
        {
            var perBone = new BodyRegion[asset.bones != null ? asset.bones.Length : 0];
            for (int k = 0; k < perBone.Length; k++)
            {
                perBone[k] = BodyRegion.Unknown;
                var bone = asset.bones[k];
                if (bone == null) continue;
                if (stage.assetOnSourceAvatar)
                {
                    if (sourceRegions.TryGetValue(bone, out var r)) perBone[k] = r;
                }
                else if (stage.assetBoneToSource.TryGetValue(bone, out var src) && src != null &&
                         sourceRegions.TryGetValue(src, out var r2))
                {
                    perBone[k] = r2;
                }
            }
            var perVertex = VertexRegions(asset, perBone);
            var perGroup = new BodyRegion[asset.GroupCount];
            for (int g = 0; g < perGroup.Length; g++) perGroup[g] = perVertex[asset.groupRep[g]];
            return perGroup;
        }

        // ------------------------------------------------------------------
        // Bone plan (armature replacement)
        // ------------------------------------------------------------------

        private static bool BuildBonePlan(NormalizedStage stage, MeshSnapshot asset, MeshSnapshot targetBody,
            ReFitSettings settings, ReFitComputation comp, ReFitReport report, SurfaceBinding[] targetBindings,
            out Matrix4x4[] bindposes, out BoneWeight[] weights)
        {
            bindposes = null;
            weights = null;
            var targetRoot = stage.targetRoot.transform;
            var targetNameIndex = HumanoidBoneMapper.BuildNameIndex(targetRoot);

            var stageBones = new List<Transform>();
            var refs = new List<ReFitBoneRef>();
            var indexOf = new Dictionary<Transform, int>();
            var keptSet = new HashSet<Transform>();

            int AddTargetBone(Transform t)
            {
                if (indexOf.TryGetValue(t, out int idx)) return idx;
                idx = stageBones.Count;
                stageBones.Add(t);
                indexOf[t] = idx;
                refs.Add(new ReFitBoneRef { origin = ReFitBoneOrigin.Target, path = ReFitUtility.IndexPath(t, targetRoot) });
                return idx;
            }

            // 1) all target body bones first (projected weights reference them directly)
            var bodyBoneToNew = new int[targetBody.bones != null ? targetBody.bones.Length : 0];
            for (int k = 0; k < bodyBoneToNew.Length; k++)
                bodyBoneToNew[k] = targetBody.bones[k] != null ? AddTargetBone(targetBody.bones[k]) : -1;

            // Reverse human map of the source (bone transform -> human bone) for chain resolution.
            var sourceHumanOf = new Dictionary<Transform, HumanBodyBones>();
            foreach (var kv in stage.sourceHumanMap)
                if (kv.Value != null && !sourceHumanOf.ContainsKey(kv.Value)) sourceHumanOf[kv.Value] = kv.Key;

            Transform ResolveToTarget(Transform assetOrSourceBone)
            {
                if (assetOrSourceBone == null) return null;
                // direct name match into the target skeleton
                if (targetNameIndex.TryGetValue(ReFitUtility.NormalizeName(assetOrSourceBone.name), out var byName))
                    return byName;
                // through the source avatar's humanoid chain
                var src = assetOrSourceBone;
                if (!stage.assetOnSourceAvatar && stage.assetBoneToSource.TryGetValue(assetOrSourceBone, out var mapped))
                    src = mapped;
                while (src != null)
                {
                    if (sourceHumanOf.TryGetValue(src, out var human) &&
                        stage.targetHumanMap.TryGetValue(human, out var tgt) && tgt != null)
                        return tgt;
                    if (src == stage.sourceRoot.transform) break;
                    src = src.parent;
                }
                return null;
            }

            // 2) resolve every asset bone
            int assetBoneCount = asset.bones != null ? asset.bones.Length : 0;
            var assetBoneToNew = new int[assetBoneCount];
            var assetBoneIsExtra = new bool[assetBoneCount];
            int mapped2 = 0, keptCount = 0;
            var keptOriginRoot = stage.assetOnSourceAvatar ? stage.sourceRoot.transform : stage.assetStageRoot;
            var keptOrigin = stage.assetOnSourceAvatar ? ReFitBoneOrigin.SourceAvatar : ReFitBoneOrigin.Asset;

            for (int k = 0; k < assetBoneCount; k++)
            {
                var bone = asset.bones[k];
                if (bone == null) { assetBoneToNew[k] = -1; continue; }
                var target = ResolveToTarget(bone);
                if (target != null)
                {
                    assetBoneToNew[k] = AddTargetBone(target);
                    mapped2++;
                }
                else
                {
                    if (indexOf.TryGetValue(bone, out int existing)) { assetBoneToNew[k] = existing; assetBoneIsExtra[k] = true; continue; }
                    int idx = stageBones.Count;
                    stageBones.Add(bone);
                    indexOf[bone] = idx;
                    refs.Add(new ReFitBoneRef { origin = keptOrigin, path = ReFitUtility.IndexPath(bone, keptOriginRoot) });
                    keptSet.Add(bone);
                    assetBoneToNew[k] = idx;
                    assetBoneIsExtra[k] = true;
                    keptCount++;
                }
            }

            if (mapped2 == 0)
            {
                report.Warn("no-target-bones",
                    "No asset bone could be matched to the target avatar's skeleton; armature replacement is not possible.");
                return false;
            }
            if (keptCount > 0)
                report.Info("kept-bones", $"{keptCount} asset bones have no target equivalent and will be preserved (physics/extra bones).");

            // 3) placements for kept subtree roots
            var placements = new List<ReFitKeptBonePlacement>();
            foreach (var bone in keptSet)
            {
                if (bone.parent != null && keptSet.Contains(bone.parent)) continue; // inner bone, moves with its subtree root
                var parentTarget = FindResolvedAncestor(bone, ResolveToTarget, keptOriginRoot);
                if (parentTarget == null)
                {
                    stage.targetHumanMap.TryGetValue(HumanBodyBones.Hips, out parentTarget);
                    if (parentTarget == null) parentTarget = targetRoot;
                }
                placements.Add(new ReFitKeptBonePlacement
                {
                    boneIndex = indexOf[bone],
                    targetParentPath = ReFitUtility.IndexPath(parentTarget, targetRoot),
                    localPosition = parentTarget.InverseTransformPoint(bone.position),
                    localRotation = Quaternion.Inverse(parentTarget.rotation) * bone.rotation,
                    localScale = SafeDivide(bone.lossyScale, parentTarget.lossyScale)
                });
            }

            // 4) root bone: target hips when available
            comp.rootBoneIndex = -1;
            if (stage.targetHumanMap.TryGetValue(HumanBodyBones.Hips, out var hips) && hips != null)
                comp.rootBoneIndex = AddTargetBone(hips);

            // 5) bindposes captured in the staged pose, relative to the staged asset renderer
            var rendererL2W = stage.assetRenderer.transform.localToWorldMatrix;
            bindposes = new Matrix4x4[stageBones.Count];
            for (int i = 0; i < stageBones.Count; i++)
                bindposes[i] = stageBones[i].worldToLocalMatrix * rendererL2W;

            // 6) weights
            weights = settings.transferWeights
                ? WeightTransfer.Transfer(asset, targetBody, targetBindings, bodyBoneToNew, assetBoneToNew, assetBoneIsExtra, settings, report)
                : RemapAllOriginal(asset, assetBoneToNew);

            comp.bones = refs.ToArray();
            comp.keptPlacements = placements.ToArray();
            return true;
        }

        private static Transform FindResolvedAncestor(Transform bone, System.Func<Transform, Transform> resolve, Transform stopAt)
        {
            var cur = bone.parent;
            while (cur != null)
            {
                var t = resolve(cur);
                if (t != null) return t;
                if (cur == stopAt) break;
                cur = cur.parent;
            }
            return null;
        }

        private static Vector3 SafeDivide(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(b.x) > 1e-8f ? a.x / b.x : 1f,
                Mathf.Abs(b.y) > 1e-8f ? a.y / b.y : 1f,
                Mathf.Abs(b.z) > 1e-8f ? a.z / b.z : 1f);
        }

        private static BoneWeight[] RemapAllOriginal(MeshSnapshot asset, int[] assetBoneToNew)
        {
            int n = asset.localVertices.Length;
            var result = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                var bw = asset.boneWeights[i];
                bw.boneIndex0 = Remap(bw.boneIndex0, assetBoneToNew);
                bw.boneIndex1 = Remap(bw.boneIndex1, assetBoneToNew);
                bw.boneIndex2 = Remap(bw.boneIndex2, assetBoneToNew);
                bw.boneIndex3 = Remap(bw.boneIndex3, assetBoneToNew);
                result[i] = bw;
            }
            return result;
        }

        private static int Remap(int idx, int[] map) => idx >= 0 && idx < map.Length && map[idx] >= 0 ? map[idx] : 0;

        // ------------------------------------------------------------------
        // Blendshape helpers
        // ------------------------------------------------------------------

        private static string UniqueShapeName(Mesh mesh, string desired)
        {
            if (string.IsNullOrEmpty(desired)) desired = "refit";
            if (mesh.GetBlendShapeIndex(desired) < 0) return desired;
            for (int i = 2; ; i++)
            {
                var candidate = $"{desired}_{i}";
                if (mesh.GetBlendShapeIndex(candidate) < 0) return candidate;
            }
        }

        /// <summary>
        /// Per-vertex normal deltas so the shape lights correctly at full weight:
        /// delta = normals(displaced) - original normals, with welding-group averaging.
        /// <paramref name="baseDeltas"/> is the already-applied previous frame (for stacked shapes), may be null.
        /// </summary>
        private static Vector3[] NormalDeltas(MeshSnapshot asset, Vector3[] frameDeltas, Vector3[] baseDeltas, Mesh mesh)
        {
            int n = asset.localVertices.Length;
            var before = new Vector3[n];
            var after = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var b = asset.localVertices[i];
                if (baseDeltas != null) b += baseDeltas[i];
                before[i] = b;
                after[i] = b + frameDeltas[i];
            }
            var beforeNormals = WeldedNormals(before, asset);
            var afterNormals = WeldedNormals(after, asset);
            var meshNormals = mesh.normals;
            bool useMeshNormals = baseDeltas == null && meshNormals != null && meshNormals.Length == n;

            var deltas = new Vector3[n];
            for (int i = 0; i < n; i++)
                deltas[i] = afterNormals[i] - (useMeshNormals ? meshNormals[i] : beforeNormals[i]);
            return deltas;
        }

        private static Vector3[] WeldedNormals(Vector3[] verts, MeshSnapshot asset)
        {
            var normals = new Vector3[verts.Length];
            var tris = asset.triangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var fn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                normals[a] += fn; normals[b] += fn; normals[c] += fn;
            }
            // weld across groups so seams keep identical normals
            var groupSum = new Vector3[asset.GroupCount];
            for (int i = 0; i < verts.Length; i++) groupSum[asset.groupOfVertex[i]] += normals[i];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = groupSum[asset.groupOfVertex[i]];
                normals[i] = v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.up;
            }
            return normals;
        }
    }
}
