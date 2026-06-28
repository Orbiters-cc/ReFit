using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The ReFit wizard: walks the user through picking the asset, the avatars and the fit mode,
    /// then runs <see cref="ReFitService"/>. UIToolkit, styled after the MCB look.
    /// </summary>
    public class ReFitWizard : EditorWindow
    {
        /// <summary>One re-fit performed during this editor session (for the reversible list on the first page).</summary>
        public class RefitLogEntry
        {
            public string assetName;
            public string meshAssetPath;
            public SkinnedMeshRenderer sceneRenderer;
            public Mesh originalMesh;
            public Mesh refitMesh;
        }

        /// <summary>All re-fits performed this session (survives reopening the window, cleared on assembly reload).</summary>
        private static readonly List<RefitLogEntry> SessionLog = new List<RefitLogEntry>();
        private static readonly Dictionary<string, Texture2D> CreditTextures = new Dictionary<string, Texture2D>();
        private const string BlackOrbitProfilePath = "Packages/orbiters.refit/blackorbit.png";
        private const string KofiSymbolPath = "Packages/orbiters.refit/kofi_symbol.png";
        private const string KofiUrl = "https://ko-fi.com/blackorbit";
        private const float ClothingDefaultTightness = 0.93f;
        private const float AccessoryDefaultTightness = 0f;

        private enum Step
        {
            AssetLocation,
            AvatarSelect,
            AssetSelect,
            FitChoice,
            TargetInput,
            MyAvatarChoice,
            SourceInput,
            BlendshapeSelect,
            AssetFileInput,
            TargetForFile,
            FileAssetChoice,
            Tightness,
            Summary,
            Settings,
            GravityPreview,
            Result
        }

        // wizard state
        private Step current = Step.AssetLocation;
        private readonly Stack<Step> history = new Stack<Step>();
        private GameObject myAvatar;
        private SkinnedMeshRenderer asset;
        private GameObject assetFileObject;
        private GameObject targetAvatar;
        private GameObject sourceAvatar;
        private string blendshape;
        private ReFitMode mode = ReFitMode.MeshToMesh;
        private ReFitSettings settings = new ReFitSettings();
        private ReFitReport validateReport;
        private bool validateScheduled;
        private ReFitResult lastResult;
        private ReFitRequest lastRequest;
        private ReFitGravityPreview gravityPreview;
        private float gravityPreviewWeight = 100f;
        private bool clearanceAdvanced;
        private bool clearanceTightnessKnown = true;
        private float clearanceTightnessPreset = 0.5f;
        private bool clearanceTightnessUserChosen;
        private bool standardTightnessInitialized;

        private ScrollView content;
        private Button backButton;
        private Button settingsButton;

        [MenuItem("Tools/Orbiters/ReFit")]
        public static void Open()
        {
            var window = GetWindow<ReFitWizard>();
            window.titleContent = new GUIContent("ReFit");
            window.minSize = new Vector2(620f, 560f);
            window.Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.refit/Editor/UI/refit.uss");
            if (styleSheet != null) root.styleSheets.Add(styleSheet);
            root.AddToClassList("refit-root");

            var header = new VisualElement();
            header.AddToClassList("refit-header");
            header.Add(ReFitLogo.Create(1.05f));
            var title = new Label("ReFit");
            title.AddToClassList("refit-title");
            header.Add(title);
            root.Add(header);

            var body = new VisualElement();
            body.AddToClassList("refit-body");
            body.style.flexGrow = 1;
            root.Add(body);

            var nav = new VisualElement();
            nav.AddToClassList("refit-nav");
            backButton = new Button(GoBack) { text = "< Back" };
            backButton.AddToClassList("refit-back");
            nav.Add(backButton);
            var navSpacer = new VisualElement();
            navSpacer.style.flexGrow = 1f;
            nav.Add(navSpacer);
            settingsButton = new Button(() =>
            {
                if (current != Step.Settings) Go(Step.Settings);
            })
            { text = "Settings" };
            settingsButton.AddToClassList("refit-back");
            nav.Add(settingsButton);
            body.Add(nav);

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("refit-scroll");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(content);
            body.Add(CreateFooterCredit());

            Render();
        }

        // ------------------------------------------------------------------
        // Navigation
        // ------------------------------------------------------------------

        private void Go(Step next)
        {
            history.Push(current);
            current = next;
            if (next == Step.Summary) { validateReport = null; validateScheduled = false; }
            Render();
        }

        private void GoBack()
        {
            if (history.Count == 0) return;
            if (current == Step.GravityPreview)
            {
                ReFitGravityPreviewService.ClearPreview(gravityPreview);
                gravityPreview = null;
            }
            current = history.Pop();
            Render();
        }

        private void Restart()
        {
            history.Clear();
            ReFitGravityPreviewService.ClearPreview(gravityPreview);
            current = Step.AssetLocation;
            myAvatar = null; asset = null; assetFileObject = null;
            targetAvatar = null; sourceAvatar = null; blendshape = null;
            mode = ReFitMode.MeshToMesh;
            ResetTightnessChoice();
            validateReport = null; lastResult = null;
            lastRequest = null; gravityPreview = null;
            Render();
        }

        private void OnDisable()
        {
            ReFitGravityPreviewService.ClearPreview(gravityPreview);
        }

        private void Render()
        {
            if (content == null) return;
            content.Clear();
            backButton.style.display = history.Count > 0 && current != Step.Result ? DisplayStyle.Flex : DisplayStyle.None;
            settingsButton.SetEnabled(current != Step.Settings);
            switch (current)
            {
                case Step.AssetLocation: BuildAssetLocation(); break;
                case Step.AvatarSelect: BuildAvatarSelect(); break;
                case Step.AssetSelect: BuildAssetSelect(); break;
                case Step.FitChoice: BuildFitChoice(); break;
                case Step.TargetInput: BuildTargetInput(Step.Tightness); break;
                case Step.MyAvatarChoice: BuildMyAvatarChoice(); break;
                case Step.SourceInput: BuildSourceInput(); break;
                case Step.BlendshapeSelect: BuildBlendshapeSelect(); break;
                case Step.AssetFileInput: BuildAssetFileInput(); break;
                case Step.TargetForFile: BuildTargetInput(Step.FileAssetChoice); break;
                case Step.FileAssetChoice: BuildFileAssetChoice(); break;
                case Step.Tightness: BuildTightness(); break;
                case Step.Summary: BuildSummary(); break;
                case Step.Settings: BuildToolSettings(); break;
                case Step.GravityPreview: BuildGravityPreview(); break;
                case Step.Result: BuildResult(); break;
            }
        }

        // ------------------------------------------------------------------
        // Steps
        // ------------------------------------------------------------------

        private void BuildAssetLocation()
        {
            Question("Where is your asset ?");
            var cards = Cards();
            cards.Add(Card("On my avatar", null, () => Go(Step.AvatarSelect)));
            cards.Add(Card("In my project files", null, () => Go(Step.AssetFileInput)));
            BuildSessionLog();
        }

        /// <summary>Lists every asset re-fitted this session, each with a Revert button that restores its original mesh.</summary>
        private void BuildSessionLog()
        {
            SessionLog.RemoveAll(e => e == null || e.sceneRenderer == null);
            if (SessionLog.Count == 0) return;

            var section = new Label("Re-fitted this session");
            section.AddToClassList("refit-section");
            section.style.marginTop = 28;
            content.Add(section);

            for (int i = SessionLog.Count - 1; i >= 0; i--)
            {
                var entry = SessionLog[i];
                bool active = entry.sceneRenderer != null && entry.refitMesh != null && entry.sceneRenderer.sharedMesh == entry.refitMesh;

                var row = new VisualElement();
                row.AddToClassList("refit-summary-row");
                row.style.alignItems = Align.Center;
                row.style.marginTop = 4;

                var name = new Label(entry.assetName + (active ? string.Empty : "  (reverted)"));
                name.AddToClassList("refit-summary-value");
                name.style.flexGrow = 1;
                row.Add(name);

                var captured = entry;
                if (active && entry.originalMesh != null)
                {
                    var revert = new Button(() => RevertEntry(captured)) { text = "Revert" };
                    revert.AddToClassList("refit-back");
                    row.Add(revert);
                }
                var ping = new Button(() =>
                {
                    if (captured.sceneRenderer != null)
                    {
                        Selection.activeGameObject = captured.sceneRenderer.gameObject;
                        EditorGUIUtility.PingObject(captured.sceneRenderer.gameObject);
                    }
                }) { text = "Select" };
                ping.AddToClassList("refit-back");
                ping.style.marginLeft = 6;
                row.Add(ping);

                content.Add(row);
            }
        }

        private void RevertEntry(RefitLogEntry entry)
        {
            if (entry?.sceneRenderer != null && entry.originalMesh != null)
            {
                Undo.RecordObject(entry.sceneRenderer, "ReFit revert");
                entry.sceneRenderer.sharedMesh = entry.originalMesh;
                EditorUtility.SetDirty(entry.sceneRenderer);
            }
            Render();
        }

        private void BuildAvatarSelect()
        {
            Question("Select your avatar");
            var avatars = FindSceneAvatars();
            if (avatars.Count == 0)
                Help("No avatar found in the open scene(s). Drop one below.");
            var cards = Cards();
            foreach (var avatar in avatars)
            {
                var a = avatar;
                cards.Add(Card(a.name, DescribeAvatar(a), () => { myAvatar = a; Go(Step.AssetSelect); }, "refit-card--list"));
            }
            var field = new ObjectField("Or pick it manually") { objectType = typeof(GameObject), allowSceneObjects = true };
            field.AddToClassList("refit-field");
            field.RegisterValueChangedCallback(e =>
            {
                if (e.newValue is GameObject go) { myAvatar = go; Go(Step.AssetSelect); }
            });
            content.Add(field);
        }

        private void BuildAssetSelect()
        {
            Question($"Select the asset on '{myAvatar.name}' to re-fit");
            var body = AutoDetectBody(myAvatar, null);
            var cards = Cards();
            foreach (var smr in myAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                var s = smr;
                var sub = $"{smr.sharedMesh.vertexCount} vertices" + (smr == body ? "  -  looks like the body" : "");
                cards.Add(Card(s.name, sub, () => { asset = s; ResetTightnessChoice(); Go(Step.FitChoice); }, "refit-card--list"));
            }
        }

        private void BuildFitChoice()
        {
            Question("Do you want to make it fit your avatar or another one ?");
            var cards = Cards();
            cards.Add(Card("To my avatar", "The asset stays on this avatar", () => { targetAvatar = myAvatar; ResetTightnessChoice(); Go(Step.MyAvatarChoice); }));
            cards.Add(Card("To another one", "Move and fit the asset onto a different avatar", () =>
            {
                sourceAvatar = myAvatar;
                mode = ReFitMode.MeshToMesh;
                Go(Step.TargetInput);
            }));
        }

        private void BuildTargetInput(Step next)
        {
            Question("Which avatar should it fit ?");
            Help("Scene object or prefab / FBX from your project files.");
            var field = new ObjectField("Destination avatar") { objectType = typeof(GameObject), allowSceneObjects = true, value = targetAvatar };
            field.AddToClassList("refit-field");
            content.Add(field);
            var nextButton = Primary("Next", () =>
            {
                targetAvatar = (GameObject)field.value;
                ResetTightnessChoice();
                Go(next);
            });
            nextButton.SetEnabled(targetAvatar != null);
            field.RegisterValueChangedCallback(e => nextButton.SetEnabled(e.newValue != null));
        }

        private void BuildMyAvatarChoice()
        {
            Question("What should it adapt to ?");
            var cards = Cards();
            cards.Add(Card("The asset was made for another avatar base",
                "Your avatar is a different or modified base; fit the asset to its body",
                () => { mode = ReFitMode.MeshToMesh; Go(Step.SourceInput); }));
            cards.Add(Card("Make it fit a blendshape",
                "Follow a body blendshape of your avatar (it already fits the base body)",
                () => Go(Step.BlendshapeSelect)));
        }

        private void BuildSourceInput()
        {
            Question("Which avatar base was the asset made for ?");
            Help("Scene object or prefab / FBX from your project files.");
            var field = new ObjectField("Source avatar base") { objectType = typeof(GameObject), allowSceneObjects = true, value = sourceAvatar };
            field.AddToClassList("refit-field");
            content.Add(field);
            var nextButton = Primary("Next", () =>
            {
                sourceAvatar = (GameObject)field.value;
                mode = ReFitMode.MeshToMesh;
                Go(Step.Tightness);
            });
            nextButton.SetEnabled(sourceAvatar != null);
            field.RegisterValueChangedCallback(e => nextButton.SetEnabled(e.newValue != null));
        }

        private void BuildBlendshapeSelect()
        {
            Question("Which blendshape should the asset follow ?");
            var body = AutoDetectBody(targetAvatar, asset);
            if (body == null || body.sharedMesh == null)
            {
                Help("No body renderer with blendshapes was found on the target avatar.");
                return;
            }
            Help($"Blendshapes of '{body.name}'.");

            var names = new List<string>();
            for (int i = 0; i < body.sharedMesh.blendShapeCount; i++) names.Add(body.sharedMesh.GetBlendShapeName(i));
            if (names.Count == 0)
            {
                Help("The target body has no blendshapes.");
                return;
            }
            int defaultIndex = Mathf.Max(0, names.IndexOf(blendshape));
            var dropdown = new DropdownField("Blendshape", names, defaultIndex);
            dropdown.AddToClassList("refit-field");
            content.Add(dropdown);

            Help("Optional: if the asset was also made for another avatar base, set it below to re-fit the mesh at the same time.");
            var sourceField = new ObjectField("Source avatar base (optional)") { objectType = typeof(GameObject), allowSceneObjects = true, value = sourceAvatar };
            sourceField.AddToClassList("refit-field");
            content.Add(sourceField);

            Primary("Next", () =>
            {
                blendshape = dropdown.value;
                sourceAvatar = (GameObject)sourceField.value;
                mode = sourceAvatar != null ? ReFitMode.MeshAndBlendshape : ReFitMode.Blendshape;
                Go(Step.Tightness);
            });
        }

        private void BuildAssetFileInput()
        {
            Question("Pick the asset in your project files");
            Help("A prefab or FBX containing the clothing / accessory (skinned mesh).");
            var field = new ObjectField("Asset file") { objectType = typeof(GameObject), allowSceneObjects = true, value = assetFileObject };
            field.AddToClassList("refit-field");
            content.Add(field);

            var list = new VisualElement();
            content.Add(list);

            void RefreshList()
            {
                list.Clear();
                asset = null;
                if (assetFileObject == null) return;
                var renderers = assetFileObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (renderers.Length == 0)
                {
                    var help = new Label("No skinned mesh found inside this object.");
                    help.AddToClassList("refit-help");
                    list.Add(help);
                    return;
                }
                if (renderers.Length == 1)
                {
                    asset = renderers[0];
                    ResetTightnessChoice();
                    Go(Step.TargetForFile);
                    return;
                }
                var section = new Label("Several meshes found - pick the one to re-fit:");
                section.AddToClassList("refit-section");
                list.Add(section);
                var cards = new VisualElement();
                cards.AddToClassList("refit-cards");
                list.Add(cards);
                foreach (var smr in renderers)
                {
                    if (smr.sharedMesh == null) continue;
                    var s = smr;
                    cards.Add(Card(s.name, $"{s.sharedMesh.vertexCount} vertices", () => { asset = s; ResetTightnessChoice(); Go(Step.TargetForFile); }, "refit-card--list"));
                }
            }

            field.RegisterValueChangedCallback(e =>
            {
                assetFileObject = e.newValue as GameObject;
                RefreshList();
            });
            RefreshList();
        }

        private void BuildFileAssetChoice()
        {
            Question("How should it be fitted ?");
            var cards = Cards();
            cards.Add(Card("It was made for another avatar base",
                "Fit its mesh onto the destination avatar's body",
                () => { mode = ReFitMode.MeshToMesh; Go(Step.SourceInput); }));
            cards.Add(Card("Make it fit a blendshape",
                "It already fits this avatar; follow one of its body blendshapes",
                () => Go(Step.BlendshapeSelect)));
        }

        private void BuildTightness()
        {
            EnsureStandardTightnessDefault();

            Question("How tight should the asset be around the growing parts ?");

            var layout = new VisualElement();
            layout.AddToClassList("refit-tightness-layout");
            content.Add(layout);

            layout.Add(BuildTightnessOption("Not tight", false, "Best for accessories"));

            var sliderColumn = new VisualElement();
            sliderColumn.AddToClassList("refit-tightness-slider-column");
            var slider = new Slider(" ", 0f, 1f)
            {
                value = clearanceTightnessKnown ? clearanceTightnessPreset : 0.5f
            };
            slider.AddToClassList("refit-tightness-slider");
            slider.RegisterValueChangedCallback(e =>
            {
                ApplyClearanceTightnessPreset(e.newValue);
            });
            sliderColumn.Add(slider);
            layout.Add(sliderColumn);

            layout.Add(BuildTightnessOption("Tight", true, "Best for clothing"));

            if (!clearanceTightnessKnown)
                Help("Custom advanced setup is active. Move the slider to replace it with a tightness preset.");

            var footer = new VisualElement();
            footer.AddToClassList("refit-tightness-footer");
            content.Add(footer);

            var next = new Button(() => Go(Step.Summary)) { text = "Next" };
            next.AddToClassList("refit-primary");
            next.AddToClassList("refit-tightness-next");
            footer.Add(next);
        }

        private VisualElement BuildTightnessOption(string title, bool tight, string caption)
        {
            var column = new VisualElement();
            column.AddToClassList("refit-tightness-option");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("refit-tightness-title");
            column.Add(titleLabel);

            column.Add(new TightnessIllustration(tight));

            var captionLabel = new Label(caption);
            captionLabel.AddToClassList("refit-tightness-caption");
            column.Add(captionLabel);

            return column;
        }

        private void EnsureStandardTightnessDefault()
        {
            if (standardTightnessInitialized)
                return;

            standardTightnessInitialized = true;
            if (clearanceTightnessUserChosen || !clearanceTightnessKnown)
                return;

            var candidate = ReFitGravityRelaxation.DetectCandidate(asset, targetAvatar);
            ApplyClearanceTightnessPreset(candidate != null && candidate.isCandidate
                ? ClothingDefaultTightness
                : AccessoryDefaultTightness, false);
        }

        // ------------------------------------------------------------------
        // Summary & execution
        // ------------------------------------------------------------------

        private void BuildSummary()
        {
            Question("Ready to ReFit");

            SummaryRow("Asset", asset != null ? asset.name : "-");
            SummaryRow("Mode", ModeLabel());
            if (mode != ReFitMode.Blendshape) SummaryRow("Made for", sourceAvatar != null ? sourceAvatar.name : "-");
            SummaryRow("Fit to", targetAvatar != null ? targetAvatar.name : "-");
            if (mode != ReFitMode.MeshToMesh) SummaryRow("Blendshape", blendshape ?? "-");
            SummaryRow("Tightness", clearanceTightnessKnown
                ? $"{Mathf.RoundToInt(clearanceTightnessPreset * 100f)}%"
                : "Custom");

            // Advanced settings
            var advanced = new Foldout { text = "Advanced options", value = false };
            advanced.AddToClassList("refit-field");
            content.Add(advanced);
            BuildSettings(advanced);

            // Validation
            var checksSection = new Label("Checks");
            checksSection.AddToClassList("refit-section");
            content.Add(checksSection);
            var messages = new VisualElement();
            content.Add(messages);

            void ShowReport()
            {
                messages.Clear();
                if (validateReport == null)
                {
                    var checking = new Label("Checking the setup...");
                    checking.AddToClassList("refit-help");
                    messages.Add(checking);
                    return;
                }
                bool any = false;
                foreach (var m in validateReport.messages)
                {
                    if (m.severity == ReFitSeverity.Info && (m.code == "body-autodetect" || m.code == "proportions-ok")) { AddMessage(messages, m); any = true; continue; }
                    if (m.severity != ReFitSeverity.Info) { AddMessage(messages, m); any = true; }
                }
                if (!any)
                {
                    var ok = new Label("All good.");
                    ok.AddToClassList("refit-help");
                    messages.Add(ok);
                }
            }
            ShowReport();

            if (!validateScheduled && validateReport == null)
            {
                validateScheduled = true;
                content.schedule.Execute(() =>
                {
                    validateReport = ReFitService.Validate(BuildRequest());
                    ShowReport();
                }).StartingIn(50);
            }

            var run = Primary("ReFit", Execute);
            run.SetEnabled(asset != null && targetAvatar != null);
            Help("Warnings never block the operation - if the proportions differ because bones were intentionally moved, you can proceed.");
        }

        private void BuildSettings(VisualElement parent)
        {
            var defaultSettings = new ReFitSettings();

            var name = new TextField("Blendshape name") { value = settings.blendshapeName };
            name.RegisterValueChangedCallback(e => settings.blendshapeName = e.newValue);
            parent.Add(name);

            var maxDist = new FloatField("Max projection distance (m)") { value = settings.maxProjectionDistance };
            maxDist.RegisterValueChangedCallback(e => settings.maxProjectionDistance = Mathf.Max(0.001f, e.newValue));
            parent.Add(maxDist);

            var falloff = new FloatField("Full-effect distance (m)") { value = settings.falloffStartDistance };
            falloff.RegisterValueChangedCallback(e => settings.falloffStartDistance = Mathf.Max(0f, e.newValue));
            parent.Add(falloff);

            AddIntFieldWithReset(parent, "Primary refit smoothing iterations", 0, 10,
                settings.primarySmoothingIterations,
                ReFitSettings.DefaultPrimarySmoothingIterations,
                value => settings.primarySmoothingIterations = value);

            AddFloatFieldWithReset(parent, "Primary refit smoothing strength", 0f, 1f,
                settings.primarySmoothingStrength,
                ReFitSettings.DefaultPrimarySmoothingStrength,
                value => settings.primarySmoothingStrength = value);

            AddIntFieldWithReset(parent, "Transferred blendshape smoothing iterations", 0, 10,
                settings.transferredBlendshapeSmoothingIterations,
                ReFitSettings.DefaultTransferredBlendshapeSmoothingIterations,
                value => settings.transferredBlendshapeSmoothingIterations = value);

            AddFloatFieldWithReset(parent, "Transferred blendshape smoothing strength", 0f, 1f,
                settings.transferredBlendshapeSmoothingStrength,
                ReFitSettings.DefaultTransferredBlendshapeSmoothingStrength,
                value => settings.transferredBlendshapeSmoothingStrength = value);

            var clearanceSection = new Label("Clearance correction");
            clearanceSection.AddToClassList("refit-section");
            clearanceSection.style.marginTop = 14;
            parent.Add(clearanceSection);

            var clearance = new Toggle("Preserve clothing clearance") { value = settings.enableClearanceCorrection };
            clearance.AddToClassList("refit-field");
            clearance.RegisterValueChangedCallback(e => settings.enableClearanceCorrection = e.newValue);
            parent.Add(clearance);

            var advanced = new Toggle("Advanced") { value = clearanceAdvanced };
            advanced.AddToClassList("refit-field");
            parent.Add(advanced);

            var clearanceControls = new VisualElement();
            parent.Add(clearanceControls);

            void RefreshClearanceControls()
            {
                clearanceControls.Clear();
                BuildClearanceControls(clearanceControls, defaultSettings);
            }

            advanced.RegisterValueChangedCallback(e =>
            {
                clearanceAdvanced = e.newValue;
                RefreshClearanceControls();
            });
            RefreshClearanceControls();

            var offset = new EnumField("Offset mode", settings.offsetMode);
            offset.RegisterValueChangedCallback(e => settings.offsetMode = (OffsetMode)e.newValue);
            parent.Add(offset);

            var normalFilter = new Toggle("Filter by normals") { value = settings.filterByNormal };
            normalFilter.RegisterValueChangedCallback(e => settings.filterByNormal = e.newValue);
            parent.Add(normalFilter);

            var regionFilter = new Toggle("Filter by body region") { value = settings.filterByBoneRegion };
            regionFilter.RegisterValueChangedCallback(e => settings.filterByBoneRegion = e.newValue);
            parent.Add(regionFilter);

            if (mode != ReFitMode.Blendshape)
            {
                var replace = new Toggle("Replace armature with target's") { value = settings.replaceArmature };
                replace.RegisterValueChangedCallback(e => settings.replaceArmature = e.newValue);
                parent.Add(replace);

                var weights = new Toggle("Transfer skin weights from target") { value = settings.transferWeights };
                weights.RegisterValueChangedCallback(e => settings.transferWeights = e.newValue);
                parent.Add(weights);

                var keepExtra = new Toggle("Keep extra bones (physics, props)") { value = settings.keepExtraBoneVertices };
                keepExtra.RegisterValueChangedCallback(e => settings.keepExtraBoneVertices = e.newValue);
                parent.Add(keepExtra);
            }

            var normals = new Toggle("Recalculate shape normals") { value = settings.recalculateNormalDeltas };
            normals.RegisterValueChangedCallback(e => settings.recalculateNormalDeltas = e.newValue);
            parent.Add(normals);
        }

        private void BuildClearanceControls(VisualElement parent, ReFitSettings defaultSettings)
        {
            if (!clearanceAdvanced)
            {
                BuildClearanceTightnessSlider(parent);
                return;
            }

            AddFloatFieldWithReset(parent, "Expanded-area tightening strength", 0f, 1f,
                1f - Mathf.Clamp01(settings.clearanceTightnessFactor),
                1f - Mathf.Clamp01(defaultSettings.clearanceTightnessFactor),
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceTightnessFactor = 1f - Mathf.Clamp01(value);
                });

            AddFloatFieldWithReset(parent, "Minimum safety distance (m)", 0f, 0.1f,
                settings.clearanceMinimumSafetyDistance,
                defaultSettings.clearanceMinimumSafetyDistance,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMinimumSafetyDistance = value;
                });

            AddFloatFieldWithReset(parent, "Max outward safety correction (m)", 0f, 0.25f,
                settings.clearanceMaxOutwardCorrection,
                defaultSettings.clearanceMaxOutwardCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxOutwardCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Max surface guard correction (m)", 0f, 0.1f,
                settings.clearanceMaxSurfaceGuardCorrection,
                defaultSettings.clearanceMaxSurfaceGuardCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxSurfaceGuardCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Surface guard trigger depth (m)", 0f, 0.02f,
                settings.clearanceSurfaceGuardTriggerDistance,
                defaultSettings.clearanceSurfaceGuardTriggerDistance,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSurfaceGuardTriggerDistance = value;
                });

            AddFloatFieldWithReset(parent, "Max inward tightening (m)", 0f, 0.25f,
                settings.clearanceMaxInwardCorrection,
                defaultSettings.clearanceMaxInwardCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxInwardCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Inward tightening strength", 0f, 1f,
                settings.clearanceInwardStrength,
                defaultSettings.clearanceInwardStrength,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceInwardStrength = value;
                });

            AddFloatFieldWithReset(parent, "Tightening starts at expansion (m)", 0f, 0.2f,
                settings.clearanceExpansionStart,
                defaultSettings.clearanceExpansionStart,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceExpansionStart = value;
                    if (settings.clearanceExpansionFull < value)
                        settings.clearanceExpansionFull = value;
                });

            AddFloatFieldWithReset(parent, "Full tightening at expansion (m)", 0f, 0.3f,
                settings.clearanceExpansionFull,
                defaultSettings.clearanceExpansionFull,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceExpansionFull = Mathf.Max(settings.clearanceExpansionStart, value);
                });

            AddIntFieldWithReset(parent, "Correction smoothing iterations", 0, 10,
                settings.clearanceSmoothingIterations,
                defaultSettings.clearanceSmoothingIterations,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSmoothingIterations = value;
                });

            AddFloatFieldWithReset(parent, "Correction smoothing strength", 0f, 1f,
                settings.clearanceSmoothingStrength,
                defaultSettings.clearanceSmoothingStrength,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSmoothingStrength = value;
                });

            AddIntFieldWithReset(parent, "Surface guard iterations", 0, 12,
                settings.clearanceSurfaceGuardIterations,
                defaultSettings.clearanceSurfaceGuardIterations,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSurfaceGuardIterations = value;
                });

            AddFloatFieldWithReset(parent, "Surface guard strength", 0f, 1f,
                settings.clearanceSurfaceGuardStrength,
                defaultSettings.clearanceSurfaceGuardStrength,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSurfaceGuardStrength = value;
                });

            AddIntFieldWithReset(parent, "Surface guard edge samples", 1, 3,
                settings.clearanceSurfaceGuardEdgeSamples,
                defaultSettings.clearanceSurfaceGuardEdgeSamples,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceSurfaceGuardEdgeSamples = value;
                });

            AddFloatFieldWithReset(parent, "Max primary total correction (m)", 0f, 0.2f,
                settings.clearanceMaxPrimaryTotalCorrection,
                defaultSettings.clearanceMaxPrimaryTotalCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxPrimaryTotalCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Max transferred total correction (m)", 0f, 0.2f,
                settings.clearanceMaxTransferredTotalCorrection,
                defaultSettings.clearanceMaxTransferredTotalCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxTransferredTotalCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Transferred inward scale", 0f, 1f,
                settings.clearanceTransferredInwardScale,
                defaultSettings.clearanceTransferredInwardScale,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceTransferredInwardScale = value;
                });

            AddFloatFieldWithReset(parent, "Open boundary correction scale", 0f, 1f,
                settings.clearanceOpenBoundaryCorrectionScale,
                defaultSettings.clearanceOpenBoundaryCorrectionScale,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceOpenBoundaryCorrectionScale = value;
                });

            AddFloatFieldWithReset(parent, "Low-confidence correction scale", 0f, 1f,
                settings.clearanceLowConfidenceCorrectionScale,
                defaultSettings.clearanceLowConfidenceCorrectionScale,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceLowConfidenceCorrectionScale = value;
                });

            AddFloatFieldWithReset(parent, "Upper-body hem follow scale", 0f, 1f,
                settings.upperBodyGarmentHemFollowScale,
                defaultSettings.upperBodyGarmentHemFollowScale,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.upperBodyGarmentHemFollowScale = value;
                });

            var islandPropagation = new Toggle("Propagate disconnected island corrections")
            {
                value = settings.clearancePropagateDisconnectedIslands
            };
            islandPropagation.RegisterValueChangedCallback(e =>
            {
                MarkClearanceTightnessCustom();
                settings.clearancePropagateDisconnectedIslands = e.newValue;
            });
            parent.Add(islandPropagation);

            AddFloatFieldWithReset(parent, "Island follow strength", 0f, 1f,
                settings.clearanceIslandPropagationStrength,
                defaultSettings.clearanceIslandPropagationStrength,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceIslandPropagationStrength = value;
                });

            AddFloatFieldWithReset(parent, "Island follow search distance (m)", 0f, 0.25f,
                settings.clearanceIslandPropagationSearchDistance,
                defaultSettings.clearanceIslandPropagationSearchDistance,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceIslandPropagationSearchDistance = value;
                });

            AddFloatFieldWithReset(parent, "Max island follow correction (m)", 0f, 0.1f,
                settings.clearanceMaxIslandPropagationCorrection,
                defaultSettings.clearanceMaxIslandPropagationCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceMaxIslandPropagationCorrection = value;
                });

            AddFloatFieldWithReset(parent, "Island follow donor threshold (m)", 0f, 0.02f,
                settings.clearanceIslandPropagationMinDonorCorrection,
                defaultSettings.clearanceIslandPropagationMinDonorCorrection,
                value =>
                {
                    MarkClearanceTightnessCustom();
                    settings.clearanceIslandPropagationMinDonorCorrection = value;
                });
        }

        private void BuildClearanceTightnessSlider(VisualElement parent)
        {
            var slider = new Slider(clearanceTightnessKnown ? "Tightness" : "Tightness (custom)", 0f, 1f)
            {
                value = clearanceTightnessKnown ? clearanceTightnessPreset : 0.5f
            };
            slider.AddToClassList("refit-field");
            slider.RegisterValueChangedCallback(e =>
            {
                ApplyClearanceTightnessPreset(e.newValue);
                slider.label = "Tightness";
            });
            parent.Add(slider);

            if (!clearanceTightnessKnown)
                AddInlineHelp(parent, "Custom advanced setup is active. Move the slider to replace it with a tightness preset.");
        }

        private void ApplyClearanceTightnessPreset(float value, bool userChosen = true)
        {
            clearanceTightnessPreset = Mathf.Clamp01(value);
            clearanceTightnessKnown = true;
            if (userChosen)
                clearanceTightnessUserChosen = true;

            settings.clearanceTightnessFactor = LerpPreset(clearanceTightnessPreset, 0.85f, 0.5f, 0.08f);
            settings.clearanceMinimumSafetyDistance = LerpPreset(clearanceTightnessPreset, 0.006f, 0.003f, 0.002f);
            settings.clearanceMaxOutwardCorrection = LerpPreset(clearanceTightnessPreset, 0.04f, 0.04f, 0.12f);
            settings.clearanceMaxSurfaceGuardCorrection = LerpPreset(clearanceTightnessPreset, 0.001f, 0.003f, 0.08f);
            settings.clearanceSurfaceGuardTriggerDistance = LerpPreset(clearanceTightnessPreset, 0.003f, 0.0015f, 0.00025f);
            settings.clearanceMaxInwardCorrection = LerpPreset(clearanceTightnessPreset, 0.005f, 0.015f, 0.18f);
            settings.clearanceInwardStrength = LerpPreset(clearanceTightnessPreset, 0.2f, 0.65f, 0.82f);
            settings.clearanceExpansionStart = LerpPreset(clearanceTightnessPreset, 0.035f, 0.01f, 0.002f);
            settings.clearanceExpansionFull = LerpPreset(clearanceTightnessPreset, 0.12f, 0.06f, 0.025f);
            settings.clearanceSmoothingIterations = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 3f, 2f, 1f));
            settings.clearanceSmoothingStrength = LerpPreset(clearanceTightnessPreset, 0.65f, 0.5f, 0.35f);
            settings.clearanceSurfaceGuardIterations = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 0f, 1f, 6f));
            settings.clearanceSurfaceGuardStrength = LerpPreset(clearanceTightnessPreset, 0.25f, 0.45f, 1f);
            settings.clearanceSurfaceGuardEdgeSamples = Mathf.RoundToInt(LerpPreset(clearanceTightnessPreset, 1f, 1f, 3f));
            settings.clearanceMaxPrimaryTotalCorrection = LerpPreset(clearanceTightnessPreset, 0.025f, 0.06f, 0.06f);
            settings.clearanceMaxTransferredTotalCorrection = LerpPreset(clearanceTightnessPreset, 0.015f, 0.035f, 0.045f);
            settings.clearanceTransferredInwardScale = LerpPreset(clearanceTightnessPreset, 0.45f, 0.75f, 0.92f);
            settings.clearanceOpenBoundaryCorrectionScale = LerpPreset(clearanceTightnessPreset, 0.18f, 0.1f, 0.06f);
            settings.clearanceLowConfidenceCorrectionScale = LerpPreset(clearanceTightnessPreset, 0.55f, 0.4f, 0.28f);
            settings.upperBodyGarmentHemFollowScale = LerpPreset(clearanceTightnessPreset, 0.45f, 0.3f, 0.18f);
            settings.clearancePropagateDisconnectedIslands = true;
            settings.clearanceIslandPropagationStrength = LerpPreset(clearanceTightnessPreset, 0.25f, 0.6f, 0.85f);
            settings.clearanceIslandPropagationSearchDistance = LerpPreset(clearanceTightnessPreset, 0.05f, 0.08f, 0.12f);
            settings.clearanceMaxIslandPropagationCorrection = LerpPreset(clearanceTightnessPreset, 0.008f, 0.025f, 0.045f);
            settings.clearanceIslandPropagationMinDonorCorrection = 0.001f;
            settings.clearanceOutwardStrength = 1f;
        }

        private void MarkClearanceTightnessCustom()
        {
            clearanceTightnessKnown = false;
            clearanceTightnessUserChosen = true;
        }

        private void ResetTightnessChoice()
        {
            standardTightnessInitialized = false;
            clearanceTightnessUserChosen = false;
            clearanceTightnessKnown = true;
            clearanceTightnessPreset = 0.5f;
        }

        private static float SmoothPreset(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }

        private static float LerpPreset(float value, float loose, float middle, float tight)
        {
            value = Mathf.Clamp01(value);
            if (value <= 0.5f)
                return Mathf.Lerp(loose, middle, SmoothPreset(value * 2f));
            return Mathf.Lerp(middle, tight, SmoothPreset((value - 0.5f) * 2f));
        }

        private static void AddInlineHelp(VisualElement parent, string text)
        {
            var label = new Label(text);
            label.AddToClassList("refit-help");
            parent.Add(label);
        }

        private static void AddIntFieldWithReset(VisualElement parent, string label, int min, int max,
            int currentValue, int defaultValue, Action<int> apply)
        {
            var row = CreateResetFieldRow();

            var field = new IntegerField(label) { value = Mathf.Clamp(currentValue, min, max) };
            PrepareResetField(field);
            field.RegisterValueChangedCallback(e =>
            {
                var value = Mathf.Clamp(e.newValue, min, max);
                if (value != e.newValue)
                    field.SetValueWithoutNotify(value);
                apply(value);
            });
            row.Add(field);

            var reset = new Button(() =>
            {
                var value = Mathf.Clamp(defaultValue, min, max);
                field.SetValueWithoutNotify(value);
                apply(value);
            })
            { text = "Reset" };
            PrepareResetButton(reset);
            row.Add(reset);

            parent.Add(row);
        }

        private static void AddFloatFieldWithReset(VisualElement parent, string label, float min, float max,
            float currentValue, float defaultValue, Action<float> apply)
        {
            var row = CreateResetFieldRow();

            var field = new FloatField(label) { value = Mathf.Clamp(currentValue, min, max) };
            PrepareResetField(field);
            field.RegisterValueChangedCallback(e =>
            {
                var value = Mathf.Clamp(e.newValue, min, max);
                if (!Mathf.Approximately(value, e.newValue))
                    field.SetValueWithoutNotify(value);
                apply(value);
            });
            row.Add(field);

            var reset = new Button(() =>
            {
                var value = Mathf.Clamp(defaultValue, min, max);
                field.SetValueWithoutNotify(value);
                apply(value);
            })
            { text = "Reset" };
            PrepareResetButton(reset);
            row.Add(reset);

            parent.Add(row);
        }

        private static VisualElement CreateResetFieldRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2;
            row.style.width = Length.Percent(100);
            row.style.flexGrow = 1f;
            row.style.overflow = Overflow.Hidden;
            return row;
        }

        private static void PrepareResetField(BaseField<int> field)
        {
            PrepareResetField((VisualElement)field);
            PrepareResetFieldLabel(field);
        }

        private static void PrepareResetField(BaseField<float> field)
        {
            PrepareResetField((VisualElement)field);
            PrepareResetFieldLabel(field);
        }

        private static void PrepareResetField(VisualElement field)
        {
            field.style.flexGrow = 1f;
            field.style.flexShrink = 1f;
            field.style.flexBasis = 0;
            field.style.minWidth = 0;
            field.style.marginRight = 6;
        }

        private static void PrepareResetFieldLabel<T>(BaseField<T> field)
        {
            if (field.labelElement == null) return;
            field.labelElement.style.flexShrink = 1f;
            field.labelElement.style.minWidth = 0;
            field.labelElement.style.whiteSpace = WhiteSpace.Normal;
        }

        private static void PrepareResetButton(Button reset)
        {
            reset.AddToClassList("refit-back");
            reset.style.flexGrow = 0f;
            reset.style.flexShrink = 0f;
            reset.style.width = 64;
            reset.style.minWidth = 64;
            reset.style.maxWidth = 64;
        }

        private void BuildToolSettings()
        {
            Question("Settings");

            var operation = new Label("Operation");
            operation.AddToClassList("refit-section");
            content.Add(operation);
            BuildSettings(content);

            var debugSection = new Label("Debug");
            debugSection.AddToClassList("refit-section");
            debugSection.style.marginTop = 18;
            content.Add(debugSection);

            var debug = new Toggle("Debug mode") { value = ReFitDebugService.Enabled };
            debug.AddToClassList("refit-field");
            debug.RegisterValueChangedCallback(e => ReFitDebugService.Enabled = e.newValue);
            content.Add(debug);
            Help("When enabled, ReFit logs diagnostics and creates scene copies of the asset after each apply step.");

            var projection = new Toggle("Projection gizmos") { value = ReFitProjectionGizmoService.Enabled };
            projection.AddToClassList("refit-field");
            projection.RegisterValueChangedCallback(e => ReFitProjectionGizmoService.Enabled = e.newValue);
            content.Add(projection);
            Help("When debug mode is enabled, ReFit stores source/target projection lines on debug snapshots. This toggle only shows or hides them in the Scene view; details appear when hovering a line.");

            AddIntFieldWithReset(
                content,
                "Projection display budget",
                ReFitProjectionGizmoService.MinDisplaySampleBudget,
                ReFitProjectionGizmoService.MaxDisplaySampleBudget,
                ReFitProjectionGizmoService.DisplaySampleBudget,
                ReFitProjectionGizmoService.DefaultDisplaySampleBudget,
                ReFitProjectionGizmoService.SetDisplaySampleBudget);
            Help("Limits Scene view drawing cost only; debug mode still captures every projection group. Increase it for small snapshots when you need denser rays.");

            var flush = new Button(() =>
            {
                var removed = ReFitDebugService.FlushSceneDebugObjects();
                Debug.Log($"[ReFit] Debug flush removed {removed} session(s).");
            })
            { text = "Remove debug objects from scene" };
            flush.AddToClassList("refit-back");
            flush.style.marginTop = 4;
            content.Add(flush);
        }

        private ReFitRequest BuildRequest()
        {
            var requestSettings = settings.Clone();
            requestSettings.captureProjectionDebug = ReFitDebugService.Enabled;
            if (requestSettings.captureProjectionDebug)
                requestSettings.maxProjectionDebugGroups = 0;
            return new ReFitRequest
            {
                mode = mode,
                assetRenderer = asset,
                sourceAvatar = mode == ReFitMode.Blendshape ? null : sourceAvatar,
                targetAvatar = targetAvatar,
                targetBlendshape = mode == ReFitMode.MeshToMesh ? null : blendshape,
                settings = requestSettings
            };
        }

        private void Execute()
        {
            try
            {
                lastRequest = BuildRequest();
                lastResult = ReFitService.Execute(lastRequest,
                    (t, label) => EditorUtility.DisplayProgressBar("ReFit", label, t));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (lastResult != null && lastResult.success)
            {
                SessionLog.Add(new RefitLogEntry
                {
                    assetName = asset != null ? asset.name : "asset",
                    meshAssetPath = lastResult.meshAssetPath,
                    sceneRenderer = lastResult.sceneRenderer,
                    originalMesh = lastResult.originalMesh,
                    refitMesh = lastResult.mesh
                });
            }
            if (lastResult != null && lastResult.success &&
                ReFitGravityPreviewService.TryCreatePreview(lastResult, lastRequest, out gravityPreview))
            {
                gravityPreviewWeight = 100f;
                ReFitGravityPreviewService.ShowPreview(lastResult.sceneRenderer, gravityPreview, gravityPreviewWeight);
                Go(Step.GravityPreview);
            }
            else
            {
                gravityPreview = null;
                Go(Step.Result);
            }
        }

        private void BuildGravityPreview()
        {
            if (lastResult == null || !lastResult.success || gravityPreview == null ||
                gravityPreview.frames == null || gravityPreview.frames.Length == 0)
            {
                Go(Step.Result);
                return;
            }

            Question("Gravity preview");
            SummaryRow("Detected as", "body clothing");
            if (gravityPreview.candidate != null)
            {
                SummaryRow("Confidence", gravityPreview.candidate.score.ToString("0.00"));
                if (gravityPreview.candidate.reasons != null && gravityPreview.candidate.reasons.Length > 0)
                    SummaryRow("Signals", string.Join(", ", gravityPreview.candidate.reasons));
            }
            SummaryRow("Preview body", string.IsNullOrEmpty(gravityPreview.bodyRendererName) ? "-" : gravityPreview.bodyRendererName);

            var shapeNames = new List<string>();
            for (int i = 0; i < gravityPreview.frames.Length; i++)
                shapeNames.Add(gravityPreview.frames[i].gravityShapeName);
            SummaryRow("Generated shapes", string.Join(", ", shapeNames));

            var slider = new Slider("Default gravity weight", 0f, ReFitGravityPreviewService.MaximumGravityWeight) { value = gravityPreviewWeight };
            slider.AddToClassList("refit-field");
            slider.RegisterValueChangedCallback(e =>
            {
                gravityPreviewWeight = e.newValue;
                ReFitGravityPreviewService.SetWeight(gravityPreviewWeight);
            });
            content.Add(slider);
            Help("The Scene view cyan wire preview shows the additive gravity shape at the selected default weight.");

            var apply = Primary("Apply gravity blendshapes", () =>
            {
                ReFitGravityPreviewService.Apply(lastResult, gravityPreview, gravityPreviewWeight);
                gravityPreview = null;
                Go(Step.Result);
            });
            apply.style.marginTop = 12;

            var skip = new Button(() =>
            {
                ReFitGravityPreviewService.ClearPreview(gravityPreview);
                gravityPreview = null;
                Go(Step.Result);
            })
            { text = "Skip" };
            skip.AddToClassList("refit-back");
            skip.style.marginTop = 8;
            content.Add(skip);
        }

        private void BuildResult()
        {
            bool ok = lastResult != null && lastResult.success;
            Question(ok ? "Done ! Your asset has been re-fitted." : "ReFit could not complete.");

            if (lastResult != null)
            {
                if (!string.IsNullOrEmpty(lastResult.meshAssetPath)) SummaryRow("Mesh asset", lastResult.meshAssetPath);
                if (!string.IsNullOrEmpty(lastResult.prefabAssetPath)) SummaryRow("Prefab", lastResult.prefabAssetPath);
                if (lastResult.gravityShapeNames != null && lastResult.gravityShapeNames.Length > 0)
                {
                    SummaryRow("Gravity shapes", string.Join(", ", lastResult.gravityShapeNames));
                    SummaryRow("Gravity default", lastResult.gravityDefaultWeight.ToString("0.#"));
                }

                var messages = new VisualElement();
                content.Add(messages);
                foreach (var m in lastResult.report.messages)
                    if (m.severity != ReFitSeverity.Info) AddMessage(messages, m);

                if (ok && lastResult.sceneRenderer != null)
                {
                    Primary("Select the result in the scene", () =>
                    {
                        Selection.activeGameObject = lastResult.sceneRenderer.gameObject;
                        EditorGUIUtility.PingObject(lastResult.sceneRenderer.gameObject);
                    });
                }
                if (!string.IsNullOrEmpty(lastResult.meshAssetPath))
                {
                    var pingMesh = new Button(() =>
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Mesh>(lastResult.meshAssetPath)))
                    { text = "Show the mesh asset" };
                    pingMesh.AddToClassList("refit-back");
                    pingMesh.style.marginTop = 8;
                    content.Add(pingMesh);
                }
            }

            var again = new Button(Restart) { text = "Re-fit another asset" };
            again.AddToClassList("refit-back");
            again.style.marginTop = 14;
            content.Add(again);

            content.Add(CreateResultCredit());
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void Question(string text)
        {
            var label = new Label(text);
            label.AddToClassList("refit-question");
            content.Add(label);
        }

        private void Help(string text)
        {
            var label = new Label(text);
            label.AddToClassList("refit-help");
            content.Add(label);
        }

        private VisualElement Cards()
        {
            var cards = new VisualElement();
            cards.AddToClassList("refit-cards");
            content.Add(cards);
            return cards;
        }

        private VisualElement Card(string label, string sublabel, Action onClick, string extraClass = null)
        {
            var card = new VisualElement();
            card.AddToClassList("refit-card");
            if (extraClass != null) card.AddToClassList(extraClass);
            var main = new Label(label);
            main.AddToClassList("refit-card-label");
            card.Add(main);
            if (!string.IsNullOrEmpty(sublabel))
            {
                var sub = new Label(sublabel);
                sub.AddToClassList("refit-card-sublabel");
                card.Add(sub);
            }
            card.RegisterCallback<ClickEvent>(_ => onClick());
            return card;
        }

        private Button Primary(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("refit-primary");
            content.Add(button);
            return button;
        }

        private void SummaryRow(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("refit-summary-row");
            var k = new Label(key);
            k.AddToClassList("refit-summary-key");
            var v = new Label(value);
            v.AddToClassList("refit-summary-value");
            row.Add(k);
            row.Add(v);
            content.Add(row);
        }

        private static void AddMessage(VisualElement parent, ReFitMessage m)
        {
            var label = new Label(m.text);
            label.AddToClassList("refit-msg");
            label.AddToClassList(m.severity == ReFitSeverity.Error ? "refit-msg--error"
                : m.severity == ReFitSeverity.Warning ? "refit-msg--warning" : "refit-msg--info");
            parent.Add(label);
        }

        private static VisualElement CreateFooterCredit()
        {
            var credit = new VisualElement();
            credit.AddToClassList("refit-credit");
            ConfigureCreditLink(credit);
            credit.Add(CreditLabel("by blackorbit", "refit-credit-text"));
            credit.Add(CreditImage(BlackOrbitProfilePath, "refit-credit-profile", true, ScaleMode.ScaleAndCrop));
            credit.Add(CreditLabel("support me on KoFi", "refit-credit-text"));
            credit.Add(CreditImage(KofiSymbolPath, "refit-credit-kofi", false, ScaleMode.ScaleToFit));
            return credit;
        }

        private static VisualElement CreateResultCredit()
        {
            var credit = new VisualElement();
            credit.AddToClassList("refit-result-credit");
            ConfigureCreditLink(credit);

            var line = new VisualElement();
            line.AddToClassList("refit-result-credit-line");
            line.Add(CreditLabel("Support me on KoFi", "refit-result-credit-text"));
            line.Add(CreditImage(KofiSymbolPath, "refit-result-credit-kofi", false, ScaleMode.ScaleToFit));
            line.Add(CreditLabel("if it helps :3", "refit-result-credit-text"));
            credit.Add(line);
            return credit;
        }

        private static Label CreditLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        private static Image CreditImage(string path, string className, bool circular, ScaleMode scaleMode)
        {
            var image = new Image
            {
                image = LoadCreditTexture(path, circular),
                scaleMode = scaleMode
            };
            image.AddToClassList(className);
            return image;
        }

        private static void ConfigureCreditLink(VisualElement credit)
        {
            credit.tooltip = KofiUrl;
            credit.RegisterCallback<MouseDownEvent>(_ => credit.AddToClassList("refit-credit--pressed"));
            credit.RegisterCallback<MouseUpEvent>(_ => credit.RemoveFromClassList("refit-credit--pressed"));
            credit.RegisterCallback<MouseLeaveEvent>(_ => credit.RemoveFromClassList("refit-credit--pressed"));
            credit.RegisterCallback<ClickEvent>(OpenKofi);
        }

        private static void OpenKofi(ClickEvent evt)
        {
            Application.OpenURL(KofiUrl);
            evt.StopPropagation();
        }

        private static Texture2D LoadCreditTexture(string path, bool circular)
        {
            string cacheKey = path + (circular ? "|circle" : "|plain");
            if (CreditTextures.TryGetValue(cacheKey, out var cached)) return cached;

            bool loadedFromFile;
            var texture = LoadCreditTextureFromFile(path, out loadedFromFile) ?? AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null)
            {
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                if (circular)
                {
                    var circularTexture = MakeCircularCreditTexture(texture);
                    if (circularTexture != null && !ReferenceEquals(circularTexture, texture))
                    {
                        if (loadedFromFile) DestroyImmediate(texture);
                        texture = circularTexture;
                    }
                }
            }

            CreditTextures[cacheKey] = texture;
            return texture;
        }

        private static Texture2D LoadCreditTextureFromFile(string path, out bool loadedFromFile)
        {
            loadedFromFile = false;
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var absolutePath = !string.IsNullOrEmpty(projectRoot) ? Path.Combine(projectRoot, path) : null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return null;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = Path.GetFileNameWithoutExtension(path)
            };
            if (!texture.LoadImage(File.ReadAllBytes(absolutePath)))
            {
                DestroyImmediate(texture);
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            loadedFromFile = true;
            return texture;
        }

        private static Texture2D MakeCircularCreditTexture(Texture2D texture)
        {
            if (texture == null)
                return null;

            try
            {
                int size = Mathf.Min(texture.width, texture.height);
                int xOffset = Mathf.Max(0, (texture.width - size) / 2);
                int yOffset = Mathf.Max(0, (texture.height - size) / 2);
                Color[] sourcePixels = texture.GetPixels(xOffset, yOffset, size, size);

                float radius = size * 0.5f;
                float softEdge = Mathf.Max(1f, size * 0.015f);
                for (int y = 0; y < size; y++)
                {
                    float dy = (y + 0.5f) - radius;
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f) - radius;
                        float distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                        float alpha = Mathf.Clamp01((radius - distance) / softEdge);
                        int index = y * size + x;
                        Color c = sourcePixels[index];
                        c.a *= alpha;
                        sourcePixels[index] = c;
                    }
                }

                var circular = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    name = texture.name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                circular.SetPixels(sourcePixels);
                circular.Apply();
                return circular;
            }
            catch (UnityException)
            {
                return texture;
            }
        }

        private sealed class TightnessIllustration : VisualElement
        {
            private const float NoTightWidth = 102f;
            private const float TightWidth = 89f;
            private const float ViewHeight = 218f;
            private readonly bool tight;

            public TightnessIllustration(bool tight)
            {
                this.tight = tight;
                pickingMode = PickingMode.Ignore;
                AddToClassList("refit-tightness-illustration");
                style.width = tight ? TightWidth : NoTightWidth;
                style.height = ViewHeight;
                generateVisualContent += OnGenerateVisualContent;
            }

            private void OnGenerateVisualContent(MeshGenerationContext context)
            {
                var rect = contentRect;
                if (rect.width <= 0f || rect.height <= 0f)
                    return;

                float viewWidth = tight ? TightWidth : NoTightWidth;
                float scale = Mathf.Min(rect.width / viewWidth, rect.height / ViewHeight);
                float offsetX = rect.x + (rect.width - viewWidth * scale) * 0.5f;
                float offsetY = rect.y + (rect.height - ViewHeight * scale) * 0.5f;
                var painter = context.painter2D;
                painter.fillColor = new Color(217f / 255f, 217f / 255f, 217f / 255f, 1f);

                Func<float, float, Vector2> point = (x, y) => new Vector2(offsetX + x * scale, offsetY + y * scale);
                if (tight)
                    DrawTightSvg(painter, point);
                else
                    DrawNoTightSvg(painter, point);
            }

            private static void DrawNoTightSvg(Painter2D painter, Func<float, float, Vector2> point)
            {
                FillPath(painter,
                    p => p.MoveTo(point(39.6337f, 0f)),
                    p => p.BezierCurveTo(point(32.9696f, 7.30301f), point(20.245f, 26.069f), point(20.245f, 42.7087f)),
                    p => p.BezierCurveTo(point(20.245f, 59.3485f), point(39.6337f, 85.4174f), point(39.6337f, 85.4174f)),
                    p => p.LineTo(point(32.1702f, 85.4174f)),
                    p => p.BezierCurveTo(point(23.9509f, 78.5767f), point(13.5891f, 60.4578f), point(13.5891f, 42.7087f)),
                    p => p.BezierCurveTo(point(13.5891f, 24.9597f), point(23.9509f, 6.84082f), point(32.1702f, 0.0000329479f)),
                    p => p.LineTo(point(39.6337f, 0f)));

                FillPath(painter,
                    p => p.MoveTo(point(34.9679f, 42.7087f)),
                    p => p.BezierCurveTo(point(34.9679f, 61.1234f), point(51.4065f, 78.854f), point(59.6258f, 85.4174f)),
                    p => p.BezierCurveTo(point(67.8451f, 78.3918f), point(84.2837f, 60.0141f), point(84.2837f, 42.7087f)),
                    p => p.BezierCurveTo(point(84.2837f, 25.4034f), point(67.8451f, 7.02567f), point(59.6258f, 0f)),
                    p => p.BezierCurveTo(point(51.4065f, 6.56348f), point(34.9679f, 24.2941f), point(34.9679f, 42.7087f)));

                FillPath(painter,
                    p => p.MoveTo(point(39.9107f, 132.563f)),
                    p => p.BezierCurveTo(point(33.2467f, 139.866f), point(6.65588f, 158.632f), point(6.65588f, 175.272f)),
                    p => p.BezierCurveTo(point(6.65588f, 191.912f), point(39.9107f, 217.981f), point(39.9107f, 217.981f)),
                    p => p.LineTo(point(32.4473f, 217.981f)),
                    p => p.BezierCurveTo(point(24.228f, 211.14f), point(-0.0000305176f, 193.021f), point(-0.0000305176f, 175.272f)),
                    p => p.BezierCurveTo(point(-0.0000305176f, 157.523f), point(24.228f, 139.404f), point(32.4473f, 132.564f)),
                    p => p.LineTo(point(39.9107f, 132.563f)));

                FillPath(painter,
                    p => p.MoveTo(point(21.3788f, 175.272f)),
                    p => p.BezierCurveTo(point(21.3788f, 193.687f), point(51.6835f, 211.417f), point(59.9028f, 217.981f)),
                    p => p.BezierCurveTo(point(68.1221f, 210.955f), point(101.78f, 192.578f), point(101.78f, 175.272f)),
                    p => p.BezierCurveTo(point(101.78f, 157.967f), point(68.1221f, 139.589f), point(59.9028f, 132.563f)),
                    p => p.BezierCurveTo(point(51.6835f, 139.127f), point(21.3788f, 156.858f), point(21.3788f, 175.272f)));

                FillPath(painter,
                    p => p.MoveTo(point(47.4233f, 95.4014f)),
                    p => p.LineTo(point(50.4739f, 95.4014f)),
                    p => p.LineTo(point(50.4739f, 104.553f)),
                    p => p.LineTo(point(58.5164f, 104.553f)),
                    p => p.LineTo(point(48.8099f, 121.47f)),
                    p => p.LineTo(point(38.826f, 104.553f)),
                    p => p.LineTo(point(47.4233f, 104.553f)),
                    p => p.LineTo(point(47.4233f, 95.4014f)));
            }

            private static void DrawTightSvg(Painter2D painter, Func<float, float, Vector2> point)
            {
                FillPath(painter,
                    p => p.MoveTo(point(26.3217f, 132.563f)),
                    p => p.BezierCurveTo(point(22.61f, 136.631f), point(20.6647f, 144.254f), point(16.3624f, 153.086f)),
                    p => p.BezierCurveTo(point(12.9402f, 160.111f), point(6.65591f, 167.9f), point(6.65591f, 175.272f)),
                    p => p.BezierCurveTo(point(6.65591f, 181.362f), point(13.0223f, 188.715f), point(16.3624f, 195.591f)),
                    p => p.BezierCurveTo(point(22.1488f, 207.502f), point(26.3217f, 217.981f), point(26.3217f, 217.981f)),
                    p => p.LineTo(point(18.8582f, 217.981f)),
                    p => p.BezierCurveTo(point(13.857f, 213.818f), point(11.6921f, 205.48f), point(7.84106f, 195.591f)),
                    p => p.BezierCurveTo(point(5.36313f, 189.227f), point(0f, 182.221f), point(0f, 175.272f)),
                    p => p.BezierCurveTo(point(0f, 167.636f), point(5.70156f, 159.931f), point(8.60014f, 153.086f)),
                    p => p.BezierCurveTo(point(12.4388f, 144.02f), point(14.1751f, 136.461f), point(18.8582f, 132.564f)),
                    p => p.LineTo(point(26.3217f, 132.563f)));

                FillPath(painter,
                    p => p.MoveTo(point(7.78976f, 175.272f)),
                    p => p.BezierCurveTo(point(7.78976f, 193.687f), point(38.0944f, 211.417f), point(46.3137f, 217.981f)),
                    p => p.BezierCurveTo(point(54.533f, 210.955f), point(88.1908f, 192.578f), point(88.1908f, 175.272f)),
                    p => p.BezierCurveTo(point(88.1908f, 157.967f), point(54.533f, 139.589f), point(46.3137f, 132.563f)),
                    p => p.BezierCurveTo(point(38.0944f, 139.127f), point(7.78976f, 156.858f), point(7.78976f, 175.272f)));

                FillPath(painter,
                    p => p.MoveTo(point(28.2633f, 0f)),
                    p => p.BezierCurveTo(point(21.5992f, 7.30301f), point(8.8746f, 26.069f), point(8.8746f, 42.7087f)),
                    p => p.BezierCurveTo(point(8.8746f, 59.3485f), point(28.2633f, 85.4174f), point(28.2633f, 85.4174f)),
                    p => p.LineTo(point(20.7998f, 85.4174f)),
                    p => p.BezierCurveTo(point(12.5805f, 78.5767f), point(2.21869f, 60.4578f), point(2.21869f, 42.7087f)),
                    p => p.BezierCurveTo(point(2.21869f, 24.9597f), point(12.5805f, 6.84082f), point(20.7998f, 0.0000329479f)),
                    p => p.LineTo(point(28.2633f, 0f)));

                FillPath(painter,
                    p => p.MoveTo(point(23.5976f, 42.7087f)),
                    p => p.BezierCurveTo(point(23.5976f, 61.1234f), point(40.0361f, 78.854f), point(48.2554f, 85.4174f)),
                    p => p.BezierCurveTo(point(56.4747f, 78.3918f), point(72.9133f, 60.0141f), point(72.9133f, 42.7087f)),
                    p => p.BezierCurveTo(point(72.9133f, 25.4034f), point(56.4747f, 7.02567f), point(48.2554f, 0f)),
                    p => p.BezierCurveTo(point(40.0361f, 6.56348f), point(23.5976f, 24.2941f), point(23.5976f, 42.7087f)));

                FillPath(painter,
                    p => p.MoveTo(point(36.0528f, 95.4014f)),
                    p => p.LineTo(point(39.1035f, 95.4014f)),
                    p => p.LineTo(point(39.1035f, 104.553f)),
                    p => p.LineTo(point(47.146f, 104.553f)),
                    p => p.LineTo(point(37.4395f, 121.47f)),
                    p => p.LineTo(point(27.4556f, 104.553f)),
                    p => p.LineTo(point(36.0528f, 104.553f)),
                    p => p.LineTo(point(36.0528f, 95.4014f)));
            }

            private static void FillPath(Painter2D painter, params Action<Painter2D>[] commands)
            {
                painter.BeginPath();
                for (int i = 0; i < commands.Length; i++)
                    commands[i](painter);
                painter.ClosePath();
                painter.Fill();
            }
        }

        private string ModeLabel()
        {
            switch (mode)
            {
                case ReFitMode.MeshToMesh: return "Fit the mesh to another body";
                case ReFitMode.Blendshape: return "Follow a body blendshape";
                case ReFitMode.MeshAndBlendshape: return "Fit the mesh + follow a body blendshape";
                default: return mode.ToString();
            }
        }

        private static string DescribeAvatar(GameObject avatar)
        {
            var animator = HumanoidBoneMapper.FindHumanoidAnimator(avatar);
            return animator != null ? "Humanoid avatar" : "Avatar";
        }

        private static List<GameObject> FindSceneAvatars()
        {
            var found = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null) continue;
                        GameObject candidate = null;
                        if (component is Animator animator && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
                            candidate = animator.gameObject;
                        else if (component.GetType().Name == "VRCAvatarDescriptor")
                            candidate = component.gameObject;
                        if (candidate != null && seen.Add(candidate)) found.Add(candidate);
                    }
                }
            }
            return found;
        }

        private static SkinnedMeshRenderer AutoDetectBody(GameObject avatar, SkinnedMeshRenderer exclude)
        {
            if (avatar == null) return null;
            SkinnedMeshRenderer best = null;
            int bestVerts = -1;
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == exclude || smr.sharedMesh == null) continue;
                if (string.Equals(smr.name, "Body", StringComparison.OrdinalIgnoreCase)) return smr;
                if (smr.sharedMesh.vertexCount > bestVerts) { bestVerts = smr.sharedMesh.vertexCount; best = smr; }
            }
            return best;
        }
    }
}
