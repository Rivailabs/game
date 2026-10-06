using System;
using System.Linq;
using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Progression;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>Profile: level, XP, earned coins, next unlock, owned practice weapons, mastery; links to the other meta screens.</summary>
    public sealed class ProfileScreen : MetaScreen
    {
        public event Action OpenTasks;
        public event Action OpenLocker;
        public event Action OpenShop;
        public event Action OpenPrivacy;

        public ProfileScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta)
            : base(ctx, ui, parent, meta, "MetaProfileScreen")
        {
            SetTitle(L("meta.profile.title"));
        }

        protected override async Task Build(RectTransform content)
        {
            ProfileView view = await Meta.Api.GetProfileAsync();
            ProgressionProfile p = view.Progress;
            if (view.IsGuest) Line(content, L("meta.profile.guest"), UiFactory.SizeSmall, UiTheme.TextMuted);
            Line(content, p.Level >= ProgressionRules.MaxLevel
                ? LF("meta.profile.levelMax", p.Level, p.XpIntoLevel)
                : LF("meta.profile.level", p.Level), UiFactory.SizeLarge);
            if (p.Level < ProgressionRules.MaxLevel) Line(content, LF("meta.profile.xp", p.XpIntoLevel));
            Line(content, LF("meta.profile.coins", p.EarnedCoins));
            Line(content, view.NextUnlocks.Count == 0
                ? L("meta.profile.nextNone")
                : LF("meta.profile.next", string.Join(", ", view.NextUnlocks.Select(Describe))), UiFactory.SizeSmall);
            Line(content, LF("meta.profile.weapons", p.OwnedWeaponIds.Count), UiFactory.SizeSmall, UiTheme.TextMuted);
            var badges = p.Mastery.Where(kv => kv.Value != MasteryTier.None).OrderBy(kv => kv.Key)
                .Select(kv => Ctx.T("weapon." + kv.Key) + " " + kv.Value).ToList();
            Line(content, badges.Count == 0 ? L("meta.profile.masteryNone") : LF("meta.profile.mastery", string.Join(", ", badges)), UiFactory.SizeSmall);

            RectTransform row = RowOf(content);
            Ui.Button(row, L("meta.profile.tasks"), () => OpenTasks?.Invoke(), UiTheme.Button);
            Ui.Button(row, L("meta.profile.locker"), () => OpenLocker?.Invoke(), UiTheme.Button);
            Ui.Button(row, L("meta.profile.shop"), () => OpenShop?.Invoke(), UiTheme.ButtonPrimary);
            Ui.Button(content, L("meta.profile.privacy"), () => OpenPrivacy?.Invoke(), UiTheme.Panel, UiFactory.SizeSmall);
        }

        private string Describe(LevelUnlock u) =>
            u.Kind == UnlockKind.Weapon
                ? LF("meta.unlock.weapon", Ctx.T("weapon." + u.WeaponId))
                : LF("meta.unlock.cosmetic", Meta.Backend.Cosmetics.Get(u.CosmeticId)?.EnglishName ?? u.CosmeticId);
    }
}
