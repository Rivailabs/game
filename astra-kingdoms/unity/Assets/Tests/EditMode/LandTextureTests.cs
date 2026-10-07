using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using NUnit.Framework;
using UnityEngine;

namespace AstraKingdoms.Tests.EditMode
{
    /// <summary>Ticket 7 presentation: one texture pixel per logical cell; colour counts equal exact cell counts.</summary>
    public sealed class LandTextureTests
    {
        [Test]
        public void TexturePixelCountsEqualTerritoryCounts()
        {
            Territory t = Territory.CreateInitial(TerrainTemplates.PlainOnly);
            var board = new BoardTexture();
            board.ShowOwnership(t);
            Color32[] pixels = board.Texture.GetPixels32();
            int a = 0, b = 0;
            foreach (Color32 c in pixels)
            {
                if (Same(c, UiTheme.BoardA)) a++;
                else if (Same(c, UiTheme.BoardB)) b++;
            }
            Assert.That(a, Is.EqualTo(t.CellCount(PlayerSide.A)));
            Assert.That(b, Is.EqualTo(t.CellCount(PlayerSide.B)));
            Assert.That(a + b, Is.EqualTo(RulesConstants.ActiveCells));
            board.Destroy();
        }

        [Test]
        public void CellZeroZeroIsTopLeftOfTheImage()
        {
            // The board is y-down; textures store rows bottom-up.
            Assert.That(BoardMapping.TextureIndex(0, 0), Is.EqualTo(255 * 256));
            Assert.That(BoardMapping.TextureIndex(255, 255), Is.EqualTo(255));
        }

        private static bool Same(Color32 x, Color32 y) => x.r == y.r && x.g == y.g && x.b == y.b && x.a == y.a;
    }
}
