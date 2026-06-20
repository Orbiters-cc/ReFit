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
