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

        private void BuildActiveCommissions()
        {
            var section = new VisualElement { name = "refit-commission-section" };
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
            if (current != Step.AssetLocation || isExecuting || commissionList == null) return;
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
            ReFitCommissionClient.FetchActiveCommissions(more ? commissionCursor : null, (page, error) =>
            {
                if (this == null || generation != commissionGeneration || scope != ReFitCommissionClient.AccountScope) return;
                commissionListLoading = false;
                commissionListError = error;
                if (page != null)
                {
                    if (!more) activeCommissions.Clear();
                    var ids = new HashSet<string>();
                    foreach (var item in activeCommissions) ids.Add(item.id);
                    foreach (var item in page.requests)
                        if (item != null && item.type == "REFIT" && item.progress != null && item.progress.active && ids.Add(item.id))
                            activeCommissions.Add(item);
                    commissionCursor = page.nextCursor;
                    commissionListPaged = more;
                }
                if (current == Step.AssetLocation) DrawCommissions();
            });
        }

        private void DrawCommissions()
        {
            if (commissionList == null) return;
            commissionList.Clear();
            commissionRefresh?.SetEnabled(!commissionListLoading && ReFitCommissionClient.HasAccount);
            if (!ReFitCommissionClient.HasAccount)
            {
                commissionList.Add(new Label("Sign in through MCB to see your commissions."));
                return;
            }
            foreach (var item in activeCommissions)
                commissionList.Add(CreateCommissionRow(item));
            if (!string.IsNullOrEmpty(commissionListError)) commissionList.Add(new Label(commissionListError));
            else if (activeCommissions.Count == 0)
                commissionList.Add(new Label(commissionListLoading || double.IsNegativeInfinity(commissionLastFetch)
                    ? "Loading commissions..." : "No ReFit commissions in progress."));
            if (!string.IsNullOrEmpty(commissionCursor))
            {
                var more = new Button(() => LoadCommissions(true)) { text = "Load more" };
                more.AddToClassList("refit-commission-more");
                more.SetEnabled(!commissionListLoading);
                commissionList.Add(more);
            }
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
    }
}
