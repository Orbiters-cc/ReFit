# ReFit

## Unreleased

- **Refits run about three times faster in the editor, with the very same result.** The spatial index under every step
  of a refit (`SurfaceBvh`: building and refitting it, its closest-point and ray queries unfiltered, by facing, all but
  one triangle or not touching an edge) runs Burst-compiled (`SurfaceBvhBurst`), on the callers' threads as before. Its
  queries took three quarters of a refit's CPU time and its builds, one thread each, a quarter of its wall time. Written
  statement for statement as the editor's Mono rounds the managed code (float expressions in double, rounded when
  stored), it builds the same tree and returns the same hits bit for bit, so nothing a refit makes changes: over the
  regression cases the refitted meshes (vertices, normals, tangents, every blendshape frame, weights, bindposes), the
  reports, every metric and the offscreen renders are identical with it on and off. Engine time over 8 regression cases
  (3 interleaved runs, mean ± SD): Release code optimization 86.1 ± 0.4 s → 28.1 ± 0.4 s (3.1×), Debug 156 ± 15 s →
  51.3 ± 0.4 s (3.0×); a refit 2.1× (the onesie) to 4.7× (the Hoodie) faster, building the spatial indices 13×.
- **Requires Unity's Burst package** (`com.unity.burst` 1.8.7, which Orbiters Toolkit and the VRChat SDK already depend
  on: a VRChat project gains no package). Used in the editor only: player and avatar builds compile none of it. With
  Burst compilation turned off (Jobs › Burst › Enable Compilation), ReFit runs its managed code as before. The first
  refit of an editor session waits about a second for Burst (three after ReFit's code changed).
- A plain single precision port had moved refitted vertices by up to 60 mm: which of two practically equidistant
  triangles a query meets decides a refit's next steps (DESIGN.md, Performance Policy). `ReFitBurstQueryTests` holds the
  Burst path to the managed code's tree node for node and to every hit's bits.

## 0.7.7 — 2026-11-03

- **Fix clipping no longer spikes, and moves only the rest mesh** (still off by default). The pass now also lifts faces
  the skin comes through between their vertices (triangle centres move their corners), builds its distance field only
  around the fabric that lacks room (2.5 mm cells over a whole garment, where one field had grown them to 4 mm), checks its
  way out against the nearest skin, starts moving fabric only when it lacks half a millimetre of room, and ends with a
  quality guard that halves, then drops, any push leaving a spike, a turned-over triangle or an over-stretched edge the fit
  did not have. The fixed deltas are the rest mesh's only: shapes move the clothing as without the fix. Over the regression
  suite's 38 cases against it off: clothing over 3 mm in the body at rest 8866 → 4657, under shapes 7378 → 3806, spikes
  7964 → 7994 (0.7.6: 8351), reversed triangles 6013 → 5961 (0.7.6: 6695), stretched 25029 → 24966; 2 cells worse beyond
  the suite's noise instead of 24 (FashionSuit jacket under the Ultipaw without its muscles 63 → 76, latex arms' skin
  coming through in motion 27 → 33), so the default stays off. Engine time 537 → 715 s over the 38 cases (about 5 s a
  piece). VALIDATION.md has every number.

## 0.7.6 — 2026-11-02

- **Fix clipping measured: it stays off by default.** Over the regression suite's 38 cases it halves the clothing left
  in the body (3 mm deep at rest 8866 → 4695, with shapes 7378 → 4168; the Classy chain 4313 → 1241, the latex corset 469
  → 262) without stretching more, but it adds spikes (7964 → 8351; latex legs 0 → 19, Comfy sweater 238 → 282, the
  Ultinova FishingOutfit jacket 288 → 337) and, on the latex corset, clipping under its shapes (219 → 293): lone vertices
  of low-poly clothing sunk 10 to 16 mm stand out of their neighbours once pushed out. Sharing the push over the fabric
  around it, a cone, smaller pushes and exact distances near the skin were tried and did not remove them. Numbers in
  VALIDATION.md; turn it on per refit (the wizard's Settings, My Avatar's fit card) where clipping matters more.

## 0.7.5 — 2026-11-02

- **Fix clipping after the fit** (`ReFitSettings.declipAfterFit`, room `declipRoomMm`, 1 mm; off by default until the
  regression suite has compared it on and off, see VALIDATION.md). Fabric a mesh refit leaves inside the target's body,
  or closer to it than the room, is pushed out along the body's distance field (`ReFitBodyField`, inside and outside from
  the air around the body), then the correction is smoothed over the fabric three times: a point never moves less than it
  needs, fabric with its room never moves, and the field is read again after each pass (stopping once nothing lacks
  room). Small pieces (a chain's links, a button) move as one; a point whose push would stretch an edge past twice its
  authored length moves with its neighbours instead (counted in the report's `declip` line). Clothing made for the body it
  is fitted from keeps what lay under that body as made (a strap pressing in): only what the fit added is fixed; clothing
  made for another body gets the room everywhere. Both fits (the cloth fit and the different-base passes) run it after
  the garments worn under it are covered, before the result becomes mesh deltas, so the body shapes drape from the fixed
  fit; the shapes themselves are not fixed again yet. The wizard's Settings page has a "Fix clipping" switch and its room
  in the clearance section; Orbiters Toolkit's `RefitJob.FixClipping` asks for it (My Avatar's fit card, MCB).
- **One push for the brush and the fit.** `ReFitDeclip` holds the push-out brush's push (`Need`) and its sharing with the
  neighbours (`Share`, now: never less than its own push along it); the brush calls it with its falloff.
- The case harness prints the `declip` report line and its `step declip` (`ReFitEngine.CoverStepCaptured`); the
  regression suite takes `declipAfterFit=…;declipRoomMm=…` like any setting.
- Cache revision 15: fits and transfers cached before are made again.

## 0.7.4 — 2026-10-29

- **Clearance corrections reach every ring of a garment.** A correction spread from its samples one ring of neighbours
  at a time and stopped after 48 rings: on a densely tessellated garment the rest took the island's average, so the
  same garment fitted differently at a higher resolution. The fill now runs until nothing connected is left, and visits
  only the rings it reaches (the same field as before wherever 48 rings sufficed).
- **Shown shapes turn the captured normals.** The surfaces ReFit measures took the shapes a body or garment shows into
  their positions but not their normals, so facing and clearance tests used the unshaped normals on the shaped surface.
  Their normal deltas are now added as Unity skins them (the authored normals stay the rest of transferred shapes).
- **Collapsed triangles give finite closest points.** A triangle with two corners on one point (or all three on a line)
  divided zero by zero and answered NaN; it is now measured as its segments.
- **Staging copies stay inert.** The hidden copies of the avatars and the clothing are made under an inactive holder and
  their behaviours disabled before they become active, so no editor-executing script's OnEnable, Start or Update runs on
  them (Awake still runs once: the copy must be active for its Animator).
- **Clothing with more than four bone influences per vertex** is told so when the refitted mesh keeps the four largest
  of each (ReFit skins with four, as Unity and VRChat render by default).
- Shape frames un-skin through inverse skin matrices computed once per refit instead of once per vertex and frame.

## 0.7.3 — 2026-10-23

- **Made for: I don't know.** ReFit then takes the clothing as My Avatar takes clothing made for another body. Placed by
  My Avatar, it is first fitted to the avatar's armature as on a drop (resized and aligned, onto the original base's
  armature on a custom base); then it is refitted from the original base when the custom base gives one, else from where
  it is on the avatar, and pushed out wherever it covers the skin. Made for has a line of its own, so its four choices
  read in full.
- **A refit whose application fails is undone whole.** After a failed refit the clothing kept bones that pointed into the
  rebuilt armature Undo had just deleted (Undo gave back its mesh, not its bones), and it showed at the avatar's feet. The
  renderer is now recorded whole before anything changes; Reset finds the original bones before it records the renderer.
- **Clothing bones find their counterparts in the avatar's skeleton only** (the bones its meshes use, their parents and
  what hangs under them), never in another object of the avatar. The FishingOutfit's tail bones "Tail1" and "Tail2" went
  onto the Masculine Canine's tail mesh objects of the same names, beside its armature: the rebuilt armature got three
  roots and the refit failed.
- **Follow body blendshapes** on clothing made over a shape the body shows (a custom base's muscles at 100, a costume
  made for its bigger chest) no longer adds the shape a second time. Where the shape moves the skin under the clothing,
  ReFit looks at which body the clothing does not go into, or else lies nearer. Made over the shape, the clothing's new
  shape takes it away (draped over the body without it) and the clothing's own mesh gives it back: at the body's weight
  the clothing is exactly as authored, and turning the shape down brings it down with the body. Made before the body was
  shaped (it clips into the shaped body, or stands off a body the shape shrinks), the shape adds it from zero as before.
  The report says which, shape by shape. Tools can say it themselves with `ReFitRequest.madeWithShapes` (a costume made
  for a bigger chest worn on a flat body: 100). Shapes an earlier refit of the clothing already follows are left as that
  refit made them. The FishingOutfit shirt fitted over the Ultipaw's muscles, then given the muscles alone, is now the
  authored shirt at 100 (it was 22 mm off on average, the chest bulging out twice) and within 4 mm of a fit made without
  them at 0.
- The refit and the shapes keep the clothing's own normals and tangents: each turns as the surface around it turns,
  instead of becoming the smooth normal of the moved surface at full weight. Hard edges, custom normals and the tangents
  normal maps use keep their look. Where a shape does not turn the surface its normals do not change, so shapes given
  to clothing alone no longer store a normal change for every vertex. Earlier fits are refitted once (cache revision 14).

## 0.7.2 — 2026-10-21

- Clothing made for another body and resized onto this one (the FashionSuit on the Ultipaw) fits close again instead
  of bulky. After the cloth fit, which lays it on the body as it was made, the coverage now pushes out only what clips,
  to the clearance the tightness asks for (3 mm by default), and spreads that push over a couple of rings of fabric;
  it used to lift every part within a centimetre of the skin to a centimetre and smooth that over the whole garment,
  and the garment worn over it followed. On the FashionSuit the shirt's sleeves sat 17 mm over the arms where the fit
  left them at 4 mm, and the jacket over it gained 18 mm on the arms and 20 on the torso; now the jacket keeps its
  authored room over the chest and shoulders (median within 2-4 mm of it) and gains 7 mm on the arms and 9 on the
  torso, with less clipping than before (deepest 5 mm instead of 44). The legs keep up to a centimetre (by how much of
  the skin's skinning is theirs, so the hips blend into it): they bend far more crouching and sitting, and trousers and
  shorts fitted to 3 mm came through the knees and thighs more often when crouching. That room is never more than the
  garment has over the legs: stockings, leggings and leg warmers made a few millimetres off the skin stay that close
  instead of ballooning to a centimetre. The feet get none, so shoes and socks no longer stand a centimetre off the foot
  (the sole below the floor). Clothing fitted from a rough start (the different-base passes) keeps the centimetre
  everywhere. Earlier fits are refitted once (cache revision 13).
- Clothing made for another body is covered as such also when the cloth fit falls back, and in shapes added to the fit
  later; the registration check counts every clothing point near the skin, a registration that reaches nothing is not
  used, and the cloth fit's counterpart is the nearer of the skin beneath and above (a thigh smaller than the source's
  no longer finds the other thigh).
- **Share ReFit diagnostics** (wizard settings › Diagnostics, off by default) sends ReFit's own warnings and errors, with
  their stack traces, to Orbiters through Orbiters Toolkit's shared reporter. The card says it covers every Unity
  project on the computer and that reports are linked to your Orbiters account while you are signed in. Tokens, keys,
  cookies, e-mails, links, VRChat ids, other accounts' and avatars' names, your user and computer names and folder
  paths are removed first; My Avatar's, VRCFury's and Unity's logs and tests' deliberate failures are never sent as
  ReFit's.
- **Export FBX** on the result page, and **Export** on each re-fitted asset, write the re-fitted mesh as it looks
  fitted (the fit applied to the mesh, the body's blendshapes kept at their weights) with its armature to an FBX file
  that Unity and Blender import as it is; an outfit goes into one file. A file saved inside Assets is imported and
  pinged. Once the fit was undone or reset, they say so instead of writing the original, unfitted mesh. Requires
  Orbiters Toolkit 0.4.1.
- Fits kept to choose from work: the avatar's Fits menu showed the current fit whatever was picked, because each kept
  fit became an empty blendshape (it was taken from the mesh's vertices, while ReFit keeps the fit in its "refit"
  blendshape). A kept fit fitted with another armature is left out of the menu, with a warning, instead of uploading
  wrong. Using a kept fit again drops the body shrink planned under the fit worn before (Orbiters Toolkit 0.4.1).
- The push-out brush never changes another avatar: a mesh other pieces may wear (a refit reused on a second avatar
  with the same base) is copied next to it at the first push, and the piece wears its copy. Each stroke is one undo step
  of the moved positions only (not the whole mesh with its blendshapes), the stroked fabric's normals follow it, and the
  brush keeps working next to other Scene view tools. Undoing something else no longer puts the last stroke back over
  what other tools did to the mesh since (a sculpt in Mixer, a reimport). A progress bar shows while the copy is saved,
  and hidden copies of the avatar (posing preview, estimates, builds) no longer make the brush copy the piece's copy
  again in every session.
- A refit is reused only while the outfit's toggles set the same body shapes (VRCFury toggles, Modular Avatar Shape
  Changers): editing them refits again instead of bringing back the old mesh. Those toggles are read under the avatar's
  own root, also when the avatar sits under another object of the scene.
- Re-fitting an outfit whose other parts share its armature: a PhysBone on a chain those parts are skinned to stays
  theirs and the rebuilt armature gets a copy, so the other parts keep moving (the component you copied in the
  Inspector stays there to paste). Objects of a prefab hidden by an earlier
  run are no longer counted as removed again (or reported as duplicate armature branches), the bone reparenting of the
  repair can be undone, and clothing left outside the avatar because moving it would unpack its prefab is a warning:
  it is not uploaded with the avatar. The old armature of a prefab that a run hid comes back with any Orbiters tool's
  reset (My Avatar, MCB), not only ReFit's own, so its PhysBones and contacts work again (Orbiters Toolkit 0.4.1).
- In motion: a check left half done when the page changed carries on when the result shows again (it stayed at
  "Checking…"), the hidden copy is made at rest while a posing preview plays, two objects of one name are told apart,
  and a deleted piece no longer breaks the Touch up card.

## 0.7.1 — 2026-10-19

- Requires Orbiters Toolkit 0.4.0: the Scene toolbar entries (projection rays, welded groups) register with the Toolkit's avatar gizmos directly; the separate integration assembly is gone.

## 0.7.0 — 2026-10-18

- Twice as fast. The 23 replayed outfits take 159 s instead of 303 s: bodies laid onto each other are kept between
  refits, the cloth drape looks for the skin it covers among those triangles only, the coverage pass re-checks only what
  moved (same result), and the spatial queries no longer allocate or recompute normals. A latex piece on the Hypernova
  refits in 2 to 3 s (5 to 7 s before), the Classy chain in 11 s (27 s), the suit vest in 14 s (22 s).
- A body laid onto the other is used whenever it reaches it under the clothing, even where the skin met along the
  clothing's normal says otherwise (feathers, fluff, a body sculpted in layers). The Rexouium jacket on the Ultirex no
  longer tears at the collar and shoulders: 12 spikes instead of 2337, in 20 s instead of 58.
- Smoother skin weights between bodies: the change of skinning is filled in over the clothing between the skin and the
  parts far off it, and where nothing matched, as smoothly as the clothing allows (weight inpainting). Crouching in the
  jockstrap clips less (188 samples to 158).
- Small rigid pieces (a chain's links, buttons, buckles) turn with the skin, up to 30 degrees, instead of only sliding.
- In motion (beta): after a refit, a hidden copy of the avatar raises its arms, twists, steps and crouches, and the
  window says where skin comes through the clothing. On request it plans a body shrink: the skin under the clothing moves
  in a little, as a body blendshape that Orbiters Toolkit turns on whenever the clothing shows (its own toggle included).
- Touch up: a push-out brush in the Scene view moves fabric out of the body where you drag, along the body's distance
  field, with Undo. [ and ] change its size.
- Fits: keep a fit, refit the piece again (looser, tighter...) and keep that one too. In VRChat the avatar's menu picks
  one (Fits/piece, built with VRCFury); in the editor, Use puts a kept fit back.
- Clothing made for another body and resized onto this one (an outfit for the Rexouium put on an Ultipaw by My Avatar) is
  covered again. Since 0.6.0 the cloth fit kept every clipping it started with, as if made that way: much of such
  clothing lies under the skin of the body it is fitted from, so after the cloth fit the different-base passes now push
  it out of the target and over the clothing worn under it, and a sleeve or a leg covers the arm or thigh that came
  through it (away from its hem). The FishingOutfit on the Ultipaw, muscles on: 760 shirt, 1159 shorts and 1216 vest
  vertices under the body before, 21, 32 and 3 now (with My Avatar 0.9.10's line-up on the original base).
- The target is fitted as it shows, the shapes the clothing follows included. An MCB custom base shows its muscles at
  100: the clothing was fitted at zero and the muscles added by its muscle shape, a drape that often stopped before the
  fabric was over them. Now the fabric is draped over the muscles as part of the fit, the muscle shape is the drape taking
  them away, and the refit gives back what that shape shows: at the avatar's own weights the clothing is the fit itself.
  The Classy pants on the Ultinova keep their waistband and belt loops (137 samples in the body at rest, 32 now), the
  FishingOutfit vest on the Ultinova has 547 vertices under the body instead of 677. With the muscles turned off the
  clothing stands a little looser than a fit made there: a drape taking a shape away only pushes fabric out. Bodies the
  source cannot be laid onto (a sculpt, the ThiccWiker) are still fitted at zero by the passes made for them. Shapes the
  clothing does not follow keep their weight too (the Ultinova's collar fluff, hidden by default, stays hidden: no fabric
  standing over it).
- Clothing lined up with the original base's skeleton as it stands (My Avatar puts an outfit on the original's armature
  over the avatar) keeps the original as it stands. It was scaled to the avatar's size, measured by its bounds, which a
  custom base's ears and feathers enlarge, and the fit carried the difference onto the clothing: the FishingOutfit rose 7
  cm on the Ultirex, its collar onto the muzzle, its sleeves torn by the coverage over the arms. It now sits where it was
  put: the shirt has 62 vertices under the body instead of 125, 17 samples in it at rest instead of 220, and the vest
  over it 3 under the shirt instead of 327.
- The coverage of clothing made for another body never pushes one leg's (or arm's) fabric out over the other limb: on the
  muscled Ultinova, whose thighs meet, it pushed the inner panel of one leg of the FishingOutfit shorts out around the
  other thigh. A shoulder counts as the torso there, not as an arm: fabric over it is a strap or a sleeve's cap.
- Fabric sunk into the body is draped out the short way: skin on the far side of a shoulder or a thigh no longer pushes
  it out through the body. Fitted over the muscles, the FishingOutfit vest's straps sink into the Ultinova's shoulders;
  the skin under the arms pushed them down over the upper arms, stretched across them when the arms rose. They stay on
  the shoulders (in motion 156 skin points come through the vest, 151 before). The ThiccWiker onesie clips less: 946
  samples in the body at rest instead of 1261, 473 vertices under it instead of 617.
- Clothing My Avatar resized from another body is covered as such, however little of it lay in the body it is fitted
  from (`ReFitSettings.madeForAnotherBody`, given by Orbiters Toolkit). The FashionSuit's trousers, made for the Vulper,
  lay in the Masculine Canine only at the calves, as little as clothing made for that body with a strap pressing in, and
  the fit kept the calves through them on the Ultipaw. With the Toolkit asking coverage for the suit's pieces too, the
  jacket on the Ultipaw has 33 vertices under the body instead of 431 (the arms no longer through the sleeves), the
  trousers 22 instead of 136.
- The separate pieces of clothing made for another body (a belt with its loops and buckle, pockets) follow the fabric
  the coverage moves: the FashionSuit's belt sank under the waistband pushed out over the Ultipaw's hips, and the
  FishingOutfit vest's strap and buckle partly into the vest. Closed rings follow the coverage repair part by part, as
  the drape already made them.
- The thighs are spread for the coverage only when they meet. Clothing skinned to the hips down the thighs (shorts often
  are) stayed where the spread thighs were, and they came back through it.
- Head skin near a collar (a muzzle the source lacks) no longer counts as body the source cannot reach: the FishingOutfit
  on the Ultirex is fitted like cloth again.
- Needs Orbiters Toolkit 0.3.24.

## 0.6.0 — 2026-10-16

- Outfits stay prefabs. Refitting a part of a prefab outfit (VRCFury, Modular Avatar) no longer unpacks it, from the
  ReFit window, MCB or My Avatar: the rebuilt armature is added to the instance, objects the prefab owns that the refit
  no longer uses are hidden instead of deleted (undo shows them again), and the clothing stays where it is in the
  hierarchy when moving it would break the prefab.
- The outfit's other parts are left alone. Their bones are no longer re-pointed at the refitted part's armature (they
  bent wherever the two skeletons differ), and the outfit's VRCFury Armature Link is no longer moved onto one refitted
  part: the rebuilt armature gets its own copy of the link while the other parts keep theirs. This is what made
  Novabeast outfits "freak out" when refitted.
- Clothing made for the same skeleton (Novabeast clothing on the Ultinova or the Hypernova) keeps its armature, its
  weights and its links: only its mesh is refitted.
- Skin weights go only to the bones the clothing was weighted to: the base's fluff, tail and ear bones listed by an
  armature exported whole no longer make the clothing swing with their physics.
- Shapes the outfit sets while it is worn count: ReFit reads the clothing's own VRCFury toggles (blendshape actions on
  the body or on every renderer) and Modular Avatar Shape Changers and fits with them on, on both bodies. A turtleneck
  that hides the chest fluff and shrinks the chest is fitted over the shrunk chest, not over the fluff.
- A new fit between two bodies. The source body is laid onto the target as one surface (starting from where the
  skeletons put it), the clothing moves with the skin under it, then it is draped over the target like cloth: where it
  covered the source and the target's skin comes through, the fabric is pushed out and the push spreads over the fabric
  around it. Body shapes are draped the same way from the fitted clothing. On the reported outfits: the Novabeast latex
  catsuit on the Hypernova goes from 422 spikes over 5 mm (66 mm at worst) to 45 (15 mm), with a clean neckline and
  shoulders that hold with all five Hypernova shapes on; the Classy and Comfy clothing on the Ultinova no longer tents
  or tears (turtleneck 810 spikes to 354, sweater 720 to 193). Refits with shapes take 5 to 15 s on these outfits
  (the catsuit 17 s before, the sweater 25 s, the turtleneck 3 s). Bodies sculpted far beyond the base (the authored
  FBX fixture) are fitted by the earlier passes.
- Seams split a hair apart (under 0.1 mm) no longer open into cracks, and a chain's links stay whole (the Classy
  chain was torn).

## 0.5.10 — 2026-10-13

- While a refit runs, the window's choices are locked and the run keeps the ones it started with. It holds the avatar,
  so MCB can't switch its version mid-refit, and the refit is recorded with its custom base.
- A piece that was refitted already is refitted from its original mesh, and its record keeps the original and the
  shapes it had; a refit that fails or is interrupted gives the earlier fit back; **Whole outfit** shows the right
  "before".
- The drape: belts and collars follow the fabric under each part of them, it runs before layers and covers are
  repaired and never pushes fabric into skin, shows its progress and stops when cancelled. Skin is hidden under another
  part only where that part closes around it (fur cards no longer hide the skin under them), and body shapes that
  shrink the body keep their full depth.
- Skin weights are transferred with much less memory, with the same result.
- Refits saved by earlier versions are worked out again (revision 6).
- The window: picking another avatar works out **Made for** and the shapes again, the Hierarchy selection only fills an
  empty choice, the narrow layout keeps its 3D stage, avatar chips shrink to fit, **Placed by hand** works without
  shapes, drops only take avatars and clothing, and the stage draws every material and frames before and after.
- Clothing names match whole words ("Speedometer" is no swimwear) and compound ones (Boxer Shorts, bikinitop).
- Needs Orbiters Toolkit 0.3.22.

## 0.5.9 — 2026-10-11

- Clothing fitted onto a different base ("Placed by hand") now covers a much larger body. Skin enclosed in another
  part of the target body (the flat chest left inside added breasts) is no longer matched or covered, and a last step
  drapes the clothing over target skin that still shows through where the base's skin was covered, spreading the push
  like cloth: on the reported ThiccWiker the onesie now covers the breasts and the buttocks, including the gluteal crease.
  Before it, fabric that the fit folded through itself (at the crease, behind the knees) is relaxed, and fabric pulled
  into a fold of the body spans it. Fluff, a mane larger than the base's and the base's own tail never drive it. The same
  step runs on every transferred body shape.
- Remaining on that onesie: the collar and the larger mane pass through each other at the back, the tail opening's coarse
  panel lies flat over the top of the crease, and the crouched test pose shows skin behind the knees.

## 0.5.8 — 2026-10-10

- The ReFit window is one page in the look of My Avatar and the Logger. It opens on the selection (or the scene's only
  avatar), shows the avatar wearing the chosen piece in a 3D stage on the left (drag to turn, scroll to zoom, right-drag
  to move; front, three-quarter, side and back presets) and the avatar's clothing as picture cards on the right, and sets
  every choice by itself: what it was made for, the body shapes, the fit. One button runs the refit; the stage then
  compares before and after with one camera, split by a handle. Choices answer on press, the setup checks run a moment
  after the last change, and every advanced option is on the Settings page. A narrow window puts the stage above the
  page. The window keeps the size it is given: pages scroll inside it. The stage uses the preview camera Orbiters Toolkit
  shares with MCB's version comparison (requires Orbiters Toolkit 0.3.20).
- Made for the original base: on an avatar whose custom base an Orbiters tool (MCB) knows, clothing for the original base
  is fitted from it (picked by default unless the clothing already has the custom base's shapes). The original base is
  opened only for the refit.
- Body shapes the avatar keeps switched on under the clothing (such as a custom base's fix shape) are picked by default,
  with a line saying which: the clothing follows them.
- Underwear, swimwear and form-fitting one-pieces (jockstrap, briefs, bikini, swimsuit, bodysuit…) count as clothing and
  fit snug by default, like the clothing the gravity detection recognises. `ReFitClothingDetection` says which. Loose
  one-pieces (onesies, jumpsuits) are not taken by name: fitted snug, the reported onesie took eight times longer for about
  a tenth less clipping.
- The snug fit no longer pulls spikes out of coarse clothing. Its surface guard brought every sample of the clothing's
  edges out of the body, also where long edges already sagged into a curved body as authored (a waistband across the
  hips); it could move only the few corners where the body grew, so it drove those out again and again (the reported
  jockstrap's waistband ends stood 15 mm out of the band). It now restores the depth the clothing had on the body it was
  made for, and lifts a sample only when the corners it may move hold at least half of it.
- Clothing facing the skin it covers (the inside of a strap, waistband or lining) keeps that skin. It was matched to a
  distant surface turned its way, so it lost the body's shapes (the inside of straps sank into a grown buttock) and folded
  thin parts.
- Body regions are transition bands: a body triangle belongs to every region holding a quarter of its skin weight, so hip
  clothing fits the side of the hips and the groin where the thighs carry most of the weight. Across two bodies a limb's
  clothing may also land on skin the other body gives to the torso (a buttock weighted to the thighs on one base and to the
  hips on the other), never on another limb.
- Where the bodies differ by more than a centimeter, a point of the original body maps to the new body along its surface
  normal (outward on a larger body) instead of to the nearest point, unless that is much farther: clothing on a grown
  buttock or thigh stays on it instead of being pulled into the nearest crease.
- Skin weights, when the armature is replaced: the clothing keeps its own weights and follows the change of skinning
  between the two bodies where it lies on the skin (where the new body hands the groin over to the thighs, a jockstrap's
  pouch edges do too), fading out for parts standing 1 to 4 cm off it, which move as their creator made them. Where both
  bodies agree nothing changes. This replaces the blend with projected body weights, accepted or rejected per vertex, that
  left blotches of thigh weight tearing pouches apart when the legs moved. A change that would move clothing onto another
  limb is still rejected.
- Matching with the thighs spread was evaluated on the jockstrap and onesie reports and left out: it did not reduce clipping,
  and clothing weighted differently from the skin under it (a hip-weighted pouch over thigh-weighted groin skin) was
  matched to skin it does not cover at rest.
- Saved transfer data of earlier versions is rebuilt (binding and transfer provenance changed).

## 0.5.7 — 2026-10-09

- The server address comes only from Orbiters Toolkit (`OrbitersEnvironment`): ReFit's own copy of the production and
  development addresses is gone (only a test used it, and its `localhost` differed from the tools' `127.0.0.1`).

## 0.5.6 — 2026-10-08

- Resetting a refitted asset takes the fit back through Orbiters Toolkit's `RefitRecords.Discard`, so MCB also forgets
  the fit it saved for the version (requires Orbiters Toolkit 0.3.18).

## 0.5.5 — 2026-10-07

- `surface-coverage-limited` warns only when clothing stays inside the body or an inner garment, or within 1 mm of it, and says how far (`clothing remains 4.20mm under the body surface`). Clothing outside it with less than the 1 cm of room the correction aims for no longer warns, nor counts as rough in Toolkit and My Avatar. The correction is unchanged.
- Coverage messages name the surface they cover: the body or `inner garment '<name>'`.
- A cancelled refit reports `refit-cancelled` once.
- A failed scene application (such as `bone-apply-incomplete`) no longer adds a second `refit-exception: Unexpected error`.
- The reset test registers the object it adds with Undo, so reverting it no longer warns.

## 0.5.4 — 2026-10-05

- Use the Orbiters Toolkit blendshape picker (requires Orbiters Toolkit 0.3.11).
- Fix refit blendshapes that moved no vertex when the clothing sits under an avatar placed away from the scene origin: the fitting copy kept the clothing's local position as a world position, so it fitted nothing. The copy now stays where the clothing is, and avatar bones are no longer matched against the clothing copy's own bones.
- Tests cover nested clothing on avatars moved, turned and scaled in the scene: the fitting copy keeps its world pose and a transfer gives the same result wherever the avatar stands.
- The wizard asks, after Tightness, whether the asset was made for this exact avatar. **No** (clothing placed roughly over the body, already clipping) refits its mesh onto the same body with the blendshapes and moves visible clipping out of the body, as it is and in every shape. The summary can refit the asset's whole outfit with it, innermost part first, so the layers stay in order.
- `ReFitSettings.coverageKeepsLayerOrder` (used by that path only): each part keeps its authored side of the outfit's other parts (`coverageLayers`), clipping another surface covers is left as it is, fabric stays beneath what lies over it in every shape, and small separate pieces (pocket square, pins) follow the fabric they sit on.

## 0.5.3 — 2026-10-03

- Improve residual body coverage and smoothing for supported cross-base garments, including separated-leg fitting for lower-body clothing.
- Parallelize independent fitting work and cache reusable preparation without changing the source avatar.
- Preserve the complete fitted pose in creator commission captures.

Re-fit clothing and accessories made for a 3D model **A** so they fit a 3D model **B** — including bases that are
derivatives of A with body modifications, and body blendshapes of B. The deformation is delivered as a
non-destructive **"refit" blendshape** (set to 100 by default) on a duplicated mesh asset; the original asset files
are never modified.

## Using the wizard

`Tools > Orbiters > ReFit`

The window opens on what you selected: an avatar, a piece of clothing it wears, or a prefab from your project files (with
nothing selected, the scene's only avatar). The avatar's clothing shows as picture cards; click one, drop a prefab or an
avatar on the stage, or (while no piece is chosen yet) select it in the Hierarchy, and the stage on the left shows the
avatar wearing it in 3D: drag to turn, scroll to zoom, right-drag (or Shift-drag) to move, double-click to frame the piece
again, or pick Front, ¾, Side or Back. In a narrow window the stage tops the page, and the wheel scrolls the page unless
the stage was clicked or Ctrl is held. Under the cards, one row per choice, already set (picking another avatar sets them
again for it):

- **Made for**: this avatar (ReFit makes it follow body shapes), the original base of the avatar's custom base when MCB
  knows it, or another avatar (picked by itself when you fit a piece from the avatar wearing it to another one).
- **Body shapes**: the shapes the clothing should follow. The ones the avatar keeps switched on under the clothing are
  picked for you, a custom base's own shapes are suggested, and **More** opens the search picker with your recent shapes.
- **Fit**: Loose, Balanced or Snug. Clothing (underwear and swimwear included) starts Snug, accessories Loose.
- **Placed by hand**: for clothing made for another avatar and placed over this one by hand. ReFit then also removes the
  clipping it already has (with or without body shapes) and, with **Whole outfit**, refits the rest of its outfit with it,
  innermost part first, so jacket, shirt and trousers stay layered. It shows when ReFit has a body to fit the piece from:
  the avatar it is worn on, or the avatar or base it was made for (not for a project file that already fits).

The **ReFit** button shows its progress; the page is locked meanwhile, and the avatar is held as the Orbiters tools hold
their own refits (MCB waits to switch its custom base version, posing previews rest). A fit of a refitted piece starts
again from its original (every tool's reset still gives the real original back), shapes added to it keep its fit, and a
refit that fails gives the piece its earlier fit back. The setup checks (armature matching, proportion comparison) appear
under it.
Warnings never block: if your custom base has intentionally relocated bones but a near-identical mesh, just proceed — the
surface projection compensates. Afterwards the stage compares before and after with the same camera: drag the handle
to move the split, anywhere else to turn. The result selects the
refitted renderer, shows its mesh, undoes the refit, or goes back for the next piece. In a VRChat avatar project it
also checks each piece **in motion** (arms up, twists, steps, a crouch) and can plan a body shrink for what comes
through, offers a **push out** brush for the Scene view, and keeps **fits** to choose from in the avatar's menu. Every
advanced option is on the **Settings** page (the sliders in the top banner, or **Advanced options**).

Right-click a skinned accessory in the Hierarchy and choose **ReFit** to start with that accessory and its avatar. The
renderer's Inspector context menu also exposes **ReFit**. With several meshes, the window shows them to pick instead of
guessing. This shortcut only configures the window; it does not change the scene.

The body shapes picker searches with wrapping name suggestions and, with no query, shows the three most recently
refitted blendshapes that exist on the body. Successful transfers from both the window and the other Orbiters tools (MCB,
My Avatar) update this local history; failed operations do not.

The main page lists active ReFit commissions below refitted assets, with creator, status and the website's
progress bar. Sign in to your Orbiters account (in My Avatar or MCB) to load your requests. Clicking a row opens its website page. The first page
refreshes every 30 seconds while this screen is visible; use **Load more** for older active requests and the
refresh icon to return to the latest first page. Switching account or API environment clears the cached list.

After processing, ReFit lists Orbiters creators who accept manual ReFit commissions, with their price range beside
their name. Click an artist card to open a short-lived commission draft on Orbiters with that artist selected.
When you are signed in to your
Orbiters account, the one-time handoff opens it; otherwise sign in on the website.
Clicking a creator captures and uploads four private views (front, three-quarter, side and elevated) of the avatar
wearing the refitted accessory. The primary refit shape is enabled on the captured copy; other shapes and the
scene pose stay as currently displayed. Capture uses an offscreen preview and does not alter the live avatar.
The handoff includes creator IDs and the asset/source/target/blendshape names, but never uploads the Unity asset,
mesh, scene file, local paths, or authentication token as commission content. On the website, review or remove
the previews and add files/images (up to eight attachments total, 10 MB each). Active requested creators can
inspect attachments before accepting and the accepting creator retains access afterward.

Run `Orbiters.ReFit.Editor.Tests.ReFitCommissionCaptureTests.RunOrThrow()` through Unity MCP or use
`Tools > Orbiters > ReFit > Run Commission Capture Tests` for the isolated capture regression checks.
These checks require an editor with graphics support, not a `-nographics` runner.

The **Settings** page includes the **Dev Environment** switch shared by the Orbiters tools (Orbiters settings). ReFit
asks Orbiters Toolkit for the server (`OrbitersEnvironment.ApiUrl("refit")`): `https://api.orbiters.cc/refit`, or the
local API at `http://127.0.0.1:4100/refit` in development.

## What it produces

- A new mesh saved under `Assets/ReFit/<asset name>/` with the `refit` blendshape (and a second blendshape when
  transferring a body shape, e.g. `refit_Belly_Big`).
- The scene renderer is switched to the new mesh; when fitting to a different avatar, the asset's armature is
  replaced with the target's bones (kept when it already is the target's skeleton), the asset's skin weights follow the
  difference between the two bodies' skinning where it lies on the skin, and bones with no target equivalent
  (skirt/physics bones, props) are preserved and re-parented. A prefab instance stays packed, and the outfit's other
  parts keep their own bones and VRCFury Armature Links.
- When the armature is **not** replaced (blendshape-only mode), a standalone prefab is also saved when possible.
- Scene application is undoable (`Ctrl+Z`); undo does not delete successfully saved output files.
- Failed armature application rolls back scene changes and does not save a partial result. Optional prefab
  saving can still produce a warning while retaining a valid mesh result.

## Closed tubular accessories

Advanced **Preserve closed tubes** is enabled by default, including requests created by MCB. It recognizes
closed, approximately planar tubular rings from their welded topology and geometry, regardless of their names
or the relative sizes of disconnected components. Open sleeves and the Hoodie continue to use cloth fitting.

On detected rings, inward-facing tube normals no longer force projections onto a distant body surface. ReFit
fits a smooth periodic centerline and transports the original cross-sectional offsets, rather than pushing
individual tube faces independently. Body-shape clearance acts on this shared centerline. This keeps thin rings
from turning into spikes or flat ribbons. Primary and transferred-shape smoothing settings remain independent.

This is not a general rigid-accessory or collision solver. Strongly nonplanar loops and arbitrary props use the
existing surface workflow. Pre-existing intersections between rings or with the body are not automatically
repaired. A `tube-contact-unresolved` warning means sampled contacts remain more than 1 mm short and require
inspection. Debug projection labels identify the tube binding policy; the logged contact residual is not a
full collision test. API callers can set `settings.preserveClosedTubes = false` for a control comparison.
Severe tube strain (over 8x edge stretch), collapsed faces or folds relative to the transported local frame
produce `tube-geometry-invalid`; unsuccessful results are not applied or saved by the service.

Regenerate existing ReFit results to use the new geometry. Saved meshes and MCB version snapshots are not
rewritten automatically. See [validation records](Documentation~/VALIDATION.md) for test coverage and limits.

## Rigid pieces (beta)

Buttons, studs, buckles and other rigid pieces (plain meshes, or meshes on one or two bones) cannot be bent by a
refit. With Advanced **Keep rigid pieces on the body (beta)** (`settings.keepRigidPiecesOnBody`), applying a refit adds
Toolkit's **Follow Body Blendshapes** to the refitted asset when it has such pieces: at upload and in Play Mode they move
and tilt with the skin under them as the body's blendshapes change. The refitted clothing itself is not affected.

## Public API (for tools such as MCB)

```csharp
using Orbiters.ReFit;
using Orbiters.ReFit.Editor;

var result = ReFitService.Execute(new ReFitRequest
{
    mode = ReFitMode.MeshToMesh,             // or Blendshape / MeshAndBlendshape
    assetRenderer = clothingRenderer,        // scene object or prefab content
    sourceAvatar = originalBasePrefab,       // the base the asset was made for
    targetAvatar = mySceneAvatar,            // scene object or prefab
    targetBlendshape = null,                 // required for the blendshape modes
    settings = new ReFitSettings()           // optional tuning
}, (t, label) => EditorUtility.DisplayProgressBar("ReFit", label, t));

if (result.success)
    Debug.Log($"Mesh: {result.meshAssetPath}  Renderer: {result.sceneRenderer.name}");
foreach (var msg in result.report.messages)
    Debug.Log(msg);
```

`ReFitService.Validate(request)` performs a dry run (staging, armature matching, proportion check) and returns the
diagnostics without changing anything — use it to surface warnings in your own UI before committing.

For non-blocking execution, `ReFitService.ExecuteCoroutine(request, progress, onComplete)` is an editor coroutine:
staging and mesh baking run on the main thread while the heavy geometry runs on a background thread, so the editor
stays responsive. Set `request.targetBlendshapes` (list) to transfer many body blendshapes in a single pass —
bindings are computed once and reused per shape. `settings.prefixTransferredShapes = false` keeps the exact body
shape names on the asset so existing animations/links drive both.

Both engine and service entry points also accept a `CancellationToken` as their final argument. Dispose an
abandoned coroutine; closing the wizard does this automatically. Settings and the shape-name list are copied at
invocation. Keep Unity input objects alive: the service rejects results if their mesh, pose or renderer changes
during background computation. Cancellation is cooperative at geometry phase boundaries, not instantaneous.
Completion/progress callbacks run on the main thread. No Unity object operations belong on caller worker threads.

`ReFitSettingsPresets.ApplyTightness(settings, value)` exposes the wizard's clearance policy (`0` loose, `1` tight).
Primary and transferred-shape smoothing are separate. Advanced **Garment type** (`Auto`, `UpperBody`, `Other`)
controls upper-body hem behavior; automatic inference no longer depends on the avatar's ancestor names.
ReFit's preflight does not silently repair the live armature. The explicit
`ReFitAssetPipeline.RepairSceneAssetArmature(request, report)` operation remains available to callers that own
that repair and its undo workflow.

Orbiters tools: ReFit registers itself with Orbiters Toolkit as its refit engine (`RefitEngine`), so MCB and My Avatar
refit through Toolkit without referencing this package. MCB's **ReFit** frame in the avatar options fits the ticked
meshes from the default base body (model A) to the applied custom version body (model B) with the version's
blendshapes; My Avatar offers the same for each accessory on a custom base. Toolkit records each refit on its renderer
(restored on reset, kept per custom base version by MCB) and, at build, makes every animation of a body blendshape
also drive the shapes transferred from it. Wizard results are recorded the same way (**Settings** › **Link transferred
blendshapes to the body**, on by default). Their fit tightness (`RefitPreferences`) is shared by MCB and My Avatar.

The engine itself (`Orbiters.ReFit.ReFitEngine`, runtime assembly, no UnityEditor dependency) can be used directly
when you want the computed mesh without asset saving or scene application.

## Deterministic test pipeline

See [the validation record](Documentation~/VALIDATION.md) for measured performance, fixture accuracy and
explicitly unverified integrations.

`Tools > Orbiters > ReFit > Run Deterministic Tests`

The runner combines synthetic rigs with the authored `ReFit unit test v1.fbx` and
`ReFit unit test v2 clothing with different armature.fbx` fixtures. The synthetic equal-surface case changes
topology, bone positions and skin weights, then transfers `TestMuscle` onto a third clothing renderer.
Authored comparisons measure forward/reverse surface distance and triangle quality; an additional v1/v2
comparison checks every corresponding base vertex and shape delta within 1 mm. This equivalence test is not a
claim that every point matches the separately authored result within 1 mm.

Licensed local Hoodie checks run only when their private assets are available. Missing prerequisites report
`SKIP`, never `PASS`. The default suite excludes the selection-changing VRCFury build and active-scene check;
those have separate explicit menu entries. Per-case outcomes and timings are written to `Temp/ReFitTests/latest.txt`.

The suite also runs `ReFitNavigationTests.RunOrThrow()` for blendshape search/history, commission row data,
header controls and accessory shortcut inference. These checks restore the user's history and do not open or
focus the wizard. `ReFitNavigationTests.ProbeCommissionEndpoint()` optionally checks the signed-in account's
read-only commission endpoint and writes counts, without account details, to `Temp/ReFitTests/commission-endpoint.txt`.

Batch/CI entry point:

```bash
"<Unity.exe>" -batchmode -quit -projectPath "<project>" -executeMethod Orbiters.ReFit.Editor.Tests.ReFitDeterministicTestRunner.RunBatchMode
```

The checks fail if the primary `refit` shape visibly moves an equal-surface case, if the transferred chest/arm
shape is too weak, if the waist moves when the source body shape has no waist delta, or if applying with armature
replacement disabled changes the clothing renderer's root bone.

Developer entry points (Unity MCP can invoke these directly without changing selection):

- `ReFitDeterministicTestRunner.RunOrThrow()`: deterministic suite; throws on failures, exposes `LastSummary`.
- `ReFitDeterministicTestRunner.BenchmarkHoodie(true)`: record an intentional baseline on the reference revision.
- `ReFitDeterministicTestRunner.BenchmarkHoodie(false)`: time one/four-shape batches and compare geometry against
  that baseline (maximum allowed drift 0.01 mm). The four-shape batch includes the muscle shape and three other
  available shapes, not necessarily four whole-body deformations.
- `ReFitDeterministicTestRunner.AuditHoodie()`: dump every debug hierarchy and per-bone influence totals/bounds,
  plus four local offscreen JPEGs. Requires graphics support; does not upload or alter the user's avatar.
- `ReFitStandaloneCompilation.Run()`: compile main sources excluding other Orbiters/project assemblies (except the
  `Orbiters.Toolkit` dependency);
  asynchronous result is in `Temp/ReFitTests/standalone/result.txt`.

These types are under `Orbiters.ReFit.Editor.Tests`. Benchmark/audit outputs contain private geometry or images:
keep `Temp/ReFitBenchmarks` out of version control. Compare matching settings and report repeated timings;
debug snapshots, editor load and asset saving are separate costs from engine-only benchmarks.

## How it works

See `Documentation~/DESIGN.md` for the algorithm: authored-pose staging and scene-pose baking, scale matching,
once-computed BVH surface bindings with normal / body-region filtering, between two bodies a non-rigid registration of
one body onto the other and a cloth-like drape of the clothing over the target (normal projection where the bodies
differ too much), barycentric delta transfer, Laplacian smoothing with distance falloff, inverse-skinning of the deltas
into blendshape space, and skin weights that follow the change of skinning between the bodies.

## Requirements

- Unity 2022.3+
- Orbiters Toolkit 0.4.0 (shared bone name matching, avatar gizmos)
- Unity's Burst package (`com.unity.burst` 1.8.7; Orbiters Toolkit and the VRChat SDK depend on it too): the spatial
  index's code in the editor
- No dependency on the VRChat SDK (humanoid `Animator` rigs recommended for best results)
- The projection and welded-group toggles join the Toolkit's Avatar Gizmos toolbar. The Orbiters account and environment come
  from Orbiters Toolkit.
