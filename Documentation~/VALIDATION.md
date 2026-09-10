# Validation Records

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
