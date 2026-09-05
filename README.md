# ReFit

Re-fit clothing and accessories made for a 3D model **A** so they fit a 3D model **B** — including bases that are
derivatives of A with body modifications, and body blendshapes of B. The deformation is delivered as a
non-destructive **"refit" blendshape** (set to 100 by default) on a duplicated mesh asset; the original asset files
are never modified.

## Using the wizard

`Tools > Orbiters > ReFit`

The wizard asks where your asset is (on an avatar in the scene, or in your project files), which avatar it should
fit, and whether it should follow a body blendshape. It then shows a summary with setup checks (armature matching,
proportion comparison) before the **ReFit** button. Warnings never block: if your custom base has intentionally
relocated bones but a near-identical mesh, just proceed — the surface projection compensates.

Right-click a skinned accessory in the Hierarchy and choose **ReFit** to start with that accessory and its
containing avatar already selected. The renderer's Inspector context menu also exposes **ReFit**. If the
selection has multiple meshes or no identifiable avatar, the wizard keeps the relevant selection step instead
of guessing. This shortcut only configures the wizard; it does not change the scene.

The body blendshape step uses a search field with wrapping name suggestions. With no query, it shows the three
most recently refitted blendshapes that exist on the selected body. Successful transfers from both the wizard
and MCB update this local history; failed operations do not. Back and Settings are in the top banner.

The main page lists active ReFit commissions below refitted assets, with creator, status and the website's
progress bar. Sign in through MCB to load your requests. Clicking a row opens its website page. The first page
refreshes every 30 seconds while this screen is visible; use **Load more** for older active requests and the
refresh icon to return to the latest first page. Switching account or API environment clears the cached list.

After processing, ReFit lists Orbiters creators who accept manual ReFit commissions, with their price range beside
their name. Click an artist card to open a short-lived commission draft on Orbiters with that artist selected.
When MCB authentication is
available, the one-time handoff opens the matching Orbiters account; standalone users can sign in on the website.
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

The **Settings** page includes the same **Dev Environment** switch as MCB. Disabled uses
`https://api.orbiters.cc/refit`; enabled uses the local API at `http://localhost:4100/refit`. When MCB is installed,
both tools share the same persisted environment selection.

## What it produces

- A new mesh saved under `Assets/ReFit/<asset name>/` with the `refit` blendshape (and a second blendshape when
  transferring a body shape, e.g. `refit_Belly_Big`).
- The scene renderer is switched to the new mesh; when fitting to a different avatar, the asset's armature is
  replaced with the target's bones, skin weights are projected from the target body, and bones with no target
  equivalent (skirt/physics bones, props) are preserved and re-parented.
- When the armature is **not** replaced (blendshape-only mode), a standalone prefab is also saved when possible.
- Scene application is undoable (`Ctrl+Z`); undo does not delete successfully saved output files.
- Failed armature application rolls back scene changes and does not save a partial result. Optional prefab
  saving can still produce a warning while retaining a valid mesh result.

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

MCB integration: when both packages are installed, MCB shows a **ReFit** frame in the avatar options (chip
selection of the asset meshes + integrated progress button). It uses the default base body as model A, the applied
custom version body as model B and the version's exposed blendshapes, tracks original meshes on the MyCustomBase
component (restored on reset, surviving Unity restarts), and commits the generated files through Unit Git.

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
- `ReFitStandaloneCompilation.Run()`: compile main sources excluding other Orbiters/project assemblies;
  asynchronous result is in `Temp/ReFitTests/standalone/result.txt`.

These types are under `Orbiters.ReFit.Editor.Tests`. Benchmark/audit outputs contain private geometry or images:
keep `Temp/ReFitBenchmarks` out of version control. Compare matching settings and report repeated timings;
debug snapshots, editor load and asset saving are separate costs from engine-only benchmarks.

## How it works

See `Documentation~/DESIGN.md` for the algorithm: authored-pose staging and scene-pose baking, scale matching,
once-computed BVH surface bindings with normal / body-region filtering, barycentric delta transfer, Laplacian
smoothing with distance falloff, inverse-skinning of the deltas into blendshape space, and barycentric skin weight
projection.

## Requirements

- Unity 2022.3+
- No dependency on the VRChat SDK (humanoid `Animator` rigs recommended for best results)
- XRay Gizmos is optional. Its adapter adds projection/island toggles when installed; the engine and wizard
  compile without it. MCB authentication/environment integration is optional too.
