using System;
using System.Collections.Generic;
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
            Summary,
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

        private ScrollView content;
        private Button backButton;

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
            body.Add(nav);

            content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("refit-scroll");
            body.Add(content);

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
            current = history.Pop();
            Render();
        }

        private void Restart()
        {
            history.Clear();
            current = Step.AssetLocation;
            myAvatar = null; asset = null; assetFileObject = null;
            targetAvatar = null; sourceAvatar = null; blendshape = null;
            mode = ReFitMode.MeshToMesh;
            validateReport = null; lastResult = null;
            Render();
        }

        private void Render()
        {
            if (content == null) return;
            content.Clear();
            backButton.style.display = history.Count > 0 && current != Step.Result ? DisplayStyle.Flex : DisplayStyle.None;
            switch (current)
            {
                case Step.AssetLocation: BuildAssetLocation(); break;
                case Step.AvatarSelect: BuildAvatarSelect(); break;
                case Step.AssetSelect: BuildAssetSelect(); break;
                case Step.FitChoice: BuildFitChoice(); break;
                case Step.TargetInput: BuildTargetInput(Step.Summary); break;
                case Step.MyAvatarChoice: BuildMyAvatarChoice(); break;
                case Step.SourceInput: BuildSourceInput(); break;
                case Step.BlendshapeSelect: BuildBlendshapeSelect(); break;
                case Step.AssetFileInput: BuildAssetFileInput(); break;
                case Step.TargetForFile: BuildTargetInput(Step.FileAssetChoice); break;
                case Step.FileAssetChoice: BuildFileAssetChoice(); break;
                case Step.Summary: BuildSummary(); break;
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
                cards.Add(Card(s.name, sub, () => { asset = s; Go(Step.FitChoice); }, "refit-card--list"));
            }
        }

        private void BuildFitChoice()
        {
            Question("Do you want to make it fit your avatar or another one ?");
            var cards = Cards();
            cards.Add(Card("To my avatar", "The asset stays on this avatar", () => { targetAvatar = myAvatar; Go(Step.MyAvatarChoice); }));
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
                Go(Step.Summary);
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
                Go(Step.Summary);
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
                    cards.Add(Card(s.name, $"{s.sharedMesh.vertexCount} vertices", () => { asset = s; Go(Step.TargetForFile); }, "refit-card--list"));
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
            var name = new TextField("Blendshape name") { value = settings.blendshapeName };
            name.RegisterValueChangedCallback(e => settings.blendshapeName = e.newValue);
            parent.Add(name);

            var maxDist = new FloatField("Max projection distance (m)") { value = settings.maxProjectionDistance };
            maxDist.RegisterValueChangedCallback(e => settings.maxProjectionDistance = Mathf.Max(0.001f, e.newValue));
            parent.Add(maxDist);

            var falloff = new FloatField("Full-effect distance (m)") { value = settings.falloffStartDistance };
            falloff.RegisterValueChangedCallback(e => settings.falloffStartDistance = Mathf.Max(0f, e.newValue));
            parent.Add(falloff);

            var iters = new SliderInt("Smoothing iterations", 0, 10) { value = settings.smoothingIterations, showInputField = true };
            iters.RegisterValueChangedCallback(e => settings.smoothingIterations = e.newValue);
            parent.Add(iters);

            var strength = new Slider("Smoothing strength", 0f, 1f) { value = settings.smoothingStrength, showInputField = true };
            strength.RegisterValueChangedCallback(e => settings.smoothingStrength = e.newValue);
            parent.Add(strength);

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

        private ReFitRequest BuildRequest()
        {
            return new ReFitRequest
            {
                mode = mode,
                assetRenderer = asset,
                sourceAvatar = mode == ReFitMode.Blendshape ? null : sourceAvatar,
                targetAvatar = targetAvatar,
                targetBlendshape = mode == ReFitMode.MeshToMesh ? null : blendshape,
                settings = settings
            };
        }

        private void Execute()
        {
            try
            {
                lastResult = ReFitService.Execute(BuildRequest(),
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
            Go(Step.Result);
        }

        private void BuildResult()
        {
            bool ok = lastResult != null && lastResult.success;
            Question(ok ? "Done ! Your asset has been re-fitted." : "ReFit could not complete.");

            if (lastResult != null)
            {
                if (!string.IsNullOrEmpty(lastResult.meshAssetPath)) SummaryRow("Mesh asset", lastResult.meshAssetPath);
                if (!string.IsNullOrEmpty(lastResult.prefabAssetPath)) SummaryRow("Prefab", lastResult.prefabAssetPath);

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
