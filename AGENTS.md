# ReFit Agent Instructions

## Armature Replacement Analysis

When investigating ReFit armature replacement, do not accept renderer-local bone counts as proof that the armature is correct. A result can have all renderer bones under the clothing object and still be wrong if it copied a full target avatar skeleton beside the original clothing skeleton instead of merging the two.

For every armature replacement fix or validation, inspect and report all of the following:

- The hierarchy shape under the clothing root, especially whether there are parallel humanoid branches such as two `Armature/Hips` chains.
- The renderer bone list compared against the original clothing bone list and the target avatar bone list.
- Which bones have nonzero mesh weight after the transfer. Bones that are not represented by the clothing's original armature and receive no clothing weight must not be present just because they exist on the target avatar.
- Preserved clothing-only bones, such as hood strings or clothing-specific `ChestUp`, must be integrated into the rebuilt clothing armature under the nearest matching target-derived parent, not left in a separate original armature branch.
- Debug snapshots after armature replacement must be self-contained clothing snapshots: the renderer, mesh, root bone, and all renderer bones should resolve inside the snapshot root unless the test explicitly covers external references.

Before declaring this class of issue fixed, run a scene-level diagnostic on the user's actual asset setup and verify that the resulting hierarchy matches the requested semantic structure, not only that `SkinnedMeshRenderer.bones` is non-null or under a broad parent.

## Repeated Failure Guard: Do Not Stop At Numeric Skinning Checks

For hoodie / clothing armature work, the following checks are mandatory and must be visible in the final verification notes:

- Inspect the actual Unity hierarchy produced by ReFit, not just logs. A passing result must not contain two sibling `Hips` branches, two sibling humanoid root branches, or an old clothing armature plus a rebuilt armature in parallel.
- Check the debug snapshots by label. `00_input_asset` can reveal stale corruption from a previous run; if it does, ReFit must either repair it before continuing or hard-fail with an explicit diagnostic. Do not ignore it because later renderer bone counts look correct.
- Validate the final hierarchy under the asset root after `03_armature_replaced`: exactly one active armature container for the refitted renderer, exactly one renderer root bone, and no duplicate normalized child names such as `hips`, `spine`, `chest`, `leftleg`, or `rightleg` under the same parent unless they are explicitly clothing-only non-skinned props.
- Treat `rootBoneInBones=True`, `nullBones=0`, and bindpose drift of zero as necessary but not sufficient. They do not prove that stale duplicate branches were removed.
- When fixing a bug reported with a screenshot, account for the screenshot's exact hierarchy label and object path. If the screenshot shows `00_input_asset`, reason about stale pre-existing scene state and make the tool robust against it instead of only validating the newly generated mesh.
- A screenshot showing two sibling `Hips` objects is itself a failing test. Do not describe the issue as fixed until an actual Unity scene inspection of the generated object reports one direct armature container, one humanoid root branch inside that container, and zero direct humanoid bone branches beside it.
- When the report says an armature replacement succeeded but the hierarchy screenshot disagrees, trust the hierarchy screenshot. Strengthen the code-level validator so the same hierarchy defect becomes a hard diagnostic instead of relying on manual visual review.
- XRay/debug gizmo objects under a clothing root are not part of the clothing asset. If `__XRayGizmos_*` renderers reference stale bones, they must be removed or explicitly ignored before deciding whether an old armature branch is still used.

## Weight Transfer And Projection Verification

When investigating weight-paint transfer, do not accept `sum > 0` as proof that a bone was transferred correctly. Compare the generated weights against both the previous clothing snapshot and the target body weights:

- For every relevant bone, report total weight, weighted vertex count, and weighted local/world bounds for `00_input_asset`, `01_scene_pose_as_default`, `02_generated_mesh_assigned`, `03_armature_replaced`, and `06_final_result`.
- A mapped clothing bone must keep a comparable influence after transfer. For example, if `Left elbow` maps to `forearm.L`, the generated `forearm.L` weight total and affected vertex region must be compared to the original `Left elbow` values, not merely checked for nonzero weight.
- Compare contamination against references, not arbitrary thresholds. If sleeve-classified vertices receive leg weights, compare that amount to the original clothing and to the target avatar's equivalent sleeve/body region before deciding whether it is acceptable.
- Do not infer an arm/leg/torso vertex region only from its current generated weights; that can validate the bug. Use multiple signals: original clothing weights, mapped source bones, nearest source body binding, target body binding, and spatial position.
- For close neighboring bones such as upper/lower arm or hips/upper leg, expect a transition band rather than a hard cutoff. Verify that the distribution is comparable to the original clothing and target body gradients, and that influence does not jump to anatomically unrelated regions.
- Left/right limb mismatches must be explicitly measured. A left sleeve vertex may blend between left shoulder/upper arm/forearm/hand, but right-arm or leg influence must be justified by source/target reference weights and should otherwise be treated as a failed projection.
- When proposing or testing projection changes, add a debug visualization plan for the actual source hit, chained target hit, ray/closest-point segment, triangle id, barycentric coordinates, distance, region, normal agreement, fallback reason, and final chosen weight source.
- Projection fixes must expose enough data to explain one vertex/group visually. A gizmo or report must show source hit, target hit, whether filters were relaxed, projected weights, original mapped weights, final weights, and the decision used to choose or blend them.

## Leaf Bone And Hierarchy Verification

When checking rebuilt leaf bones, do not stop at parent/child counts or bindpose validity:

- Read the full hierarchy by path and judge whether each chain makes semantic sense from root to leaves. State which branches are target-derived, which are preserved clothing-only branches, and where they attach.
- Compare leaf bone transforms across `00`, `01`, `02`, `03`, and `06`: world position, local/world rotation, parent distance, inferred tail direction, and whether a child/tail helper exists.
- If a leaf looks correct in `00`/`01`/`02` but changes in `03`, isolate whether the change came from target bone remapping, materialized transform placement, parent reassignment, bindpose refresh, or the debug/XRay renderer's leaf-tail heuristic.
- For leaf bones with no deforming child, validate the intended tail separately from the deforming joint transform. Do not move the deforming bone just to make the gizmo look longer. Prefer the target avatar child bone that represents the next anatomical joint, such as target `hand`/`wrist` for a generated forearm leaf, then fall back to preserved source clothing children or an inferred source tail if the target has no usable child.
- Non-deforming tail helpers must not be inserted into `SkinnedMeshRenderer.bones` and must not create target-only deforming bones such as lower legs on clothing that had no equivalent. They are hierarchy/gizmo children only.
- Before declaring a rebuilt armature valid, inspect representative chains such as `Hips/Spine/Chest/.../upper_arm.L/forearm.L`, left/right legs, hood bones, and hood strings, including lengths and orientations, not only duplicate branches.
