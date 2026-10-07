using System;
using System.Collections.Generic;
using System.Globalization;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Combat
{
    /// <summary>
    /// Concise, localized explanation of a resolved volley (plan: "Player journey"): hit or miss,
    /// element multiplier, dodge or cover effect and remaining HP. Every number comes from the
    /// engine's <see cref="VolleyExplanation"/>; nothing is recomputed. Detailed breakdowns stay in
    /// the replay/help view (<see cref="VolleyExplanation.ToText"/>).
    /// </summary>
    public sealed class ExplanationBuilder
    {
        private readonly Localizer _loc;
        private readonly Func<PlayerSide, string> _name;

        public ExplanationBuilder(Localizer localizer, Func<PlayerSide, string> playerName)
        {
            _loc = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _name = playerName ?? throw new ArgumentNullException(nameof(playerName));
        }

        /// <summary>Lines for the shared screen. Concealed sides show "veiled" instead of weapon details.</summary>
        public List<string> Build(VolleyExplanation e, bool concealA = false, bool concealB = false)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            var lines = new List<string>(6);
            foreach (PlayerSide attacker in new[] { PlayerSide.A, PlayerSide.B })
            {
                bool conceal = attacker == PlayerSide.A ? concealA : concealB;
                lines.Add(AttackLine(e, attacker, conceal));
            }
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
                lines.Add(HpLine(e[side]));
            if (e.ResultAfter != DuelResult.InProgress) lines.Add(ResultLine(e.ResultAfter));
            return lines;
        }

        public string AttackLine(VolleyExplanation e, PlayerSide attacker, bool conceal)
        {
            PlayerSide target = CombatGeometry.Opponent(attacker);
            PlayerVolleyReport own = e[attacker];
            PlayerVolleyReport tgt = e[target];
            string who = _name(attacker);
            if (own.IsPass) return _loc.Format("explain.pass", who);

            string weapon = conceal ? _loc.Get("explain.veiled") : WeaponLabel(own);
            var hits = new List<ContactReport>();
            foreach (ContactReport c in tgt.Incoming)
                if (c.Attacker == attacker) hits.Add(c);

            if (hits.Count == 0)
            {
                bool clashed = false;
                foreach (ProjectileTrack t in own.Projectiles) clashed |= t.Termination == TerminationReason.ClashDestroyed;
                if (clashed) return _loc.Format("explain.clash", who, weapon);
                if (tgt.EffectiveDodge != Dodge.None) return _loc.Format("explain.missDodged", who, weapon, _name(target), DodgeLabel(tgt.EffectiveDodge));
                return _loc.Format("explain.miss", who, weapon);
            }

            bool allBlocked = true;
            foreach (ContactReport c in hits) allBlocked &= c.Blocked;
            if (allBlocked) return _loc.Format("explain.blocked", who, weapon, _name(target));

            int damage = 0;
            ContactReport first = null;
            foreach (ContactReport c in hits)
            {
                if (c.Blocked) continue;
                damage += c.DamageUnits + c.ChainBonusUnits;
                if (first == null) first = c;
            }
            string kind = ContactLabel(first.Kind, hits.Count);
            string element = ElementEffect(own.DefensiveElement, tgt.DefensiveElement, first.ElementFactor, conceal);
            string modifiers = Modifiers(first, tgt);
            return _loc.Format("explain.hit", who, weapon, kind, _name(target), Hp.Format(damage), element, modifiers);
        }

        public string HpLine(PlayerVolleyReport r)
        {
            string extra = string.Empty;
            if (r.BurnDamageUnits > 0) extra += " " + _loc.Format("explain.burn", Hp.Format(r.BurnDamageUnits));
            int heal = r.OceanHealUnits + r.RiverHealUnits;
            if (heal > 0) extra += " " + _loc.Format("explain.heal", Hp.Format(heal));
            return _loc.Format("explain.hp", _name(r.Side), Hp.Format(r.HpBeforeUnits), Hp.Format(r.HpAfterUnits)) + extra;
        }

        public string ResultLine(DuelResult result)
        {
            switch (result)
            {
                case DuelResult.AWins: return _loc.Format("explain.duelWon", _name(PlayerSide.A));
                case DuelResult.BWins: return _loc.Format("explain.duelWon", _name(PlayerSide.B));
                case DuelResult.Draw: return _loc.Get("explain.duelDraw");
                default: return string.Empty;
            }
        }

        public string WeaponLabel(PlayerVolleyReport r)
        {
            if (r.IsBrahmastra) return _loc.Get("weapon.brahmastra");
            if (!WeaponCatalog.IsRegularId(r.WeaponId)) return _loc.Get("explain.pass.short");
            return _loc.Format("weapon.withElement", WeaponName(r.WeaponId), ElementName(r.DefensiveElement));
        }

        public string WeaponName(int weaponId) => _loc.Get("weapon." + weaponId);

        public string ElementName(Element e) => _loc.Get("element." + e.ToString().ToLowerInvariant());

        public string DodgeLabel(Dodge d) => _loc.Get("dodge." + d.ToString().ToLowerInvariant());

        private string ContactLabel(ContactKind kind, int count)
        {
            string label;
            switch (kind)
            {
                case ContactKind.Graze: label = _loc.Get("contact.graze"); break;
                case ContactKind.BurstFull: label = _loc.Get("contact.burst"); break;
                case ContactKind.BurstGraze: label = _loc.Get("contact.burstGraze"); break;
                case ContactKind.Brahmastra: label = _loc.Get("contact.strike"); break;
                default: label = _loc.Get("contact.core"); break;
            }
            return count > 1 ? _loc.Format("contact.multiple", label, count) : label;
        }

        private string ElementEffect(Element attack, Element defend, Rational factor, bool conceal)
        {
            string mult = Multiplier(factor);
            if (conceal) return _loc.Format("element.effect.hidden", mult);
            if (factor.Numerator > factor.Denominator)
                return _loc.Format("element.effect.strong", mult, ElementName(attack), ElementName(defend));
            if (factor.Numerator < factor.Denominator)
                return _loc.Format("element.effect.weak", mult, ElementName(attack), ElementName(defend));
            return _loc.Format("element.effect.even", mult);
        }

        private string Modifiers(ContactReport c, PlayerVolleyReport target)
        {
            var parts = new List<string>();
            if (c.DodgeFactor.Numerator != c.DodgeFactor.Denominator)
                parts.Add(_loc.Format("modifier.dodge", DodgeLabel(target.EffectiveDodge), Multiplier(c.DodgeFactor)));
            if (c.TargetCovered && c.CoverFactor.Numerator != c.CoverFactor.Denominator)
                parts.Add(_loc.Format("modifier.cover", Multiplier(c.CoverFactor)));
            if (c.CoverIgnored) parts.Add(_loc.Get("modifier.coverIgnored"));
            if (parts.Count == 0) return string.Empty;
            return " " + string.Join(" ", parts);
        }

        /// <summary>Exact decimal text of a rational factor, e.g. 3/2 -> "x1.5" (presentation only).</summary>
        public static string Multiplier(Rational r)
        {
            decimal value = r.Denominator == 0 ? 0m : (decimal)r.Numerator / r.Denominator;
            return "×" + value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
