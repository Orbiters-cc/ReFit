using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.ReFit.Editor
{
    public partial class ReFitWizard
    {
        private IVisualElementScheduledItem commissionPoll;
        private VisualElement commissionList;
        private Button commissionRefresh;
        private readonly List<ReFitCommissionClient.Commission> activeCommissions = new List<ReFitCommissionClient.Commission>();
        private string commissionScope;
        private string commissionCursor;
        private string commissionListError;
        private bool commissionListLoading;
        private bool commissionListPaged;
        private int commissionGeneration;
        private double commissionLastFetch = double.NegativeInfinity;

        /// <summary>Opens the result page of a refit made by another Orbiters tool, where the user can commission a creator.</summary>
        public static void OpenForCommission(ReFitRequest request, ReFitResult result)
        {
            if (request == null || result == null) return;
            var window = Summon();
            if (window.isExecuting) return;
            window.Restart();
            window.asset = request.assetRenderer;
            window.myAvatar = window.targetAvatar = request.targetAvatar;
            window.sourceAvatar = request.sourceAvatar;
            window.madeFor = request.sourceAvatar != null && request.sourceAvatar != request.targetAvatar ? MadeFor.Other : MadeFor.Target;
            window.mode = request.mode;
            window.settings = request.settings?.Clone() ?? new ReFitSettings();
            if (request.targetBlendshapes != null) window.blendshapes.AddRange(request.targetBlendshapes);
            window.lastRequest = request;
            window.lastResult = result;
            window.lastSourceName = request.sourceAvatar != null ? request.sourceAvatar.name : null;
            window.page = Page.Result;
            window.Show();
            window.Render();
            if (result.success && result.sceneRenderer != null) window.RequestAfterPicture();
        }

        private void BuildActiveCommissions()
        {
            var section = new VisualElement { name = "refit-commission-section" };
            section.AddToClassList("refit-section-block");
            var heading = new VisualElement();
            heading.AddToClassList("refit-commission-section-heading");
            var title = new Label("ReFit commissions in progress");
            title.AddToClassList("refit-section");
            heading.Add(title);
            commissionRefresh = new Button(() => LoadCommissions(false)) { tooltip = "Refresh commissions" };
            commissionRefresh.Add(new Image { image = EditorGUIUtility.IconContent("Refresh").image, scaleMode = ScaleMode.ScaleToFit });
            commissionRefresh.AddToClassList("refit-commission-refresh");
            heading.Add(commissionRefresh);
            section.Add(heading);
            commissionList = new VisualElement { name = "refit-active-commissions" };
            section.Add(commissionList);
            content.Add(section);
            EnsureCommissionScope();
            DrawCommissions();
        }

        private void EnsureCommissionScope()
        {
            string scope = ReFitCommissionClient.AccountScope;
            if (commissionScope == scope) return;
            commissionScope = scope;
            commissionGeneration++;
            activeCommissions.Clear();
            commissionCursor = null;
            commissionListError = null;
            commissionListLoading = false;
            commissionListPaged = false;
            commissionLastFetch = double.NegativeInfinity;
        }

        private void PollCommissions()
        {
            if (page != Page.Studio || isExecuting || commissionList == null) return;
            EnsureCommissionScope();
            if (!ReFitCommissionClient.HasAccount) { DrawCommissions(); return; }
            if (!commissionListLoading && !commissionListPaged && EditorApplication.timeSinceStartup - commissionLastFetch >= 30)
                LoadCommissions(false);
        }

        private void LoadCommissions(bool more)
        {
            EnsureCommissionScope();
            if (commissionListLoading || !ReFitCommissionClient.HasAccount) return;
            commissionListLoading = true;
            commissionLastFetch = EditorApplication.timeSinceStartup;
            int generation = commissionGeneration;
            string scope = commissionScope;
            DrawCommissions();
            ReFitCommissionClient.FetchActiveCommissions(more ? commissionCursor : null, (result, error) =>
            {
                if (this == null || generation != commissionGeneration || scope != ReFitCommissionClient.AccountScope) return;
                commissionListLoading = false;
                commissionListError = error;
                if (result != null)
                {
                    if (!more) activeCommissions.Clear();
                    var ids = new HashSet<string>();
                    foreach (var item in activeCommissions) ids.Add(item.id);
                    foreach (var item in result.requests)
                        if (item != null && item.type == "REFIT" && item.progress != null && item.progress.active && ids.Add(item.id))
                            activeCommissions.Add(item);
                    commissionCursor = result.nextCursor;
                    commissionListPaged = more;
                }
                if (page == Page.Studio) DrawCommissions();
            });
        }

        private void DrawCommissions()
        {
            if (commissionList == null) return;
            commissionList.Clear();
            commissionRefresh?.SetEnabled(!commissionListLoading && ReFitCommissionClient.HasAccount);
            if (!ReFitCommissionClient.HasAccount)
            {
                commissionList.Add(Muted("Sign in through MCB or My Avatar to see your commissions."));
                return;
            }
            foreach (var item in activeCommissions)
                commissionList.Add(CreateCommissionRow(item));
            if (!string.IsNullOrEmpty(commissionListError)) commissionList.Add(Muted(commissionListError));
            else if (activeCommissions.Count == 0)
                commissionList.Add(Muted(commissionListLoading || double.IsNegativeInfinity(commissionLastFetch)
                    ? "Loading commissions\u2026" : "No ReFit commissions in progress."));
            if (!string.IsNullOrEmpty(commissionCursor))
            {
                var more = new Button(() => LoadCommissions(true)) { text = "Load more" };
                more.AddToClassList("refit-commission-more");
                more.SetEnabled(!commissionListLoading);
                commissionList.Add(more);
            }
        }

        private static Label Muted(string text)
        {
            var label = new Label(text);
            label.AddToClassList("refit-help");
            return label;
        }

        internal static VisualElement CreateCommissionRow(ReFitCommissionClient.Commission item, bool loadImage = true)
        {
            var row = new Button(() => Application.OpenURL(ReFitCommissionClient.CommissionUrl(item.id)));
            row.AddToClassList("refit-commission-row");
            var heading = new VisualElement();
            heading.AddToClassList("refit-commission-heading");
            var title = new Label(item.name) { tooltip = item.name };
            title.AddToClassList("refit-commission-name");
            heading.Add(title);
            var label = new Label(item.progress?.label ?? "") { tooltip = item.progress?.label ?? "" };
            label.AddToClassList("refit-commission-status");
            heading.Add(label);
            var creator = new VisualElement();
            creator.AddToClassList("refit-commission-creator");
            var avatar = new VisualElement();
            avatar.AddToClassList("refit-commission-avatar");
            creator.Add(avatar);
            var creatorName = new Label(item.acceptedCreator?.username ?? "No creator assigned");
            creatorName.AddToClassList("refit-commission-creator-name");
            creator.Add(creatorName);
            if (loadImage && item.acceptedCreator != null)
                ReFitCommissionClient.LoadCircularTexture(item.acceptedCreator.avatarUrl, texture =>
                {
                    if (texture != null) avatar.style.backgroundImage = new StyleBackground(texture);
                });
            heading.Add(creator);
            row.Add(heading);
            var track = new VisualElement();
            track.AddToClassList("refit-commission-track");
            var fill = new VisualElement();
            fill.AddToClassList("refit-commission-fill");
            float percent = item.progress?.percent ?? 0;
            fill.style.width = Length.Percent(float.IsNaN(percent) ? 0 : Mathf.Clamp(percent, 0, 100));
            track.Add(fill);
            row.Add(track);
            return row;
        }

        private void BuildCommissionSuggestions()
        {
            EnsureCommissionCreators();

            var section = new VisualElement();
            section.AddToClassList("refit-commission-section");
            var title = new Label("Doesn't look right?");
            title.AddToClassList("refit-commission-title");
            section.Add(title);
            var subtitle = new Label("Commission an artist for a manual refit. They see four pictures of the result.");
            subtitle.AddToClassList("refit-commission-subtitle");
            section.Add(subtitle);

            if (commissionCreatorsLoading)
            {
                var loading = new Label("Loading available creators...");
                loading.AddToClassList("refit-help");
                section.Add(loading);
            }
            else if (!string.IsNullOrEmpty(commissionError))
            {
                var error = new Label(commissionError);
                error.AddToClassList("refit-msg");
                error.AddToClassList("refit-msg--warning");
                section.Add(error);
                var retry = new Button(() =>
                {
                    commissionCreators = null;
                    commissionError = null;
                    EnsureCommissionCreators();
                    Render();
                }) { text = "Try again" };
                retry.AddToClassList("refit-button");
                retry.AddToClassList("refit-button--ghost");
                section.Add(retry);
            }
            else if (commissionCreators == null || commissionCreators.Length == 0)
            {
                var unavailable = new Label("No creator currently lists ReFit commissions.");
                unavailable.AddToClassList("refit-help");
                section.Add(unavailable);
            }
            else
            {
                // Two cards a row, wrapping: every creator fits the column, whatever its width.
                var grid = new VisualElement();
                grid.AddToClassList("refit-commission-grid");
                foreach (var creator in commissionCreators) grid.Add(CreateCommissionCreatorCard(creator));
                section.Add(grid);

            }

            content.Add(section);
        }

        private VisualElement CreateCommissionCreatorCard(ReFitCommissionCreator creator)
        {
            var card = new Button(() => OpenCommissionWebsite(creator.id));
            card.text = string.Empty;
            card.AddToClassList("refit-commission-card");
            card.SetEnabled(!commissionHandoffLoading);
            card.tooltip = commissionHandoffLoading ? "Opening Orbiters..." : "Request a manual refit on Orbiters";

            var banner = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            banner.AddToClassList("refit-commission-banner");
            card.Add(banner);
            ReFitCommissionClient.LoadTexture(creator.bannerUrl, texture =>
            {
                if (banner == null) return;
                if (texture != null)
                {
                    banner.image = texture;
                    return;
                }
                ReFitCommissionClient.LoadTexture(creator.avatarUrl, fallback =>
                {
                    if (banner != null) banner.image = fallback;
                });
            });

            var identity = new VisualElement();
            identity.AddToClassList("refit-commission-identity");
            var avatarFrame = new VisualElement();
            avatarFrame.AddToClassList("refit-commission-avatar");
            var avatar = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            avatar.AddToClassList("refit-commission-avatar-image");
            avatarFrame.Add(avatar);
            identity.Add(avatarFrame);
            ReFitCommissionClient.LoadCircularTexture(creator.avatarUrl, texture =>
            {
                if (avatar != null) avatar.image = texture;
            });
            // Name over price: the name keeps the card's whole width, however narrow the column.
            var text = new VisualElement();
            text.AddToClassList("refit-commission-text");
            var name = new Label(string.IsNullOrWhiteSpace(creator.username) ? "Creator" : creator.username);
            name.AddToClassList("refit-commission-name");
            name.tooltip = name.text;
            text.Add(name);
            var price = new Label(ReFitCommissionClient.PriceLabel(creator));
            price.AddToClassList("refit-commission-price");
            price.tooltip = price.text;
            text.Add(price);
            identity.Add(text);
            card.Add(identity);
            return card;
        }

        private void EnsureCommissionCreators()
        {
            if (commissionCreators != null || commissionCreatorsLoading) return;
            commissionCreatorsLoading = true;
            commissionError = null;
            bool requestedDevEnvironment = ReFitCommissionClient.IsDevEnvironment;
            ReFitCommissionClient.FetchCreators((creators, error) =>
            {
                commissionCreatorsLoading = false;
                if (requestedDevEnvironment != ReFitCommissionClient.IsDevEnvironment)
                {
                    commissionCreators = null;
                    commissionError = null;
                    return;
                }
                commissionCreators = creators ?? Array.Empty<ReFitCommissionCreator>();
                commissionError = error;
                if (page == Page.Result) Render();
            });
        }

        private void OpenCommissionWebsite(int creatorId)
        {
            if (commissionHandoffLoading) return;
            commissionHandoffLoading = true;
            commissionError = null;
            Render();

            var payload = new ReFitCommissionHandoffRequest
            {
                creatorIds = new List<int> { creatorId },
                details = new ReFitCommissionDetails
                {
                    assetName = asset != null ? asset.name : string.Empty,
                    sourceAvatar = lastSourceName ?? (sourceAvatar != null ? sourceAvatar.name : string.Empty),
                    targetAvatar = targetAvatar != null ? targetAvatar.name : string.Empty,
                    blendshape = mode == ReFitMode.MeshToMesh ? string.Empty : string.Join(", ", blendshapes),
                    mode = mode.ToString()
                }
            };
            List<ReFitCommissionPhoto> photos;
            try
            {
                photos = ReFitCommissionCapture.Capture(targetAvatar, lastResult?.sceneRenderer != null ? lastResult.sceneRenderer : asset,
                    lastResult?.primaryShapeName ?? settings.blendshapeName);
            }
            catch (Exception exception)
            {
                commissionHandoffLoading = false;
                commissionError = "Could not capture avatar previews: " + exception.Message;
                Render();
                return;
            }
            ReFitCommissionClient.CreateHandoff(payload, (url, error) =>
            {
                commissionHandoffLoading = false;
                commissionError = error;
                if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
                if (page == Page.Result) Render();
            }, photos);
        }
    }
}
