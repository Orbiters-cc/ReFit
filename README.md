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

## What it produces

- A new mesh saved under `Assets/ReFit/<asset name>/` with the `refit` blendshape (and a second blendshape when
  transferring a body shape, e.g. `refit_Belly_Big`).
- The scene renderer is switched to the new mesh; when fitting to a different avatar, the asset's armature is
  replaced with the target's bones, skin weights are projected from the target body, and bones with no target
  equivalent (skirt/physics bones, props) are preserved and re-parented.
- When the armature is **not** replaced (blendshape-only mode), a standalone prefab is also saved when possible.
- Everything is undoable (`Ctrl+Z`).

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

MCB integration: when both packages are installed, MCB shows a **ReFit** frame in the avatar options (chip
selection of the asset meshes + integrated progress button). It uses the default base body as model A, the applied
custom version body as model B and the version's exposed blendshapes, tracks original meshes on the MyCustomBase
component (restored on reset, surviving Unity restarts), and commits the generated files through Unit Git.

The engine itself (`Orbiters.ReFit.ReFitEngine`, runtime assembly, no UnityEditor dependency) can be used directly
when you want the computed mesh without asset saving or scene application.

## How it works

See `Documentation~/DESIGN.md` for the algorithm: pose normalization into a shared neutral pose, scale matching,
once-computed BVH surface bindings with normal / body-region filtering, barycentric delta transfer, Laplacian
smoothing with distance falloff, inverse-skinning of the deltas into blendshape space, and barycentric skin weight
projection.

## Requirements

- Unity 2022.3+
- No dependency on the VRChat SDK (humanoid `Animator` rigs recommended for best results)
