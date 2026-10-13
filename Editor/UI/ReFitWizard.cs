using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        // A choice made on press runs this long after it, once the pressed look has painted.
        private const long PressPaint = 16;
        // The scene settles this long after its last change before the window reads it again.
        private const long SceneSettle = 300;

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

        /// <summary>
        /// Every choice a run uses, copied when it starts: the page stays locked while it runs, and nothing chosen meanwhile (a
        /// selection, a drop) reaches it. The original base opened for the run and the fit taken back for it live here too,
        /// never in the window's choices.
        /// </summary>
        private sealed class Inputs
        {
            public SkinnedMeshRenderer asset;
            public GameObject targetAvatar, sourceAvatar;
            public SkinnedMeshRenderer sourceBody;
            public MadeFor madeFor;
            public CustomBaseInfo originalBase;
            public List<string> blendshapes;
            public ReFitMode mode;
            public ReFitSettings settings;
            public bool madeForAvatar, refitOutfit;
            // The target's custom base and version, recorded with the fit (MCB keeps a fit for its version only).
            public string baseKey, baseName;
            public CustomBaseOriginal resolvedOriginal;
            // The parts' earlier fits, taken back for the run: each given back unless its new fit succeeds.
            public readonly Dictionary<SkinnedMeshRenderer, ReFitRecordIntegration.PreviousFit> previous =
                new Dictionary<SkinnedMeshRenderer, ReFitRecordIntegration.PreviousFit>();
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
        // The page scrolls; the stage tops it when narrow, kept there while the page under it is built again.
        private ScrollView scroll;
        private VisualElement content;
        private Button backButton;
        private Button settingsButton;
        private OrbitersGlowSurfaceElement glow;
        // Shown (not an inactive docked tab): only then does a selection in the Hierarchy reach it.
        private bool visible;
        private IVisualElementScheduledItem sceneUpdate;
        private string sceneSignature;

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
            scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("refit-scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.contentContainer.AddToClassList("refit-page-content");
            panel.Add(scroll);
            content = new VisualElement();
            content.AddToClassList("refit-page");
            scroll.Add(content);
            var credit = new SupportCredit();
            credit.AddToClassList("refit-credit");
            panel.Add(credit);
            narrow = false;
            root.RegisterCallback<GeometryChangedEvent>(_ => UpdateLayout());

            commissionPoll?.Pause();
            commissionPoll = root.schedule.Execute(PollCommissions).Every(2000);
            // Its page is made when it shows (a docked tab's when it is first brought up); hidden again, it is told.
            visible = true;
            Listen(true);
            // After a script reload the choices are back; what is not kept is found again.
            if (page == Page.Result && lastResult == null) page = Page.Studio;
            if (targetAvatar != null && originalBase == null) originalBase = ReFitAutoSetup.OriginalBase(targetAvatar);
            if (asset == null && targetAvatar == null) AutoDetect();
            sceneSignature = SceneSignature();
            Render();
            if (asset != null && page != Page.Result) RequestBeforePicture();
        }

        private void OnBecameVisible() => visible = true;
        private void OnBecameInvisible() => visible = false;

        // The scene the window reads: selection, hierarchy, undo, scenes opened or closed, a custom base version applied.
        private void Listen(bool on)
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.hierarchyChanged -= OnSceneChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            CustomBases.Changed -= OnCustomBaseChanged;
            if (!on) return;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.hierarchyChanged += OnSceneChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            CustomBases.Changed += OnCustomBaseChanged;
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

        // Moved only when the layout switches: off a panel, the stage frees its renderer.
        private void PlaceStage()
        {
            if (stage == null || scroll == null) return;
            if (narrow)
            {
                if (stage.parent != scroll.contentContainer || scroll.contentContainer.IndexOf(stage) != 0)
                    scroll.contentContainer.Insert(0, stage);
            }
            else if (stage.parent != stageColumn) stageColumn.Add(stage);
            stage.SetInline(narrow);
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
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
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
            sceneUpdate?.Pause();
            commissionGeneration++;
            commissionListLoading = false;
            Listen(false);
            // A run stopped half way gives back what it took (its avatar, the original base, a part's earlier fit).
            StopExecutionPump();
            isExecuting = false;
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
            // Locked while a run works: its choices are taken, and none made now could reach it.
            content.SetEnabled(!isExecuting);
            PlaceStage();
            ShowPictures();
        }

        // ------------------------------------------------------------------
        // The scene changing under the window
        // ------------------------------------------------------------------

        private void OnSceneChanged() => ScheduleSceneRefresh(false);
        private void OnUndoRedo() => ScheduleSceneRefresh(true);
        private void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, OpenSceneMode openMode) => ScheduleSceneRefresh(true);
        private void OnSceneClosed(UnityEngine.SceneManagement.Scene scene) => ScheduleSceneRefresh(true);
        private void OnCustomBaseChanged(Transform avatarRoot)
        {
            if (avatarRoot == null || targetAvatar == null || avatarRoot == targetAvatar.transform) ScheduleSceneRefresh(true);
        }

        private bool sceneRefreshAll;

        // The window reads the scene again a moment after its last change (a run in progress reads it when done).
        private void ScheduleSceneRefresh(bool all)
        {
            sceneRefreshAll |= all;
            sceneUpdate?.Pause();
            sceneUpdate = rootVisualElement.schedule.Execute(RefreshFromScene).StartingIn(SceneSettle);
        }

        // Choices destroyed meanwhile are let go, the custom base is read again, and the page and the stage follow what
        // changed: an undone refit, a removed piece, another scene. Structure unchanged (an object renamed): nothing is rebuilt.
        private void RefreshFromScene()
        {
            if (content == null) return;
            if (isExecuting) { ScheduleSceneRefresh(false); return; }
            bool all = sceneRefreshAll;
            sceneRefreshAll = false;
            ReFitService.ClearValidationCache();
            if (Gone(targetAvatar)) { targetAvatar = null; myAvatar = null; originalBase = null; }
            if (Gone(myAvatar)) myAvatar = null;
            if (Gone(asset)) asset = null;
            if (Gone(assetFileObject)) assetFileObject = null;
            if (Gone(sourceAvatar)) { sourceAvatar = null; sourceBody = null; }
            if (Gone(sourceBody)) sourceBody = null;
            if (targetAvatar != null) originalBase = ReFitAutoSetup.OriginalBase(targetAvatar);
            if (madeFor == MadeFor.OriginalBase && originalBase == null) madeFor = MadeFor.Target;
            string signature = SceneSignature();
            if (!all && signature == sceneSignature) return;
            sceneSignature = signature;
            if (asset == null && targetAvatar == null) AutoDetect();
            UpdateMode();
            InvalidateChecks();
            if (page == Page.Result) return;
            ClearPictures(true);
            RequestBeforePicture();
            Render();
        }

        // Set once, destroyed since (a reference Unity reports as null).
        private static bool Gone(UnityEngine.Object value) => !ReferenceEquals(value, null) && value == null;

        // What the page and the stage show of the scene: the choices, the avatars, the pieces and how they are worn.
        private string SceneSignature()
        {
            var parts = new List<int>
            {
                Id(asset), Id(targetAvatar), Id(sourceAvatar), Id(assetFileObject),
                asset != null && asset.sharedMesh != null ? asset.sharedMesh.GetInstanceID() : 0,
                asset != null && asset.gameObject.activeInHierarchy ? 1 : 0
            };
            parts.AddRange(ReFitAutoSetup.SceneAvatars().Select(Id));
            if (targetAvatar != null)
                foreach (var renderer in targetAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    parts.Add(renderer.GetInstanceID());
                    parts.Add(renderer.gameObject.activeInHierarchy ? 1 : 0);
                }
            return string.Join(",", parts) + "|" + (originalBase?.Key ?? "");
        }

        private static int Id(UnityEngine.Object value) => value != null ? value.GetInstanceID() : 0;

        // ------------------------------------------------------------------
        // Requests
        // ------------------------------------------------------------------

        /// <summary>The mode follows the choices: another body refits the mesh, shapes are followed when picked.</summary>
        private void UpdateMode()
        {
            bool otherBody = madeFor != MadeFor.Target;
            mode = otherBody ? (blendshapes.Count > 0 ? ReFitMode.MeshAndBlendshape : ReFitMode.MeshToMesh) : ReFitMode.Blendshape;
        }

        /// <summary>The window's choices now.</summary>
        private Inputs Current() => new Inputs
        {
            asset = asset, targetAvatar = targetAvatar, sourceAvatar = sourceAvatar, sourceBody = sourceBody, madeFor = madeFor,
            originalBase = originalBase, blendshapes = new List<string>(blendshapes), mode = mode,
            settings = settings?.Clone() ?? new ReFitSettings(), madeForAvatar = madeForAvatar, refitOutfit = refitOutfit
        };

        /// <summary>Why the ReFit button cannot run yet, or null when it can.</summary>
        private string MissingChoice() => MissingChoice(Current());

        private static string MissingChoice(Inputs run)
        {
            if (run.asset == null) return "Pick the clothing to refit.";
            if (run.targetAvatar == null) return "Pick the avatar it should fit.";
            if (run.madeFor == MadeFor.Other && run.sourceAvatar == null) return "Pick the avatar it was made for.";
            if (run.madeFor == MadeFor.OriginalBase && run.originalBase == null) return "The original base is not available.";
            // Placed by hand, it is fitted out of the body where it clips, shapes or not.
            if (run.mode == ReFitMode.Blendshape && run.blendshapes.Count == 0 && !OnItsAvatar(run, run.asset))
                return "It already fits this avatar: pick body shapes to follow, or the base it was made for.";
            return null;
        }

        private ReFitRequest BuildRequest(SkinnedMeshRenderer part) => RequestFor(Current(), part);

        private static ReFitRequest RequestFor(Inputs run, SkinnedMeshRenderer part)
        {
            var requestSettings = run.settings.Clone();
            requestSettings.captureProjectionDebug = ReFitDebugService.Enabled;
            if (requestSettings.captureProjectionDebug)
                requestSettings.maxProjectionDebugGroups = 0;
            var request = new ReFitRequest
            {
                mode = run.mode,
                assetRenderer = part,
                sourceAvatar = run.mode == ReFitMode.Blendshape ? null : run.sourceAvatar,
                sourceBodyRenderer = run.mode == ReFitMode.Blendshape ? null : run.sourceBody,
                targetAvatar = run.targetAvatar,
                // The body the shapes were picked from: the refit uses the same one.
                targetBodyRenderer = ReFitAutoSetup.Body(run.targetAvatar, part),
                targetBlendshapes = run.mode == ReFitMode.MeshToMesh ? null : new List<string>(run.blendshapes),
                settings = requestSettings
            };
            if (run.madeForAvatar || !PlacedByHandApplies(run, part)) return request;
            // Not made for it: the coverage pass made for clothing from another avatar base.
            requestSettings.coverDifferentBaseBody = true;
            if (OnItsAvatar(run, part))
            {
                // Its mesh is refitted onto the same body, from where it was placed, with the blendshapes. The outfit's other
                // parts keep their side of it; clipping they cover stays as it is.
                request.mode = run.blendshapes.Count > 0 ? ReFitMode.MeshAndBlendshape : ReFitMode.MeshToMesh;
                if (request.mode == ReFitMode.MeshToMesh) request.targetBlendshapes = null;
                request.sourceAvatar = run.targetAvatar;
                request.sourceBodyRenderer = request.targetBodyRenderer;
                requestSettings.coverageKeepsLayerOrder = true;
                request.coverageLayers = OutfitParts(run, part).Where(p => p != part).ToList();
            }
            return request;
        }

        // Placed by hand needs a body to fit it from: another one (a mesh refit), or the avatar it was placed on. A project
        // file following this avatar's shapes has neither.
        private static bool PlacedByHandApplies(Inputs run, SkinnedMeshRenderer part) => run.mode != ReFitMode.Blendshape || OnAvatar(run, part);

        // Worn on the avatar it should fit (in its hierarchy), with no other body chosen to fit it from.
        private static bool OnAvatar(Inputs run, SkinnedMeshRenderer part) => run.mode != ReFitMode.MeshToMesh && run.madeFor != MadeFor.OriginalBase &&
            run.targetAvatar != null && part != null && !EditorUtility.IsPersistent(part) && part.transform.IsChildOf(run.targetAvatar.transform) &&
            (run.sourceAvatar == null || run.sourceAvatar == run.targetAvatar);

        // Refit onto the same avatar from where it was placed by hand.
        private static bool OnItsAvatar(Inputs run, SkinnedMeshRenderer part) => !run.madeForAvatar && OnAvatar(run, part);

        // The outfit a part belongs to: its object directly under the avatar, when that holds other meshes. Body and
        // editor-only renderers excluded.
        private static List<SkinnedMeshRenderer> OutfitParts(Inputs run, SkinnedMeshRenderer part)
        {
            var parts = new List<SkinnedMeshRenderer> { part };
            var avatar = run.targetAvatar;
            if (part == null || avatar == null || !part.transform.IsChildOf(avatar.transform) || part.transform == avatar.transform) return parts;
            var top = part.transform;
            while (top.parent != null && top.parent != avatar.transform) top = top.parent;
            var body = ReFitAutoSetup.Body(avatar);
            foreach (var renderer in top.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (renderer != part && renderer != body && renderer.sharedMesh != null && (renderer.hideFlags & HideFlags.DontSaveInEditor) == 0)
                    parts.Add(renderer);
            return parts;
        }

        // The parts refitted in a run: the asset, or its outfit innermost first.
        private static List<SkinnedMeshRenderer> RunParts(Inputs run)
        {
            if (!OnItsAvatar(run, run.asset) || !run.refitOutfit) return new List<SkinnedMeshRenderer> { run.asset };
            return ReFitOutfit.InnerFirst(ReFitAutoSetup.Body(run.targetAvatar), OutfitParts(run, run.asset));
        }

        // ------------------------------------------------------------------
        // Execution
        // ------------------------------------------------------------------

        private void StartExecution()
        {
            if (isExecuting) return;
            var run = Current();
            if (MissingChoice(run) != null) return;
            try
            {
                isExecuting = true;
                executionProgress = 0f;
                executionStep = "Preparing…";
                lastResult = null;
                outfitResults.Clear();
                executionCoroutines.Clear();
                executionCoroutines.Push(RunExecution(run));
                EditorApplication.update -= PumpExecution;
                EditorApplication.update += PumpExecution;
                Render();
                stage?.SetBusy("Fitting…");
            }
            catch (Exception ex)
            {
                CompleteExecutionWithError(ex);
            }
        }

        private IEnumerator RunExecution(Inputs run)
        {
            // The press shows at once; the pictures and the original base come on the next frame.
            yield return null;
            // Held as the Orbiters tools hold their own refits: the custom base cannot change meanwhile, and the posing preview
            // rests, on the avatar it should fit and on the one it was made for.
            using (ReFitRecordIntegration.Hold(run.targetAvatar))
            using (ReFitRecordIntegration.Hold(run.sourceAvatar != run.targetAvatar ? run.sourceAvatar : null))
            {
                try
                {
                    // Read now it is held (the window's copy follows the scene a moment late): the base the fit is made for.
                    var customBase = CustomBases.Describe(run.targetAvatar.transform);
                    if (run.madeFor == MadeFor.OriginalBase) run.originalBase = customBase != null && customBase.CanFit ? customBase : null;
                    run.baseKey = customBase?.Key;
                    run.baseName = customBase?.Name;
                    if (run.madeFor == MadeFor.OriginalBase)
                    {
                        executionStep = "Opening " + (run.originalBase?.BaseName ?? "the original base") + "…";
                        yield return null;
                        if (!ResolveOriginal(run))
                        {
                            lastResult = new ReFitResult();
                            lastResult.report.Error("original-base-missing",
                                "The original base of " + (run.originalBase?.Name ?? "this custom base") + " could not be opened.");
                            FinishExecution();
                            yield break;
                        }
                    }
                    lastSourceName = run.mode == ReFitMode.Blendshape ? null : run.sourceAvatar != null ? run.sourceAvatar.name : null;
                    if (run.madeFor == MadeFor.OriginalBase && run.originalBase != null) lastSourceName = run.originalBase.BaseName ?? lastSourceName;

                    // A fit starts again from each part's original mesh, never on top of an earlier refit (which would also
                    // become the "original" every tool's reset gives back); shapes added to a refit keep it. Every part first:
                    // the stage compares the result with those originals.
                    var parts = RunParts(run);
                    foreach (var part in parts)
                        if (part != null && RefitsMesh(run, part) && !run.previous.ContainsKey(part))
                        {
                            var previous = ReFitRecordIntegration.StartAgain(part);
                            if (previous != null) run.previous[part] = previous;
                        }
                    CaptureBeforePictures(run);
                    if (parts.Count == 1)
                    {
                        lastRequest = RequestFor(run, run.asset);
                        yield return ReFitService.ExecuteCoroutine(
                            lastRequest,
                            (t, label) =>
                            {
                                executionProgress = Mathf.Clamp01(t);
                                executionStep = label;
                            },
                            result => lastResult = result);
                        Settle(run, lastRequest, lastResult, run.asset);
                    }
                    else
                        for (int i = 0; i < parts.Count; i++)
                        {
                            var part = parts[i];
                            if (part == null) continue;
                            // Built now: the parts refitted before it are its layers as refitted.
                            var request = RequestFor(run, part);
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
                            Settle(run, request, result, part);
                            outfitResults.Add((part, request, result));
                            if (part == run.asset) { lastRequest = request; lastResult = result; }
                        }
                    ReleaseOriginal(run);
                    FinishExecution();
                }
                finally
                {
                    // Stopped half way (the window closed) or failed before a part ran: its earlier fit comes back.
                    foreach (var previous in run.previous.Values) previous.Keep();
                    run.previous.Clear();
                    ReleaseOriginal(run);
                }
            }
        }

        // The part's mesh is refitted (not only given shapes): from the avatar or base it was made for, or placed by hand.
        private static bool RefitsMesh(Inputs run, SkinnedMeshRenderer part) => run.mode != ReFitMode.Blendshape || OnItsAvatar(run, part);

        // After each part: a result replaces the part's earlier fit and is recorded; a failed one gives the earlier fit back.
        private static void Settle(Inputs run, ReFitRequest request, ReFitResult result, SkinnedMeshRenderer part)
        {
            if (part != null && run.previous.TryGetValue(part, out var previous)) run.previous.Remove(part);
            else previous = null;
            if (result == null || !result.success)
            {
                previous?.Keep();
                return;
            }
            previous?.Drop();
            Record(run, request, result, part);
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

        // Applied and saved: from here a failure is the window's, never the refit's.
        private static void Record(Inputs run, ReFitRequest request, ReFitResult result, SkinnedMeshRenderer part)
        {
            if (result == null || !result.success) return;
            try { ReFitRecordIntegration.TryRegister(request, result, run.baseKey, run.baseName); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                result.report.Warn("record-failed", "The refit is applied, but the Orbiters tools could not record it: " + ex.Message);
            }
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
            isExecuting = false;
            executionProgress = 1f;
            executionStep = null;
            gravityPreview = null;
            page = Page.Result;
            // The result is applied and saved: what the page shows of it may fail without making it a failed refit.
            try
            {
                // The gravity preview shows one garment: an outfit run goes to its results.
                if (outfitResults.Count == 0 && lastResult != null && lastResult.success &&
                    ReFitGravityPreviewService.TryCreatePreview(lastResult, lastRequest, out gravityPreview))
                {
                    gravityPreviewWeight = 100f;
                    ReFitGravityPreviewService.ShowPreview(lastResult.sceneRenderer, gravityPreview, gravityPreviewWeight);
                }
            }
            catch (Exception ex)
            {
                gravityPreview = null;
                Debug.LogException(ex);
                lastResult?.report.Warn("gravity-preview", "The gravity preview could not be shown: " + ex.Message);
            }
            try
            {
                // Taken at once, while the avatar is still held at rest like the before picture (a posing preview resumes after).
                if (lastResult != null && lastResult.success && lastResult.sceneRenderer != null && afterScene == null)
                    afterScene = MakeScene(lastResult.sceneRenderer, lastRequest?.targetAvatar ?? targetAvatar);
                stage?.SetBusy(null);
                Render();
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        // The original base comes from the tool that knows the custom base (it may import a file): only for the run.
        private static bool ResolveOriginal(Inputs run)
        {
            ReleaseOriginal(run);
            try { run.resolvedOriginal = run.originalBase?.ResolveOriginal?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[ReFit] Could not open the original base: " + ex.Message); run.resolvedOriginal = null; }
            if (run.resolvedOriginal == null || run.resolvedOriginal.Avatar == null) { ReleaseOriginal(run); return false; }
            run.sourceAvatar = run.resolvedOriginal.Avatar;
            run.sourceBody = run.resolvedOriginal.Body;
            return true;
        }

        private static void ReleaseOriginal(Inputs run)
        {
            if (run?.resolvedOriginal == null) return;
            run.sourceAvatar = null;
            run.sourceBody = null;
            try { run.resolvedOriginal.Dispose(); }
            catch (Exception ex) { Debug.LogWarning("[ReFit] Could not close the original base: " + ex.Message); }
            run.resolvedOriginal = null;
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

        /// <summary>
        /// An element that acts as soon as it is pressed (choices that are cheap and safe to apply at once). The press paints
        /// first (pressed, and <paramref name="shown"/>: the choice's look, such as the card lit); the choice itself, which may
        /// read the avatar and builds the page again, follows a frame later. <paramref name="now"/> acts within the press
        /// (a menu opens under the pointer from its event).
        /// </summary>
        private void OnPress(VisualElement element, Action action, Action shown = null, bool now = false)
        {
            void Press()
            {
                element.AddToClassList("refit-pressed");
                shown?.Invoke();
                element.MarkDirtyRepaint();
                if (now) action();
                else rootVisualElement.schedule.Execute(() => { if (!isExecuting) action(); }).StartingIn(PressPaint);
            }
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !element.enabledInHierarchy || isExecuting) return;
                Press();
                evt.StopPropagation();
            });
            element.RegisterCallback<PointerUpEvent>(_ => element.RemoveFromClassList("refit-pressed"));
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList("refit-pressed"));
            element.focusable = true;
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.Space) && element.enabledInHierarchy && !isExecuting) Press();
            });
        }

        /// <summary>Lights <paramref name="element"/> with <paramref name="className"/>, its siblings off: a choice shown on press.</summary>
        private static Action Light(VisualElement element, string className) => () =>
        {
            if (element.parent != null)
                foreach (var sibling in element.parent.Children()) sibling.RemoveFromClassList(className);
            element.AddToClassList(className);
        };

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
