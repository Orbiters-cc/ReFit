# ReFit Design

## Boundaries

`orbiters.refit` contains geometry, staging, settings and result data, with no UnityEditor dependency.
`ReFitEngine.Prepare` and `Bake` run on Unity's main thread. `ComputeGeometry` operates on captured arrays and
immutable surface indices; the coroutine runs it on a worker. Do not call the whole engine on a worker thread.
Armature planning is in the `ReFitEngine.Armature` partial; it resolves Unity transforms during preparation.
The split does not introduce a second engine or alternate algorithm.

`orbiters.refit.Editor` owns undo, output assets, snapshots and UI. `ReFitService` is the public facade.
`ReFitXRayIntegration` registers the projection and welded-group toggles of `ReFitGizmoIntegration` with the
Toolkit's Avatar Gizmos toolbar. MCB's optional reflection/authentication bridge is centralized
in `MCBIntegrationService`, not repeated in commission UI code.

## Staging and Correspondence

1. Clone source, target and accessory into a hidden, disposable stage. Preserve authored body poses rather
   than driving every humanoid into zero muscles. Exclude nested garment copies from target body/bone
   discovery. Resolve externally referenced clothing bones onto the stage.
2. Compare bodies with body blendshapes disabled, except those the clothing sets while it is worn
   (`ReFitRequest.wornBodyShapes`: an outfit toggle hiding fluff or shrinking the skin under it), and on the target the
   shapes the clothing does not follow, at their weight (`ReFitWornShapes.AsShown`: a custom base's hidden collar fluff,
   its face), and, fitting clothing from another body, the followed shapes it shows (see "Shapes the target shows"); other
   followed shapes are fitted at zero and transferred. The target identity includes those. The editor service
   reads them from the clothing's own VRCFury toggles (the toggle turning the part on, its blendshape actions on the body
   or on every renderer) and Modular Avatar Shape Changers, without referencing either package; both bodies are
   captured with them, and the target identity includes them. Scale the source side by body surface height; hips/head
   and hand-span landmarks are fallbacks when body bounds are unavailable. Clothing in the target's space whose weighted
   bones stand on the source's bones of their name (within 1 cm on average) was lined up with the source as it stands, as
   My Avatar puts an outfit on the original base's armature over the avatar: the source keeps its size and place. Scaled
   to the target's bounds (a custom base's ears and feathers enlarge them), the source left the clothing above it, and the
   fit carried that onto the target: the FishingOutfit rose 7 cm on the Ultirex. Align avatar roots, not hips
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
   The registration is not used, and the passes below take over, where it cannot reach the target under the clothing
   (a body scaled far beyond the base): when 15 % of the clothing within 10 cm of the skin lies 3 cm or more from the
   skin met along the source normal (`registrationDisagreementLimit`) and the laid source is more than 1 cm from the
   target skin under 10 % of that clothing. Laid that close, the registration is trusted over the normal, which meets
   fluff, feathers or another sheet of a body sculpted in layers (the Rexouium jacket on the Ultirex: 68 % disagreed with
   the source laid within 1 mm). Bodies laid onto each other are kept (three in memory, twelve in the project's
   `Library/ReFit`, keyed by every input and the algorithm's revision): refitting several pieces between the same bodies,
   or again in a later session, lays them once.

## Deformation and Skinning

With the registration, `ReFitClothFit` (`clothFit`) fits mesh refits like cloth, in place of the displacement,
clearance, coverage and drape passes below:

- **Motion.** Each welded group takes the registered motion of the source skin around it: skin turned towards it, of a
  compatible region, within its distance to the skin plus 2.5 spreads (a spread is 1.5 cm or 0.3 of that distance,
  whichever is larger; 15 cm at most), weighted down from the nearest; skin the authored fabric covers drives it, other
  skin only where none is under it. The motion is smooth over the fabric, so nothing jumps between body parts, and
  distance falloff scales it as before. Pieces smaller than 3 cm (a chain's links, a button) move rigidly: the turn and
  move closest to their groups' motions (`RigidFit`, Horn's unit quaternions), the turn at most 30 degrees (motions far
  from rigid, a link half in the skin pushed out at one end, would flip it over). A link lying across the neck and the
  chest would otherwise tear; turning, it lies on the skin instead of sliding through it.
- **Coverage.** A target vertex is covered where its counterpart on the source (the registered source within 1.2 cm,
  facing the same way; for main skin farther away, the registered source met along its normal, 30 cm inward or 5 cm
  outward) was under the authored fabric: fabric turned the same way met straight out of the skin, or along two of four
  rays tilted 26 degrees (folds the fabric bridges). Skin past a hem is never covered, however close, and target skin
  without a counterpart (fluff the source lacks) never moves the fabric; how much of it shows is reported. When more
  than 2 % of the target's main skin under the clothing (the head left out: a muzzle near a collar is not under it) has
  no counterpart or lies over 2 cm from the registered source (a sculpted edit far beyond the base), the cloth fit gives
  way to the passes below.
- **Drape.** 48 passes push fabric points out of covered skin under them to their authored room (0.6 to 3 mm) and push
  fabric triangles out over covered skin coming through them. What already came through the fabric as authored stays
  allowed (coarse fabric sagging into a curved body, straps pressing into it): the drape corrects only what the move
  adds. Pushes move at most 6 mm a pass (1.5 mm in the last eight) and 12 cm in all; they spread over the fabric (two
  Laplacian passes of the correction each pass, lighter in the last eight), and a push lifts the fabric around it at
  most 0.4 less per metre away, so a bump tents the fabric as a cone instead of lifting a whole panel. Pieces smaller
  than 3 cm move as one rigid piece. Fabric looks for covered skin in a hierarchy of the covered triangles only, not
  past the rest of the body.
- **Shapes.** Body shapes are draped the same way from the fitted clothing: its groups take the shape motion of the
  target skin under them, then the drape runs over the shaped body with the coverage of the fitted clothing. A shape
  corrects only what it adds: clipping the fit left is measured once and allowed, since shapes are summed when several
  are on and each would otherwise apply the same correction again.

**Shapes the target shows.** Clothing fitted from another body is fitted on the target with the followed shapes it
shows (an MCB custom base's muscles, at 100 by default), so what shows by default is the fit itself, not a fit at zero
plus each shape's drape (one drape of 48 passes, often stopped with fabric still under the muscles). A shape counts as
shown from 10 % of its frame (`ReFitWornShapes.Shown`, as Unity applies it at the body's weight); below that, for shapes
of several frames (each frame is draped on its own), shapes the clothing sets while worn and clothing on its own body (its
source is the target, without the shapes), the shape is fitted at zero as before (clothing given shapes alone: below).
The target is captured both ways (as shown, and without the shown shapes, its own enclosed skin left out).
- The source is laid onto the target without the shown shapes, the bodies alike, then carried by them: each registered
  point moves as the skin nearest to it (`ReFitBodyRegistration.Carried`). Laid straight onto the Ultinova's muscles, it
  reached too little of them, and the Classy pants gave way to the projection passes (44 s instead of 12).
- Each shown shape's clothing frame is the drape taking it away: the body moved by minus the share of the frame it shows,
  the fabric draped from the fit over that body, and the clothing's motion divided by minus the share. Shapes the target
  does not show are draped adding them, from that fit.
- The passes without the cloth fit (bodies that cannot be laid onto each other) fit at zero, as before, and add the shapes:
  their clearance and coverage only push fabric out, so a shape taken away left the clothing as far out as it had pushed it
  (the FBX fixture's sculpt: 28 mm from its authored result, 10 allowed; the ThiccWiker onesie: 1607 samples in the body
  instead of 1261).
- The refit shape gives back what those frames show (`GiveBackShown`): the refit with the shapes at the body's weights is
  the fit, and the refit alone is the clothing draped back without them. The drape taking a shape away only pushes, so
  fabric the shape pushed out stays out where the skin allows: without its muscles the clothing stands a little looser
  than a fit made there (where nothing was pushed, the refit alone is the fit at zero).
- Garments worn under it are captured with their own versions of the shown shapes at the body's weight and take them away
  with it. Skin weights are matched on the target without the shown shapes, where the bodies are alike: matched on the
  muscles, the Comfy tank top's hips followed a change of skinning they should not have, and came through in steps.
- The thighs are spread for the coverage by how they meet without the shapes: spread for the muscled Ultipaw and Ultinova,
  the FishingOutfit shorts (skinned to the hips down the thighs) stayed where the spread thighs were and they came back
  through them (Ultipaw: 197 vertices under the body instead of 22).

**Clothing given shapes alone** (no refit shape) was made either over a followed shape the target shows (a custom base's
muscles at 100) or before the body was shaped (made on the base, a slider moved afterwards). Fitted at zero, the first
got the shape a second time; fitted as shown, the second would keep clipping. ReFit tells them apart
(`ReFitWornShapes.MadeWithShown`) on the clothing points within 5 cm of the skin whose skin the shape moves by 1 mm or
more along its normal (8 at least), from each point's gap over the body as shown and over the body without the shape:
- Clothing is not made into the body it was made on: when more than 5 % more of those points lie over 2 mm in one body
  than in the other, it was made on the other.
- Otherwise (loose clothing, or clipping as much either way) it was made on the body it lies nearer (median gap). A
  wrong guess there is the mild one: taken for made with the shape, the clothing stays as authored at the body's weight.
- A per-point vote against the clothing's gap where the shape moves nothing was tried first: on the FishingOutfit shirt
  fitted over the Ultipaw's muscles (17 mm over them), that gap came from 41 points at the collar and hems 35 mm off,
  and the shirt was taken for made without the muscles.

`ReFitRequest.madeWithShapes` says it instead (Haï's Starmesh calls it Inverse: a costume made for a bigger chest worn on
a flat body), and shapes its ReFit metadata says a refit already follows are fitted at zero (that refit shape gives them
back).
- Made with a shape, the clothing is bound to the target as it shows, and the shape's clothing frame is the drape taking
  it away, as above. With no refit shape to give it back, the clothing's own mesh does (`GiveBackShownToRest`): its
  vertices move by minus the shown share of that frame and its normals and tangents turn with them, so at the body's
  weight the clothing is the authored mesh exactly and at zero it is the drape without the shape.
- Made without it, or moved too little to tell, the shape is fitted at zero and added from there, as before.

**Normals and tangents.** The refit and each shape turn the clothing's authored normals and tangents with the surface
(`SurfaceDeltas`): per vertex, the rotation from the position-welded smooth normal before the frame to the one after it,
a stacked frame first turning the authored normal by what lies under it. Hard edges, custom normals and normal-mapped
tangents keep their look, and a vertex whose welded normal does not change gets no delta, so shapes stay sparse. Until
0.7.3 every normal became the welded smooth normal at full weight (hard edges rounded, a normal delta on every vertex of
a shape given to clothing alone) and tangents were left as authored.

On the replayed outfits it runs in 5 to 15 s with their shapes, with up to nine times fewer spikes and no torn panels
with all shapes on (see VALIDATION.md).

With the different-base coverage asked for, clothing made for another body than the source (resized onto it by My Avatar
or by hand) is told apart from clothing made for it: more than 10 % of the clothing within 10 cm of the source skin lay
over 5 mm under it as authored (next to none for clothing made for that body, from 14 % to 66 % for the Rexouium
FishingOutfit on the Masculine Canine or the Novabeast). The cloth fit keeps what came through the fabric as authored,
and nothing there was meant to, so the different-base passes below (the coverage repair and `ReFitDrape`) cover the
target after it and the inner layers have the last word, as for clothing placed on the target itself. Shapes draped by
the cloth fit stay over the garments worn under it too. Clothing made for the source keeps the cloth fit alone. A caller
that knows the clothing was resized from another body says so (`ReFitSettings.madeForAnotherBody`: My Avatar, through
Orbiters Toolkit's `RefitJob.MadeForAnotherBody`, for clothing resized onto the avatar and not made for its original
base), and the share is not asked: the FashionSuit's trousers, made for the Vulper, lay under the Masculine Canine only
at the calves (4 %), the Rexouium FishingOutfit under the Ultirex's original, which it was made for, as much (4.4 %).

Mesh refit without the registration starts from the bound target-minus-source surface displacement. Blendshape transfer evaluates the
target body's frame displacement at the target binding. Primary and transferred fields have independent
smoothing controls. Clearance correction, surface guards, bounded hem response and detached-island coherence
refine the fields. Shared `ReFitSettingsPresets.ApplyTightness` drives the wizard's clearance policy.
The surface guard samples triangle centers and edges, so it must not treat the clothing's own authoring as clipping:
each sample's depth on the body the clothing was made for is measured once, and the guard restores that depth (coarse
edges sag into curved bodies as authored). It lifts a sample only through corners holding at least half of it, since
lifting it through a lone movable corner pulls that corner into a spike.
`garmentKind` permits an explicit override instead of depending on ancestor names.
Keeping the clothing's armature, the different-base coverage spreads both thighs by 20 degrees on the staged copies
(the clothing's own thigh bones with them) so fabric can be pushed between thighs that touch, and returns the fit through
the clothing's skinning. It does so only when the target's thighs meet (16 points of one thigh's skin within 1 cm of the
other's, facing it; one mesh point gathering both halves of the crotch does not count): clothing skinned to the hips
down the thighs stays where the spread thighs were, and they come back through it.
With the different-base coverage, skin enclosed in another part of the target (`MeshSnapshot.HideEnclosedSkin`: out of
it, the first surface met faces the same way, and from halfway out every way along the skin leaves through a surface
too, so a single-sided fluff card encloses nothing) is left out of every target query, and `ReFitDrape` follows the
body's coverage repair of the primary and each transferred field; the inner layers' repair and the covers come after it.
It keeps covered only target skin whose counterpart on the source lay under the authored clothing (fold walls and skin
without a counterpart take the decision of the skin around them); of clothing made for another body, also skin of an arm
or a leg through fabric lying over that limb of the source, farther than 3 cm along the fabric from a hem (a cuff sunk
into a larger arm reads as a tight cuff, pushed out it flares; on the torso, skin through a panel is as often a hip
below a hem or a neck in a collar, and covering it pulled a shirt's hem over the crotch), and that fabric is not kept
under the skin as made. It relaxes fabric crossing itself where it did not as authored (a shape frame only the crossings
it adds, never into the skin), lets fabric span the folds of the body (a shape frame only over folds of its skin), then
pushes the fabric out over covered skin still showing through, spreading the push over the fabric and capping it at what
the skin around needed. Only skin the fabric is nearer than the other side of that part of the body pushes it: going on
into the body twice as far as the fabric lies, the way must not leave it through skin turned the other way, the skin
enclosed in another part included (under a custom base's muscles and mane). Past the middle of a part, fabric goes out
the short way, from the skin across: a vest's strap sunk into a muscled shoulder goes out over its top, where the
armpit's skin pushed it down the arm. Closed rings follow the fabric under each part of them; small closed details ride
with it whole. Of clothing made for another body, the separate pieces up to an eighth of the garment (a belt, its loops
and buckle, a pocket) follow what untangling and bridging did to the fabric under each part of them, then are draped
like the rest: left where they were, the FashionSuit's belt and buckle sank under the waistband relaxed out over the
Ultipaw's hips. The coverage repair before the drape carries them the same way, and its closed rings part by part
(`ReFitDrape.CarryDetails` for both): carried whole by the largest correction, a belt slid forward and sank at the back.
Other clothing keeps its pieces to their own corrections: the onesie, a tangle of its own (4882 crossings), carried its
zipper up to 45 mm with the collar beside it (1070 samples in the body at rest, 946). The fabric's BVH is refitted
between rebuilds, and the drape reports progress and stops on cancellation. A fit made with the coverage records it, and
shapes added to it later are covered and draped the same way, the fit standing for where the clothing was made. Of
clothing made for another body, fabric of one leg or arm (the limb of the source it lay over as made, away from its
hems) is never pushed out over the other limb's skin: the muscled Ultinova's thighs meet and squeeze the inner panel of
one leg of the FishingOutfit shorts into the other thigh, and pushed out over that thigh it wrapped around it. There the
shoulders (clavicles) belong to the torso and the arms start at the upper arms, where a sleeve does: fabric over a
shoulder is a strap or a sleeve's cap.

**Fix clipping** (`declipAfterFit`, off by default; `ReFitDeclip`). After either fit, once the garments worn under it are
covered and before the deltas become mesh-local, fabric lacking at least half a millimetre of its room (`declipRoomMm`,
1 mm: inside the target's body or nearer it than that) moves out along the body's signed distance field
(`ReFitBodyField`, unsealed) by all it lacks, at most 3 cm. Welded groups and the centres of the fabric's triangles both
count: a centre lacking room moves its three corners, so a face the skin comes through between its vertices is lifted
whole and a sunk vertex takes its faces along. The field covers only the fabric lacking room: points lacking it by the
nearest skin, gathered into clusters (6 cm buckets that touch), each split along its longest side until its grid is at
most 2 million cells of 2.5 mm, with 2 cm around (one field over a whole catsuit grew its cells to 4 mm, whose trilinear
distances are millimetres off; splitting down to 300 000 cells cut fields through the ThiccWiker's thick, layered body
and read its inside wrong). A way out turned against the nearest skin's normal is not taken, and a push leaving a point
deeper by the nearest skin is dropped. The correction is then smoothed over the welded groups three times, each group
halfway to its neighbours' mean but never less than its own need along its way out, and fabric with its room never
moving; the field is read again after each pass, pushing on what still lacks room, and the pass stops when nothing does.
Pieces smaller than 3 cm move as one, by the push of their point lacking most and on until none lacks room. Last, the
quality guard keeps the fabric from coming out worse than the fit left it, as the case harness measures it: wherever a
push makes a vertex move 4.5 mm unlike its neighbours (the harness counts a spike from 5 mm; a spike the fit already made
may stay), turns a triangle over against the authored clothing, or takes an edge outside half to twice its authored
length, the pushes around it are halved, six times, then dropped until none is left: no push at all is the fit as it was.
Clothing made for the body it is fitted from keeps the gap it had over that body where it is less than the room (negative:
a strap pressing in), its triangle centres too (measured on that body, so a coarse face sagged into it as made keeps the
sag); clothing made for another body gets the room everywhere. Only the rest mesh takes the fix: the shapes, their
coverage, the saved binding and the refit's metadata keep the fit as it was, so a shape moves the clothing as without the
fix, from a rest that only moved out (draped from the fixed fit, the latex corset clipped more under its shapes, 219 ->
293). The push-out brush uses the same push and sharing (`ReFitDeclip.Need`, `Share`), with its falloff. The idea (push
outwards, Laplacian smoothing, optional body SDF) follows the MIT VRCloth-Declipper (dev.omelette_ak.vrcloth-declipper);
none of its code is used.

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
the clothing, and rejected when they would move weight onto another limb. With `inpaintSkinningChange` (on), the
change is known only where the clothing lies on the skin (within 1 cm) and known to be none where it stands far off it
(4 cm or more) or where the binding is not trusted; between them, and at points without a match, it is filled in as
smoothly as the clothing allows (the weight inpainting of Abdrashitov et al., SIGGRAPH Asia 2023: the cotangent
Laplacian L and lumped mass M, minimising xᵀ(L + L M⁻¹ L)x with the known values held). `WeightInpainting` splits the
unknowns into the parts of the clothing they connect, solves a part only for the bones known to change around it, fills
it harmonically first (well conditioned, solved to convergence) and refines that with 300 iterations of the smoother
system, whose residual cannot tell when it converged. Vertices without mapped weights take the
target's projected weights; extras are preserved according to the configured policy, with four normalized
influences per vertex. Only bones the clothing is weighted to receive body weights: an armature exported whole lists
the base's fluff, tail and ear bones, and their body weights would make the clothing swing with their physics; the
body's weight on them goes to their nearest weighted ancestor.

## After the Fit (VRChat avatar projects)

The ReFit window's result page checks each refitted piece in motion, offers a body shrink, a push-out brush and kept fits.

- **In motion** (`ReFitMotionCheck`). A hidden copy of the avatar (Orbiters Toolkit's `HiddenAvatarCopy`) is posed ten
  ways: arms up, forward, down, twisted left and right, a step on either leg, a crouch, elbows at the sides and forward,
  each bone aimed in the avatar's frame. Clothing follows as the build will make it (`AttachmentFollow`: VRCFury Armature
  Links, My Avatar attachments, the bone matcher for unlinked armatures). A body vertex is covered where fabric facing
  the same way lies over it within 5 cm, straight out of it, and it is under that fabric at rest; skin already through at
  rest (a part enclosed in a pouch, fluff) is not covered. In each pose a covered vertex is measured against the fabric
  of its patch only (the fabric within 4 cm of where it was, turned within 60 degrees), so it never jumps to another
  fold or layer. The check takes 0.2 to 0.4 s per piece and runs as the result shows, one piece per frame.
- **Body shrink.** On request the covered skin that comes through moves straight in (against its normal) by its depth
  and 3 mm, at most 3 cm and never closer than 1 mm to its bone. Six rounds: a move that takes a vertex through the
  fabric in another pose is tried at half its length in the next round. Kept, it is recorded with the refit
  (`OrbitersRefit.bodyShrinkVertices`, mesh-space offsets) and Orbiters Toolkit's build adds it to the body as a
  blendshape that every animation showing or hiding the clothing sets too (`RefitShrinkBuild`). A new fit drops it.
- **Push out** (`ReFitClipFixTool`, on `ReFitDeclip`'s push and sharing). A Scene view brush: fabric under it lying inside the body or closer than the room
  asked moves out along the body's distance field, most at the brush's centre, shared with its neighbours so no spike is
  made, into the piece's rest shape through each vertex's skinning, with Undo. The field (`ReFitBodyField`) is a signed
  distance grid over the piece (4 million cells at most) whose inside and outside come from a flood of the air from the
  grid's border, not from the nearest triangle's normal: skin sheets inside the body do not make the air around them
  inside. It is built on a worker thread (about 3 s for a sweater); the brush shows at once and pushes once it is there.
  Sealing (closing gaps narrower than a few cells, so air in a crease counts as inside, with its distance the way out of
  the crease) is available but off: tight trousers wrap each thigh, and a sealed gap between them would push the
  inner-leg fabric out of it.
- **Kept fits** (`OrbitersRefit.keptFits`). A fit kept by name stays when the piece is refitted again; using a kept fit
  puts the renderer back as that fit left it (mesh, bones, pose) and keeps the current one. Orbiters Toolkit's build
  turns every kept fit other than the worn one into a blendshape of (a copy of) the worn mesh and adds VRCFury toggles,
  one at a time, under the avatar menu's "Fits/piece" (`RefitKeptFitsBuild`, before VRCFury builds the menu). Fits kept
  for another custom base are dropped with it.

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
  for repeated guards and final measurement. A snapshot keeps its hierarchy (`MeshSnapshot.Index`, rebuilt only when its
  vertex array or hidden triangles change), so every stage reading a body reads the same one.
- Keep what is derived from a body across refits (`ReFitBodyCache`): each run stages and captures its bodies anew, and
  a captured body with the same positions, normals and triangles as one an earlier run read (the outfit's other pieces, a
  refit run again) takes that run's enclosed skin, hierarchies and main surface. Three bodies are kept, six while a batch
  runs (`ReFitService.BeginBatch`, `ExecuteBatchCoroutine`; Orbiters Toolkit's runner holds one around an outfit).
- Compute final penetration diagnostics after all corrections, not repeatedly when intermediate results
  are overwritten without influencing geometry.
- Sort BVH subranges in place and use a bounded query-local stack, avoiding per-query allocations and
  shared mutable query state.
- Share bindings, captured body data and staging across a multi-shape request. Keep verified arithmetic and
  projection order. A bounds-only BVH refit drifted the output while ties broke by the tree's layout (below); since
  ties break by triangle index, a refitted hierarchy answers exactly as a rebuilt one.
- Emit phase timings and compare geometry against an independently recorded baseline. Native/GPU backends
  are not included: redundant work was removable without platform binaries or numeric changes.
- Filter BVH queries with structs over cached face normals (`FacingFilter`, `MeshSnapshot.FaceNormals`): no delegate,
  closure or cross product per triangle tested. Build hierarchies with a binned surface area heuristic (16 bins, the
  median below 40 levels or where no bin boundary separates the triangles), triangles stored in leaf order with their
  corners side by side, children's distances kept on the traversal stack, rays descending the nearer child first.
  Query a hierarchy of only
  the triangles that may be found when few are left (`SurfaceBvh.Build(snapshot, include)`), not the whole surface with
  a filter rejecting the rest.
- Skip work whose result cannot change: the coverage regularization re-tests a limit only when one of its groups moved
  since it was last met (same order, same arithmetic, identical output); the drape tests whether a skin vertex is a fold
  wall once; inpainting skips parts with no known change.
- Record where the time goes inside the slow phases (`registration-timing`, `deformation-timing`, the cloth drape's
  passes, the coverage's build, passes and regularization) and measure before optimizing.
- Distance ties (a point equally close to two triangles, a ray through a shared edge) break by the lower triangle index,
  and bounds are widened by a rounding slack so no node holding a possible answer is pruned: a query's answer depends
  on the triangles only, never on how the hierarchy was built, refitted, traversed or seeded (`SurfaceBvh`'s remarks).
  Until revision 17 they broke by the layout, and each new layout moved metrics signed by the nearest face by a few
  samples. Summation orders are fixed too: neighbour lists are sorted by index, the registration's solver sums over at
  most 64 ranges whatever the core count, and the cloth fit's motion samples keep the heaviest, equal weights by index.

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
