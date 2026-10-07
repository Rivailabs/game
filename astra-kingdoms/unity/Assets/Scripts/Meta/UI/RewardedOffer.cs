using System;
using AstraKingdoms.Client.Meta.Platform;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Client;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>
    /// The optional rewarded-ad button (ticket 63). It appears only outside a match, only where the
    /// policy has a placement, only when the audience rules allow an offer, and only when an ad SDK
    /// is present. Declining or a failure simply leaves things as they were.
    /// </summary>
    public static class RewardedOffer
    {
        public const string Placement = "rewarded.home";

        public static void AddTo(Transform parent, UiFactory ui, MetaServices meta, ScreenContext context, Action<string> done)
        {
            if (meta.IsInMatch() || meta.AdProvider is UnavailableRewardedAdProvider) return;
            LocalMetaBackend b = meta.Backend;
            OfferDecision d = AdRules.CanOfferRewarded(b.AdPolicy, context, b.Audience, b.Ads.RewardedToday(b.PlayerId));
            if (d != OfferDecision.Available) return;
            meta.Analytics.Track(AnalyticsEventType.AdRewardState, AnalyticsEvents.AdRewardState(Placement, "opportunity"));
            ui.Button(parent, meta.Text.LF("meta.ads.offer", b.AdPolicy.RewardedCoins), async () =>
            {
                meta.Analytics.Track(AnalyticsEventType.AdRewardState, AnalyticsEvents.AdRewardState(Placement, "opted_in"));
                var (result, _, ticketId) = await meta.AdFlow.RunAsync(context, b.PersonalisedAdsConsent);
                if (result == AdFlowResult.AwaitingVerification && meta.DevelopmentBuild && meta.AdProvider is FakeRewardedAdProvider)
                {
                    // Development only: stand in for the ad network's signed server callback.
                    if (b.DevelopmentSimulateVerifiedCallback(ticketId, "dev-" + ticketId) == CallbackOutcome.Granted) result = AdFlowResult.Rewarded;
                }
                meta.Analytics.Track(AnalyticsEventType.AdRewardState, AnalyticsEvents.AdRewardState(Placement, StateToken(result)));
                done?.Invoke(meta.Text.LF("meta.ads.result", result));
            }, UiTheme.Panel, UiFactory.SizeSmall, "RewardedOffer");
        }

        private static string StateToken(AdFlowResult r)
        {
            switch (r)
            {
                case AdFlowResult.Rewarded: return "reward_verified";
                case AdFlowResult.Declined: return "declined";
                case AdFlowResult.AwaitingVerification: return "impression";
                default: return "failed";
            }
        }
    }
}
