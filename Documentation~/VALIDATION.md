# Validation Records

## 2026-10-18: Clothing Made for Another Body on the Custom Bases (0.7.0)

Reported: the Rexouium FishingOutfit put on an Ultipaw by My Avatar (resized to 91 %) and refitted from the original base
with the muscles on came out half fitted, skin through the sleeves, the shorts and around the vest. Nine cases replay it
(`ReFitCaseValidation.CustomBaseCases`): its shirt, shorts and vest (over the shirt, fitted first) on the Ultipaw, the
Ultinova and the Ultirex, each with its custom base's default shapes (the muscles at 100) and following its muscle
shapes, placed as My Avatar places it (`AttachmentFit` onto the avatar, then the line-up with the original base) and
refitted as Orbiters Toolkit asks (armature kept, default tightness, coverage when `ClothingCoverage` asks for it). The
regression table gains the clothing vertices more than 3 mm under the body however they were authored (`restUnder3mm`,
`bareUnder3mm` without the followed shapes, `shapeUnder3mm`): the older count leaves out what already lay under the body
as made, which for clothing made for another body is the whole problem.

Vertices under the body with the default shapes on, 0.7.0 as first committed then now: Ultipaw shirt 760 to 30, shorts
1159 to 22, vest 1216 to 14 (59 under the shirt); Ultinova shirt 545 to 153, shorts 3177 to 337, vest 789 to 677;
Ultirex shirt 426 to 125, shorts 859 to 177, vest 466 to 82. In pictures the Ultipaw outfit sits where it should, collar
at the neck, back and thighs covered, the vest over the shirt; the Ultinova and Ultirex outfits likewise, the Ultinova's
mane and the Ultirex's dorsal spikes through them (the avatars' own parts).

What did it, in order of weight:
- Placement (My Avatar 0.9.10, Orbiters Toolkit 0.3.24). The Ultipaw's armature stands 8 cm above the Masculine Canine's
  on the same body (hips, spine and legs; neck 3 cm, shoulders 1 cm). My Avatar aligned the outfit to it, and its line-up
  with the original base did nothing: the original imports without a humanoid avatar, so the preview had no hips. Fitted
  onto the original's armature bone by bone, the Ultipaw shirt went from 203 vertices under the body to 46.
- Coverage after the cloth fit. The cloth fit keeps what came through the fabric as authored, and since 0.6.0 took the
  place of the different-base passes: clothing made for another body kept all its clipping. Clothing over 10 % of which,
  near the skin, lay more than 5 mm under the source as authored (Ultipaw 30 to 43 %, Ultinova 14 to 66 %) is now covered
  by those passes after the cloth fit: Ultipaw shorts 1159 to 42, vest 1216 to 0 before the placement fix. A limb through
  its own sleeve or leg is covered too, away from the hems: Ultipaw shirt 46 to 3.
- The star pose. The coverage spread the thighs by 20 degrees on the staged copies; the FishingOutfit's shorts are skinned
  to the hips down the thighs (2062 of their 6018 thigh-weighted vertices under half), so their inner legs stayed where
  the spread thighs were and the thighs came back through them: 251 vertices, 22 without the spread. Thighs are spread
  now only when they meet (the ThiccWiker's: 31 points within 1 cm facing each other; the Ultipaw's: none, once the
  hundreds of vertices its crotch gathers into one point on the middle are left out).
- The head left out of the cloth fit's unreached-skin guard: on the Ultirex 2 to 3 % of the skin "under" the shirt was the
  muzzle by its collar, and the cloth fit gave way to the projection passes.
- The target fitted as it shows (shapes it does not follow at their weight): on the replayed Ultinova outfits the
  turtleneck clips less (218 to 183 samples at rest, 210 to 155 with the shapes), the sweater and the tank top slightly
  less at rest, the Classy pants' motion check counts more skin through (441 to 708 points), the chain 4584 to 4642.

Tried and rejected: covering skin through the fabric on the torso as well (any panel over the skin it came through): the
Ultinova shirt's hem was pulled over the crotch like a leotard and the Ultipaw collar pushed out into a turtleneck. Limbs
only, farther than 3 cm from a hem.

The other replayed cases are unchanged (geometry identical; engine times 10 to 15 % higher in that run for every case,
the untouched latex ones included: a cold cache of bodies laid onto each other and machine load). Tests: 102 of the
listed ReFit, Orbiters Toolkit and My Avatar classes through the stepped runner, the window run in its own log scope.

Found on the way: Orbiters Toolkit's `HiddenAvatarCopy` of an object never saved (the replays' own hidden copies, for the
motion check) landed in the open scene and stayed there: 142 copies in the open scene after the day's runs, removed
(never saved). It stays in its preview scene now.

Still open: the Ultinova vest keeps 677 vertices under the body (around the mane and the collar) and 120 under the shirt,
the Ultirex vest 327 under the shirt;
garments are kept in layer order only with the coverage; the cloth fit's shape drapes stop after 48 passes with 50 to 300
fabric points still pushed.

## 2026-10-18: Speed, Motion Check, Inpainted Weights and Kept Fits (0.7.0)

`ReFitRegressionSuite` replays the 23 cases whose assets are in the project (Novabeast latex on the Hypernova, LunaVRC
Classy and Comfy on the Ultinova, the Rexouium jacket on the Ultirex, the hoodie, the FashionSuit pieces on the Marvii
edit, the jockstrap, the onesie and the authored FBX fixture), one per editor update, and lines two runs up
(`Compare`). "Before" is 0.6.0 on the same project (`Library/ReFitTests/regression/base_all.tsv`), "after" this release
(`opt5.tsv`).

Engine time over all cases: 303.4 s before, 159.0 s after (48 % less). Per case (s): catsuit 10.7 to 7.1, corset 14.6
to 5.7, panties 5.2 to 1.8, legs 6.7 to 3.0, arms 5.3 to 2.0, sleeve top 6.2 to 2.5, collar 5.2 to 1.8, turtleneck 11.3
to 5.7, Classy pants 14.4 to 10.3, chain 26.7 to 11.1, sweater 11.4 to 7.5, sweats 13.0 to 7.5, tank top 7.9 to 3.4,
Rexouium jacket 58.3 to 20.4, hoodie 12.9 to 9.0, suit jacket 14.8 to 8.8, suit pants 7.5 to 4.8, vest 21.6 to 14.3,
tie and bow tie 3.1 to 2.2, jockstrap 8.0 to 3.0, onesie 35.1 to 24.8. Where it came from, measured step by step:
the bodies laid onto each other kept between refits (2.3 s per Novabeast piece after the first); the cloth drape looking
for covered skin among the covered triangles only (chain 20.8 to 11.0 s: 162 covered skin points behind 6 cm searches
through the whole body); the coverage regularization re-testing only the limits whose groups moved (vest 20.2 to 14.2 s,
onesie 33.0 to 24.7 s, identical output); struct query filters over cached face normals; a quickselect median in the
hierarchy build; the registration's three axes solved together in parallel.

Geometry at rest and with shapes is unchanged or better on 20 cases (sleeve top 55 to 44 samples over 3 mm, collar 60 to
50, catsuit shape lag 300 to 284). The Classy chain went from 6775 samples over 3 mm to 4584: its links now turn with the
skin (capped at 30 degrees; uncapped, 1043 triangles flipped). A few counts moved by a handful of samples in either
direction (collar with shapes 68 to 78, Classy pants with shapes 205 to 210, sweats with both shapes 28 to 50, the
worst points unchanged): the samples are signed by the nearest body face, and the new hierarchy layout breaks distance
ties differently; the fits themselves were checked identical where the path did not change (the sweats' fit vertex by
vertex). The onesie, fitted by the different-base passes, moved the same way (1164 to 1249 over 3 mm, both badly
clipped: it is a body far from its base).

The Rexouium jacket on the Ultirex is the change worth seeing. 0.6.0 refused the registration (68 % of the jacket
disagreed with the skin met along the source normal: the Ultirex's feathers) although it laid the source within 0.7 mm
(95 % within 8.8 mm), and fell back to the different-base passes: 2337 spikes over 5 mm (72 mm), 9554 stretched
triangles, edges stretched 3.5 times at the 95th percentile, a torn collar and spiky shoulders in the renders. The
registration is now trusted where it reaches the target under the clothing: 12 spikes (10.8 mm), 961 stretched
triangles, edges 1.25 times at the 95th percentile, a whole collar and smooth sleeves front and back, in 20 s instead
of 58. Its samples over 3 mm read 121 to 227, but none of the 24 deepest is inside the body by winding number (10 of 24
before): they lie under the Ultirex's back feathers.

Skin weights inpainted (`inpaintSkinningChange`, on): with it off, the jockstrap's crouch clipping is 202 samples over
3 mm, on 158 (0.6.0: 188); the tank top's crouch with shapes 73 off, 65 on. Weight tears (a bone's weight jumping by
more than 0.35 along an edge) are back to 0.6.0's on the sweater (86) and tank top (23) once the Laplacian was made
the cotangent one (an earlier draft weighed each edge by an angle beside it); the jockstrap has 67 (51) and the jacket
20 (13). On a 150 by 60 tube with known ends the fill stays within 0 to 1, monotone, the same around each ring, in 163 ms
for 7800 unknowns.

In motion: on the jockstrap (Ultinova paw, skeleton-bound) nothing comes through with the arms posed; the steps and
the crouch bring 82 to 153 skin points through, up to 30 mm, and the crouch render shows the raised thighs swallowing
the pouch. The latex sleeve top has up to 9.5 mm at the chest and shoulders with the arms down or forward. A planned
shrink takes the points through over all poses from 199 to 126 (sleeve top) and 381 to 253 (jockstrap); it is
conservative by design (a move that takes skin through in another pose is shortened, never kept). The motion columns
of the replay are meaningful only where the clothing follows the avatar as built: the hoodie, the onesie and the suit
pieces are model files without their outfit's Armature Links, posed apart from the body (up to 915 mm).

Push out: on the turtleneck 264 fabric points inside the body went to 120 after 157 automatic brush steps (3 ms each);
the field over the piece (306 x 145 x 93 cells of 4.6 mm) took 13 s to build until the border cells' search for the
skin went parallel and bounded (4 s), and is now built on a worker thread (the brush starts in 35 ms).

Tests: ReFit's ReFitQueryAndRigidTests, ReFitWeightInpaintingTests, ReFitSyntheticTests, ReFitPlacementTests and Orbiters
Toolkit's RefitKeptFitsAndShrinkTests (kept fits, the build's pick-one menu with VRCFury's toggles, the body shrink
following the clothing's toggle on a build copy), RefitRecordsTests and RefitBuildTests: 49 passed. Deterministic suite
(stepped runner): 86 passed, 2 opt-in scene/VRCFury checks skipped, the window run test passing in its own log scope.
The result page's new cards were captured in a hidden window on a refit staged in a preview scene
(`ReFitWizardPreview`).

## 2026-10-16: Prefab Outfits, Body Registration and Cloth Fit (0.6.0)

Three reports: VRCFury outfits had to be unpacked and then "freaked out" once refitted; the Novabeast latex clothing
clipped a lot on the Hypernova; the LunaVRC Novabeast clothing (Classy, Comfy) looked poor on the Ultinova.

The freak-out had four causes, all fixed: the refit unpacked the outfit prefab to rebuild the armature; it pointed the
outfit's one VRCFury Armature Link at the refitted part's new armature, unlinking the other parts; it rebound the other
parts' bones onto the new armature without their bindposes (the Ultinova's custom model has its forearms 21 mm off and
its breast bones turned 180 degrees, so they bent there); and it gave the clothing the body's weights on the base's
fluff and tail physics bones. `ReFitPrefabOutfitValidation.Run` (the Ultinova custom model, the Classy prefab, Turtle
neck and Dress Pants refitted) reports the prefab still connected, the untouched Chain moved 0.00 mm, and one link per
armature with one skinned user each, with and without an offset armature. A deterministic check refits the v2 fixture's
clothing as a model prefab instance and finds it still packed.

Outfit replays (`ReFitCaseValidation`, at rest; "before" is the 0.5.10 path on the same cases, `registerBodies=false`):

| Case | Spikes over 5 mm | Clipping samples over 3 mm | Shape lag over 3 mm | Reversed triangles | Engine |
| --- | --- | --- | --- | --- | --- |
| Latex catsuit, Hypernova, five shapes | 422 (66 mm) → 45 (15 mm) | 166 (27 mm) → 257 (15 mm) | 117 (176 mm) → 73 (32 mm) | 137 → 2 | 17.0 → 11.2 s |
| Turtle neck, Ultinova | 810 (100 mm) → 354 (32 mm) | 331 (26 mm) → 223 (56 mm) | 179 (16 mm) → 42 (7 mm) | 75 → 86 | 2.5 → 11.7 s |
| Sweater, Ultinova | 720 (62 mm) → 193 (31 mm) | 194 (17 mm) → 113 (27 mm) | 234 (24 mm) → 71 (13 mm) | 197 → 90 | 25.2 → 12.2 s |

The other cases after: latex corset 74 spikes, 467 samples over 3 mm (the deepest, 16 mm, under the tip of the
Hypernova's chest fluff, which lies over the corset's top);
panties 0 and 136; legs 0 and 154; arms 0 and 11; sleeve top 18 and 54; collar 56 and 59; dress pants 183 and 136;
sweats 180 and 28; tank top 123 and 81. The Classy chain went from torn (edges stretched up to 134 times, 2666 reversed
triangles) to whole (0 spikes, no stretched or reversed triangle): proximity welding had fused its 91 touching links
into two pieces, and now joins only vertices on open edges. Every case lays the bodies onto each other with at most
0.1 % of the skin under the clothing out of reach.

The catsuit has more shallow clipping samples than before (166 to 257 over 3 mm) at half the depth, mostly the
Hypernova's wrist and ankle fluff, which the Novabeast has no counterpart for; the deepest turtleneck samples lie inside
the Ultinova's chest fluff at the front of the neck, and the chain's inside the mane. In the renders the catsuit's
neckline, shoulders and V stay clean with all five shapes on, and the Classy and Comfy pieces show no tenting or tears.

FBX fixtures: the sculpted body B reaches up to 90 cm out of the base's surface. 8 % of its skin under the clothing is
out of the registration's reach, so the fit falls back to the earlier passes (2 % limit): 8.4 mm generated to expected
(at most 10 mm), as before. Tried and left out: a pull of the target's skin back onto the source in the registration
(13.4 to 12.8 mm on the fixture, and skin patches on the catsuit's shoulders with all shapes on), and leaving skin near
open fabric edges out of the drape (no change on the fixture).

Deterministic suite (stepped runner): 86 passed, 2 opt-in scene/VRCFury checks skipped, and the window run test failed
only for want of a log scope; run inside one, it passes. Orbiters Toolkit: AttachmentTests and AttachmentVolumeFitTests
28 passed; ClothingCoverage, FitCheck, RefitBuild and RefitRecords 44 passed.

## 2026-10-11: Different-Base Drape (onesie on the ThiccWiker: chest and rear)

The reported onesie still showed the ThiccWiker's breasts and the gluteal crease. Three causes: the target's flat chest
left inside the added breasts was matched and covered instead of the breasts; skin far larger than the base's had no
coverage support beyond 8 cm; and the fit followed the target's folds (the crease, behind the knees) into pleats
crossing the fabric itself. With "Placed by hand" only:

- `MeshSnapshot.HideEnclosedSkin`: skin enclosed in another part of the target body is left out of every query.
- `ReFitDrape`, after the coverage repair and on every transferred frame: target skin whose counterpart on the source
  was under the onesie (fold walls and skin without a counterpart take the decision around them; head skin, fluff
  through the fabric and the base's tail do not drive it) pushes the fabric out over it; the push spreads like cloth.
  Before it, fabric crossing itself where it did not as authored is relaxed (the two legs pressed together excepted),
  and fabric pulled into a fold lifts out of the body to span it.

| Onesie at rest | Before | After |
| --- | --- | --- |
| Skin showing through, chest and rear | breasts (850 + 830 vertices), crease | none in renders; the drape reports 2 left |
| Clipping samples over 3 mm | 1023 (39 mm) | 1634 (72 mm), 594 of 697 vertices under the fluff (mane, chest tuft) |
| Reversed triangles / spikes over 5 mm | 2622 / 2056 | 1520 / 1098 |

Making the top of the crease less flat (a reported request) was tried and left out: weaker bridging or spreading changed
nothing visible, lower primary smoothing showed the base tail's outline as a panel, and sinking fabric that floats over
the crease into it narrowed the back and tore the collar while barely deepening the crease. The onesie's tail-opening
panel has vertices 4 to 8 cm apart there; it floats 3 to 7 cm over the crease and can only span it.

The clipping count rose because fabric lifted over larger breasts and buttocks now lies under the larger fluff, which
covers it; the deterministic onesie check (at most 1250 samples) fails on it and was left as is. The crouched pose
still shows skin behind the knees, as before. The jockstrap replay ("Yes, it was made for it") is unchanged to the
digit. Deterministic suite: 77 passed, 2 opt-in skipped, the onesie count above failed, and the layered-hem coverage
check failed until the bridging lift followed the body's normal (it passes now).

## 2026-10-09: Fit Default for Underwear, and the ReFit Window (0.5.8)

Underwear and swimwear are now clothing by name and fit snug (tightness 0.93) like the body clothing the gravity detection
finds; the reported jockstrap used to get the accessory default (0). Replayed with
`ReFitCaseValidation.Queue("jockstrap", "snug", true, "fit", "tightness=0.93")` and `"loose"` with `"tightness=0"`
(renders under `Temp/ReFitTests/cases/jockstrap-snug-*` and `jockstrap-loose-*`, which an editor restart clears):

| Jockstrap on the Ultipaw | Loose (before) | Snug (now) |
| --- | --- | --- |
| Rest | 4 samples, 7.2 mm | 4 samples, 7.2 mm |
| Spread | 0 samples, 1.1 mm | 0 samples, 1.5 mm |
| Crouched | 160 samples, 16.6 mm | 154 samples, 16.6 mm |
| `orbit muscles` at 100, rest | 778 samples, 24.5 mm | 799 samples, 24.3 mm |
| `orbit muscles` at 100, crouched | 1287 samples, 36.5 mm | 1130 samples, 36.5 mm |
| Engine | 2.0 s | 5.2 s |

The renders look the same at rest; the muscle shape still swallows the straps in the gluteal fold either way (below).

Loose one-pieces stay out of the name list. The reported onesie fitted snug had about a tenth less clipping (rest 935
samples vs 1023, spread 829 vs 928, crouched 2011 vs 2349) but deeper worst points at rest (46.5 mm vs 39.2 mm) and took
179 s instead of 23 s (deformation 84 s instead of 12.6 s, the `VVorthy` transfer 93 s).

Spikes at the hips (snug jockstrap): the window's after picture showed the waistband's ends at both hips jagged. Close-ups
at 1600 px from three-quarter and side, rest and crouched (`Temp/ReFitTests/spikes/before-*`, `after-*`, `compare-*`)
showed real geometry, not z-fighting: folded flaps along the band's lower edge and horns when crouched. A handful of
waistband vertices (402, 554, 353, 570 and their rim twins) moved 15 to 20 mm while their neighbours moved 3 mm, edges
stretched up to 23 times. The surface guard did it. Its samples on the band's 12 to 21 mm long edges sag into the hips
already on the Winterpaw as authored (up to 7.4 mm, 2.7 mm on average) and 1 to 2 mm deeper on the Ultipaw; the guard
treated all of it as clipping, could move only the few corners where the body grew enough, and pushed those out on each
of its six iterations. Turning the guard off removed the spikes and changed no clipping count. The guard now restores
each sample's authored depth instead of lifting it out of the body, and lifts a sample only when the corners it may move
hold at least half of it.

| Jockstrap, snug, rest | Before | After |
| --- | --- | --- |
| Vertices moved over 5 mm unlike their neighbours | 11 (worst 15 mm) | 0 (worst 3.6 mm, the pouch's folded bottom) |
| Edge stretch, max | 23.0 | 4.6 |
| Clipping rest / spread / crouched | 4 / 0 / 154 samples | 4 / 0 / 156 samples |

The reported-jockstrap test now also requires no such spike at rest and edges stretched at most 8 times. Loose and
balanced fits never ran the guard here (no spikes either way). The real hoodie replays and the clearance checks that run
the guard at the same strength (hem bounds, detached islands) still pass.

Window defaults, on the same fixtures in preview scenes: on the ThiccWiker wearing the onesie, the active body shapes found
under it are `VVorthy - normal fix - Reverted` (it moves 9564 body vertices, up to 80 mm) and not `Claw.short` (its claws
are more than 3 cm from the onesie). The jockstrap and a bodysuit are clothing by name; a bracelet, brass ring, zebra tail
or the onesie are not.

The window was checked in hidden copies (`orbiters_editor_window`) against My Avatar and the Logger: neutral greys,
16 px cards, the #00DA6D primary with 8 px corners and white text, pill chips, flat buttons that dip on press. The
before/after stage reuses the before pictures' framing for the after pictures, so the halves line up.

Deterministic suite (reflection runner, one case per editor update): 79 passed, 2 opt-in scene/VRCFury checks skipped, none failed; the reported jockstrap now runs snug (rest 4 samples, spread 0, crouched 154) and the onesie loose as before (rest 1023, 39.2 mm). Orbiters Toolkit refit tests (ClothingCoverage, FitCheck, RefitBuild, RefitRecords, AttachmentVolumeFit): 47 passed.

## 2026-10-09: Crotch and Different-Base Fit (reported jockstrap and onesie)

Two user reports replayed with `ReFitCaseValidation` on transform-only copies in preview scenes (private assets under
`Assets/ReFitCases`; renders, CSVs and reports under `Temp/ReFitTests/cases`, not to be committed or published):

- A jockstrap made for the Winterpaw, refitted onto an Ultipaw like the wizard does ("made for another avatar base",
  "Yes, it was made for it", armature replaced, `orbit muscles` transferred). The two bodies are within 4 mm at rest
  around it, but the Ultipaw gives the groin and the hips' sides to the thighs where the Winterpaw gives them to the hips.
- A Wickerbeast onesie refitted onto a ThiccWiker with the different-base coverage ("Placed by hand"), the
  ThiccWiker's always-on `VVorthy - normal fix - Reverted` shape transferred and mirrored at 100.

Clipping counts clothing vertices and triangle centers outside the authored body that end over 3 mm inside the target
body (worst depth, solid-angle confirmed); poses turn the thighs out 35 degrees ("spread") or raise them 75 degrees with
bent knees ("crouch") on every copied rig.

| Case | Before | After |
| --- | --- | --- |
| Jockstrap at rest | 5 samples, 7.2 mm; edge stretch P95 2.21, 41 reversed triangles | 4 samples, 7.2 mm (the unfitted jockstrap clips there too); P95 1.22, 66 reversed in the pouch's folded bottom |
| Jockstrap spread | 487 samples, 28 mm; pouch shredded | 0 samples, 1.1 mm |
| Jockstrap crouched | 916 samples, 65 mm | 160 samples, 16.6 mm |
| Jockstrap weights | blotches of thigh weight over the hip-weighted pouch | smooth hips-to-thigh gradient, 0.3 % on the other side's leg |
| Onesie at rest | buttocks, inner thighs and back of the knees bare; 98 mm deep | 1023 samples (thighs pressing together, tail opening), 39 mm |

Causes found: skin-facing clothing faces matched to far surfaces (the inside of straps and waistbands bound 10 to 15 cm
away, so they lost the body's shapes); hard dominant-weight regions refusing the hips' sides and a re-weighted buttock;
nearest-point matching pulling a grown buttock's clothing into the gluteal crease; skin weights blended or rejected per
vertex (discontinuous thigh weight tore the pouch apart in poses). Matching with the thighs spread was evaluated and
left out: rest and pose clipping were unchanged for the onesie, the jockstrap's muscle transfer clipped more (1117 vs 873
samples), and a pose pairs clothing with skin it does not cover at rest when the two are weighted differently.

Remaining: with `orbit muscles` at 100 the Ultipaw's thighs and buttocks grow into each other and swallow the straps in
the gluteal fold (778 samples); the ThiccWiker's breasts show through the onesie's chest; the onesie's tail opening
widens over the much larger buttocks.

## 2026-09-10: Closed Tubular Accessories

Validated with Unity 2022.3.22f1 via MCP and offscreen Unity camera renders. No desktop input was used and
no live accessory was reset or assigned a generated mesh. Private PNGs and geometry reports stay under
`Temp/ReFitTests/accessories`; they must not be committed or published.

- The deterministic suite passed 64 checks, skipped 2 opt-in scene/build checks, and failed none. Both authored
  FBX fixtures and the existing Hoodie skinning, staging, clearance and lifecycle checks passed.
- Standalone compilation passed with other Orbiters/project assemblies excluded.
- The runtime guard rejects severely stretched, collapsed or locally folded tube geometry before service
  application. An excessive-expansion fixture checks that this is an error, not a successful result.
- New public fixtures reproduce the opposite-face normal-binding bug on two equal-size closed rings. They
  check analytic expansion, thickness, zero-field identity, translated/rotated/3.57x-scaled inputs, open-sleeve
  rejection, the disable switch, complete engine output, different A/B default radii and repeated shapes within a batch. Final triangles
  are checked at 0/25/50/75/100%, with independent crossing/coplanar intersection and volume-check self-tests.
- Private output passed a Unity serialized-mesh round trip. All 24 authored glowstick shapes individually retain
  their original world geometry within 0.001 mm with generated shapes disabled; skin weights match. The Hoodie's
  authored shapes have 0.009156 mm original-to-output pose-baking drift, identical with the feature on/off.
- All 24 components of the private glowstick mesh are detected. Primary maximum edge ratio is 1.043. At full
  muscles it is 2.488 rather than the prior 66.615; 148 triangles still exceed 2x edge stretch around the strongly
  changing lower legs. These are reported, not hidden. There are zero orientation reversals, degenerate faces,
  or within-ring self-intersections at all five sampled weights.
- Separate rings intersect in the original asset (18 triangle pairs). At full muscles there are 11 pairs,
  including four newly intersecting triangle pairs. This is not a claim of collision-free inter-ring packing.
- Independent nearest-surface checks include vertices and triangle centers. Suspected >1 mm crossings are
  cross-checked with solid-angle winding: zero newly outside-to-inside crossings were confirmed for the
  glowsticks. Collapsed/internal body polygons can give misleading nearest-face normals; candidate counts and
  pre-existing interior samples remain in the report. This finite sampling is not a continuous collision proof.
- Front, three-quarter, arm, leg and rear-shoulder PNGs were inspected against original and previously generated
  geometry. The previous shoulder spikes/ribbons are absent and the rings remain tubular. Pre-existing body
  occlusion remains visible; this solver does not repair the accessory's original fit.
- The live Hoodie is tested from its MCB-stored original mesh and pose on a transform-only copied avatar.
  Zero Hoodie components are classified as closed tubes. With the feature enabled/disabled, all output vertices
  and shape frames agree within 0.001 mm. A rear-triceps clipping spot is visible and unchanged; no claim is made
  that this accessory fix resolves the existing Hoodie clearance limitation.

The isolated replay must preserve `assetRenderer` as a descendant of its copied `targetAvatar`. A standalone
copy changes ReFit's intentional staging policy and is not an equivalent MCB regression test.

### Performance

Paired engine-only trials in off/on/on/off order, without rendering, file saves or scene application:

| Requested shapes | Previous policy | Tube preservation |
| --- | ---: | ---: |
| 1 | 4.05-4.18 s | 4.22-4.47 s |
| 7 (MCB's available body/face selection) | 10.58-10.88 s | 10.97-11.10 s |

The single-shape muscle delta exactly matches its result in the seven-shape batch. This is a quality fix with
a modest additional cost, not a general speedup. Closed-tube topology/centerline analysis is shared across
shapes within a run; no native binary or GPU dependency was added.

### Reproduction

The public regression remains part of `ReFitDeterministicTestRunner.RunOrThrow()` / `RunBatchMode()`.
`ReFitTubeTests.RunOrThrow()` runs it separately without private assets. In the configured private scene, call:

```csharp
Orbiters.ReFit.Editor.Tests.ReFitAccessoryValidation.RunGlowsticks();
Orbiters.ReFit.Editor.Tests.ReFitAccessoryValidation.RunHoodie();
Orbiters.ReFit.Editor.Tests.ReFitAccessoryValidation.BenchmarkGlowsticks();
```

These explicit private entry points require MCB, the named local accessories and graphics support. They copy
transforms/mesh references rather than instantiating arbitrary avatar behaviours. The default suite does not
depend on these private entry points. Source assets, bones and live blendshape weights are not replaced.

## 2026-09-05: Architecture and Performance

Validated in Unity 2022.3.22f1 through MCP, without desktop input. No live user avatar was refitted or reset.
Private assets were instantiated as disposable test objects. Output images/geometry remain local under `Temp`.

## Final Checks

- Deterministic suite: 62 passed, 2 skipped, 0 failed.
- Both authored FBXs passed; v2's inserted joint now reproduces v1's base vertices and transferred deltas within
  1 mm. The pre-existing source-joint snapping error was isolated from the performance changes before fixing it.
- Real local Hoodie checks cover target-space follow-up transfer, projected weights, armature links, clearance,
  seam/triangle behavior and leaf helpers. The public three-argument service coroutine applies multiple shapes
  and persists the output. A null completion callback no longer prevents application.
- Lifecycle checks cover abandoned staging, cancellation before bake, late cancellation from progress,
  completion-callback errors, failed armature application and prefab connectivity restoration on undo.
- Cache/input checks cover unchanged reuse, changed smoothing, changed shape content at identical counts,
  primary-slider changes and native same-count mesh edits.
- Main-source standalone compilation passed with other Orbiters/project references excluded.
- The XRay adapter's external-toggle registration check passed.

The two explicit skips are the selection-changing VRCFury test-copy build and active-scene Hoodie state check.
They are not counted as passing. Full MCB version application, fresh-project UPM resolution and other Unity/
operating-system versions were not exercised. The MCB API contract test is not a substitute for its full UI flow.

## Performance

Two paired repetitions alternate reference/current execution order, using the same captured settings and
disposable inputs. Times below are engine-only, excluding scene application, saved assets and debug copies.

| Settings | Shapes | Reference | Current |
| --- | ---: | ---: | ---: |
| API defaults | 1 | 6.48-6.71 s | 3.16-3.28 s |
| API defaults | 4 | 12.61-12.92 s | 5.13-5.17 s |
| Current local wizard clearance settings | 1 | 19.07-19.79 s | 14.58-14.61 s |
| Current local wizard clearance settings | 4 | 29.44-30.81 s | 22.15-23.05 s |

The four-shape sample is one muscle shape and three available visemes, not four full-body deformations.
The stronger wizard settings use six surface-guard iterations and three edge samples. This workload remains
expensive; the result is a reduction, not an instantaneous fit.

For the paired experiment, the reference engine/clearance code came from the pre-change revision, with only
temporary class renaming and ownership plumbing needed to run beside the new staging lifecycle. Both used
the current low-level BVH/staging helpers, so these numbers isolate engine/clearance savings, not every change.
The reference code was removed afterward. Separate binary geometry recorded before implementation also
matched the optimized default-settings output with zero vertex/shape-delta drift.

All paired geometry comparisons passed the 0.01 mm limit. An experimental BVH bounds-only update was rejected
because it produced measurable drift. No native binary, GPU backend or approximate projection was retained.
Earlier unpaired runs varied with editor/system load; do not compare a historical slow run with a selected
fast run to claim a guaranteed speedup.

## Hierarchy and Visual Audit

The generated Hoodie contains one armature and one Hips root, with 27 deforming bones rather than the target's
whole 95-bone body skeleton. Target-derived spine/chest/shoulder/arm chains are connected. Clothing-only ChestUp
is under Chest and carries the preserved hood and string chains; it is not a second parallel torso.

Forearm leaf helpers point to the target wrist positions, approximately 0.2362 m away. Thigh helpers use the
target shin positions, approximately 0.4426 m away, not the feet. Original hood/string tail lengths are
preserved. These helper transforms do not appear in the skinning bone list. Debug copies have internal bone
references, and the stage-by-stage report includes every transform path, position, rotation and parent distance.

The original left/right forearm weight totals of approximately 146.6 become approximately 152.9, with affected
vertex counts changing from 169 to 200. Upper-arm totals remain around 143-144. The audit also records complete
original/target/generated influence bounds, rather than treating nonzero totals as sufficient evidence.

Four offscreen views were inspected for the raw-FBX fixture, including a run with the open wizard's exact
clearance settings. Full-muscles shoulder clipping remains visible. The optimized geometry matches the
reference; this work must not be described as fixing that remaining fitting limitation. The fixture's imported
pose is not a replay of the user's manually positioned live garment.

## Authored Result Accuracy

The v2 forward base surface distance measured mean 8.673 mm, P95 32.223 mm, maximum 293.956 mm; the shaped result
measured mean 8.792 mm, P95 32.662 mm, maximum 279.491 mm. Reverse distances are larger (base mean 53.453 mm,
maximum 1.0529 m). The separate authored result has different coverage; report both directions rather than
claiming a globally submillimeter reproduction. Existing thresholds were tightened, not relaxed to pass v2.

Before the inserted-joint pose fix, v2's forward base mean was about 78.5 mm. The fix removes that additional
armature-induced error; the v1/v2 equivalence assertion is much stricter than comparison to the independently
authored reference surface. Remaining coverage/outlier errors warrant separate geometric investigation.

## Reproduction

Use the README entry points. Detailed local evidence is in `Temp/ReFitTests/latest.txt`,
`Temp/ReFitTests/standalone/result.txt`, `Temp/ReFitBenchmarks/paired-defaults.txt`,
`Temp/ReFitBenchmarks/paired-wizard-settings.txt`, and the `hoodie-audit` / `hoodie-wizard-settings` directories.
Do not commit those private artifacts or overwrite an independent baseline on the optimized revision.
