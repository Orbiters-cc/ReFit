# ReFit — Design

## Goal

Make clothing/accessories made for model A fit model B (a different base, a derivative base with body edits, or a
body blendshape of B), delivered as a non-destructive `refit` blendshape at 100% on a duplicated mesh asset.

## Pipeline

1. **Staging** (`PoseNormalizer`) — Source avatar, target avatar and asset are cloned under a hidden root.
   Behaviours are disabled, both avatars are driven into the muscle-neutral humanoid pose
   (`HumanPoseHandler`, all muscles 0, identity body rotation). The source side is uniformly scaled so its
   armspan (fallback: hips→head) matches the target, then hip-aligned. A standalone asset is posed onto the
   source skeleton by normalized-name bone matching (positions + rotations, parent-first).
2. **Proportion check** (`ProportionChecker`) — mean distance between 19 humanoid landmarks, normalized by the
   target armspan. Above the threshold a *warning* is emitted; it **never blocks** (relocated-bones bases are a
   supported workflow).
3. **Snapshots** (`MeshSnapshot`) — skinning is evaluated manually (LBS from bones × bindposes) so the exact
   per-vertex skin matrix `M(v)` is available. Blendshape weights are read from the renderers (the visible shape
   is the basis); the transferred target shape is overridden to 0 (basis) / 1 (shaped). Welding groups
   (position-identical vertices across UV/normal seams) and group adjacency are precomputed.
4. **Binding** (`SurfaceBvh` + `SurfaceBindingSolver`) — each asset welding group binds once to the closest point
   (triangle + barycentric) on the source body, BVH-accelerated, restricted by normal agreement (≤ 80°) and
   coarse body-region compatibility (regions from dominant humanoid bone weights; torso compatible with all,
   left/right limb and arm/leg pairs rejected). Filters relax automatically when nothing valid is in range.
   Bindings are computed once and reused for every evaluation — the key speed advantage over per-shape
   closest-point transfer (cf. ShapeKeyTransferBlender).
5. **Deformation**
   - *Mesh refit*: the bound source point is re-bound to the target body; delta = `cpB − cpA`
     (`Translate` mode) or with the offset rotated from the source normal to the target normal
     (`RotateWithNormal`). Smoothstep falloff over the asset↔body distance
     (`falloffStartDistance` → `maxProjectionDistance`), then Laplacian smoothing of the **delta field**
     over group adjacency.
   - *Blendshape refit*: barycentric transform at the target binding between the basis and shaped target
     snapshots (same-topology, exact).
6. **Blendshape space conversion** — world deltas must become pre-skinning mesh deltas:
   - armature kept: `delta_local = M(v)⁻¹ · delta_world` (inverse linear-blend skinning — the classic bug in
     naive tools is skipping this);
   - armature replaced: new bindposes are captured in the staged pose
     (`bindpose_i = bone.worldToLocal × renderer.localToWorld`), making mesh space ≡ staged renderer space, so
     `delta_local = w2l·(world + delta) − local`. At `refit = 100` the asset matches the target exactly; at 0 it
     shows its original shape on the target rig.
   Normal deltas are recomputed (area-weighted, weld-averaged) so the shape lights correctly at full weight.
7. **Skinning transfer** (`WeightTransfer`) — target body weights are barycentrically interpolated at each
   binding (top-4, renormalized). Vertices ≥ 40% weighted to bones without a target equivalent keep their
   original weights; those bones are preserved: subtree roots are re-parented under the nearest resolved target
   bone with their staged relative TRS (duplicated when they belonged to the source avatar).
8. **Output** (`ReFitAssetPipeline`, editor) — mesh saved to `Assets/ReFit/<asset>/`, scene application with full
   Undo, prefab saved when the armature is kept. Bone references travel as child-index paths so they resolve on
   clones and freshly instantiated prefabs.

## Architecture

- `orbiters.refit` (Runtime asmdef, UnityEngine only): types, mapper, normalizer, snapshots, BVH, solver, weight
  transfer, `ReFitEngine` (request → `ReFitComputation`, no asset/scene side effects).
- `orbiters.refit.Editor`: `ReFitService` (public facade: `Execute` / `Validate`), `ReFitAssetPipeline`,
  UIToolkit wizard styled after MCB.

## Async & multi-shape

The engine is split into three phases: **Prepare** (main thread: staging, snapshots, regions, bone plan),
**ComputeGeometry** (thread-safe: BVH, bindings, deltas, smoothing, weight projection — runs in `Task.Run` for
`RunCoroutine`/`ExecuteCoroutine`) and **Bake** (main thread: Mesh creation). Multiple target blendshapes are
transferred in one pass by reusing the bindings; per-shape world deltas are `M(v)·frameDelta` evaluated at the
binding's barycentric coordinates.

## Scale & alignment robustness

Scale matching prefers humanoid landmarks (armspan / hips-to-head) but falls back to the **body meshes' baked
world height** when an avatar is non-humanoid or imported at a different unit scale — this prevents the
"result 3× too small" failure on generic/furry rigs. Alignment likewise falls back from hips to baked body
centers.

## MCB integration (orbiters.mcb, optional dependency via ORBITERS_REFIT)

`ReFitDrawer` (avatar options frame at the top of the version options: a toggle per asset + an integrated-progress
**Apply** button; Apply re-fits newly-ticked assets and restores un-ticked ones; no message in the nominal case)
+ `MCBReFitIntegration`:
- **Candidates** = SMRs whose renderer path is not in the version's targeted-FBX renderer set
  (`SmrPathService.CollectSmrPathsByFbx` matches by mesh name, so version-swapped Body/Tail meshes are excluded).
- **Model A** = a hidden clone of the *scene avatar* with the targeted body meshes swapped back to the base FBX
  meshes. Because it shares the scene avatar's exact skeleton, scale and pose, binding is clean and there is no
  scale/armature mismatch (this replaced the earlier raw-FBX-as-source approach). Model B = the live version body.
- Blendshapes = the version's exposed `customBlendshapes`, transferred in one pass with their exact names
  (`prefixTransferredShapes = false`) so MCB's by-name shape drivers animate the asset too.
- `replaceArmature = false` (the asset already rides the avatar's armature).
- Original meshes tracked in `MyCustomBase.appliedRefits` (serialized → survives restarts), restored on reset
  and on un-ticking; Unit Git commit of the generated files when ORBITERS_UNITGIT.

## Known limits / future work

- Completely different bases rely on closest-point projection; concave regions can need manual cleanup.
  Detected via the proportion warning (still proceedable, per design).
- 4 bone influences per vertex (`Mesh.boneWeights`); >4-influence meshes are approximated.
- Replaced-armature results reference scene bones, so no standalone prefab in that mode (VRCFury/MA armature-link
  style output could be added later).
- Possible optimizations: Burst/job kernels, GPU compute (cf. Auto Body Hider), input-hash caching.
- Preview overlay (BellyBulge-style before/after dots) on the summary step.
