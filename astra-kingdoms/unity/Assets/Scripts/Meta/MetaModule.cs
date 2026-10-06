using System;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.MatchFlow;
using AstraKingdoms.Client.Meta.UI;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using UnityEngine;

namespace AstraKingdoms.Client.Meta
{
    /// <summary>
    /// Plugs the V1 meta screens into the existing client through one additive hook
    /// (<see cref="GameFlow.UiBuilt"/>): a "Profile and shop" button on Home, the meta overlay
    /// screens, local match reporting for the guest profile, and analytics instrumentation.
    /// Nothing in the match flow depends on this module.
    /// </summary>
    public static class MetaModule
    {
        private static MetaServices _services;
        private static GameFlow _wiredFlow;

        public static MetaServices Services => _services;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            GameFlow.UiBuilt -= OnUiBuilt;
            GameFlow.UiBuilt += OnUiBuilt;
        }

        private static bool InMatch(GameFlow flow)
        {
            LocalMatchHost h = flow != null ? flow.Host : null;
            return h != null && h.Stage != HostStage.MatchOver;
        }

        private static void OnUiBuilt(GameFlow flow)
        {
            try
            {
                if (_services == null)
                {
                    _services = MetaServices.Create(flow.Context.Settings.Language, () => InMatch(_wiredFlow), GameBootstrap.DevelopmentTools);
                    _services.Analytics.Track(AnalyticsEventType.ValidSession, AnalyticsEvents.ValidSession(Application.version, GameBootstrap.DevelopmentTools));
                }
                if (_wiredFlow != flow)
                {
                    _wiredFlow = flow;
                    flow.MatchFinished += OnMatchFinished;
                    MatchController controller = flow.GetComponent<MatchController>();
                    if (controller != null) controller.MatchStarted += OnMatchStarted;
                    if (flow.GetComponent<MetaLifecycle>() == null) flow.gameObject.AddComponent<MetaLifecycle>();
                }
                BuildScreens(flow);
            }
            catch (Exception ex)
            {
                // The meta layer must never break the core game loop.
                Debug.LogException(ex);
            }
        }

        private static void BuildScreens(GameFlow flow)
        {
            Transform root = flow.Canvas.transform;
            UiFactory ui = flow.Ui;
            var profile = new ProfileScreen(flow.Context, ui, root, _services);
            var tasks = new DailyTasksScreen(flow.Context, ui, root, _services);
            var locker = new LockerScreen(flow.Context, ui, root, _services);
            var shop = new ShopScreen(flow.Context, ui, root, _services);
            var privacy = new PrivacyAccountScreen(flow.Context, ui, root, _services);
            profile.OpenTasks += tasks.Open;
            profile.OpenLocker += locker.Open;
            profile.OpenShop += shop.Open;
            profile.OpenPrivacy += privacy.Open;
            foreach (MetaScreen child in new MetaScreen[] { tasks, locker, shop, privacy }) child.Closed += profile.Open;

            RectTransform entry = ui.Button(flow.Home.Root, _services.Text.L("meta.home.button"), profile.Open, UiTheme.Panel, UiFactory.SizeSmall,
                "MetaEntry").GetComponent<RectTransform>();
            UiFactory.Region(entry, 0.78f, 0.9f, 0.98f, 0.98f);
        }

        private static string MatchIdToken(string id) => AnalyticsSchema.IsWellFormedToken(id) ? id : "invalid";

        private static MatchKind KindFor(GameFlow flow) => flow.Mode == PlayMode.Practice ? MatchKind.Practice : MatchKind.LocalSharedPhone;

        private static void OnMatchStarted(LocalMatchHost host)
        {
            if (_services == null || _wiredFlow == null) return;
            AnalyticsClient a = _services.Analytics;
            bool automation = _wiredFlow.Mode == PlayMode.Automation;
            TrafficFlags flags = automation ? TrafficFlags.Automation | TrafficFlags.Bot : TrafficFlags.None;
            string matchId = MatchIdToken(host.Engine.MatchId);
            a.Track(AnalyticsEventType.MatchStart, AnalyticsEvents.MatchStart(matchId, KindFor(_wiredFlow), host.Config.Catalog), flags);
            host.VolleyResolved += v =>
            {
                a.Track(AnalyticsEventType.Resolution, AnalyticsClient.Props("match_id", matchId, "round", (long)v.Round, "volley", (long)v.Volley), flags);
                foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
                {
                    if (host.Seat(side) != SeatKind.Human) continue;
                    a.Track(AnalyticsEventType.Lock, AnalyticsClient.Props("match_id", matchId, "round", (long)v.Round, "volley", (long)v.Volley,
                        "timed_out", v.Result.Explanation[side].IsPass), flags);
                }
            };
            host.CutApplied += (side, cells) =>
                a.Track(AnalyticsEventType.LandCutCompletion, AnalyticsClient.Props("match_id", matchId, "round", (long)host.Engine.RoundIndex,
                    "cells", (long)cells, "method", cells == 0 ? "timeout" : "drawn"), flags);
        }

        private static async void OnMatchFinished(LocalMatchHost host, MatchResult result)
        {
            try
            {
                if (_services == null || _wiredFlow == null) return;
                bool automation = _wiredFlow.Mode == PlayMode.Automation;
                MatchKind kind = KindFor(_wiredFlow);
                MatchRecord record = host.ToRecord();
                bool developerTest = GameBootstrap.DevelopmentTools && !MetaServices.GrantInDevelopmentBuilds;
                // Practice: the person is seat A. Shared phone: the device profile cannot be attributed to a seat.
                MatchOutcomeReport report = MatchReportBuilder.FromRecord(record, _services.Backend.PlayerId, PlayerSide.A, kind, DateTimeOffset.UtcNow,
                    attributeOutcome: kind == MatchKind.Practice, isAutomation: automation, isDeveloperTest: developerTest);
                TrafficFlags flags = automation ? TrafficFlags.Automation | TrafficFlags.Bot : TrafficFlags.None;
                _services.Analytics.Track(AnalyticsEventType.MatchEnd, AnalyticsEvents.MatchEnd(
                    new MatchOutcomeReport(MatchIdToken(report.MatchResultId), report.PlayerId, report.Kind, report.Outcome, report.Ending, report.CompletedAt),
                    result.RoundsPlayed), flags);
                MatchGrantResult grant = await _services.Api.ReportLocalMatchAsync(report);
                if (grant.Status == GrantStatus.Granted && grant.LevelAfter > grant.LevelBefore)
                    Debug.Log("[Meta] Level " + grant.LevelAfter + " reached (" + grant.NewUnlocks.Count + " unlock(s)).");
                _services.Analytics.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
