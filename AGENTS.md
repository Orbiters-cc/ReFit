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
