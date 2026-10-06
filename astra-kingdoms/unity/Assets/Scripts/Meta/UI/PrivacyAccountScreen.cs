using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Privacy;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>
    /// Privacy and account: neutral age question, separate consent switches (analytics, crash reports,
    /// personalised ads for adults only), the discoverable in-app deletion path with a confirming
    /// second tap, and the external deletion, privacy and grievance links.
    /// </summary>
    public sealed class PrivacyAccountScreen : MetaScreen
    {
        private int _age = 18;
        private bool _ageEdited;
        private bool _confirmDelete;

        public PrivacyAccountScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta)
            : base(ctx, ui, parent, meta, "MetaPrivacyScreen")
        {
            SetTitle(L("meta.privacy.title"));
        }

        protected override Task Build(RectTransform content)
        {
            AudienceProfile audience = Meta.Backend.Audience;
            string ageText = _ageEdited ? _age.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : audience.AgeGroup == AgeGroup.Unknown ? L("meta.privacy.ageUnknown") : audience.AgeGroup.ToString();
            Line(content, LF("meta.privacy.age", ageText));
            RectTransform ageRow = RowOf(content);
            Ui.Button(ageRow, L("meta.privacy.ageMinus"), () => StepAge(-1), UiTheme.Panel);
            Ui.Button(ageRow, L("meta.privacy.agePlus"), () => StepAge(+1), UiTheme.Panel);
            Ui.Button(ageRow, L("meta.privacy.ageConfirm"), async () => { await Meta.Api.SetDeclaredAgeAsync(_age); _ageEdited = false; Refresh(); }, UiTheme.Button);
            Ui.Button(ageRow, L("meta.privacy.ageSkip"), async () => { await Meta.Api.SetDeclaredAgeAsync(null); _ageEdited = false; Refresh(); }, UiTheme.Button);

            AnalyticsConsent c = Meta.Backend.AnalyticsConsent;
            bool analytics = c.ProductAnalytics == ConsentChoice.Granted, crash = c.CrashReports == ConsentChoice.Granted;
            bool ads = Meta.Backend.PersonalisedAdsConsent;
            Ui.Button(content, LF("meta.privacy.analytics", OnOff(analytics)), () => SetConsent(!analytics, crash, ads), UiTheme.Button, UiFactory.SizeSmall);
            Ui.Button(content, LF("meta.privacy.crash", OnOff(crash)), () => SetConsent(analytics, !crash, ads), UiTheme.Button, UiFactory.SizeSmall);
            if (audience.AgeGroup == AgeGroup.Adult)
                Ui.Button(content, LF("meta.privacy.personalisedAds", OnOff(ads)), () => SetConsent(analytics, crash, !ads), UiTheme.Button, UiFactory.SizeSmall);

            Ui.Button(content, _confirmDelete ? L("meta.privacy.deleteConfirm") : L("meta.privacy.delete"), async () =>
            {
                if (!_confirmDelete)
                {
                    _confirmDelete = true;
                    Refresh();
                    return;
                }
                _confirmDelete = false;
                DeletionState state = await Meta.Api.RequestDeletionAsync();
                Meta.Analytics.ResetAnalyticsId();
                Meta.Analytics.OnConsentChanged();
                Report(state == DeletionState.Completed ? L("meta.privacy.deleteDone") : state.ToString());
            }, UiTheme.ButtonDanger, UiFactory.SizeSmall);
            RectTransform links = RowOf(content);
            Ui.Button(links, L("meta.privacy.deleteWeb"), () => Meta.OpenUrl(Meta.Links.AccountDeletionUrl), UiTheme.Panel, UiFactory.SizeSmall);
            Ui.Button(links, L("meta.privacy.policy"), () => Meta.OpenUrl(Meta.Links.PrivacyPolicyUrl), UiTheme.Panel, UiFactory.SizeSmall);
            Ui.Button(links, L("meta.privacy.grievance"), () => Meta.OpenUrl(Meta.Links.GrievanceUrl), UiTheme.Panel, UiFactory.SizeSmall);
            Line(content, LF("meta.privacy.path", Meta.Links.InAppPath), UiFactory.SizeSmall, UiTheme.TextMuted);
            return Task.CompletedTask;
        }

        private string OnOff(bool on) => on ? L("meta.privacy.on") : L("meta.privacy.off");

        private void StepAge(int delta)
        {
            _age = Mathf.Clamp(_age + delta, 5, 99);
            _ageEdited = true;
            Refresh();
        }

        private void SetConsent(bool analytics, bool crash, bool ads)
        {
            Meta.SetConsent(new AnalyticsConsent(analytics ? ConsentChoice.Granted : ConsentChoice.Denied, crash ? ConsentChoice.Granted : ConsentChoice.Denied), ads);
            Refresh();
        }
    }
}
