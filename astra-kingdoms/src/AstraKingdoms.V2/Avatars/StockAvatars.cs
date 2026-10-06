using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AstraKingdoms.V2.Avatars
{
    public sealed class StockAvatar
    {
        public string Id { get; }
        public string EnglishName { get; }
        public string AssetKey { get; }

        public StockAvatar(string id, string englishName, string assetKey)
        {
            Id = id;
            EnglishName = englishName;
            AssetKey = assetKey;
        }

        public string NameKey => "avatar." + Id;
    }

    /// <summary>Stock illustrated avatars: the default for everyone (plan: "Stock illustrated avatars are the default").</summary>
    public static class StockAvatarCatalog
    {
        public const string DefaultId = "avatar.stock-01";

        public static readonly IReadOnlyList<StockAvatar> All = Enumerable.Range(1, 12)
            .Select(i => new StockAvatar("avatar.stock-" + i.ToString("00", CultureInfo.InvariantCulture),
                new[] { "Archer", "Scholar", "Gardener", "Builder", "Weaver", "Sailor", "Musician", "Potter", "Rider", "Astronomer", "Dancer", "Healer" }[i - 1],
                "avatars/stock/" + i.ToString("00", CultureInfo.InvariantCulture)))
            .ToArray();

        public static bool Contains(string id) => All.Any(a => a.Id == id);
    }

    public enum AvatarKind : byte
    {
        Stock = 0,
        Photo = 1,
    }

    /// <summary>What a player's avatar is. A photo avatar always remembers the stock avatar to fall back to.</summary>
    public sealed class AvatarProfile
    {
        public AvatarKind Kind { get; }
        public string StockId { get; }
        /// <summary>Opaque reference to the converted avatar (never the raw photo).</summary>
        public string PhotoRef { get; }

        public AvatarProfile(AvatarKind kind, string stockId, string photoRef)
        {
            Kind = kind;
            StockId = stockId ?? StockAvatarCatalog.DefaultId;
            PhotoRef = photoRef;
        }

        public static readonly AvatarProfile Default = new AvatarProfile(AvatarKind.Stock, StockAvatarCatalog.DefaultId, null);

        /// <summary>The id other players' clients render: a stock id, or "photo:{ref}".</summary>
        public string PublicId => Kind == AvatarKind.Photo && PhotoRef != null ? "photo:" + PhotoRef : StockId;
    }

    public interface IAvatarProfileStore
    {
        AvatarProfile Get(string playerId);
        void Set(string playerId, AvatarProfile profile);
        bool Delete(string playerId);
    }

    public sealed class InMemoryAvatarProfileStore : IAvatarProfileStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, AvatarProfile> _rows = new Dictionary<string, AvatarProfile>(StringComparer.Ordinal);

        public AvatarProfile Get(string playerId)
        {
            lock (_gate) return _rows.TryGetValue(playerId, out AvatarProfile p) ? p : AvatarProfile.Default;
        }

        public void Set(string playerId, AvatarProfile profile)
        {
            lock (_gate) _rows[playerId] = profile;
        }

        public bool Delete(string playerId)
        {
            lock (_gate) return _rows.Remove(playerId);
        }
    }

    /// <summary>Stock avatar selection. Works for every account regardless of age, consent or feature flags.</summary>
    public sealed class AvatarService
    {
        private readonly IAvatarProfileStore _store;

        public AvatarService(IAvatarProfileStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        public AvatarProfile Current(string playerId) => _store.Get(playerId);

        public bool SelectStock(string playerId, string stockId)
        {
            if (!StockAvatarCatalog.Contains(stockId)) return false;
            AvatarProfile current = _store.Get(playerId);
            // Choosing a stock avatar while a photo avatar exists keeps the photo stored but unused; deleting it is a separate, explicit action.
            _store.Set(playerId, new AvatarProfile(AvatarKind.Stock, stockId, current.Kind == AvatarKind.Photo ? current.PhotoRef : null));
            return true;
        }

        public IAvatarProfileStore Store => _store;
    }
}
