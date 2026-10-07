using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.EditorTools;
using AstraKingdoms.Rules.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AstraKingdoms.Tests.EditMode
{
    /// <summary>
    /// Tickets 26-28 and 36 inside the editor: the placeholder archer meets the attachment and budget
    /// contract, the animator builder produces the required clip set, and both arena treatments
    /// build without colliders. (Written for the Unity Test Runner; not yet run in an editor.)
    /// </summary>
    public sealed class PresentationEditModeTests
    {
        [SetUp]
        public void NewScene() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        [Test]
        public void PlaceholderArcher_HasEveryAttachmentAndFitsTheBudget()
        {
            ArcherRig rig = ArcherRigBuilder.CreatePlaceholder(PlayerSide.A, null, Color.white);
            Assert.That(rig.Bind(), Is.Empty);
            Assert.That(rig.BowGrip, Is.Not.Null);
            AssetMetrics m = AssetBudgetValidatorMenu.Measure(rig.gameObject, AssetCategory.Archer);
            Assert.That(AssetBudgetValidator.ValidateAsset(m), Is.Empty);
            Assert.That(rig.GetComponentInChildren<Collider>(), Is.Null, "rendering never decides contacts");
        }

        [Test]
        public void AnimatorBuilder_CreatesTheRequiredClipSet()
        {
            ArcherAnimatorBuilder.Build();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ArcherAnimatorBuilder.ControllerPath);
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.animationClips.Length, Is.EqualTo(11));
            foreach (AnimationClip clip in controller.animationClips)
            {
                ArcherClip kind = (ArcherClip)System.Enum.Parse(typeof(ArcherClip), clip.name);
                Assert.That(AnimationUtility.GetAnimationClipSettings(clip).loopTime, Is.EqualTo(ArcherPoseLibrary.Get(kind).Loop), clip.name);
            }
        }

        [Test]
        public void BothArenaTreatments_BuildWithoutColliders()
        {
            foreach (ArenaVariant v in ArenaVariants.All)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                ArenaVariantSceneBuilder.Populate(v.Id);
                Assert.That(GameObject.Find("Ground"), Is.Not.Null, v.Id);
                Assert.That(GameObject.Find(ArenaLayout.FighterAName).transform.position.x, Is.EqualTo(0f).Within(1e-5f));
                GameObject props = GameObject.Find(ArenaVariantBuilder.PropsRootName + "_" + v.Id);
                Assert.That(props.GetComponentInChildren<Collider>(), Is.Null);
                Assert.That(AssetBudgetValidator.ValidateAsset(AssetBudgetValidatorMenu.Measure(props, AssetCategory.Arena)), Is.Empty, v.Id);
            }
        }
    }
}
