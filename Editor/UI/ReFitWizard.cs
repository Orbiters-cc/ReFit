using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The ReFit window: one page (the studio) that finds the clothing, the avatar and the base it was made for by itself,
    /// shows the avatar wearing it, and runs <see cref="ReFitService"/> with one button; then compares before and after on
    /// the same stage. Settings and every advanced option are one click away. UI Toolkit, in the Orbiters tools' look.
    /// </summary>
    public partial class ReFitWizard : EditorWindow
    {
        /// <summary>One re-fit performed during this editor session (for the reversible list on the first page).</summary>
        public class RefitLogEntry
        {
            public string assetName;
            public string meshAssetPath;
            public SkinnedMeshRenderer sceneRenderer;
            public Mesh originalMesh;
            public ReFitRendererState originalRendererState;
            public Mesh refitMesh;
        }

        /// <summary>All re-fits performed this session (survives reopening the window, cleared on assembly reload).</summary>
        private static readonly List<RefitLogEntry> SessionLog = new List<RefitLogEntry>();
        private static readonly Color32 ReFitGreen = new Color32(0, 218, 109, 255);
        private static readonly Color ReFitProgressTrack = new Color(0.22f, 0.22f, 0.22f);
        private const string StyleSheetPath = "Packages/orbiters.refit/Editor/UI/refit.uss";

        private enum Page { Studio, Settings, Result }

        /// <summary>Which body the asset was made for.</summary>
        private enum MadeFor
        {
            /// <summary>The avatar it should fit: only body shapes are followed.</summary>
            Target,
            /// <summary>The original base of the target's custom base, as an Orbiters tool (MCB) knows it.</summary>
            OriginalBase,
            /// <summary>Another avatar picked by the user (or the one it is worn on, when fitting it to another).</summary>
            Other
        }

        // What is being refitted (serialized: the window keeps its choices across script reloads)
        [SerializeField] private Page page = Page.Studio;
        [SerializeField] private GameObject myAvatar;
        [SerializeField] private SkinnedMeshRenderer asset;
        [SerializeField] private GameObject assetFileObject;
        [SerializeField] private GameObject targetAvatar;
        [SerializeField] private GameObject sourceAvatar;
        [SerializeField] private SkinnedMeshRenderer sourceBody;
        [SerializeField] private MadeFor madeFor = MadeFor.Target;
        private CustomBaseInfo originalBase;
        private CustomBaseOriginal resolvedOriginal;
        [SerializeField] private List<string> blendshapes = new List<string>();
        [SerializeField] private List<string> activeShapes = new List<string>();
        [SerializeField] private ReFitMode mode = ReFitMode.Blendshape;
        [SerializeField] private ReFitSettings settings = new ReFitSettings();
        [SerializeField] private bool shapesOpen;
        [SerializeField] private bool clearanceAdvanced;
        [SerializeField] private bool clearanceTightnessKnown = true;
        [SerializeField] private float clearanceTightnessPreset = 0.5f;
        [SerializeField] private bool clearanceTightnessUserChosen;
        [SerializeField] private bool assetIsClothing;
        // Was the asset made for this exact avatar? If not, ReFit also moves it out of the body where it clips.
        [SerializeField] private bool madeForAvatar = true;
        // Not made for it: refit the other parts of its outfit with it, innermost first, so their layers stay in order.
        [SerializeField] private bool refitOutfit = true;

        // Checks and the run
        private ReFitReport validateReport;
        private string validateKey;
        private ReFitResult lastResult;
        private ReFitRequest lastRequest;
        private string lastSourceName;
        private ReFitGravityPreview gravityPreview;
        private float gravityPreviewWeight = 100f;
        private readonly List<(SkinnedMeshRenderer part, ReFitRequest request, ReFitResult result)> outfitResults =
            new List<(SkinnedMeshRenderer, ReFitRequest, ReFitResult)>();
        private readonly Stack<IEnumerator> executionCoroutines = new Stack<IEnumerator>();
        private bool isExecuting;
        private float executionProgress;
        private string executionStep;
        private ReFitCommissionCreator[] commissionCreators;
        private bool commissionCreatorsLoading;
        private bool commissionHandoffLoading;
        private string commissionError;

        // Chrome: the header, then the stage on the left and the page on the right (stacked when narrow).
        private VisualElement body, stageColumn, panel;
        private bool narrow;
        private ScrollView content;
        private Button backButton;
        private Button settingsButton;
        private OrbitersGlowSurfaceElement glow;

        [MenuItem("Tools/Orbiters/ReFit")]
        public static void Open()
        {
            var window = Summon();
            // Opening it on a selection starts with that selection.
            if (!window.isExecuting && window.asset == null && Selection.activeGameObject != null)
                window.UseSelection(Selection.activeGameObject);
            window.Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null) root.styleSheets.Add(styleSheet);
            root.AddToClassList("refit-root");

            glow = new OrbitersGlowSurfaceElement(new Color(0.169f, 0.169f, 0.169f, 1f), new Color(0.169f, 0.169f, 0.169f, 1f), 0f, -60f, 220f);
            glow.AddToClassList("refit-glow");
            root.Add(glow);
            root.RegisterCallback<PointerMoveEvent>(_ => glow.WakeForSeconds(15));

            var header = new VisualElement();
            header.AddToClassList("refit-header");
            // The brand is centred over the whole header; the back and settings buttons sit at its edges.
            var brand = new VisualElement { pickingMode = PickingMode.Ignore };
            brand.AddToClassList("refit-brand");
            var logo = ReFitLogo.Create(0.82f);
            logo.AddToClassList("refit-logo");
            logo.pickingMode = PickingMode.Ignore;
            brand.Add(logo);
            var title = new Label("ReFit") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("refit-title");
            brand.Add(title);
            var beta = new StageBadge(FeatureStage.Beta);
            beta.AddToClassList("refit-beta-badge");
            brand.Add(beta);
            header.Add(brand);
            backButton = HeaderButton("‹", "Back", GoBack);
            backButton.AddToClassList("refit-header-button--back");
            header.Add(backButton);
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1f;
            header.Add(spacer);
            // The same button opens and closes the settings.
            settingsButton = HeaderButton(null, "Settings", () => { if (page == Page.Settings) GoBack(); else Go(Page.Settings); });
            var gear = new VectorIcon(IconGlyph.Sliders);
            gear.AddToClassList("refit-header-icon");
            settingsButton.Add(gear);
            header.Add(settingsButton);
            root.Add(header);

            body = new VisualElement();
            body.AddToClassList("refit-body");
            root.Add(body);
            stageColumn = new VisualElement();
            stageColumn.AddToClassList("refit-stage-column");
            body.Add(stageColumn);
            stage?.Release();
            stage = BuildStage();
            stageColumn.Add(stage);
            panel = new VisualElement();
            panel.AddToClassList("refit-panel");
            body.Add(panel);
            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("refit-scroll");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            content.contentContainer.AddToClassList("refit-page-content");
            panel.Add(content);
            var credit = new SupportCredit();
            credit.AddToClassList("refit-credit");
            panel.Add(credit);
            narrow = false;
            root.RegisterCallback<GeometryChangedEvent>(_ => UpdateLayout());

            commissionPoll?.Pause();
            commissionPoll = root.schedule.Execute(PollCommissions).Every(2000);
            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            // After a script reload the choices are back; what is not kept is found again.
            if (page == Page.Result && lastResult == null) page = Page.Studio;
            if (targetAvatar != null && originalBase == null) originalBase = ReFitAutoSetup.OriginalBase(targetAvatar);
            if (asset == null && targetAvatar == null) AutoDetect();
            Render();
            if (asset != null && page != Page.Result) RequestBeforePicture();
        }

        // Two columns, the stage on the left; narrower than NarrowWidth, the stage tops the page and scrolls with it.
        private void UpdateLayout()
        {
            float width = rootVisualElement.layout.width;
            if (float.IsNaN(width) || width <= 0f || stage == null) return;
            bool wanted = width < NarrowWidth;
            if (wanted == narrow && stage.parent != null) return;
            narrow = wanted;
            rootVisualElement.EnableInClassList("refit-root--narrow", narrow);
            PlaceStage();
        }

        private void PlaceStage()
        {
            if (stage == null || content == null) return;
            if (narrow)
            {
                if (stage.parent != content.contentContainer || content.contentContainer.IndexOf(stage) != 0)
                    content.contentContainer.Insert(0, stage);
            }
            else if (stage.parent != stageColumn) stageColumn.Add(stage);
            stage.EnableInClassList("refit-stage--inline", narrow);
            stageColumn.style.display = narrow ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ------------------------------------------------------------------
        // Navigation
        // ------------------------------------------------------------------

        [SerializeField] private Page returnPage = Page.Studio;

        private void Go(Page next)
        {
            if (isExecuting) return;
            if (next == Page.Settings) returnPage = page;
            page = next;
            if (content != null) content.scrollOffset = Vector2.zero;
            Render();
        }

        private void GoBack()
        {
            if (isExecuting) return;
            if (page == Page.Settings) { page = returnPage; Render(); return; }
            if (page == Page.Result) { BackToStudio(); return; }
        }

        /// <summary>From a result to the studio, with the same avatar, ready for another piece.</summary>
        private void BackToStudio()
        {
            ReFitGravityPreviewService.ClearPreview(gravityPreview);
            gravityPreview = null;
            lastResult = null;
            lastRequest = null;
            outfitResults.Clear();
            ClearPictures(true);
            page = Page.Studio;
            InvalidateChecks();
            if (asset != null) RequestBeforePicture();
            Render();
        }

        private void Restart()
        {
            ReFitGravityPreviewService.ClearPreview(gravityPreview);
            page = Page.Studio;
            myAvatar = null; asset = null; assetFileObject = null;
            targetAvatar = null; sourceAvatar = null; sourceBody = null; originalBase = null;
            madeFor = MadeFor.Target;
            blendshapes.Clear(); activeShapes.Clear();
            mode = ReFitMode.Blendshape;
            ResetTightnessChoice();
            madeForAvatar = true; refitOutfit = true; outfitResults.Clear();
            validateReport = null; validateKey = null; lastResult = null;
            lastRequest = null; gravityPreview = null;
            commissionCreators = null;
            commissionCreatorsLoading = false;
            commissionHandoffLoading = false;
            commissionError = null;
            ClearPictures(true);
            Render();
        }

        private void OnDisable()
        {
            // First: whatever else fails, the stage's renderer is freed.
            stage?.Release();
            commissionPoll?.Pause();
            commissionGeneration++;
            commissionListLoading = false;
            Selection.selectionChanged -= OnSelectionChanged;
            StopExecutionPump();
            isExecuting = false;
            ReleaseOriginal();
            ReFitGravityPreviewService.ClearPreview(gravityPreview);
            ClearPictures(true);
        }

        private void OnDestroy() => stage?.Release();

        private void Render()
        {
            if (content == null) return;
            content.Clear();
            // Hidden, not removed: the brand stays centred.
            backButton.style.visibility = page != Page.Studio ? Visibility.Visible : Visibility.Hidden;
            backButton.SetEnabled(!isExecuting);
            settingsButton.SetEnabled(!isExecuting);
            settingsButton.EnableInClassList("refit-header-button--on", page == Page.Settings);
            switch (page)
            {
                case Page.Settings: BuildToolSettings(); break;
                case Page.Result: BuildResult(); break;
                default: BuildStudio(); break;
            }
            PlaceStage();
            ShowPictures();
        }

        // ------------------------------------------------------------------
        // Requests
        // ------------------------------------------------------------------

        /// <summary>The mode follows the choices: another body refits the mesh, shapes are followed when picked.</summary>
        private void UpdateMode()
        {
            bool otherBody = madeFor != MadeFor.Target;
            mode = otherBody ? (blendshapes.Count > 0 ? ReFitMode.MeshAndBlendshape : ReFitMode.MeshToMesh) : ReFitMode.Blendshape;
        }

        /// <summary>Why the ReFit button cannot run yet, or null when it can.</summary>
        private string MissingChoice()
        {
            if (asset == null) return "Pick the clothing to refit.";
            if (targetAvatar == null) return "Pick the avatar it should fit.";
            if (madeFor == MadeFor.Other && sourceAvatar == null) return "Pick the avatar it was made for.";
            if (madeFor == MadeFor.OriginalBase && originalBase == null) return "The original base is not available.";
            if (mode == ReFitMode.Blendshape && blendshapes.Count == 0)
                return "It already fits this avatar: pick body shapes to follow, or the base it was made for.";
            return null;
        }

        private ReFitRequest BuildRequest(SkinnedMeshRenderer part)
        {
            var requestSettings = settings.Clone();
            requestSettings.captureProjectionDebug = ReFitDebugService.Enabled;
            if (requestSettings.captureProjectionDebug)
                requestSettings.maxProjectionDebugGroups = 0;
            var request = new ReFitRequest
            {
                mode = mode,
                assetRenderer = part,
                sourceAvatar = mode == ReFitMode.Blendshape ? null : sourceAvatar,
                sourceBodyRenderer = mode == ReFitMode.Blendshape ? null : sourceBody,
                targetAvatar = targetAvatar,
                targetBlendshapes = mode == ReFitMode.MeshToMesh ? null : new List<string>(blendshapes),
                settings = requestSettings
            };
            if (madeForAvatar) return request;
            // Not made for it: the coverage pass made for clothing from another avatar base.
            requestSettings.coverDifferentBaseBody = true;
            if (OnItsAvatar(part))
            {
                // Its mesh is refitted onto the same body, from where it was placed, with the blendshapes. The outfit's other
                // parts keep their side of it; clipping they cover stays as it is.
                request.mode = ReFitMode.MeshAndBlendshape;
                request.sourceAvatar = targetAvatar;
                request.sourceBodyRenderer = null;
                requestSettings.coverageKeepsLayerOrder = true;
                request.coverageLayers = OutfitParts(part).Where(p => p != part).ToList();
            }
            return request;
        }

        // Refit onto the same avatar from where it was placed: it is on that avatar's hierarchy.
        private bool OnItsAvatar(SkinnedMeshRenderer part) => !madeForAvatar && mode != ReFitMode.MeshToMesh && targetAvatar != null &&
            part != null && part.transform.IsChildOf(targetAvatar.transform) && (sourceAvatar == null || sourceAvatar == targetAvatar);

        // The outfit a part belongs to: its object directly under the avatar, when that holds other meshes. Body and
        // editor-only renderers excluded.
        private List<SkinnedMeshRenderer> OutfitParts(SkinnedMeshRenderer part)
        {
            var parts = new List<SkinnedMeshRenderer> { part };
            if (part == null || targetAvatar == null || !part.transform.IsChildOf(targetAvatar.transform) || part.transform == targetAvatar.transform) return parts;
            var top = part.transform;
            while (top.parent != null && top.parent != targetAvatar.transform) top = top.parent;
            var body = ReFitAutoSetup.Body(targetAvatar);
            foreach (var renderer in top.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer != part && renderer != body && renderer.sharedMesh != null && (renderer.hideFlags & HideFlags.DontSaveInEditor) == 0)
                    parts.Add(renderer);
            return parts;
        }

        // The parts refitted in this run: the asset, or its outfit innermost first.
        private List<SkinnedMeshRenderer> RunParts()
        {
            if (!OnItsAvatar(asset) || !refitOutfit) return new List<SkinnedMeshRenderer> { asset };
            return ReFitOutfit.InnerFirst(ReFitAutoSetup.Body(targetAvatar), OutfitParts(asset));
        }

        // ------------------------------------------------------------------
        // Execution
        // ------------------------------------------------------------------

        private void StartExecution()
        {
            if (isExecuting || MissingChoice() != null) return;
            try
            {
                isExecuting = true;
                executionProgress = 0f;
                executionStep = "Preparing…";
                lastResult = null;
                outfitResults.Clear();
                backButton?.SetEnabled(false);
                settingsButton?.SetEnabled(false);
                executionCoroutines.Clear();
                executionCoroutines.Push(RunExecution());
                EditorApplication.update -= PumpExecution;
                EditorApplication.update += PumpExecution;
                stage?.SetBusy("Fitting…");
            }
            catch (Exception ex)
            {
                CompleteExecutionWithError(ex);
            }
        }

        private IEnumerator RunExecution()
        {
            // The press shows at once; the pictures and the original base come on the next frame.
            yield return null;
            CaptureBeforePictures();
            if (madeFor == MadeFor.OriginalBase)
            {
                executionStep = "Opening " + (originalBase?.BaseName ?? "the original base") + "…";
                yield return null;
                if (!ResolveOriginal())
                {
                    lastResult = new ReFitResult();
                    lastResult.report.Error("original-base-missing",
                        "The original base of " + (originalBase?.Name ?? "this custom base") + " could not be opened.");
                    FinishExecution();
                    yield break;
                }
            }
            lastSourceName = mode == ReFitMode.Blendshape ? null : sourceAvatar != null ? sourceAvatar.name : null;
            if (madeFor == MadeFor.OriginalBase && originalBase != null) lastSourceName = originalBase.BaseName ?? lastSourceName;
            lastRequest = BuildRequest(asset);

            var parts = RunParts();
            if (parts.Count == 1)
            {
                // A part not made for this avatar starts again from its original mesh, not from an earlier refit.
                if (OnItsAvatar(asset) && ReFitRecordIntegration.TryRestore(asset)) lastRequest = BuildRequest(asset);
                yield return ReFitService.ExecuteCoroutine(
                    lastRequest,
                    (t, label) =>
                    {
                        executionProgress = Mathf.Clamp01(t);
                        executionStep = label;
                    },
                    result => lastResult = result);
            }
            else
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part == null) continue;
                    ReFitRecordIntegration.TryRestore(part);
                    // Built now: the parts refitted before it are its layers as refitted.
                    var request = BuildRequest(part);
                    // One prefab of the outfit, once every part is refitted.
                    request.settings.savePrefab &= i == parts.Count - 1;
                    ReFitResult result = null;
                    int index = i;
                    yield return ReFitService.ExecuteCoroutine(
                        request,
                        (t, label) =>
                        {
                            executionProgress = Mathf.Clamp01((index + Mathf.Clamp01(t)) / parts.Count);
                            executionStep = part.name + ": " + label;
                        },
                        r => result = r);
                    Record(request, result, part);
                    outfitResults.Add((part, request, result));
                    if (part == asset) { lastRequest = request; lastResult = result; }
                }

            FinishExecution();
        }

        /// <summary>Drives nested ReFit enumerators from editor updates while geometry runs on its worker task.</summary>
        private void PumpExecution()
        {
            try
            {
                while (executionCoroutines.Count > 0)
                {
                    var currentCoroutine = executionCoroutines.Peek();
                    if (!currentCoroutine.MoveNext())
                    {
                        if (executionCoroutines.Count > 0 && ReferenceEquals(executionCoroutines.Peek(), currentCoroutine))
                            executionCoroutines.Pop();
                        else
                            return;
                        continue;
                    }

                    if (currentCoroutine.Current is IEnumerator nested)
                    {
                        executionCoroutines.Push(nested);
                        continue;
                    }

                    return;
                }

                StopExecutionPump();
            }
            catch (Exception ex)
            {
                CompleteExecutionWithError(ex);
            }
        }

        private void StopExecutionPump()
        {
            EditorApplication.update -= PumpExecution;
            while (executionCoroutines.Count > 0)
                (executionCoroutines.Pop() as IDisposable)?.Dispose();
        }

        private void CompleteExecutionWithError(Exception ex)
        {
            StopExecutionPump();
            lastResult = new ReFitResult();
            lastResult.report.Error("refit-exception", $"Unexpected error: {ex.Message}\n{ex.StackTrace}");
            FinishExecution();
        }

        private static void Record(ReFitRequest request, ReFitResult result, SkinnedMeshRenderer part)
        {
            if (result == null || !result.success) return;
            ReFitRecordIntegration.TryRegister(request, result);
            SessionLog.Add(new RefitLogEntry
            {
                assetName = part != null ? part.name : "asset",
                meshAssetPath = result.meshAssetPath,
                sceneRenderer = result.sceneRenderer,
                originalMesh = result.originalMesh,
                originalRendererState = result.originalRendererState,
                refitMesh = result.mesh
            });
        }

        private void FinishExecution()
        {
            StopExecutionPump();
            isExecuting = false;
            executionProgress = 1f;
            executionStep = null;
            if (outfitResults.Count == 0) Record(lastRequest, lastResult, asset);
            ReleaseOriginal();
            gravityPreview = null;
            // The gravity preview shows one garment: an outfit run goes to its results.
            if (outfitResults.Count == 0 && lastResult != null && lastResult.success &&
                ReFitGravityPreviewService.TryCreatePreview(lastResult, lastRequest, out gravityPreview))
            {
                gravityPreviewWeight = 100f;
                ReFitGravityPreviewService.ShowPreview(lastResult.sceneRenderer, gravityPreview, gravityPreviewWeight);
            }
            stage?.SetBusy(null);
            page = Page.Result;
            Render();
            if (lastResult != null && lastResult.success) RequestAfterPicture();
        }

        // The original base comes from the tool that knows the custom base (it may import a file): only for the run.
        private bool ResolveOriginal()
        {
            ReleaseOriginal();
            try { resolvedOriginal = originalBase?.ResolveOriginal?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[ReFit] Could not open the original base: " + ex.Message); resolvedOriginal = null; }
            if (resolvedOriginal == null || resolvedOriginal.Avatar == null) { ReleaseOriginal(); return false; }
            sourceAvatar = resolvedOriginal.Avatar;
            sourceBody = resolvedOriginal.Body;
            return true;
        }

        private void ReleaseOriginal()
        {
            if (resolvedOriginal == null) return;
            if (madeFor == MadeFor.OriginalBase) { sourceAvatar = null; sourceBody = null; }
            try { resolvedOriginal.Dispose(); }
            catch (Exception ex) { Debug.LogWarning("[ReFit] Could not close the original base: " + ex.Message); }
            resolvedOriginal = null;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Button HeaderButton(string glyph, string tooltip, Action onClick)
        {
            var button = new Button { text = glyph ?? string.Empty, tooltip = tooltip };
            button.AddToClassList("refit-header-button");
            Pressable(button);
            ButtonInteraction.RegisterImmediateClick(button, onClick);
            return button;
        }

        /// <summary>A flat button that dips on pointer down; the action runs on Unity's click.</summary>
        private static Button FlatButton(string text, Action onClick, params string[] classes)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("refit-button");
            foreach (var name in classes) button.AddToClassList(name);
            Pressable(button);
            return button;
        }

        private static void Pressable(VisualElement element)
        {
            element.RegisterCallback<PointerDownEvent>(_ => element.AddToClassList("refit-pressed"), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerUpEvent>(_ => element.RemoveFromClassList("refit-pressed"), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList("refit-pressed"));
        }

        /// <summary>An element that acts as soon as it is pressed (choices that are cheap and safe to apply at once).</summary>
        private static void OnPress(VisualElement element, Action action)
        {
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !element.enabledInHierarchy) return;
                element.AddToClassList("refit-pressed");
                action();
                evt.StopPropagation();
            });
            element.RegisterCallback<PointerUpEvent>(_ => element.RemoveFromClassList("refit-pressed"));
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList("refit-pressed"));
            element.focusable = true;
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space) action();
            });
        }

        private static Label Text(VisualElement parent, string text, params string[] classes)
        {
            var label = new Label(text);
            foreach (var name in classes) label.AddToClassList(name);
            parent.Add(label);
            return label;
        }

        private static VisualElement Box(VisualElement parent, params string[] classes)
        {
            var element = new VisualElement();
            foreach (var name in classes) element.AddToClassList(name);
            parent.Add(element);
            return element;
        }

        private void Help(string text) => Text(content, text, "refit-help");

        private void SummaryRow(VisualElement parent, string key, string value)
        {
            var row = Box(parent, "refit-summary-row");
            Text(row, key, "refit-summary-key");
            Text(row, value, "refit-summary-value");
        }

        private static void AddMessage(VisualElement parent, ReFitMessage m)
        {
            var row = Box(parent, "refit-note");
            var dot = Box(row, "refit-note__dot");
            dot.AddToClassList(m.severity == ReFitSeverity.Error ? "refit-note__dot--error"
                : m.severity == ReFitSeverity.Warning ? "refit-note__dot--warning" : "refit-note__dot--info");
            var label = Text(row, m.text, "refit-note__text", "refit-msg");
            label.AddToClassList(m.severity == ReFitSeverity.Error ? "refit-msg--error"
                : m.severity == ReFitSeverity.Warning ? "refit-msg--warning" : "refit-msg--info");
        }

        private string ModeLabel()
        {
            switch (mode)
            {
                case ReFitMode.MeshToMesh: return "Fit the mesh to another body";
                case ReFitMode.Blendshape: return "Follow body blendshapes";
                case ReFitMode.MeshAndBlendshape: return "Fit the mesh + follow body blendshapes";
                default: return mode.ToString();
            }
        }
    }
}
