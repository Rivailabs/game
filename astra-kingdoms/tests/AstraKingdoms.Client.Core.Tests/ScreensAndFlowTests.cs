using AstraKingdoms.Client.Flow;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Settings;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Tests;

/// <summary>Screens and usability tickets 43-47.</summary>
public sealed class ScreensAndFlowTests
{
    private sealed class Store : IKeyValueStore, IKeyValueEraser
    {
        public readonly Dictionary<string, object> Values = new();
        public bool HasKey(string key) => Values.ContainsKey(key);
        public float GetFloat(string key, float fallback) => Values.TryGetValue(key, out object v) ? (float)v : fallback;
        public int GetInt(string key, int fallback) => Values.TryGetValue(key, out object v) ? (int)v : fallback;
        public string GetString(string key, string fallback) => Values.TryGetValue(key, out object v) ? (string)v : fallback;
        public void SetFloat(string key, float value) => Values[key] = value;
        public void SetInt(string key, int value) => Values[key] = value;
        public void SetString(string key, string value) => Values[key] = value;
        public void Save() { }
        public void DeleteKey(string key) => Values.Remove(key);
    }

    private static Localizer English() => new(LocalizationTable.Parse("en", File.ReadAllText(Path.Combine(Paths.Localization, "en.txt"))));

    // ------------------------------------------------------------------ 43

    [Test]
    public void SharedPhone_PracticeAndTutorial_WorkOffline()
    {
        foreach (Connectivity c in Enum.GetValues<Connectivity>())
        {
            var modes = ModeCatalog.Build(c, onlineServiceConfigured: false);
            Assert.That(modes.Single(m => m.Mode == PlayModeId.SharedPhone).Available, Is.True);
            Assert.That(modes.Single(m => m.Mode == PlayModeId.Practice).Available, Is.True);
            Assert.That(modes.Single(m => m.Mode == PlayModeId.Tutorial).Available, Is.True);
            Assert.That(modes.Where(m => m.RequiresNetwork).All(m => !m.Available && m.UnavailableKey == "mode.unavailable.notInThisBuild"), Is.True);
        }
        var offline = ModeCatalog.Build(Connectivity.Offline, onlineServiceConfigured: true);
        Assert.That(offline.Single(m => m.Mode == PlayModeId.PlayOnline).UnavailableKey, Is.EqualTo("mode.unavailable.offline"));
        Assert.That(ModeCatalog.Build(Connectivity.Online, true).All(m => m.Available), Is.True);
        Assert.That(offline.Where(m => m.Primary).Select(m => m.Mode), Is.EqualTo(new[] { PlayModeId.PlayOnline, PlayModeId.Practice, PlayModeId.FriendRoom }),
            "home priorities: Play, Practice, Play with a Friend");
        LocalizationTable en = English().EnglishTable;
        foreach (ModeEntry m in offline)
        {
            Assert.That(en.Contains(m.TitleKey) && en.Contains(m.HintKey), Is.True, m.Mode.ToString());
            if (m.UnavailableKey != null) Assert.That(en.Contains(m.UnavailableKey), Is.True);
        }
        Assert.That(en.Contains("mode.unavailable.notInThisBuild"), Is.True);
    }

    [Test]
    public void Launch_AsksSettingsThenOffersTheTutorial()
    {
        var s = new SettingsModel();
        Assert.That(LaunchFlow.First(s), Is.EqualTo(LaunchScreen.FirstRunSettings));
        s.FirstRunComplete = true;
        Assert.That(LaunchFlow.First(s), Is.EqualTo(LaunchScreen.TutorialOffer));
        s.TutorialOffered = true;
        Assert.That(LaunchFlow.First(s), Is.EqualTo(LaunchScreen.Home));
    }

    // ------------------------------------------------------------------ 44

    [TestCase(CatalogPreset.Starter)]
    [TestCase(CatalogPreset.Full)]
    public void Loadout_LoansAreClearAndNeverRestrictTheCatalogue(CatalogPreset preset)
    {
        IReadOnlyList<int> catalogue = LoadoutModel.RoomCatalogue(preset);
        foreach (int level in new[] { 1, 2, 8, 16, 20 })
        {
            var m = new LoadoutModel(preset, level);
            Assert.That(m.Rows.Select(r => r.Weapon.Id), Is.EqualTo(catalogue), "the same catalogue at every account level");
            foreach (LoadoutRow r in m.Rows)
                Assert.That(r.Access, Is.EqualTo(r.Weapon.UnlockLevel <= level ? WeaponAccess.Owned : WeaponAccess.Loaned));
            Assert.That(m.EquippedCount, Is.EqualTo(m.MaxSlots));
            Assert.DoesNotThrow(() => Loadout.Create(preset, m.EquippedIds()), "the default is a legal loadout");
        }
        Assert.That(new LoadoutModel(CatalogPreset.Full, 1).AnyLoaned, Is.True);
        Assert.That(new LoadoutModel(CatalogPreset.Starter, 1).AnyLoaned, Is.False);
    }

    [Test]
    public void Loadout_SlotAndReserveRulesMatchTheRules()
    {
        var m = new LoadoutModel(CatalogPreset.Full, 20);
        int[] six = m.EquippedIds();
        int spare = m.Rows.First(r => !r.Equipped).Weapon.Id;
        Assert.That(m.Toggle(spare), Is.EqualTo("loadout.full"));
        Assert.That(m.SetReserve(six[0]), Is.EqualTo("loadout.reserveDistinct"));
        Assert.That(m.SetReserve(spare), Is.Null);
        Assert.DoesNotThrow(() => Loadout.Create(CatalogPreset.Full, m.EquippedIds(), m.Reserve));
        Assert.That(m.Toggle(six[0]), Is.Null);
        Assert.That(m.Reserve, Is.EqualTo(0), "a reserve needs six equipped");
        Assert.That(new LoadoutModel(CatalogPreset.Starter, 1).SetReserve(1), Is.EqualTo("loadout.reserveFullOnly"));
        var one = new LoadoutModel(CatalogPreset.Starter, 1);
        foreach (int id in one.EquippedIds().Skip(1)) one.Toggle(id);
        Assert.That(one.Toggle(one.EquippedIds()[0]), Is.EqualTo("loadout.needOne"));
        Assert.That(Mastery.Stars(0), Is.EqualTo(0));
        Assert.That(Mastery.Stars(20), Is.EqualTo(2));
        Assert.That(Mastery.DrillsAvailable(1), Has.Count.EqualTo(5));
        LocalizationTable en = English().EnglishTable;
        foreach (string k in new[] { "loadout.full", "loadout.reserveDistinct", "loadout.reserveFullOnly", "loadout.reserveNeedsSix", "loadout.needOne", "loadout.notInRoom" })
            Assert.That(en.Contains(k), Is.True, k);
    }

    // ------------------------------------------------------------------ 45

    [Test]
    public void Tutorial_IntroducesOneChoiceAtATime()
    {
        var t = new TutorialMachine(untimed: true);
        Assert.That(t.Enabled, Is.EqualTo(TutorialControl.Continue));
        Assert.That(t.Handle(TutorialEvent.WeaponSelected), Is.False, "nothing happens out of order");
        t.Handle(TutorialEvent.Continue);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.ChooseWeapon));
        Assert.That(t.IsEnabled(TutorialControl.Aim), Is.False);
        t.Handle(TutorialEvent.WeaponSelected);
        Assert.That(t.IsEnabled(TutorialControl.Aim) && t.IsEnabled(TutorialControl.Power) && !t.IsEnabled(TutorialControl.Dodge), Is.True);
        t.Handle(TutorialEvent.AimChanged);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.AimAndPower), "power is part of the same lesson");
        t.Handle(TutorialEvent.PowerChanged);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.ChooseDodge));
        Assert.That(t.IsEnabled(TutorialControl.Lock), Is.False);
        t.Handle(TutorialEvent.DodgeSelected);
        Assert.That(t.IsEnabled(TutorialControl.Lock), Is.True);
        t.Handle(TutorialEvent.Locked);
        Assert.That(t.Enabled, Is.EqualTo(TutorialControl.None), "watch the reveal");
        t.Handle(TutorialEvent.RevealFinished);
        Assert.That(t.Handle(TutorialEvent.Continue), Is.False, "cannot skip the outcome explanation");
        t.Handle(TutorialEvent.OutcomeExplained);
        t.Handle(TutorialEvent.Continue);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.FreePlay));
        t.Handle(TutorialEvent.CutWindowOpened);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.DrawCut));
        Assert.That(t.Enabled, Is.EqualTo(TutorialControl.Card | TutorialControl.Cut));
        t.Handle(TutorialEvent.CutConfirmed);
        t.Handle(TutorialEvent.CutWindowOpened);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.FreePlay), "the cut lesson is taught once");
        t.Handle(TutorialEvent.MatchEnded);
        Assert.That(t.Step, Is.EqualTo(TutorialStep.AdvancedStatuses), "statuses come after the core loop");
        t.Handle(TutorialEvent.Continue);
        Assert.That(t.Finished, Is.True);
        LocalizationTable en = English().EnglishTable;
        foreach (TutorialStep s in Enum.GetValues<TutorialStep>()) Assert.That(en.Contains("tutorial.step." + s), Is.True, s.ToString());
    }

    [Test]
    public void Tutorial_UntimedOptionRemovesDeadlines_AndTheMatchIsReal()
    {
        HostTimings untimed = new TutorialMachine(true).Timings();
        Assert.That(untimed.ChoiceSeconds, Is.GreaterThan(3600));
        Assert.That(new TutorialMachine(false).Timings().ChoiceSeconds, Is.EqualTo(RulesConstants.ChoiceDeadlineMs / 1000.0));
        // The tutorial plays a real practice match against a labelled Easy bot; nothing is scripted.
        MatchFactory.ForAutoplay(3, out byte[] seed, out string id);
        var host = new LocalMatchHost(MatchConfig.Pilot(MatchMode.Practice), seed, id, SeatKind.Human, SeatKind.Bot, BotDifficulty.Easy, null, untimed);
        host.Start();
        Assert.That(host.Stage, Is.EqualTo(HostStage.LoadoutEntry));
        host.SubmitLoadout(PlayerSide.A, new[] { 1, 2, 3, 4, 5 });
        host.Tick(10_000);
        Assert.That(host.Stage, Is.EqualTo(HostStage.Entry), "untimed: the choice waits for the player");
        Assert.That(host.StageSecondsRemaining, Is.GreaterThan(3600));
    }

    [Test]
    public void LossExplanation_ComesFromTheVolleyRecord()
    {
        Loadout l = Loadout.Create(CatalogPreset.Starter, new[] { 1, 2, 3, 4, 5 });
        // A passes (times out) while B fires: A's loss must say so, and the HP summary is exact.
        VolleyResult r = Duel.Start(1, TerrainType.Plain, PlayerSide.B, l, l).Resolve(VolleyInput.Pass(), new VolleyInput(1, 60, 0, 100, Dodge.None));
        Localizer loc = English();
        List<TextRef> why = LossExplanation.Build(r.Explanation, PlayerSide.A, s => s.ToString(), loc);
        Assert.That(why.Select(w => w.Key), Does.Contain("tutorial.why.pass"));
        TextRef hp = why.Single(w => w.Key == "tutorial.why.hpSummary");
        Assert.That(hp.Args[0], Is.EqualTo(Hp.Format(r.Explanation.A.HpBeforeUnits - r.Explanation.A.HpAfterUnits)));
        foreach (TextRef w in why) Assert.That(loc.EnglishTable.Contains(w.Key), Is.True, w.Key);
        foreach (string k in new[] { "tutorial.why.clashed", "tutorial.why.theyDodged", "tutorial.why.blocked", "tutorial.why.missed", "tutorial.why.elementBeatYou", "tutorial.why.noDodge" })
            Assert.That(loc.EnglishTable.Contains(k), Is.True, k);
    }

    // ------------------------------------------------------------------ 46

    [Test]
    public void Settings_NewAccessibilityOptionsPersist_AndDataDeletionResets()
    {
        var store = new Store();
        var s = new SettingsModel { ReducedMotion = true, ShowPatterns = false, TutorialUntimed = false, TutorialOffered = true, TutorialCompleted = true, FirstRunComplete = true, Language = "hi" };
        s.Save(store);
        var t = new SettingsModel();
        t.Load(store);
        Assert.That((t.ReducedMotion, t.ShowPatterns, t.TutorialUntimed, t.TutorialOffered, t.TutorialCompleted), Is.EqualTo((true, false, false, true, true)));
        Assert.That(t.ShakeDisabled, Is.True, "reduced motion also disables camera shake");
        Assert.That(SettingsModel.Keys.All(k => store.HasKey(k)), Is.True, "every persisted key is known to the delete action");

        var controls = new LocalDataControls();
        bool recordsDeleted = false;
        controls.Register(LocalDataControls.SettingsCategory(store, store, t));
        controls.Register(new LocalDataCategory("records", "data.records", () => recordsDeleted = true));
        controls.Register(new LocalDataCategory("broken", "data.records", () => throw new IOException("disk")));
        Assert.That(controls.DeleteAll(), Is.EqualTo(new[] { "broken" }), "failures are reported, not hidden");
        Assert.That(store.Values, Is.Empty);
        Assert.That(recordsDeleted, Is.True);
        Assert.That((t.FirstRunComplete, t.Language, t.ShowPatterns), Is.EqualTo((false, Localizer.English, true)));
        LocalizationTable en = English().EnglishTable;
        foreach (string k in new[] { "data.settings", "data.records", "data.practice" }) Assert.That(en.Contains(k), Is.True, k);
    }

    // ------------------------------------------------------------------ 47

    [Test]
    public void Background_StopsTheLocalClockAndCoversPrivateScreens()
    {
        var life = new SessionLifecycle();
        life.MatchStarted(SessionMatchKind.SharedPhone);
        Assert.That(life.HostDelta(0.033), Is.EqualTo(0.033));
        Assert.That(life.TogglePause(), Is.False, "player pause is practice-only");
        life.ApplicationPaused(true);
        Assert.That(life.HidesPrivateScreens, Is.True);
        Assert.That(life.HostDelta(0.033), Is.EqualTo(0));
        life.ApplicationPaused(false);
        Assert.That(life.HostDelta(300), Is.EqualTo(0), "the resume frame (time spent away) is dropped");
        Assert.That(life.Overlay, Is.EqualTo(SessionOverlay.ResumeCover));
        Assert.That(life.HostDelta(0.033), Is.EqualTo(0), "the clock waits under the cover");
        life.ResumeTapped();
        Assert.That(life.HostDelta(0.033), Is.EqualTo(0.033));

        // Driving a real host through the lifecycle never expires a deadline while away.
        MatchFactory.ForAutoplay(9, out byte[] seed, out string id);
        var host = new LocalMatchHost(MatchConfig.Pilot(), seed, id, SeatKind.Human, SeatKind.Human);
        host.Start();
        host.ConfirmReady(); host.SubmitLoadout(host.StageSide!.Value, new[] { 1 });
        host.ConfirmReady(); host.SubmitLoadout(host.StageSide!.Value, new[] { 1 });
        host.Tick(life.HostDelta(2.0));
        Assert.That(host.Stage, Is.EqualTo(HostStage.EntryReady));
        double before = host.StageSecondsRemaining;
        life.ApplicationPaused(true);
        host.Tick(life.HostDelta(600));
        life.ApplicationPaused(false);
        host.Tick(life.HostDelta(600));
        Assert.That(host.StageSecondsRemaining, Is.EqualTo(before));
    }

    [Test]
    public void Practice_PauseMenu_AndOnlineRecoveryShowsTheAuthoritativePhase()
    {
        var life = new SessionLifecycle();
        life.MatchStarted(SessionMatchKind.Practice);
        Assert.That(life.TogglePause(), Is.True);
        Assert.That(life.Overlay, Is.EqualTo(SessionOverlay.PauseMenu));
        Assert.That(life.HostDelta(1), Is.EqualTo(0));
        life.ResumeTapped();
        Assert.That(life.HostDelta(1), Is.EqualTo(1));

        life.MatchStarted(SessionMatchKind.Online);
        Assert.That(life.HostDelta(1), Is.EqualTo(0), "the server owns the online clock");
        life.ConnectionChanged(ConnectionState.Reconnecting);
        Assert.That(life.Overlay, Is.EqualTo(SessionOverlay.Reconnecting));
        life.ConnectionChanged(ConnectionState.Lost);
        Assert.That(life.Overlay, Is.EqualTo(SessionOverlay.ConnectionLost));
        life.AuthoritativePhaseRestored("phase.selection");
        Assert.That((life.Overlay, life.RecoveredPhaseKey), Is.EqualTo((SessionOverlay.Recovered, "phase.selection")));
        LocalizationTable en = English().EnglishTable;
        foreach (string k in new[] { "session.resumeTitle", "session.resumeHint", "session.reconnecting", "session.lost", "session.recovered", "session.keepWaiting", "session.leave", "session.continue", "phase.selection" })
            Assert.That(en.Contains(k), Is.True, k);
    }
}
