using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Meta.Shop;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>
    /// Shop (ticket 60): every offer shows the store's own price, exactly what it contains and the
    /// terms; audience restrictions come from the backend. Paid purchases go through
    /// <see cref="ClientPurchaseFlow"/> (ticket 61): nothing is granted on the device.
    /// </summary>
    public sealed class ShopScreen : MetaScreen
    {
        public ShopScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta)
            : base(ctx, ui, parent, meta, "MetaShopScreen")
        {
            SetTitle(L("meta.shop.title"));
            _onUpdate = OnPurchaseUpdate;
            meta.Purchases.Updated += _onUpdate;
        }

        private readonly System.Action<PurchaseFlowUpdate> _onUpdate;

        private void OnPurchaseUpdate(PurchaseFlowUpdate u)
        {
            if (Root == null)
            {
                Meta.Purchases.Updated -= _onUpdate; // this screen was destroyed by a UI rebuild
                return;
            }
            if (Visible) Report(LF("meta.shop.flow", u.State + (u.Refusal.HasValue ? " (" + u.Refusal.Value + ")" : string.Empty)));
        }

        protected override async Task Build(RectTransform content)
        {
            Line(content, L("meta.shop.hint"), UiFactory.SizeSmall, UiTheme.TextMuted);
            if (Meta.UsingTestStore) Line(content, L("meta.shop.testStore"), UiFactory.SizeSmall, UiTheme.Warning);
            bool provisional = false;
            foreach (ShopOfferView o in await Meta.Api.GetShopAsync(Meta.Purchases.Prices()))
            {
                provisional |= o.PriceIsProvisional;
                RectTransform row = RowOf(content);
                Ui.Label(row, LF("meta.shop.offer", o.Title, o.PriceText ?? "-"), UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
                if (o.CanBuy)
                {
                    OfferCurrency currency = o.Currency;
                    string id = o.Id;
                    Ui.Button(row, L("meta.shop.buy"), async () =>
                    {
                        if (currency == OfferCurrency.Paid)
                        {
                            await Meta.Purchases.BuyAsync(id);
                        }
                        else
                        {
                            CoinPurchaseStatus s = await Meta.Api.BuyWithCoinsAsync(id);
                            Report(LF("meta.shop.flow", s));
                        }
                    }, UiTheme.ButtonPrimary, UiFactory.SizeSmall);
                }
                else
                {
                    Ui.Label(row, o.State == OfferState.Owned ? L("meta.shop.owned") : LF("meta.shop.state", o.State), UiFactory.SizeSmall,
                        TextAnchor.MiddleCenter, UiTheme.TextMuted);
                }
                Line(content, LF("meta.shop.contents", string.Join(", ", o.ContentNames)) + "  " + o.Terms, UiFactory.SizeSmall, UiTheme.TextMuted);
            }
            if (provisional) Line(content, L("meta.shop.provisional"), UiFactory.SizeSmall, UiTheme.TextMuted);
            Ui.Button(content, L("meta.shop.restore"), async () =>
            {
                await Meta.Purchases.RestoreAsync();
                Refresh();
            }, UiTheme.Panel, UiFactory.SizeSmall);
            RewardedOffer.AddTo(content, Ui, Meta, ScreenContext.Shop, Report);
        }
    }
}
