# ReFit Design

## Boundaries

`orbiters.refit` contains geometry, staging, settings and result data, with no UnityEditor dependency.
`ReFitEngine.Prepare` and `Bake` run on Unity's main thread. `ComputeGeometry` operates on captured arrays and
immutable surface indices; the coroutine runs it on a worker. Do not call the whole engine on a worker thread.
Armature planning is in the `ReFitEngine.Armature` partial; it resolves Unity transforms during preparation.
The split does not introduce a second engine or alternate algorithm.

`orbiters.refit.Editor` owns undo, output assets, snapshots and UI. `ReFitService` is the public facade.
The optional `orbiters.refit.XRay.Editor` assembly registers external gizmos through `ReFitGizmoIntegration`;
the main editor assembly has no XRay reference. MCB's optional reflection/authentication bridge is centralized
in `MCBIntegrationService`, not repeated in commission UI code.

## Staging and Correspondence

1. Clone source, target and accessory into a hidden, disposable stage. Preserve authored body poses rather
   than driving every humanoid into zero muscles. Exclude nested garment copies from target body/bone
   discovery. Resolve externally referenced clothing bones onto the stage.
2. Compare bodies with body blendshapes disabled. Scale the source side by body surface height; hips/head
   and hand-span landmarks are fallbacks when body bounds are unavailable. Align avatar roots, not hips
   or surface centers: their relative offsets can be intentional edits.
3. For a standalone source-space garment, evaluate source-joint snapping. An inserted/displaced accessory
   joint can make this worse despite correct name matching. Sample up to 512 vertices before/after snapping;
   if both mean and P95 body distance worsen by more than 1 mm, restore the garment's scene pose. Log both
   measurements. Then bake the staged skin pose into an owned default-pose mesh. Target-space garments are
   not independently snapped onto the source.
4. `MeshSnapshot` captures evaluated vertices, skin matrices, topology, welding groups and adjacency.
   The primary comparison uses default body surfaces. Each requested target frame is captured separately.
5. `SurfaceBindingSolver` binds welded clothing groups to source/target triangles using barycentric coordinates,
   distance falloff, normal and bone-region filtering. Relaxed correspondence is recorded in diagnostics.
   Weight transfer applies its own influence compatibility policy: a geometric hit alone does not authorize
   unrelated bone influence. Bindings are reused across requested shapes.

## Deformation and Skinning

Mesh refit starts from the bound target-minus-source surface displacement. Blendshape transfer evaluates the
target body's frame displacement at the target binding. Primary and transferred fields have independent
smoothing controls. Clearance correction, surface guards, bounded hem response and detached-island coherence
refine the fields. Shared `ReFitSettingsPresets.ApplyTightness` drives the wizard's clearance policy.
`garmentKind` permits an explicit override instead of depending on ancestor names.

World-space deformation becomes mesh-local pre-skin deltas. Keeping the original rig requires inverse skin
conversion; replacing it uses planned bindposes and a target-space basis. This is not an exact surface-fitting
guarantee: sparse garment polygons, ambiguous projections and correction caps remain observable limitations.
Signed clearance and triangle/edge checks complement point-distance tests.

Replacement builds one clothing skeleton from the clothing's required bone set. Matched transforms use target
bone information; clothing-only hood, string and other extra chains remain attached to mapped parents.
Missing downstream deformation bones are not imported wholesale. Leaf visualization helpers use the nearest
appropriate target child (shin after thigh, wrist after forearm), with original-clothing fallback; helpers are
not added to the renderer's deforming bone list. Target weights are projected/remapped, and extras are preserved
according to the configured policy, with four normalized influences per vertex.

## Ownership and Application

Request settings and shape names are copied at invocation; Unity object references are not deep-copied until
staging. Validation and service preflight are read-only. Coroutine input stamps include transforms/renderers,
metadata and native mesh dirty revisions, rejecting concurrent input edits before application.

The stage owns pose-baked temporary meshes until it transfers one to the engine. The engine releases its
template on success, failure or coroutine disposal. A direct engine caller owns the computation's output mesh,
including any partially baked mesh on failure. The editor service disposes failed output automatically.
Cancellation is cooperative between geometry phases. Dispose abandoned coroutines rather than discarding
enumerators. No scene transaction remains open while waiting for the worker.

After computation the service opens a short synchronous Undo group, captures debug stages, applies the mesh
and skeleton, then saves the mesh. Failed application restores scene state, including prefab connectivity when
unpacking was necessary, and removes only assets created by that operation. Optional prefab saving can warn
while leaving a valid mesh result. Successful scene undo does not delete saved files.
Core application never selects or pings objects; UI selection is an explicit user action.

## Cache Provenance

Cached target bindings/deltas carry content identities, not just shape names or vertex counts. Identities cover
vertices, topology, normals, skin weights, bindposes, bone names/poses, garment shape content/base-affecting
sliders, target frame deltas and relevant geometry settings. Output-only settings are excluded. Data without
matching provenance is recomputed, not silently treated as valid. This is cache validation, not cryptographic
integrity checking.

## Performance Policy

- Reuse source and target BVHs per operation. Build a shaped-body BVH once per clearance pass, reusing it
  for repeated guards and final measurement.
- Compute final penetration diagnostics after all corrections, not repeatedly when intermediate results
  are overwritten without influencing geometry.
- Sort BVH subranges in place and use a bounded query-local stack, avoiding per-query allocations and
  shared mutable query state.
- Share bindings, captured body data and staging across a multi-shape request. Keep verified arithmetic and
  projection order; a bounds-only BVH-refit experiment was rejected after measurable output drift.
- Emit phase timings and compare geometry against an independently recorded baseline. Native/GPU backends
  are not included: redundant work was removable without platform binaries or numeric changes.

## Verification and Limits

The deterministic runner combines synthetic cases, both authored FBXs and optional licensed local regressions.
Missing prerequisites are skips, not passes. The v2 inserted-joint result is compared vertex-by-vertex and
shape-by-shape with v1 within 1 mm. Comparison to the separately authored result uses forward/reverse surface
statistics and triangle-quality bounds. Neither test implies universal submillimeter authored accuracy.

Inspect full debug hierarchies, target versus extra provenance, parent distances, leaf directions and per-bone
influence totals/counts/bounds. Offscreen previews are necessary additional evidence, not replacements for
measurements. Defaults and tightly fitted wizard presets can produce different coverage; record actual settings
when diagnosing clipping. Private mesh/photo artifacts stay under `Temp`.

The default suite excludes the selection-changing VRCFury build and active-scene Hoodie checks; both have
explicit separate entry points. Main-source standalone compilation excludes Orbiters/project references;
fresh-project UPM resolution remains a separate packaging check. See the README for commands.
