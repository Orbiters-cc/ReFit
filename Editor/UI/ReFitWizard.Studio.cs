using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        private static readonly ReFitScenePicture.View[] StageViews =
            { ReFitScenePicture.View.Front, ReFitScenePicture.View.ThreeQuarter, ReFitScenePicture.View.Side, ReFitScenePicture.View.Back };
        private static readonly string[] StageViewNames = { "Front", "¾", "Side", "Back" };
        // Avatars shown as chips beside the clothing; the others are one click away in a menu.
        private const int AvatarChips = 3;
        private const float BalancedTightness = 0.5f;

        private ReFitStageView stage;
        private SegmentedControl stageViews;
        [SerializeField] private int stageView = 1;
        // The avatar wearing the piece as it is (or was before the run), and the result: baked once, turned in 3D.
        private ReFitScenePicture.Scene beforeScene, afterScene;
        // Why the piece could not be shown, said on the stage instead of waiting for it.
        private string pictureError;
        private IVisualElementScheduledItem pictureUpdate, checkUpdate, thumbnailPoll;
        private readonly List<(Image image, GameObject root, string key)> pendingThumbnails = new List<(Image, GameObject, string)>();

        // ------------------------------------------------------------------
        // Choosing what to refit
        // ------------------------------------------------------------------

        /// <summary>The window opens on what is around: the selection, else the scene's only avatar.</summary>
        private void AutoDetect()
        {
            if (Selection.activeGameObject != null && UseSelection(Selection.activeGameObject)) return;
            var avatars = ReFitAutoSetup.SceneAvatars();
            if (avatars.Count == 1) SetAvatar(avatars[0]);
        }

        // A clothing piece or an avatar picked in the Hierarchy is taken while the studio waits for a choice.
        private void OnSelectionChanged()
        {
            if (isExecuting || page != Page.Studio || content == null) return;
            var selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected)) return;
            var renderer = selected.GetComponent<SkinnedMeshRenderer>();
            if (renderer != null)
            {
                if (renderer == asset || renderer.sharedMesh == null) return;
                var wearer = ReFitAutoSetup.AvatarOf(selected);
                if (wearer != null && renderer == ReFitAutoSetup.Body(wearer)) return;
            }
            else if (ReFitAutoSetup.IsAvatar(selected) && !ReFitAutoSetup.InsideAvatar(selected))
            {
                if (selected == targetAvatar) return;
            }
            else
            {
                // An outfit's root on an avatar (not its armature, not the body).
                var wearer = ReFitAutoSetup.AvatarOf(selected);
                if (wearer == null || asset != null && asset.transform.IsChildOf(selected.transform)) return;
                var meshes = ReFitAutoSetup.Meshes(selected);
                if (meshes.Count == 0 || meshes.Contains(ReFitAutoSetup.Body(wearer))) return;
            }
            if (UseSelection(selected)) Render();
        }

        /// <summary>Takes an avatar, a worn piece, or a project file holding clothing. False when it holds nothing to refit.</summary>
        internal bool UseSelection(GameObject item)
        {
            if (item == null) return false;
            bool persistent = EditorUtility.IsPersistent(item);
            if (!persistent && ReFitAutoSetup.IsAvatar(item) && !ReFitAutoSetup.InsideAvatar(item))
            {
                SetAvatar(item);
                var worn = ReFitAutoSetup.Clothing(item);
                if (worn.Count == 1) SetAsset(worn[0]);
                return true;
            }
            var meshes = ReFitAutoSetup.Meshes(item);
            if (meshes.Count == 0) return false;
            if (persistent)
            {
                assetFileObject = item;
                if (targetAvatar == null)
                {
                    var avatars = ReFitAutoSetup.SceneAvatars();
                    if (avatars.Count == 1) SetAvatar(avatars[0]);
                }
                if (meshes.Count == 1) SetAsset(meshes[0]);
                else { asset = null; ClearPictures(true); }
                return true;
            }
            var avatar = ReFitAutoSetup.AvatarOf(item);
            if (avatar != null && avatar != targetAvatar) SetAvatar(avatar);
            if (meshes.Count == 1) SetAsset(meshes[0]);
            else
            {
                // Several meshes: the user picks one in the strip.
                asset = null;
                if (avatar == null) assetFileObject = item;
                ClearPictures(true);
            }
            return true;
        }

        private void SetAvatar(GameObject avatar)
        {
            targetAvatar = avatar;
            myAvatar = avatar;
            originalBase = ReFitAutoSetup.OriginalBase(avatar);
            if (asset != null && !EditorUtility.IsPersistent(asset) && avatar != null && !asset.transform.IsChildOf(avatar.transform))
            {
                // The asset now moves from its own avatar to this one.
                var wearer = ReFitAutoSetup.AvatarOf(asset.gameObject);
                if (wearer != null) { madeFor = MadeFor.Other; sourceAvatar = wearer; }
            }
            UpdateMode();
            InvalidateChecks();
            ClearPictures(true);
            if (asset != null) RequestBeforePicture();
        }

        /// <summary>The piece to refit, with smart defaults for everything else.</summary>
        private void SetAsset(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return;
            asset = renderer;
            bool persistent = EditorUtility.IsPersistent(renderer);
            var wearer = persistent ? null : ReFitAutoSetup.AvatarOf(renderer.gameObject);
            if (persistent) assetFileObject = renderer.transform.root.gameObject;
            else assetFileObject = wearer == null ? renderer.transform.root.gameObject : null;
            if (wearer != null && targetAvatar == null) { targetAvatar = wearer; originalBase = ReFitAutoSetup.OriginalBase(wearer); }
            if (targetAvatar == null)
            {
                var avatars = ReFitAutoSetup.SceneAvatars();
                if (avatars.Count == 1) { targetAvatar = avatars[0]; originalBase = ReFitAutoSetup.OriginalBase(targetAvatar); }
            }
            myAvatar = wearer ?? targetAvatar;

            // Made for: the avatar it is worn on when moving it, the original base a custom base was made from (unless the
            // creator already adapted it to the custom base), else it already fits.
            sourceAvatar = null; sourceBody = null;
            if (wearer != null && targetAvatar != null && wearer != targetAvatar) { madeFor = MadeFor.Other; sourceAvatar = wearer; }
            else if (originalBase != null && !HasCustomShapes(renderer)) madeFor = MadeFor.OriginalBase;
            else madeFor = MadeFor.Target;

            // Shapes the avatar keeps on under it, and how snug it fits.
            blendshapes.Clear(); activeShapes.Clear();
            if (targetAvatar != null)
            {
                try { activeShapes.AddRange(ReFitAutoSetup.ActiveShapes(ReFitAutoSetup.Body(targetAvatar, renderer), renderer)); }
                catch (Exception ex) { Debug.LogWarning("[ReFit] Could not read the avatar's active shapes: " + ex.Message); }
                blendshapes.AddRange(activeShapes);
            }
            assetIsClothing = ReFitClothingDetection.IsClothing(renderer, targetAvatar);
            if (!clearanceTightnessUserChosen)
                ApplyClearanceTightnessPreset(assetIsClothing ? ReFitAutoSetup.ClothingTightness : ReFitAutoSetup.AccessoryTightness, false);
            madeForAvatar = true;
            shapesOpen = false;
            UpdateMode();
            InvalidateChecks();
            ClearPictures(true);
            RequestBeforePicture();
        }

        // The clothing already has some of the custom base's shapes: its creator adapted it to the custom base.
        private bool HasCustomShapes(SkinnedMeshRenderer renderer)
        {
            if (originalBase == null || renderer.sharedMesh == null) return false;
            foreach (var shape in originalBase.Shapes)
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                    if (string.Equals(renderer.sharedMesh.GetBlendShapeName(i), shape, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // The studio page
        // ------------------------------------------------------------------

        private void BuildStudio()
        {
            BuildPieces();
            if (asset != null) BuildSetup();
            BuildRefittedAssets();
            BuildActiveCommissions();
        }

        /// <summary>The 3D stage, made once with the window: pages change beside it, its camera stays.</summary>
        private ReFitStageView BuildStage()
        {
            var view = new ReFitStageView();
            view.Dropped += objects =>
            {
                var item = objects.OfType<GameObject>().FirstOrDefault() ?? objects.OfType<Component>().Select(c => c.gameObject).FirstOrDefault();
                if (item != null && !isExecuting && page != Page.Result && UseSelection(item)) Render();
            };
            stageViews = new SegmentedControl(StageViewNames, index =>
            {
                stageView = index;
                stage?.SetView(ReFitScenePicture.Direction(StageViews[index]));
            });
            stageViews.AddToClassList("refit-stage__views");
            stageViews.pickingMode = PickingMode.Position;
            stageViews.SetIndex(stageView);
            view.Overlay.Add(stageViews);
            view.SetView(ReFitScenePicture.Direction(StageViews[Mathf.Clamp(stageView, 0, StageViews.Length - 1)]));
            return view;
        }

        // The stage shows what the window has: the piece on the avatar, the result beside it, or why there is nothing.
        private void ShowPictures()
        {
            if (stage == null) return;
            var after = page == Page.Result ? afterScene : null;
            if (beforeScene == null && after == null)
            {
                if (asset != null && pictureError != null && !isExecuting)
                {
                    stage.ShowEmpty("Nothing to show of " + asset.name, "It can still be refitted. " + pictureError);
                    stage.SetBusy(null);
                    return;
                }
                if (asset != null)
                {
                    stage.ShowBlank();
                    stage.SetBusy(isExecuting ? "Fitting…" : "Setting the stage…");
                    return;
                }
                stage.ShowEmpty(targetAvatar != null ? "Pick the clothing to refit" : "Pick an avatar or its clothing",
                    targetAvatar != null ? "Click it in the list, select it in the Hierarchy, or drop a prefab here."
                        : "Select it in the Hierarchy, or drop it here.");
                stage.SetBusy(null);
                return;
            }
            stage.Show(beforeScene, after, asset != null ? asset.GetInstanceID() : 1);
            stage.SetBusy(isExecuting ? "Fitting…" : null);
        }

        // The pieces of the avatar (or the project file) as picture cards, the chosen one lit.
        private void BuildPieces()
        {
            var pieces = PieceCandidates();
            var section = Box(content, "refit-section-block");
            var heading = Box(section, "refit-section-heading");
            Text(heading, pieces.Count > 0 ? "Clothing" : "Avatar", "refit-section");
            var avatars = ReFitAutoSetup.SceneAvatars();
            // An avatar outside the open scenes (a prefab stage, a preview) is still the one it fits.
            if (targetAvatar != null && !avatars.Contains(targetAvatar)) avatars.Insert(0, targetAvatar);
            if (targetAvatar != null || avatars.Count > 0)
            {
                var avatarRow = Box(heading, "refit-avatar-row");
                // The chosen avatar first, then a few others; the rest in a menu.
                var shown = avatars.OrderBy(a => a == targetAvatar ? 0 : 1).Take(AvatarChips).ToList();
                foreach (var avatar in shown) avatarRow.Add(AvatarChip(avatar, avatar == targetAvatar));
                if (avatars.Count > shown.Count)
                {
                    var more = Box(avatarRow, "refit-chip", "refit-chip--avatar", "refit-chip--more");
                    more.tooltip = "Fit onto another avatar of the scene";
                    Text(more, "+" + (avatars.Count - shown.Count), "refit-chip__text");
                    OnPress(more, () =>
                    {
                        var menu = new GenericMenu();
                        foreach (var avatar in avatars)
                        {
                            var captured = avatar;
                            menu.AddItem(new GUIContent(avatar.name), avatar == targetAvatar, () =>
                            {
                                if (captured == null || captured == targetAvatar || isExecuting) return;
                                SetAvatar(captured);
                                Render();
                            });
                        }
                        menu.DropDown(more.worldBound);
                    });
                }
            }
            if (pieces.Count == 0)
            {
                if (targetAvatar != null) Text(section, "Nothing to refit on " + targetAvatar.name + " yet: drop clothing on it or on the stage.", "refit-help");
                else Text(section, "No avatar in the open scene. Drop clothing and an avatar here, or pick them below.", "refit-help");
                var field = new ObjectField("Clothing file") { objectType = typeof(GameObject), allowSceneObjects = true };
                field.AddToClassList("refit-field");
                field.RegisterValueChangedCallback(e => { if (e.newValue is GameObject go && UseSelection(go)) Render(); });
                section.Add(field);
                return;
            }
            var strip = new ScrollView(ScrollViewMode.Horizontal);
            strip.AddToClassList("refit-pieces");
            strip.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            strip.horizontalScrollerVisibility = ScrollerVisibility.Auto;
            section.Add(strip);
            foreach (var piece in pieces) strip.Add(PieceCard(piece));
            var from = Box(section, "refit-pieces-footer");
            var file = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = false, tooltip = "Refit clothing from your project files" };
            file.AddToClassList("refit-file-field");
            file.RegisterValueChangedCallback(e => { if (e.newValue is GameObject go && UseSelection(go)) Render(); });
            Text(from, "Or from your project files", "refit-caption");
            from.Add(file);
        }

        private List<SkinnedMeshRenderer> PieceCandidates()
        {
            if (assetFileObject != null && (asset == null || asset.transform.IsChildOf(assetFileObject.transform)))
                return ReFitAutoSetup.Meshes(assetFileObject);
            return ReFitAutoSetup.Clothing(targetAvatar);
        }

        private VisualElement AvatarChip(GameObject avatar, bool selected)
        {
            var chip = new VisualElement { tooltip = selected ? "The avatar it fits" : "Fit onto " + avatar.name };
            chip.AddToClassList("refit-chip");
            chip.AddToClassList("refit-chip--avatar");
            chip.EnableInClassList("refit-chip--selected", selected);
            var icon = new VectorIcon(IconGlyph.Person);
            icon.AddToClassList("refit-chip__icon");
            chip.Add(icon);
            Text(chip, avatar.name, "refit-chip__text");
            if (!selected) OnPress(chip, () => { SetAvatar(avatar); Render(); });
            return chip;
        }

        private VisualElement PieceCard(SkinnedMeshRenderer piece)
        {
            var card = new VisualElement { tooltip = piece.name + " · " + piece.sharedMesh.vertexCount.ToString("N0") + " vertices" };
            card.AddToClassList("refit-piece");
            card.EnableInClassList("refit-piece--selected", piece == asset);
            card.EnableInClassList("refit-piece--hidden", !EditorUtility.IsPersistent(piece) && !piece.gameObject.activeInHierarchy);
            var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            picture.AddToClassList("refit-piece__picture");
            card.Add(picture);
            string key = "refit-piece|" + piece.GetInstanceID() + "|" + piece.sharedMesh.GetInstanceID() + "|" + piece.sharedMesh.vertexCount;
            var texture = EditorUtility.IsPersistent(piece) ? AssetPreview.GetAssetPreview(piece.sharedMesh) : ModelThumbnails.Get(piece.gameObject, key);
            if (texture != null) picture.image = texture;
            else if (!EditorUtility.IsPersistent(piece)) WaitForThumbnail(picture, piece.gameObject, key);
            var check = new VectorIcon(IconGlyph.Check);
            check.AddToClassList("refit-piece__check");
            card.Add(check);
            Text(card, piece.name, "refit-piece__name");
            if (piece != asset) OnPress(card, () => { SetAsset(piece); Render(); });
            return card;
        }

        private void WaitForThumbnail(Image image, GameObject root, string key)
        {
            pendingThumbnails.Add((image, root, key));
            if (thumbnailPoll != null) { thumbnailPoll.Resume(); return; }
            thumbnailPoll = rootVisualElement.schedule.Execute(() =>
            {
                for (int i = pendingThumbnails.Count - 1; i >= 0; i--)
                {
                    var (picture, owner, id) = pendingThumbnails[i];
                    if (picture.panel == null || owner == null) { pendingThumbnails.RemoveAt(i); continue; }
                    var texture = ModelThumbnails.Get(owner, id);
                    if (texture == null) continue;
                    picture.image = texture;
                    picture.AddToClassList("refit-piece__picture--in");
                    pendingThumbnails.RemoveAt(i);
                }
                if (pendingThumbnails.Count == 0) thumbnailPoll.Pause();
            }).Every(200);
        }

        // ------------------------------------------------------------------
        // Setup: made for, shapes, fit, placed by hand, then the one button
        // ------------------------------------------------------------------

        private void BuildSetup()
        {
            var card = Box(content, "refit-card");

            // Made for
            var madeRow = SetupRow(card, "Made for");
            var options = new List<(MadeFor choice, string label, string tip)>
            {
                (MadeFor.Target, targetAvatar != null && targetAvatar != myAvatar && myAvatar != null ? targetAvatar.name : "This avatar",
                    "It already fits " + (targetAvatar != null ? targetAvatar.name : "the avatar") + ": ReFit makes it follow body shapes.")
            };
            if (originalBase != null)
                options.Add((MadeFor.OriginalBase, ShortBaseName(), "Made for the original " + (originalBase.BaseName ?? "base") +
                    ": ReFit fits it to " + (originalBase.Name ?? targetAvatar.name) + "."));
            options.Add((MadeFor.Other, madeFor == MadeFor.Other && sourceAvatar != null ? sourceAvatar.name : "Other…",
                "Made for another avatar: pick it."));
            var made = new SegmentedControl(options.Select(o => new SegmentedControl.Option(o.label, null, o.tip)), index =>
            {
                madeFor = options[index].choice;
                if (madeFor != MadeFor.Other) { sourceAvatar = null; sourceBody = null; }
                UpdateMode();
                InvalidateChecks();
                Render();
            });
            made.AddToClassList("refit-setup__control");
            made.SetIndex(options.FindIndex(o => o.choice == madeFor));
            madeRow.Add(made);
            if (madeFor == MadeFor.Other)
            {
                var pick = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true, value = sourceAvatar, tooltip = "The avatar or base model it was made for" };
                pick.AddToClassList("refit-field");
                pick.AddToClassList("refit-setup__picker");
                pick.RegisterValueChangedCallback(e =>
                {
                    sourceAvatar = e.newValue as GameObject;
                    sourceBody = null;
                    UpdateMode();
                    InvalidateChecks();
                    Render();
                });
                card.Add(pick);
                // The scene's other avatars, one click each.
                var others = ReFitAutoSetup.SceneAvatars().Where(other => other != targetAvatar).Take(4).ToList();
                if (others.Count > 0)
                {
                    var line = Box(card, "refit-suggestions");
                    Text(line, "In the scene", "refit-suggestions__label");
                    foreach (var other in others)
                    {
                        var chip = Box(line, "refit-chip", "refit-chip--suggested");
                        chip.EnableInClassList("refit-chip--selected", other == sourceAvatar);
                        var icon = new VectorIcon(IconGlyph.Person);
                        icon.AddToClassList("refit-chip__icon");
                        chip.Add(icon);
                        Text(chip, other.name, "refit-chip__text");
                        var captured = other;
                        if (other != sourceAvatar)
                            OnPress(chip, () =>
                            {
                                sourceAvatar = captured;
                                sourceBody = null;
                                UpdateMode();
                                InvalidateChecks();
                                Render();
                            });
                    }
                }
            }
            Text(card, MadeForCaption(), "refit-setup__caption");

            // Body shapes
            BuildShapes(card);

            // Fit
            var fitRow = SetupRow(card, "Fit");
            var presets = new[] { ReFitAutoSetup.AccessoryTightness, BalancedTightness, ReFitAutoSetup.ClothingTightness };
            var fit = new SegmentedControl(new[]
            {
                new SegmentedControl.Option("Loose", null, "Accessories: keeps its own shape and room around the body"),
                new SegmentedControl.Option("Balanced"),
                new SegmentedControl.Option("Snug", null, "Clothing: follows the body closely where it grows")
            }, index =>
            {
                ApplyClearanceTightnessPreset(presets[index]);
                InvalidateChecks();
                fitCaption.text = FitCaption();
            });
            fit.AddToClassList("refit-setup__control");
            int fitIndex = !clearanceTightnessKnown ? -1 : Array.FindIndex(presets, p => Mathf.Abs(p - clearanceTightnessPreset) < 0.02f);
            fit.SetIndex(fitIndex);
            fitRow.Add(fit);
            fitCaption = Text(card, FitCaption(), "refit-setup__caption");

            // Placed by hand
            var handRow = SetupRow(card, "Placed by hand");
            var hand = new ToggleSwitch(!madeForAvatar, value =>
            {
                madeForAvatar = !value;
                InvalidateChecks();
                Render();
            });
            hand.AddToClassList("refit-setup__switch");
            handRow.Add(hand);
            Text(card, madeForAvatar ? "Off: it was made to fit, as it is." :
                "It sinks into the body here and there: ReFit also pushes it out where it clips, with the shapes too.", "refit-setup__caption");
            var outfit = OnItsAvatar(asset) ? OutfitParts(asset) : null;
            if (outfit != null && outfit.Count > 1)
            {
                var outfitRow = SetupRow(card, "Whole outfit");
                var together = new ToggleSwitch(refitOutfit, value => { refitOutfit = value; InvalidateChecks(); Render(); });
                together.AddToClassList("refit-setup__switch");
                together.tooltip = "Inner parts first, so the outer ones stay over them: " + string.Join(", ", outfit.Select(p => p.name));
                outfitRow.Add(together);
                Text(card, refitOutfit ? "Inner parts first, each keeping its side of the others: " + string.Join(", ", RunParts().Select(p => p.name)) + "."
                    : "Only this part; it keeps its side of the outfit's other parts.", "refit-setup__caption");
            }

            // The one button
            var run = new ReFitProgressButtonElement(
                StartExecution,
                () => new ReFitProgressButtonData
                {
                    text = isExecuting && !string.IsNullOrWhiteSpace(executionStep) ? executionStep : "ReFit " + (asset != null ? asset.name : ""),
                    enabled = !isExecuting && MissingChoice() == null,
                    isRunning = isExecuting,
                    progress = isExecuting ? executionProgress : 1f,
                    fillColor = ReFitGreen,
                    trackColor = ReFitProgressTrack
                });
            run.AddToClassList("refit-primary");
            Pressable(run);
            content.Add(run);

            var checks = Box(content, "refit-checks");
            checksBox = checks;
            ShowChecks();
            ScheduleChecks();

            var links = Box(content, "refit-links");
            links.Add(FlatButton("Advanced options", () => Go(Page.Settings), "refit-link"));
        }

        private Label fitCaption;
        private VisualElement checksBox;

        private static VisualElement SetupRow(VisualElement card, string title)
        {
            var row = Box(card, "refit-setup__row");
            Text(row, title, "refit-setup__label");
            return row;
        }

        private string ShortBaseName()
        {
            var name = originalBase?.BaseName;
            if (string.IsNullOrEmpty(name)) return "Original base";
            return name.Length > 18 ? name.Substring(0, 17) + "…" : name;
        }

        private string MadeForCaption()
        {
            string target = targetAvatar != null ? targetAvatar.name : "the avatar";
            switch (madeFor)
            {
                case MadeFor.OriginalBase:
                    return "Made for " + (originalBase?.BaseName ?? "the original base") + ": ReFit fits it to " +
                           (originalBase?.Name ?? target) + (originalBase != null && originalBase.Source == "MCB" ? " (from MCB)." : ".");
                case MadeFor.Other:
                    return sourceAvatar != null ? "Made for " + sourceAvatar.name + ": ReFit fits it to " + target + "."
                        : "Pick the avatar or base model it was made for.";
                default:
                    return "It already fits " + target + ": ReFit makes it follow the body shapes below.";
            }
        }

        private string FitCaption()
        {
            if (!clearanceTightnessKnown) return "Custom tightness (Advanced options).";
            string kind = assetIsClothing ? "Clothing fits snug by default." : "Accessories fit loose by default.";
            return kind + "  " + Mathf.RoundToInt(clearanceTightnessPreset * 100f) + "%";
        }

        private void BuildShapes(VisualElement card)
        {
            var row = SetupRow(card, "Body shapes");
            var chips = Box(row, "refit-shapes");
            foreach (var shape in blendshapes.ToList())
            {
                var chip = Box(chips, "refit-chip", "refit-chip--shape");
                Text(chip, shape, "refit-chip__text");
                var remove = new VectorIcon(IconGlyph.Close) { tooltip = "Remove" };
                remove.AddToClassList("refit-chip__remove");
                chip.Add(remove);
                string captured = shape;
                OnPress(chip, () => { blendshapes.Remove(captured); UpdateMode(); InvalidateChecks(); Render(); });
            }
            var add = Box(chips, "refit-chip", "refit-chip--add");
            var plus = new VectorIcon(shapesOpen ? IconGlyph.Chevron : IconGlyph.Plus);
            plus.AddToClassList("refit-chip__icon");
            add.Add(plus);
            Text(add, shapesOpen ? "Done" : blendshapes.Count == 0 ? "Add shapes" : "More", "refit-chip__text");
            OnPress(add, () => { shapesOpen = !shapesOpen; Render(); });

            if (activeShapes.Count > 0)
                Text(card, (targetAvatar != null ? targetAvatar.name : "The avatar") + " keeps " + Quote(activeShapes) +
                    " on: picked so the clothing follows " + (activeShapes.Count == 1 ? "it." : "them."), "refit-setup__caption");
            else
                Text(card, mode == ReFitMode.Blendshape && blendshapes.Count == 0 ? "Pick the shapes it should follow." :
                    "Each shape becomes a shape on the clothing that follows the body's.", "refit-setup__caption");

            // Suggestions: the custom base's own shapes, one click each.
            var suggested = originalBase != null ? originalBase.Shapes.Where(s => !blendshapes.Contains(s)).Take(6).ToList() : new List<string>();
            if (suggested.Count > 0 && !shapesOpen)
            {
                var line = Box(card, "refit-suggestions");
                Text(line, "Suggested", "refit-suggestions__label");
                foreach (var shape in suggested)
                {
                    var chip = Box(line, "refit-chip", "refit-chip--suggested");
                    var icon = new VectorIcon(IconGlyph.Plus);
                    icon.AddToClassList("refit-chip__icon");
                    chip.Add(icon);
                    Text(chip, shape, "refit-chip__text");
                    string captured = shape;
                    OnPress(chip, () => { blendshapes.Add(captured); UpdateMode(); InvalidateChecks(); Render(); });
                }
            }

            if (!shapesOpen) return;
            var body = ReFitAutoSetup.Body(targetAvatar, asset);
            if (body == null || body.sharedMesh == null || body.sharedMesh.blendShapeCount == 0)
            {
                Text(card, "The avatar's body has no blendshapes.", "refit-help");
                return;
            }
            var names = new List<string>();
            for (int i = 0; i < body.sharedMesh.blendShapeCount; i++) names.Add(body.sharedMesh.GetBlendShapeName(i));
            var picker = new ReFitBlendshapePicker(names, blendshapes, () =>
            {
                UpdateMode();
                InvalidateChecks();
                // The chips above follow the picker on its next close; the button state follows now.
            });
            picker.AddToClassList("refit-shape-picker");
            card.Add(picker);
        }

        private static string Quote(List<string> names) =>
            names.Count <= 2 ? string.Join(" and ", names.Select(n => "‘" + n + "’"))
                : "‘" + names[0] + "’ and " + (names.Count - 1) + " more";

        // ------------------------------------------------------------------
        // Checks (dry run), debounced after the last change
        // ------------------------------------------------------------------

        private void InvalidateChecks()
        {
            validateReport = null;
            validateKey = null;
        }

        private string ChecksKey() => (asset != null ? asset.GetInstanceID() : 0) + "|" + (targetAvatar != null ? targetAvatar.GetInstanceID() : 0) + "|" +
            (sourceAvatar != null ? sourceAvatar.GetInstanceID() : 0) + "|" + madeFor + "|" + mode + "|" + madeForAvatar + "|" + string.Join(",", blendshapes);

        private void ScheduleChecks()
        {
            checkUpdate?.Pause();
            if (MissingChoice() != null || madeFor == MadeFor.OriginalBase || validateKey == ChecksKey()) return;
            checkUpdate = rootVisualElement.schedule.Execute(() =>
            {
                if (isExecuting || page != Page.Studio || MissingChoice() != null) return;
                string key = ChecksKey();
                try { validateReport = ReFitService.Validate(BuildRequest(asset)); }
                catch (Exception ex) { validateReport = new ReFitReport(); validateReport.Warn("check-failed", ex.Message); }
                validateKey = key;
                ShowChecks();
            }).StartingIn(700);
        }

        private void ShowChecks()
        {
            if (checksBox == null) return;
            checksBox.Clear();
            string missing = MissingChoice();
            if (missing != null) { Text(checksBox, missing, "refit-checks__hint"); return; }
            if (madeFor == MadeFor.OriginalBase)
            {
                Text(checksBox, "Ready. " + (originalBase?.BaseName ?? "The original base") + " is opened for the refit only.", "refit-checks__ready");
                return;
            }
            if (validateReport == null) { Text(checksBox, "Checking the setup…", "refit-checks__hint"); return; }
            bool any = false;
            foreach (var m in validateReport.messages)
                if (m.severity != ReFitSeverity.Info) { AddMessage(checksBox, m); any = true; }
            if (!any) Text(checksBox, "Ready. Warnings never block: bones moved on purpose are fine.", "refit-checks__ready");
        }

        // ------------------------------------------------------------------
        // Stage pictures
        // ------------------------------------------------------------------

        private void RequestBeforePicture()
        {
            if (asset == null || beforeScene != null) return;
            pictureUpdate?.Pause();
            pictureUpdate = rootVisualElement.schedule.Execute(() =>
            {
                if (asset == null || isExecuting || page == Page.Result || beforeScene != null) return;
                SetBeforeScene(MakeScene(asset));
            }).StartingIn(60);
        }

        private void RequestAfterPicture()
        {
            var renderer = lastResult?.sceneRenderer;
            if (renderer == null || afterScene != null) return;
            rootVisualElement.schedule.Execute(() =>
            {
                if (page != Page.Result || lastResult?.sceneRenderer == null || afterScene != null) return;
                var made = MakeScene(lastResult.sceneRenderer);
                afterScene = made;
                ShowPictures();
            }).StartingIn(30);
        }

        /// <summary>Before a run, the avatar wearing the piece as it is now: the stage compares it with the result.</summary>
        private void CaptureBeforePictures()
        {
            if (asset == null) return;
            ClearPictures(false);
            SetBeforeScene(MakeScene(asset));
        }

        // The stage switches to the new scene before the old one is freed.
        private void SetBeforeScene(ReFitScenePicture.Scene made)
        {
            var old = beforeScene;
            beforeScene = made;
            ShowPictures();
            if (old != null && old != made) old.Dispose();
        }

        // The avatar wearing the piece, baked (scripts, constraints and physics never run). A project file is shown on its
        // own, from a copy in a preview scene that is closed again at once.
        private ReFitScenePicture.Scene MakeScene(SkinnedMeshRenderer piece)
        {
            if (piece == null || piece.sharedMesh == null) return null;
            var avatar = targetAvatar != null && !EditorUtility.IsPersistent(targetAvatar) ? targetAvatar : null;
            var preview = default(UnityEngine.SceneManagement.Scene);
            pictureError = null;
            try
            {
                if (EditorUtility.IsPersistent(piece))
                {
                    preview = EditorSceneManager.NewPreviewScene();
                    var source = piece.transform.root.gameObject;
                    var copy = PrefabUtility.InstantiatePrefab(source, preview) as GameObject;
                    if (copy == null) throw new InvalidOperationException("Its file could not be opened.");
                    string path = AnimationUtility.CalculateTransformPath(piece.transform, source.transform);
                    var part = string.IsNullOrEmpty(path) ? copy.transform : copy.transform.Find(path);
                    piece = part != null ? part.GetComponent<SkinnedMeshRenderer>() : null;
                    if (piece == null) throw new InvalidOperationException("Its mesh was not found in its file.");
                    avatar = copy;
                }
                return ReFitScenePicture.Collect(avatar, piece, null);
            }
            catch (Exception ex)
            {
                pictureError = ex.Message;
                Debug.LogWarning("[ReFit] Could not show " + (piece != null ? piece.name : "the piece") + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private void ClearPictures(bool before)
        {
            var oldAfter = afterScene;
            var oldBefore = before ? beforeScene : null;
            afterScene = null;
            if (before) { beforeScene = null; pictureError = null; }
            ShowPictures();
            oldAfter?.Dispose();
            oldBefore?.Dispose();
        }

        // ------------------------------------------------------------------
        // The result
        // ------------------------------------------------------------------

        private void BuildResult()
        {
            bool ok = lastResult != null && lastResult.success;
            var card = Box(content, "refit-card", "refit-result");
            var heading = Box(card, "refit-result__heading");
            Box(heading, "refit-result__dot", ok ? "refit-result__dot--ok" : "refit-result__dot--failed");
            string title;
            if (outfitResults.Count > 0)
            {
                int done = outfitResults.Count(r => r.result != null && r.result.success);
                title = done == outfitResults.Count ? $"The {done} parts of the outfit are re-fitted" : $"{done} of {outfitResults.Count} parts re-fitted";
            }
            else title = ok ? (asset != null ? asset.name : "The asset") + " is re-fitted" : "ReFit could not complete";
            Text(heading, title, "refit-result__title");
            if (ok) Text(card, Comparing() ? "Drag the handle on the stage to compare before and after; drag elsewhere to turn." : "The result is applied in the scene. Ctrl+Z undoes it.", "refit-setup__caption");

            if (outfitResults.Count > 0)
                foreach (var item in outfitResults)
                {
                    bool partOk = item.result != null && item.result.success;
                    SummaryRow(card, item.part != null ? item.part.name : "-", partOk ? "Re-fitted" : "Not re-fitted");
                    if (!partOk && item.result != null)
                        foreach (var m in item.result.report.messages)
                            if (m.severity == ReFitSeverity.Error) AddMessage(card, m);
                }

            if (lastResult != null)
            {
                var shapes = lastResult.GeneratedShapeNames();
                if (shapes.Length > 0)
                {
                    var chips = Box(card, "refit-shapes", "refit-result__shapes");
                    foreach (var shape in shapes) { var chip = Box(chips, "refit-chip", "refit-chip--generated"); Text(chip, shape, "refit-chip__text"); }
                }
                if (!string.IsNullOrEmpty(lastResult.meshAssetPath)) SummaryRow(card, "Mesh", lastResult.meshAssetPath);
                if (!string.IsNullOrEmpty(lastResult.prefabAssetPath)) SummaryRow(card, "Prefab", lastResult.prefabAssetPath);
                if (lastResult.gravityShapeNames != null && lastResult.gravityShapeNames.Length > 0)
                    SummaryRow(card, "Gravity", string.Join(", ", lastResult.gravityShapeNames) + " at " + lastResult.gravityDefaultWeight.ToString("0.#"));
                foreach (var m in lastResult.report.messages)
                    if (m.severity != ReFitSeverity.Info) AddMessage(card, m);

                var actions = Box(card, "refit-result__actions");
                if (ok && lastResult.sceneRenderer != null)
                    actions.Add(FlatButton("Select", () =>
                    {
                        Selection.activeGameObject = lastResult.sceneRenderer.gameObject;
                        EditorGUIUtility.PingObject(lastResult.sceneRenderer.gameObject);
                    }, "refit-button--ghost"));
                if (!string.IsNullOrEmpty(lastResult.meshAssetPath))
                    actions.Add(FlatButton("Show mesh", () => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Mesh>(lastResult.meshAssetPath)), "refit-button--ghost"));
                if (ok && lastResult.sceneRenderer != null && outfitResults.Count == 0)
                    actions.Add(FlatButton("Undo", UndoResult, "refit-button--ghost"));
                var spacer = Box(actions, "refit-grow");
                spacer.pickingMode = PickingMode.Ignore;
                actions.Add(FlatButton(ok ? "Fit another" : "Back", BackToStudio, "refit-button--primary"));
            }

            if (gravityPreview != null && gravityPreview.frames != null && gravityPreview.frames.Length > 0) BuildGravity();
            BuildCommissionSuggestions();
        }

        private bool Comparing() => lastResult?.sceneRenderer != null && beforeScene != null;

        private void BuildGravity()
        {
            var card = Box(content, "refit-card");
            var heading = Box(card, "refit-result__heading");
            Text(heading, "Gravity", "refit-section");
            Text(heading, "beta", "refit-pill");
            Text(card, "Body clothing hangs a little with the body's shapes. The cyan wire in the Scene view shows it at this weight.", "refit-setup__caption");
            var slider = new Slider(0f, ReFitGravityPreviewService.MaximumGravityWeight) { value = gravityPreviewWeight };
            slider.AddToClassList("refit-slider");
            slider.RegisterValueChangedCallback(e =>
            {
                gravityPreviewWeight = e.newValue;
                ReFitGravityPreviewService.SetWeight(gravityPreviewWeight);
            });
            card.Add(slider);
            var actions = Box(card, "refit-result__actions");
            actions.Add(FlatButton("Skip", () =>
            {
                ReFitGravityPreviewService.ClearPreview(gravityPreview);
                gravityPreview = null;
                Render();
            }, "refit-button--ghost"));
            var spacer = Box(actions, "refit-grow");
            spacer.pickingMode = PickingMode.Ignore;
            actions.Add(FlatButton("Add gravity shapes", () =>
            {
                ReFitGravityPreviewService.Apply(lastResult, gravityPreview, gravityPreviewWeight);
                gravityPreview = null;
                Render();
            }, "refit-button--primary"));
        }

        // Takes the last result back: the renderer as it was, and the record the Orbiters tools keep.
        private void UndoResult()
        {
            var renderer = lastResult?.sceneRenderer;
            if (renderer == null) return;
            var entry = SessionLog.FindLast(e => e != null && e.sceneRenderer == renderer && IsActive(e));
            bool restored = entry != null && entry.originalRendererState != null && entry.originalRendererState.Restore(renderer, "ReFit undo");
            if (entry != null && !restored && entry.originalMesh != null)
            {
                Undo.RecordObject(renderer, "ReFit undo");
                renderer.sharedMesh = entry.originalMesh;
                EditorUtility.SetDirty(renderer);
                restored = true;
            }
            var record = ReFitRecordIntegration.GetRefittedAssets().FirstOrDefault(a => a?.renderer == renderer);
            if (record != null) ReFitRecordIntegration.TryReset(record, restored);
            BackToStudio();
        }

        // ------------------------------------------------------------------
        // Re-fitted assets of the project and of this session
        // ------------------------------------------------------------------

        private void BuildRefittedAssets()
        {
            SessionLog.RemoveAll(e => e == null || e.sceneRenderer == null);
            var recordedAssets = ReFitRecordIntegration.GetRefittedAssets();
            var recordedRendererIds = new HashSet<int>();
            foreach (var item in recordedAssets)
                if (item?.renderer != null) recordedRendererIds.Add(item.renderer.GetInstanceID());

            var activeSessionEntries = new List<RefitLogEntry>();
            for (int i = SessionLog.Count - 1; i >= 0; i--)
            {
                var entry = SessionLog[i];
                if (IsActive(entry) && !recordedRendererIds.Contains(entry.sceneRenderer.GetInstanceID()))
                    activeSessionEntries.Add(entry);
            }
            if (recordedAssets.Count == 0 && activeSessionEntries.Count == 0) return;

            var section = Box(content, "refit-section-block");
            Text(section, "Re-fitted", "refit-section");
            foreach (var item in recordedAssets)
            {
                var captured = item;
                AddRefittedAssetRow(section, captured.DisplayName, captured.renderer, () =>
                {
                    // A refit from this session also undoes its armature replacement; the record is then only dropped.
                    var sessionEntry = SessionLog.FindLast(e => e?.originalRendererState != null &&
                                                                e.sceneRenderer == captured.renderer && IsActive(e));
                    bool restored = sessionEntry != null && sessionEntry.originalRendererState.Restore(sessionEntry.sceneRenderer, "ReFit revert");
                    if (ReFitRecordIntegration.TryReset(captured, restored) || sessionEntry != null) Render();
                });
            }
            foreach (var entry in activeSessionEntries)
            {
                var captured = entry;
                AddRefittedAssetRow(section, entry.assetName, entry.sceneRenderer, () => RevertEntry(captured));
            }
        }

        private void AddRefittedAssetRow(VisualElement parent, string displayName, SkinnedMeshRenderer renderer, Action resetAction)
        {
            var row = Box(parent, "refit-row");
            Box(row, "refit-row__dot");
            var name = Text(row, displayName, "refit-row__name");
            name.tooltip = displayName;
            var capturedRenderer = renderer;
            row.Add(FlatButton("Select", () =>
            {
                if (capturedRenderer == null) return;
                Selection.activeGameObject = capturedRenderer.gameObject;
                EditorGUIUtility.PingObject(capturedRenderer.gameObject);
            }, "refit-button--small"));
            row.Add(FlatButton("Reset", resetAction, "refit-button--small"));
        }

        private static bool IsActive(RefitLogEntry entry) =>
            entry.sceneRenderer != null && entry.refitMesh != null && entry.sceneRenderer.sharedMesh == entry.refitMesh &&
            (entry.originalRendererState != null || entry.originalMesh != null);

        private void RevertEntry(RefitLogEntry entry)
        {
            if (entry?.sceneRenderer != null)
            {
                if (entry.originalRendererState != null)
                    entry.originalRendererState.Restore(entry.sceneRenderer, "ReFit revert");
                else if (entry.originalMesh != null)
                {
                    Undo.RecordObject(entry.sceneRenderer, "ReFit revert");
                    entry.sceneRenderer.sharedMesh = entry.originalMesh;
                    EditorUtility.SetDirty(entry.sceneRenderer);
                }
            }
            Render();
        }
    }
}
