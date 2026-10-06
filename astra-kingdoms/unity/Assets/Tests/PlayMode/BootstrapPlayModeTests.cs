using System.Collections;
using AstraKingdoms.Client;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Automation;
using AstraKingdoms.Client.Match;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AstraKingdoms.Tests.PlayMode
{
    /// <summary>Runtime smoke tests: the bootstrap builds the UI and arena, and a full automated match completes.</summary>
    public sealed class BootstrapPlayModeTests
    {
        private GameObject _root;

        [TearDown]
        public void Cleanup()
        {
            if (_root != null) Object.Destroy(_root);
            foreach (string name in new[] { ArenaLayout.FighterAName, ArenaLayout.FighterBName, "Ground", "Main Camera", "EventSystem" })
            {
                GameObject go = GameObject.Find(name);
                if (go != null) Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator BootstrapCreatesFightersAndUi()
        {
            _root = new GameObject("GameBootstrap");
            var boot = _root.AddComponent<GameBootstrap>();
            yield return null;
            Assert.That(boot.Flow, Is.Not.Null);
            Assert.That(boot.Flow.Canvas, Is.Not.Null);
            Assert.That(GameObject.Find(ArenaLayout.FighterAName).transform.position.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(GameObject.Find(ArenaLayout.FighterBName).transform.position.x, Is.EqualTo(8f).Within(1e-4f));
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        [UnityTest]
        public IEnumerator AutomatedBotMatchFinishesAndReplays()
        {
            _root = new GameObject("GameBootstrap");
            var boot = _root.AddComponent<GameBootstrap>();
            yield return null;
            boot.Flow.AutomationTimings = new HostTimings { BotCutDelaySeconds = 0 };
            var runner = _root.AddComponent<AutomationRunner>();
            runner.Run(LaunchOptions.Parse(new[] { "-autoplay", "1", "-autoplaySpeed", "50" }), boot.Flow);
            float deadline = Time.realtimeSinceStartup + 240f;
            while (!runner.Finished && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(runner.Finished, Is.True, "automation did not finish in time");
            Assert.That(runner.Results.Count, Is.EqualTo(1));
            Assert.That(runner.Results[0].Passed, Is.True, runner.Results[0].Error ?? runner.Results[0].ReplayDetail);
            Assert.That(runner.Frames.Count, Is.GreaterThan(0));
        }
#endif
    }
}
