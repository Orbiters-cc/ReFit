# Validation Records

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
- A Wickerbeast onesie refitted onto a ThiccWiker with the different-base coverage ("No, I placed it myself"), the
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
