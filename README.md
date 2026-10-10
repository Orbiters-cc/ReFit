# ReFit

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
nothing selected, the scene's only avatar). The avatar's clothing shows as picture cards; click one, select it in the
Hierarchy or drop a prefab on the stage, and the stage on the left shows the avatar wearing it in 3D: drag to turn,
scroll to zoom, right-drag (or Shift-drag) to move, double-click to frame the piece again, or pick Front, ¾, Side or
Back. Under the cards, one row per choice, already set:

- **Made for**: this avatar (ReFit makes it follow body shapes), the original base of the avatar's custom base when MCB
  knows it, or another avatar (picked by itself when you fit a piece from the avatar wearing it to another one).
- **Body shapes**: the shapes the clothing should follow. The ones the avatar keeps switched on under the clothing are
  picked for you, a custom base's own shapes are suggested, and **More** opens the search picker with your recent shapes.
- **Fit**: Loose, Balanced or Snug. Clothing (underwear and swimwear included) starts Snug, accessories Loose.
- **Placed by hand**: for clothing made for another avatar and placed over this one by hand. ReFit then also removes the
  clipping it already has and, with **Whole outfit**, refits the rest of its outfit with it, innermost part first, so
  jacket, shirt and trousers stay layered.

The **ReFit** button shows its progress. The setup checks (armature matching, proportion comparison) appear under it.
Warnings never block: if your custom base has intentionally relocated bones but a near-identical mesh, just proceed — the
surface projection compensates. Afterwards the stage compares before and after with the same camera: drag the handle
to move the split, anywhere else to turn. The result selects the
refitted renderer, shows its mesh, undoes the refit, or goes back for the next piece. Every advanced option is on the
**Settings** page (the sliders in the top banner, or **Advanced options**).

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
  replaced with the target's bones, the asset's skin weights follow the difference between the two bodies' skinning
  where it lies on the skin, and bones with no target equivalent (skirt/physics bones, props) are preserved and
  re-parented.
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
once-computed BVH surface bindings with normal / body-region filtering, normal projection between differing bodies,
barycentric delta transfer, Laplacian smoothing with distance falloff, inverse-skinning of the deltas into blendshape
space, and skin weights that follow the change of skinning between the bodies.

## Requirements

- Unity 2022.3+
- Orbiters Toolkit 0.3.x (shared bone name matching)
- No dependency on the VRChat SDK (humanoid `Animator` rigs recommended for best results)
- XRay Gizmos is optional. Its adapter adds projection/island toggles when installed; the engine and wizard
  compile without it. The Orbiters account and environment come from Orbiters Toolkit.
