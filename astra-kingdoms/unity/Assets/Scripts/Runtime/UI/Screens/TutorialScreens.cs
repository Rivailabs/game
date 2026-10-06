using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Client.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Tutorial coach panel (ticket 45): the current step's instruction, a Continue button when the
    /// step waits for one, and after a reveal the outcome — with "why you lost this volley" reasons
    /// built from the engine's record when the volley went against the player. It sits on top of the
    /// real match screens and never hides the actual rules or result.
    /// </summary>
    public sealed class TutorialOverlay : UiScreen
    {
        private readonly Text _prompt;
        private readonly Text _detail;
        private readonly Button _continue;

        public event Action ContinueTapped;

        public TutorialOverlay(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "TutorialOverlay", new Color(0, 0, 0, 0), false)
        {
            RectTransform box = ui.Panel(Root, "Coach", UiTheme.Panel);
            UiFactory.Region(box, 0.28f, 0.36f, 0.68f, 0.84f);
            RectTransform col = ui.Column(box, "Column", 8, 16);
            UiFactory.Stretch(col);
            ui.Label(col, T("tutorial.title"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.Warning);
            _prompt = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.UpperLeft, UiTheme.Text);
            _detail = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.UpperLeft, UiTheme.TextMuted);
            UiFactory.Flexible(col);
            _continue = ui.Button(col, T("common.continue"), () => ContinueTapped?.Invoke(), UiTheme.ButtonPrimary);
        }

        public void ShowStep(TutorialMachine machine, IReadOnlyList<TextRef> details = null)
        {
            _prompt.text = T(machine.PromptKey);
            var lines = new List<string>();
            if (details != null) foreach (TextRef d in details) lines.Add(TF(d.Key, d.Args));
            _detail.text = string.Join("\n", lines);
            bool waits = machine.IsEnabled(TutorialControl.Continue) ||
                         (machine.Step == TutorialStep.ReadOutcome && machine.OutcomeShown);
            _continue.gameObject.SetActive(waits);
            Show();
        }
    }

    /// <summary>Offered once after the first-run settings: start the guided duel, or go Home.</summary>
    public sealed class TutorialOfferScreen : UiScreen
    {
        public event Action<bool> Chosen;

        public TutorialOfferScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "TutorialOffer", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 24, 40);
            UiFactory.Region(col, 0.25f, 0.15f, 0.75f, 0.85f);
            ui.Label(col, T("tutorial.offerTitle"), UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            ui.Label(col, T("tutorial.offerHint"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            UiFactory.Flexible(col);
            ui.Button(col, T("tutorial.start"), () => Chosen?.Invoke(true), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Button(col, T("tutorial.skip"), () => Chosen?.Invoke(false), UiTheme.Button);
        }
    }

    /// <summary>
    /// Pause, background and connection overlay (ticket 47), driven by <see cref="SessionLifecycle"/>.
    /// The resume cover is opaque so a private entry screen is never exposed after returning to the
    /// app; online states show reconnecting/lost choices and the server's phase after recovery.
    /// </summary>
    public sealed class SessionOverlayScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _hint;
        private readonly Button _primary;
        private readonly Button _secondary;
        private SessionOverlay _shown;

        public event Action Resume;
        public event Action Leave;
        public event Action OpenSettings;

        public SessionOverlayScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "SessionOverlay", UiTheme.Opaque)
        {
            RectTransform col = ui.Column(Root, "Column", 24, 40);
            UiFactory.Region(col, 0.25f, 0.2f, 0.75f, 0.8f);
            UiFactory.Flexible(col);
            _title = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _hint = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _primary = ui.Button(col, string.Empty, () => Resume?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            _secondary = ui.Button(col, string.Empty, OnSecondary, UiTheme.Button);
            UiFactory.Flexible(col);
        }

        public void Present(SessionLifecycle life)
        {
            _shown = life.Overlay;
            if (_shown == SessionOverlay.None)
            {
                Hide();
                return;
            }
            _title.text = T("session.overlay." + _shown);
            switch (_shown)
            {
                case SessionOverlay.ResumeCover:
                    _hint.text = T("session.resumeHint");
                    Buttons("session.continue", null);
                    break;
                case SessionOverlay.PauseMenu:
                    _hint.text = T("hud.paused");
                    Buttons("hud.resume", "home.settings");
                    break;
                case SessionOverlay.Reconnecting:
                    _hint.text = T("session.reconnecting");
                    Buttons(null, "session.leave");
                    break;
                case SessionOverlay.ConnectionLost:
                    _hint.text = T("session.lost");
                    Buttons("session.keepWaiting", "session.leave");
                    break;
                case SessionOverlay.Recovered:
                    _hint.text = TF("session.recovered", T(life.RecoveredPhaseKey ?? "phase.unknown"));
                    Buttons("session.continue", null);
                    break;
            }
            Show();
        }

        private void Buttons(string primaryKey, string secondaryKey)
        {
            _primary.gameObject.SetActive(primaryKey != null);
            if (primaryKey != null) UiFactory.ButtonLabel(_primary).text = T(primaryKey);
            _secondary.gameObject.SetActive(secondaryKey != null);
            if (secondaryKey != null) UiFactory.ButtonLabel(_secondary).text = T(secondaryKey);
        }

        private void OnSecondary()
        {
            if (_shown == SessionOverlay.PauseMenu) OpenSettings?.Invoke();
            else Leave?.Invoke();
        }
    }

    /// <summary>
    /// Weapons, mastery and practice (ticket 44): every weapon with its element shape, whether this
    /// account owns it or it is loaned in Full rooms, mastery stars (recognition only), and a practice
    /// drill against a labelled bot. Nothing here changes combat values or what an opponent may use.
    /// </summary>
    public sealed class WeaponsScreen : UiScreen
    {
        private readonly RectTransform _list;
        private readonly Text _summary;

        public event Action<int> PracticeWeapon;
        public event Action Closed;

        public WeaponsScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "WeaponsScreen", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 6, 24);
            UiFactory.Region(col, 0.08f, 0.02f, 0.92f, 0.98f);
            ui.Label(col, T("weapons.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _summary = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _list = ui.Column(col, "List", 4, 0);
            UiFactory.Flexible(col);
            ui.Button(col, T("common.back"), () => Closed?.Invoke(), UiTheme.Button);
        }

        public void Open(int accountLevel, IReadOnlyDictionary<int, int> volleysFired)
        {
            for (int i = _list.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_list.GetChild(i).gameObject);
            var model = new LoadoutModel(Rules.Core.CatalogPreset.Full, accountLevel);
            _summary.text = TF("weapons.summary", accountLevel);
            foreach (LoadoutRow row in model.Rows)
            {
                int id = row.Weapon.Id;
                int fired = 0;
                if (volleysFired != null) volleysFired.TryGetValue(id, out fired);
                int stars = Mastery.Stars(fired);
                string access = row.Access == WeaponAccess.Owned ? T("loadout.owned") : TF("weapons.unlocksAt", row.Weapon.UnlockLevel);
                Button b = WeaponButton.Create(Ui, _list, Ctx, row.Weapon, () => PracticeWeapon?.Invoke(id));
                UiFactory.ButtonLabel(b).text = TF("weapons.row", WeaponButton.Caption(Ctx, row.Weapon), access, Stars(stars), fired);
                // Owned weapons have their drill; any other weapon is practised on a Full-room loan.
                b.interactable = true;
            }
            Show();
        }

        private static string Stars(int n) => new string('★', n) + new string('☆', Mastery.StarThresholds.Length - n);
    }
}
