using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        // ------------------------------------------------------------------
        // Settings page: connection, every advanced option, Orbiters tools, debug
        // ------------------------------------------------------------------

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
            clearanceSection.AddToClassList("refit-subsection");
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

            var garmentKind = new EnumField("Garment type", settings.garmentKind);
            garmentKind.RegisterValueChangedCallback(e => settings.garmentKind = (ReFitGarmentKind)e.newValue);
            parent.Add(garmentKind);
            var tubes = new Toggle("Preserve closed tubes") { value = settings.preserveClosedTubes,
                tooltip = "Automatically detect closed tubular rings and preserve their thickness while fitting. Other meshes keep surface fitting." };
            tubes.RegisterValueChangedCallback(e => settings.preserveClosedTubes = e.newValue);
            parent.Add(tubes);
#if REFIT_VRCHAT_AVATARS
            var rigid = new Toggle("Keep rigid pieces on the body (beta)") { value = settings.keepRigidPiecesOnBody,
                tooltip = "Buttons, studs, buckles and other pieces a refit cannot bend follow the body's blendshapes when the avatar is built (Orbiters Follow Body Blendshapes)." };
            rigid.RegisterValueChangedCallback(e => settings.keepRigidPiecesOnBody = e.newValue);
            parent.Add(rigid);
#endif

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
            // The precise value behind the studio's Loose / Balanced / Snug.
            var slider = new Slider(clearanceTightnessKnown ? "Tightness" : "Tightness (custom)", 0f, 1f)
            {
                value = clearanceTightnessKnown ? clearanceTightnessPreset : 0.5f,
                showInputField = true
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

            ReFitSettingsPresets.ApplyTightness(settings, clearanceTightnessPreset);
        }

        private void MarkClearanceTightnessCustom()
        {
            clearanceTightnessKnown = false;
            clearanceTightnessUserChosen = true;
        }

        private void ResetTightnessChoice()
        {
            clearanceTightnessUserChosen = false;
            clearanceTightnessKnown = true;
            clearanceTightnessPreset = 0.5f;
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
            reset.AddToClassList("refit-button");
            reset.AddToClassList("refit-button--small");
            reset.style.flexGrow = 0f;
            reset.style.flexShrink = 0f;
            reset.style.width = 64;
            reset.style.minWidth = 64;
            reset.style.maxWidth = 64;
        }

        private void BuildToolSettings()
        {
            Text(content, "Settings", "refit-page-title");
            Text(content, "Every option of the fit, kept for this window until you close it. The defaults suit most clothing.", "refit-help");

            var fitting = SettingsCard("Fitting");
            BuildSettings(fitting);

            if (ReFitRecordIntegration.IsAvailable)
            {
                var tools = SettingsCard("Orbiters tools");
                var recordResults = new Toggle("Link transferred blendshapes to the body")
                {
                    value = ReFitRecordIntegration.Enabled
                };
                recordResults.AddToClassList("refit-field");
                recordResults.RegisterValueChangedCallback(e => ReFitRecordIntegration.Enabled = e.newValue);
                tools.Add(recordResults);
                AddInlineHelp(tools, "Records each result on the refitted mesh: its transferred blendshapes follow every animation of the body's shapes when the avatar is built, MCB keeps it per custom base version, and MCB or My Avatar can restore the original mesh. Enabled by default.");
            }

            var connection = SettingsCard("Connection");
            var devEnvironment = new Toggle("Dev Environment")
            {
                value = ReFitCommissionClient.IsDevEnvironment
            };
            devEnvironment.AddToClassList("refit-field");
            devEnvironment.RegisterValueChangedCallback(e =>
            {
                ReFitCommissionClient.IsDevEnvironment = e.newValue;
                commissionCreators = null;
                commissionError = null;
                commissionCreatorsLoading = false;
                commissionHandoffLoading = false;
            });
            connection.Add(devEnvironment);
            AddInlineHelp(connection, "Uses the local Orbiters API on port 4100. Production is used when disabled. This setting is shared with MCB when MCB is installed.");

            var debugCard = SettingsCard("Debug");
            var debug = new Toggle("Debug mode") { value = ReFitDebugService.Enabled };
            debug.AddToClassList("refit-field");
            debug.RegisterValueChangedCallback(e => ReFitDebugService.Enabled = e.newValue);
            debugCard.Add(debug);
            AddInlineHelp(debugCard, "When enabled, ReFit logs diagnostics and creates scene copies of the asset after each apply step.");

            var projection = new Toggle("Projection gizmos") { value = ReFitProjectionGizmoService.Enabled };
            projection.AddToClassList("refit-field");
            projection.RegisterValueChangedCallback(e => ReFitProjectionGizmoService.Enabled = e.newValue);
            debugCard.Add(projection);
            AddInlineHelp(debugCard, "When debug mode is enabled, ReFit stores source/target projection lines on debug snapshots. This toggle only shows or hides them in the Scene view; details appear when hovering a line.");

            AddIntFieldWithReset(
                debugCard,
                "Projection display budget",
                ReFitProjectionGizmoService.MinDisplaySampleBudget,
                ReFitProjectionGizmoService.MaxDisplaySampleBudget,
                ReFitProjectionGizmoService.DisplaySampleBudget,
                ReFitProjectionGizmoService.DefaultDisplaySampleBudget,
                ReFitProjectionGizmoService.SetDisplaySampleBudget);
            AddInlineHelp(debugCard, "Limits Scene view drawing cost only; debug mode still captures every projection group. Increase it for small snapshots when you need denser rays.");

            var flush = FlatButton("Remove debug objects from scene", () =>
            {
                var removed = ReFitDebugService.FlushSceneDebugObjects();
                Debug.Log($"[ReFit] Debug flush removed {removed} session(s).");
            }, "refit-button--ghost", "refit-settings__action");
            debugCard.Add(flush);
        }

        // A titled card of the settings page.
        private VisualElement SettingsCard(string title)
        {
            var card = Box(content, "refit-card", "refit-settings-card");
            Text(card, title, "refit-section", "refit-settings-card__title");
            return card;
        }
    }
}
