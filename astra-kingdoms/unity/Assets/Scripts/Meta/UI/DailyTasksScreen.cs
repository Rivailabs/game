using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Economy;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>The three optional daily cosmetic tasks with idempotent claims (ticket 58).</summary>
    public sealed class DailyTasksScreen : MetaScreen
    {
        public DailyTasksScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta)
            : base(ctx, ui, parent, meta, "MetaDailyTasksScreen")
        {
            SetTitle(L("meta.tasks.title"));
        }

        protected override async Task Build(RectTransform content)
        {
            Line(content, L("meta.tasks.hint"), UiFactory.SizeSmall, UiTheme.TextMuted);
            foreach (DailyTaskView t in await Meta.Api.GetDailyTasksAsync())
            {
                RectTransform row = RowOf(content);
                Ui.Label(row, LF("meta.tasks.row", t.Definition.EnglishText, t.Progress, t.Definition.Target, t.Definition.RewardCoins),
                    UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
                if (t.Claimable)
                {
                    string id = t.Definition.Id, day = t.DayKey;
                    Ui.Button(row, L("meta.tasks.claim"), async () =>
                    {
                        ClaimResult r = await Meta.Api.ClaimDailyTaskAsync(id, day);
                        Report(LF("meta.tasks.result", r.Status));
                    }, UiTheme.ButtonPrimary);
                }
                else
                {
                    Ui.Label(row, t.Claimed ? L("meta.tasks.claimed") : L("meta.tasks.notDone"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
                }
            }
            RewardedOffer.AddTo(content, Ui, Meta, ScreenContext.DailyTasks, Report);
        }
    }
}
