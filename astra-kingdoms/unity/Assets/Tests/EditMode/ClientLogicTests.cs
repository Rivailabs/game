using System.Collections.Generic;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using NUnit.Framework;

namespace AstraKingdoms.Tests.EditMode
{
    /// <summary>Client logic inside the editor (the same cases also run under dotnet test without Unity).</summary>
    public sealed class ClientLogicTests
    {
        [Test]
        public void SimplifierNeverExceeds128Vertices()
        {
            var stroke = new List<CellPoint>();
            for (int i = 0; i < 2000; i++)
            {
                double a = i * 2 * System.Math.PI / 2000;
                stroke.Add(new CellPoint(128 + (int)(60 * System.Math.Cos(a) + 5 * System.Math.Sin(a * 37)), 128 + (int)(60 * System.Math.Sin(a))));
            }
            List<CellPoint> simplified = StrokeSimplifier.Simplify(stroke);
            Assert.That(simplified.Count, Is.LessThanOrEqualTo(RulesConstants.MaxCutVertices));
            Assert.That(simplified.Count, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void LocalizationResourcesHaveNoMissingKeys()
        {
            Localizer loc = LocalizationLoader.Load(Localizer.English);
            Assert.That(loc.EnglishTable.Count, Is.GreaterThan(100));
            Assert.That(loc.EnglishTable.Errors, Is.Empty);
            foreach (string code in Localizer.SupportedLanguages)
            {
                Assert.That(loc.HasLanguage(code), Is.True, code);
                Assert.That(loc.MissingKeys(code), Is.Empty, code);
            }
        }

        [Test]
        public void HostAdvancesPhasesUnderASimulatedClock()
        {
            MatchFactory.ForAutoplay(1, out byte[] seed, out string id);
            var host = new LocalMatchHost(MatchConfig.Pilot(MatchMode.SharedPhone), seed, id, SeatKind.Human, SeatKind.Human);
            host.Start();
            for (int i = 0; i < 2; i++)
            {
                Assert.That(host.Stage, Is.EqualTo(HostStage.LoadoutReady));
                host.ConfirmReady();
                Assert.That(host.SubmitLoadout(host.StageSide.Value, new[] { 1, 2, 3, 4, 5 }).Accepted, Is.True);
            }
            Assert.That(host.Stage, Is.EqualTo(HostStage.TerrainAnnounce));
            host.Tick(2.0);
            Assert.That(host.Stage, Is.EqualTo(HostStage.EntryReady));
            Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection));
            host.Tick(12.0); // first entrant times out
            Assert.That(host.Stage, Is.EqualTo(HostStage.Handover));
            host.Tick(6.0); // handover window ends
            Assert.That(host.Stage, Is.EqualTo(HostStage.Entry));
            host.Tick(12.0); // second entrant times out: deadline, both Pass
            Assert.That(host.Stage, Is.EqualTo(HostStage.Resolution));
            host.Tick(2.5);
            Assert.That(host.Engine.Phase, Is.EqualTo(MatchPhase.Selection));
            Assert.That(host.Engine.GetVolleyResult(1, 1).Explanation.A.IsPass, Is.True);
        }
    }
}
