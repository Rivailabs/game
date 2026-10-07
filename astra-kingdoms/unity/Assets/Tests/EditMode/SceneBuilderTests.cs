using AstraKingdoms.Client;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.EditorTools;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AstraKingdoms.Tests.EditMode
{
    /// <summary>Ticket 4: the script-built grey-box scene has both fighters where the rules put them.</summary>
    public sealed class SceneBuilderTests
    {
        [SetUp]
        public void NewScene() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        [Test]
        public void FightersStandAtZeroAndEightMetres()
        {
            GreyBoxSceneBuilder.Populate();
            GameObject a = GameObject.Find(ArenaLayout.FighterAName);
            GameObject b = GameObject.Find(ArenaLayout.FighterBName);
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.transform.position.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(b.transform.position.x, Is.EqualTo(8f).Within(1e-5f));
            Assert.That(a.transform.position.z, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(a.transform.position.y, Is.EqualTo(0.9f).Within(1e-5f), "capsule centre between 0.35 m and 1.45 m");
        }

        [Test]
        public void CapsuleMatchesRulesBodyTest()
        {
            Vector3 s = ArenaLayout.CapsuleScale;
            // Unity's capsule primitive: 2 m tall, radius 0.5 m. Rules: axis 0.35-1.45 m, radius 0.25 m.
            Assert.That(s.x * 0.5f, Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(s.y * 2f, Is.EqualTo(1.6f).Within(1e-5f));
        }

        [Test]
        public void SceneHasBootstrapCameraAndNoColliders()
        {
            GreyBoxSceneBuilder.Populate();
            Assert.That(Object.FindFirstObjectByType<GameBootstrap>(), Is.Not.Null);
            Assert.That(Camera.main, Is.Not.Null);
            Assert.That(GameObject.Find(ArenaLayout.FighterAName).GetComponent<Collider>(), Is.Null,
                "rendering must never decide contacts");
        }
    }
}
