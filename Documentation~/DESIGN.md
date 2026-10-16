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
2. Compare bodies with body blendshapes disabled, except those the clothing sets while it is worn
   (`ReFitRequest.wornBodyShapes`: an outfit toggle hiding fluff or shrinking the skin under it). The editor service
   reads them from the clothing's own VRCFury toggles (the toggle turning the part on, its blendshape actions on the body
   or on every renderer) and Modular Avatar Shape Changers, without referencing either package; both bodies are
   captured with them, and the target identity includes them. Scale the source side by body surface height; hips/head
   and hand-span landmarks are fallbacks when body bounds are unavailable. Align avatar roots, not hips
   or surface centers: their relative offsets can be intentional edits.
3. For a standalone source-space garment, evaluate source-joint snapping. An inserted/displaced accessory
   joint can make this worse despite correct name matching. Sample up to 512 vertices before/after snapping;
   if both mean and P95 body distance worsen by more than 1 mm, restore the garment's scene pose. Log both
   measurements. Then bake the staged skin pose into an owned default-pose mesh. Target-space garments are
   not independently snapped onto the source.
4. `MeshSnapshot` captures evaluated vertices, skin matrices, topology, welding groups and adjacency.
   Welding joins vertices at the same mesh position, and vertices on open edges within 0.1 mm of each other in the world:
   a seam the modelling tool split a hair apart otherwise opens once its sides move differently. Closed pieces touching
   (a chain's links) stay apart. The primary comparison uses
   default body surfaces. Each requested target frame is captured separately.
5. `SurfaceBindingSolver` binds welded clothing groups to source/target triangles using barycentric coordinates,
   distance falloff, normal and bone-region filtering. Relaxed correspondence is recorded in diagnostics.
   A clothing face turned towards the skin it covers (a lining, the inside of a strap) keeps that skin unless a
   face turned its way is about as close. Regions are bands: a triangle belongs to every region with a quarter of
   its skin weight; across bodies a limb's clothing also accepts torso skin, never another limb's. Source points
   map to the target (`ChainPoint`) by nearest point where the bodies overlap within 1 cm, otherwise along the
   source normal onto faces turned the same way, unless that is over three times the nearest distance plus 2 cm.
   Weight transfer applies its own influence compatibility policy: a geometric hit alone does not authorize
   unrelated bone influence. Bindings are reused across requested shapes.
6. Between two bodies (`registerBodies`), `ReFitBodyRegistration` lays the source body onto the target as one surface
   before any clothing point is matched. It starts where the skeletons put the source skin: each humanoid joint carried
   onto its counterpart, its segment turned onto the target's and scaled by their length ratio (half to double), each
   vertex blended through its skinning; the start is kept only when it brings the source at least 2 mm closer to the
   target on average. Welded source vertices are then pulled to target skin turned their way, of a compatible region
   (along the normal when that is at most 1.5 times the nearest distance plus 5 mm), weighted by normal agreement and
   distance, while a graph-Laplacian keeps the move smooth. Ten passes go from stiff (proportions) to loose (shape), each
   solved by Jacobi-preconditioned conjugate gradients per axis from the previous move; skin enclosed in another part
   has no data term. Source points then map to the registered point pressed onto the nearest target skin along its
   normal. Neighbouring clothing points keep neighbouring counterparts, where independent nearest points jump into
   creases and onto fluff. On the Novabeast to Ultinova bodies the registered source lies on the target within 1 mm
   (95 %), on the Hypernova within 15 mm (its larger fluff has no source counterpart).
   The registration is not used, and the passes below take over, where it disagrees with the skin met along the source
   normal: when 15 % of the clothing within 10 cm of the skin lies 3 cm or more from that skin's registered
   counterpart (a body scaled far beyond the base, where the registration cannot reach).

## Deformation and Skinning

With the registration, `ReFitClothFit` (`clothFit`) fits mesh refits like cloth, in place of the displacement,
clearance, coverage and drape passes below:

- **Motion.** Each welded group takes the registered motion of the source skin around it: skin turned towards it, of a
  compatible region, within its distance to the skin plus 2.5 spreads (a spread is 1.5 cm or 0.3 of that distance,
  whichever is larger; 15 cm at most), weighted down from the nearest; skin the authored fabric covers drives it, other
  skin only where none is under it. The motion is smooth over the fabric, so nothing jumps between body parts, and
  distance falloff scales it as before. Pieces smaller than 3 cm (a chain's links, a button) take the mean motion of
  their groups: a link lying across the neck and the chest would otherwise tear.
- **Coverage.** A target vertex is covered where its counterpart on the source (the registered source within 1.2 cm,
  facing the same way; for main skin farther away, the registered source met along its normal, 30 cm inward or 5 cm
  outward) was under the authored fabric: fabric turned the same way met straight out of the skin, or along two of four
  rays tilted 26 degrees (folds the fabric bridges). Skin past a hem is never covered, however close, and target skin
  without a counterpart (fluff the source lacks) never moves the fabric; how much of it shows is reported. When more
  than 2 % of the target's main skin under the clothing has no counterpart or lies over 2 cm from the registered source
  (a sculpted edit far beyond the base), the cloth fit gives way to the passes below.
- **Drape.** 48 passes push fabric points out of covered skin under them to their authored room (0.6 to 3 mm) and push
  fabric triangles out over covered skin coming through them. What already came through the fabric as authored stays
  allowed (coarse fabric sagging into a curved body, straps pressing into it): the drape corrects only what the move
  adds. Pushes move at most 6 mm a pass (1.5 mm in the last eight) and 12 cm in all; they spread over the fabric (two
  Laplacian passes of the correction each pass, lighter in the last eight), and a push lifts the fabric around it at
  most 0.4 less per metre away, so a bump tents the fabric as a cone instead of lifting a whole panel. Pieces smaller
  than 3 cm move as one.
- **Shapes.** Body shapes are draped the same way from the fitted clothing: its groups take the shape motion of the
  target skin under them, then the drape runs over the shaped body with the coverage of the fitted clothing. A shape
  corrects only what it adds: clipping the fit left is measured once and allowed, since shapes are summed when several
  are on and each would otherwise apply the same correction again.

On the replayed outfits it runs in 5 to 15 s with their shapes, with up to nine times fewer spikes and no torn panels
with all shapes on (see VALIDATION.md).

Mesh refit without the registration starts from the bound target-minus-source surface displacement. Blendshape transfer evaluates the
target body's frame displacement at the target binding. Primary and transferred fields have independent
smoothing controls. Clearance correction, surface guards, bounded hem response and detached-island coherence
refine the fields. Shared `ReFitSettingsPresets.ApplyTightness` drives the wizard's clearance policy.
The surface guard samples triangle centers and edges, so it must not treat the clothing's own authoring as clipping:
each sample's depth on the body the clothing was made for is measured once, and the guard restores that depth (coarse
edges sag into curved bodies as authored). It lifts a sample only through corners holding at least half of it, since
lifting it through a lone movable corner pulls that corner into a spike.
`garmentKind` permits an explicit override instead of depending on ancestor names.
With the different-base coverage, skin enclosed in another part of the target (`MeshSnapshot.HideEnclosedSkin`: out of
it, the first surface met faces the same way, and from halfway out every way along the skin leaves through a surface too,
so a single-sided fluff card encloses nothing) is left out of every target query, and `ReFitDrape` follows the body's
coverage repair of the primary and each transferred field; the inner layers' repair and the covers come after it. It
keeps covered only target skin whose counterpart on the source lay under the authored clothing (fold walls and skin
without a counterpart take the decision of the skin around them), relaxes fabric crossing itself where it did not as
authored (a shape frame only the crossings it adds, never into the skin), lets fabric span the folds of the body (a
shape frame only over folds of its skin), then pushes the fabric out over covered skin still showing through, spreading
the push over the fabric and capping it at what the skin around needed. Closed rings follow the fabric under each part
of them; small closed details ride with it whole. The fabric's BVH is refitted between rebuilds, and the drape reports
progress and stops on cancellation. A fit made with the coverage records it, and shapes added to it later are covered
and draped the same way, the fit standing for where the clothing was made.

World-space deformation becomes mesh-local pre-skin deltas. Keeping the original rig requires inverse skin
conversion; replacing it uses planned bindposes and a target-space basis. This is not an exact surface-fitting
guarantee: sparse garment polygons, ambiguous projections and correction caps remain observable limitations.
Signed clearance and triangle/edge checks complement point-distance tests.

The armature is kept, whatever `replaceArmature` asks, when it already is the target's skeleton as the clothing is
worn: every weighted clothing bone stands within 2 mm and 2 degrees of the target bone of its name or humanoid role
(clothing-only chains under a matched bone excepted). Novabeast clothing on the Ultinova or the Hypernova keeps its
armature, its weights and its outfit's links.

Replacement builds one clothing skeleton from the clothing's required bone set. Matched transforms use target
bone information; clothing-only hood, string and other extra chains remain attached to mapped parents.
Missing downstream deformation bones are not imported wholesale. Leaf visualization helpers use the nearest
appropriate target child (shin after thigh, wrist after forearm), with original-clothing fallback; helpers are
not added to the renderer's deforming bone list. The clothing's mapped weights follow the change between the
target and source skinning at each group's matched points (both bodies' bones mapped into the new bone list),
scaled by the distance falloff and by contact (full within 1 cm of the skin, none from 4 cm), lightly smoothed over
the clothing, and rejected when they would move weight onto another limb. Vertices without mapped weights take the
target's projected weights; extras are preserved according to the configured policy, with four normalized
influences per vertex. Only bones the clothing is weighted to receive body weights: an armature exported whole lists
the base's fluff, tail and ear bones, and their body weights would make the clothing swing with their physics; the
body's weight on them goes to their nearest weighted ancestor.

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
and skeleton, then saves the mesh. Failed application restores scene state and removes only assets created by that
operation.

Prefabs stay packed. The rebuilt armature is added to the clothing (a prefab instance accepts added objects); stale
objects a prefab instance owns are hidden instead of removed (Restore shows them again); the clothing is moved under
the avatar only when that moves a prefab root or an object outside any prefab. The outfit's other parts keep their own
bones: rebinding serialized references never touches another `SkinnedMeshRenderer`, whose bindposes belong to its old
bones (rebound, it bent wherever the skeletons differ). A VRCFury Armature Link merging an armature other parts still
use stays theirs, and the rebuilt armature gets its own copy of the link; one merging only the refitted part's old
armature is pointed at the rebuilt one. Links merging another part's armature are left alone. The final hierarchy check
accepts an armature the other parts are skinned to. Optional prefab saving can warn
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
