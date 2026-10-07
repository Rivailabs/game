using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Avatars
{
    /// <summary>
    /// Owner decisions that gate photo avatars (plan: "approve audience eligibility, consent, processing
    /// destinations, retention, deletion and abuse handling before enabling uploads"). Everything is
    /// off by default; with the defaults no account can start a photo conversion.
    /// </summary>
    public sealed class PhotoFeatureConfig
    {
        /// <summary>Remote feature flag. Default off.</summary>
        public bool FeatureFlagEnabled { get; set; }
        /// <summary>The audience decision (who may use photos) has been made and recorded.</summary>
        public bool AudienceDecisionApproved { get; set; }
        /// <summary>Reference to the legal/privacy review of the data map below (required).</summary>
        public string LegalReviewReference { get; set; }
        /// <summary>Version of the consent text shown; recorded with each consent.</summary>
        public string ConsentTextVersion { get; set; } = "photo-consent-draft-1";
        /// <summary>Whether children may use photos at all (also needs verified parental consent). Default no.</summary>
        public bool AllowChildrenWithVerifiedParentalConsent { get; set; }
        /// <summary>Whether the converted avatar may leave the device (profile server and other players' caches). Default no.</summary>
        public bool PublishToProfileServer { get; set; }
        /// <summary>The digest of the data map the review approved; enabling fails if the map changed since.</summary>
        public string ApprovedDataMapDigest { get; set; }
    }

    public enum PhotoBlock : byte
    {
        FeatureFlagOff = 0,
        AudienceDecisionPending = 1,
        LegalReviewMissing = 2,
        DataMapChangedSinceReview = 3,
        UnknownAge = 4,
        ChildrenNotPermitted = 5,
        ParentalConsentMissing = 6,
        Suspended = 7,
    }

    public sealed class PhotoGateResult
    {
        public IReadOnlyList<PhotoBlock> Blocks { get; }
        public bool Allowed => Blocks.Count == 0;

        public PhotoGateResult(IReadOnlyList<PhotoBlock> blocks) => Blocks = blocks;
    }

    public static class PhotoGate
    {
        public static PhotoGateResult Evaluate(PhotoFeatureConfig config, PhotoDataMap map, AudienceProfile audience, IFeatureGate gate, string playerId, DateTimeOffset now)
        {
            var blocks = new List<PhotoBlock>();
            if (config == null || !config.FeatureFlagEnabled) blocks.Add(PhotoBlock.FeatureFlagOff);
            if (config == null || !config.AudienceDecisionApproved) blocks.Add(PhotoBlock.AudienceDecisionPending);
            if (config == null || string.IsNullOrWhiteSpace(config.LegalReviewReference)) blocks.Add(PhotoBlock.LegalReviewMissing);
            if (config == null || map == null || config.ApprovedDataMapDigest != map.Digest(config)) blocks.Add(PhotoBlock.DataMapChangedSinceReview);
            switch (audience?.AgeGroup ?? AgeGroup.Unknown)
            {
                case AgeGroup.Unknown:
                    blocks.Add(PhotoBlock.UnknownAge);
                    break;
                case AgeGroup.Child:
                    if (config == null || !config.AllowChildrenWithVerifiedParentalConsent) blocks.Add(PhotoBlock.ChildrenNotPermitted);
                    else if (audience.ParentalConsent != ParentalConsent.Verified) blocks.Add(PhotoBlock.ParentalConsentMissing);
                    break;
            }
            if (gate != null && gate.IsSuspended(playerId, SocialFeature.PhotoAvatar, now)) blocks.Add(PhotoBlock.Suspended);
            return new PhotoGateResult(blocks);
        }
    }

    /// <summary>The four photo artifacts the plan requires documenting separately.</summary>
    public enum PhotoArtifact : byte
    {
        RawPhoto = 0,
        ConversionIntermediate = 1,
        AvatarImage = 2,
        Thumbnail = 3,
    }

    /// <summary>Every place a photo-derived artifact can exist.</summary>
    public enum DataLocation : byte
    {
        /// <summary>App-private temporary directory on the phone.</summary>
        DeviceTemp = 0,
        /// <summary>App-private persistent storage on the phone.</summary>
        DevicePrivate = 1,
        /// <summary>The game's profile server (only when publishing is approved).</summary>
        ProfileServer = 2,
        /// <summary>Other players' cached copies: friend list, clan roster, visit snapshots.</summary>
        FriendListCache = 3,
        ClanRosterCache = 4,
        VisitSnapshotCache = 5,
    }

    public sealed class PhotoDataFlowEntry
    {
        public PhotoArtifact Artifact { get; }
        public string Purpose { get; }
        public IReadOnlyList<DataLocation> Locations { get; }
        public string Retention { get; }
        public string Deletion { get; }

        public PhotoDataFlowEntry(PhotoArtifact artifact, string purpose, IReadOnlyList<DataLocation> locations, string retention, string deletion)
        {
            Artifact = artifact;
            Purpose = purpose;
            Locations = locations;
            Retention = retention;
            Deletion = deletion;
        }
    }

    /// <summary>
    /// The photo data-flow record (plan: "document the raw photo, conversion intermediates, avatar and
    /// thumbnail separately: purpose, device/server/vendor destinations, retention and deletion").
    /// The pipeline enforces it: an artifact is never written to a location missing from its entry.
    /// No vendor destination exists: conversion is on-device only.
    /// </summary>
    public sealed class PhotoDataMap
    {
        public IReadOnlyList<PhotoDataFlowEntry> Entries { get; }

        public PhotoDataMap(IEnumerable<PhotoDataFlowEntry> entries) => Entries = entries.ToArray();

        public PhotoDataFlowEntry For(PhotoArtifact artifact) => Entries.First(e => e.Artifact == artifact);

        public bool Allows(PhotoArtifact artifact, DataLocation location, PhotoFeatureConfig config)
        {
            if (!For(artifact).Locations.Contains(location)) return false;
            bool leavesDevice = location != DataLocation.DeviceTemp && location != DataLocation.DevicePrivate;
            return !leavesDevice || (config != null && config.PublishToProfileServer);
        }

        /// <summary>Structural rules the review relies on. Empty when valid.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            foreach (PhotoArtifact a in Enum.GetValues(typeof(PhotoArtifact)))
                if (Entries.Count(e => e.Artifact == a) != 1) errors.Add(a + " must be documented exactly once");
            foreach (PhotoArtifact a in new[] { PhotoArtifact.RawPhoto, PhotoArtifact.ConversionIntermediate })
                if (Entries.Any(e => e.Artifact == a && e.Locations.Any(l => l != DataLocation.DeviceTemp)))
                    errors.Add(a + " may only exist in device temporary storage");
            foreach (PhotoDataFlowEntry e in Entries)
                if (string.IsNullOrWhiteSpace(e.Purpose) || string.IsNullOrWhiteSpace(e.Retention) || string.IsNullOrWhiteSpace(e.Deletion))
                    errors.Add(e.Artifact + " needs purpose, retention and deletion");
            return errors;
        }

        /// <summary>Digest of the map plus the publishing decision: what a review signs off.</summary>
        public string Digest(PhotoFeatureConfig config)
        {
            var sb = new StringBuilder();
            foreach (PhotoDataFlowEntry e in Entries.OrderBy(x => x.Artifact))
                sb.Append(e.Artifact).Append('|').Append(string.Join(",", e.Locations)).Append('|').Append(e.Purpose).Append('|').Append(e.Retention).Append('|').Append(e.Deletion).Append('\n');
            sb.Append("publish=").Append(config != null && config.PublishToProfileServer ? "yes" : "no");
            sb.Append("|children=").Append(config != null && config.AllowChildrenWithVerifiedParentalConsent ? "yes" : "no");
            return StableHash.Sha256Hex(sb.ToString());
        }

        public static readonly PhotoDataMap Default = new PhotoDataMap(new[]
        {
            new PhotoDataFlowEntry(PhotoArtifact.RawPhoto, "Input to on-device cartoon conversion chosen by the player.",
                new[] { DataLocation.DeviceTemp }, "Deleted as soon as conversion finishes, fails or is cancelled.",
                "Pipeline deletes the temp file in a finally block; startup sweep removes leftovers from a crash."),
            new PhotoDataFlowEntry(PhotoArtifact.ConversionIntermediate, "Model working images (face crop, segmentation mask, stylised layers).",
                new[] { DataLocation.DeviceTemp }, "Deleted with the raw photo.", "Tracked by the conversion scratch area and deleted by the pipeline."),
            new PhotoDataFlowEntry(PhotoArtifact.AvatarImage, "The cartoon avatar the player chose to use (may still be personal data).",
                new[] { DataLocation.DevicePrivate, DataLocation.ProfileServer, DataLocation.FriendListCache, DataLocation.ClanRosterCache, DataLocation.VisitSnapshotCache },
                "Until the player removes it, withdraws consent, deletes the account, or moderation removes it.",
                "Withdrawal deletes device and server copies, evicts every registered cache and reverts to the stock avatar."),
            new PhotoDataFlowEntry(PhotoArtifact.Thumbnail, "Small avatar for lists.",
                new[] { DataLocation.DevicePrivate, DataLocation.ProfileServer, DataLocation.FriendListCache, DataLocation.ClanRosterCache },
                "Same as the avatar.", "Deleted together with the avatar."),
        });
    }

    /// <summary>A store for photo-derived bytes at one location. Server and cache implementations may fail (outage).</summary>
    public interface IAvatarBlobStore
    {
        DataLocation Location { get; }
        string Put(string playerId, PhotoArtifact artifact, byte[] bytes);
        /// <summary>True when deleted or already absent; false when the store could not be reached (retry later).</summary>
        bool Delete(string key);
        IReadOnlyList<string> Keys(string playerId);
    }

    public sealed class InMemoryAvatarBlobStore : IAvatarBlobStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, KeyValuePair<string, byte[]>> _blobs = new Dictionary<string, KeyValuePair<string, byte[]>>(StringComparer.Ordinal);
        private int _counter;

        public InMemoryAvatarBlobStore(DataLocation location) => Location = location;

        public DataLocation Location { get; }
        /// <summary>Simulates an outage: deletes fail while true.</summary>
        public bool Unreachable { get; set; }

        public string Put(string playerId, PhotoArtifact artifact, byte[] bytes)
        {
            lock (_gate)
            {
                string key = Location + "/" + playerId + "/" + artifact + "/" + (++_counter).ToString(CultureInfo.InvariantCulture);
                _blobs[key] = new KeyValuePair<string, byte[]>(playerId, (byte[])bytes.Clone());
                return key;
            }
        }

        public bool Delete(string key)
        {
            if (Unreachable) return false;
            lock (_gate)
            {
                _blobs.Remove(key);
                return true;
            }
        }

        public IReadOnlyList<string> Keys(string playerId)
        {
            lock (_gate) return _blobs.Where(kv => kv.Value.Key == playerId).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Somewhere another surface caches an avatar (friend list, clan roster, visit snapshots, profile cache).</summary>
    public interface IAvatarCache
    {
        DataLocation Location { get; }
        void Store(string ownerId, string publicAvatarId);
        /// <summary>True when evicted or absent; false when unreachable.</summary>
        bool Evict(string ownerId);
        bool Holds(string ownerId);
    }

    public sealed class InMemoryAvatarCache : IAvatarCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _entries = new Dictionary<string, string>(StringComparer.Ordinal);

        public InMemoryAvatarCache(DataLocation location) => Location = location;

        public DataLocation Location { get; }
        public bool Unreachable { get; set; }

        public void Store(string ownerId, string publicAvatarId)
        {
            lock (_gate) _entries[ownerId] = publicAvatarId;
        }

        public bool Evict(string ownerId)
        {
            if (Unreachable) return false;
            lock (_gate)
            {
                _entries.Remove(ownerId);
                return true;
            }
        }

        public bool Holds(string ownerId)
        {
            lock (_gate) return _entries.ContainsKey(ownerId);
        }
    }

    /// <summary>Where the on-device converter writes its working files, so the pipeline can delete them all.</summary>
    public interface IPhotoScratch
    {
        string WriteIntermediate(byte[] bytes);
    }

    public sealed class AvatarConversion
    {
        public byte[] AvatarImage { get; }
        public byte[] Thumbnail { get; }

        public AvatarConversion(byte[] avatarImage, byte[] thumbnail)
        {
            AvatarImage = avatarImage ?? throw new ArgumentNullException(nameof(avatarImage));
            Thumbnail = thumbnail ?? throw new ArgumentNullException(nameof(thumbnail));
        }
    }

    /// <summary>
    /// The on-device photo-to-cartoon converter. There is deliberately no server or vendor variant of
    /// this interface: photos never leave the phone. The real implementation (a bundled model run
    /// through a Unity inference package or an Android plugin) is NOT written; it needs a model choice,
    /// licence review and device qualification.
    /// </summary>
    public interface IOnDeviceAvatarConverter
    {
        Task<AvatarConversion> ConvertAsync(byte[] rawPhoto, IPhotoScratch scratch, CancellationToken ct);
    }

    public enum PhotoConversionStatus : byte
    {
        Converted = 0,
        /// <summary>The player declined; nothing was stored and the stock avatar stays.</summary>
        Declined = 1,
        Blocked = 2,
        Failed = 3,
        Cancelled = 4,
    }

    public sealed class PhotoConversionResult
    {
        public PhotoConversionStatus Status { get; }
        public IReadOnlyList<PhotoBlock> Blocks { get; }
        public string Detail { get; }

        public PhotoConversionResult(PhotoConversionStatus status, IReadOnlyList<PhotoBlock> blocks = null, string detail = null)
        {
            Status = status;
            Blocks = blocks ?? Array.Empty<PhotoBlock>();
            Detail = detail;
        }
    }

    public sealed class PhotoConsentRecord
    {
        public string PlayerId { get; }
        public string ConsentTextVersion { get; }
        public DateTimeOffset GivenAt { get; }

        public PhotoConsentRecord(string playerId, string consentTextVersion, DateTimeOffset givenAt)
        {
            PlayerId = playerId;
            ConsentTextVersion = consentTextVersion;
            GivenAt = givenAt;
        }
    }

    public sealed class DeletionReceipt
    {
        public string PlayerId { get; }
        public string Reason { get; }
        public DateTimeOffset At { get; }
        public IDictionary<DataLocation, bool> Outcomes { get; } = new SortedDictionary<DataLocation, bool>();
        /// <summary>
        /// True only when every known location confirmed deletion. A successful local deletion does not
        /// prove external copies are gone; pending locations are retried by <see cref="PhotoAvatarService.RetryPendingDeletions"/>.
        /// </summary>
        public bool Complete => Outcomes.Values.All(v => v);

        public DeletionReceipt(string playerId, string reason, DateTimeOffset at)
        {
            PlayerId = playerId;
            Reason = reason;
            At = at;
        }
    }

    /// <summary>
    /// The optional photo-avatar flow, gated and data-mapped. Declining (or never being offered photos)
    /// leaves the stock avatar and every kingdom and social feature fully usable: nothing else in V2
    /// reads this service.
    /// </summary>
    public sealed class PhotoAvatarService
    {
        private sealed class Scratch : IPhotoScratch
        {
            private readonly IAvatarBlobStore _temp;
            private readonly string _player;
            public readonly List<string> Keys = new List<string>();
            public Scratch(IAvatarBlobStore temp, string player) { _temp = temp; _player = player; }
            public string WriteIntermediate(byte[] bytes)
            {
                string k = _temp.Put(_player, PhotoArtifact.ConversionIntermediate, bytes);
                Keys.Add(k);
                return k;
            }
        }

        private readonly object _gate = new object();
        private readonly PhotoFeatureConfig _config;
        private readonly PhotoDataMap _map;
        private readonly IOnDeviceAvatarConverter _converter;
        private readonly AvatarService _avatars;
        private readonly IFeatureGate _features;
        private readonly IClock _clock;
        private readonly Dictionary<DataLocation, IAvatarBlobStore> _stores = new Dictionary<DataLocation, IAvatarBlobStore>();
        private readonly List<IAvatarCache> _caches = new List<IAvatarCache>();
        private readonly Dictionary<string, PhotoConsentRecord> _consents = new Dictionary<string, PhotoConsentRecord>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<DataLocation, string>> _pendingBlobDeletes = new List<KeyValuePair<DataLocation, string>>();
        private readonly List<KeyValuePair<IAvatarCache, string>> _pendingEvictions = new List<KeyValuePair<IAvatarCache, string>>();

        public PhotoAvatarService(PhotoFeatureConfig config, PhotoDataMap map, IOnDeviceAvatarConverter converter, AvatarService avatars,
            IEnumerable<IAvatarBlobStore> stores, IEnumerable<IAvatarCache> caches, IFeatureGate features, IClock clock)
        {
            _config = config ?? new PhotoFeatureConfig();
            _map = map ?? PhotoDataMap.Default;
            _converter = converter;
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            _features = features ?? OpenFeatureGate.Instance;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            foreach (IAvatarBlobStore s in stores ?? Array.Empty<IAvatarBlobStore>()) _stores[s.Location] = s;
            if (!_stores.ContainsKey(DataLocation.DeviceTemp) || !_stores.ContainsKey(DataLocation.DevicePrivate))
                throw new ArgumentException("device temp and device private stores are required");
            _caches.AddRange(caches ?? Array.Empty<IAvatarCache>());
        }

        public PhotoGateResult Gate(string playerId, AudienceProfile audience) => PhotoGate.Evaluate(_config, _map, audience, _features, playerId, _clock.UtcNow);

        public PhotoConsentRecord Consent(string playerId)
        {
            lock (_gate) return _consents.TryGetValue(playerId, out PhotoConsentRecord c) ? c : null;
        }

        /// <summary>
        /// Runs one conversion. The raw photo and every intermediate are deleted whatever happens
        /// (success, failure, cancellation). Only the avatar and thumbnail are kept, and only in the
        /// locations the data map allows under the current publishing decision.
        /// </summary>
        public async Task<PhotoConversionResult> ConvertAsync(string playerId, byte[] rawPhoto, bool consentGiven, AudienceProfile audience, CancellationToken ct = default)
        {
            if (!consentGiven) return new PhotoConversionResult(PhotoConversionStatus.Declined);
            PhotoGateResult gate = Gate(playerId, audience);
            if (!gate.Allowed) return new PhotoConversionResult(PhotoConversionStatus.Blocked, gate.Blocks);
            if (_converter == null) return new PhotoConversionResult(PhotoConversionStatus.Failed, detail: "no on-device converter installed");
            IAvatarBlobStore temp = _stores[DataLocation.DeviceTemp];
            string rawKey = temp.Put(playerId, PhotoArtifact.RawPhoto, rawPhoto ?? Array.Empty<byte>());
            var scratch = new Scratch(temp, playerId);
            AvatarConversion output;
            try
            {
                output = await _converter.ConvertAsync(rawPhoto, scratch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new PhotoConversionResult(PhotoConversionStatus.Cancelled);
            }
            catch (Exception ex)
            {
                return new PhotoConversionResult(PhotoConversionStatus.Failed, detail: ex.GetType().Name);
            }
            finally
            {
                temp.Delete(rawKey);
                foreach (string k in scratch.Keys) temp.Delete(k);
            }

            lock (_gate)
            {
                RemoveStoredLocked(playerId, new DeletionReceipt(playerId, "replaced", _clock.UtcNow)); // a new avatar replaces the old one everywhere
                string photoRef = null;
                foreach (KeyValuePair<DataLocation, IAvatarBlobStore> s in _stores.OrderBy(x => x.Key))
                {
                    if (_map.Allows(PhotoArtifact.AvatarImage, s.Key, _config))
                    {
                        string key = s.Value.Put(playerId, PhotoArtifact.AvatarImage, output.AvatarImage);
                        if (s.Key == DataLocation.DevicePrivate) photoRef = StableHash.Sha256Hex(key).Substring(0, 16);
                    }
                    if (_map.Allows(PhotoArtifact.Thumbnail, s.Key, _config)) s.Value.Put(playerId, PhotoArtifact.Thumbnail, output.Thumbnail);
                }
                AvatarProfile current = _avatars.Current(playerId);
                _avatars.Store.Set(playerId, new AvatarProfile(AvatarKind.Photo, current.StockId, photoRef));
                _consents[playerId] = new PhotoConsentRecord(playerId, _config.ConsentTextVersion, _clock.UtcNow);
            }
            return new PhotoConversionResult(PhotoConversionStatus.Converted);
        }

        /// <summary>Lets another surface cache the avatar only when the data map allows that location.</summary>
        public bool CacheFor(IAvatarCache cache, string ownerId)
        {
            AvatarProfile p = _avatars.Current(ownerId);
            if (p.Kind == AvatarKind.Photo && !_map.Allows(PhotoArtifact.AvatarImage, cache.Location, _config)) return false;
            cache.Store(ownerId, p.PublicId);
            return true;
        }

        /// <summary>
        /// Consent withdrawal, removal by the player, moderation removal or account deletion: deletes
        /// device and server copies, evicts every registered cache, reverts to the stock avatar and drops
        /// the consent record. Unreachable locations are queued for retry and reported as pending.
        /// </summary>
        public DeletionReceipt Withdraw(string playerId, string reason)
        {
            var receipt = new DeletionReceipt(playerId, reason, _clock.UtcNow);
            lock (_gate)
            {
                RemoveStoredLocked(playerId, receipt);
                AvatarProfile current = _avatars.Current(playerId);
                _avatars.Store.Set(playerId, new AvatarProfile(AvatarKind.Stock, current.StockId, null));
                _consents.Remove(playerId);
            }
            return receipt;
        }

        private void RemoveStoredLocked(string playerId, DeletionReceipt receipt)
        {
            foreach (KeyValuePair<DataLocation, IAvatarBlobStore> s in _stores)
            {
                bool ok = true;
                foreach (string key in s.Value.Keys(playerId))
                    if (!s.Value.Delete(key)) { ok = false; _pendingBlobDeletes.Add(new KeyValuePair<DataLocation, string>(s.Key, key)); }
                receipt.Outcomes[s.Key] = ok;
            }
            foreach (IAvatarCache cache in _caches)
            {
                bool ok = cache.Evict(playerId);
                if (!ok) _pendingEvictions.Add(new KeyValuePair<IAvatarCache, string>(cache, playerId));
                receipt.Outcomes[cache.Location] = receipt.Outcomes.TryGetValue(cache.Location, out bool prev) ? prev && ok : ok;
            }
        }

        /// <summary>Retries deletions that failed during an outage. Returns how many are still pending.</summary>
        public int RetryPendingDeletions()
        {
            lock (_gate)
            {
                _pendingBlobDeletes.RemoveAll(p => _stores[p.Key].Delete(p.Value));
                _pendingEvictions.RemoveAll(p => p.Key.Evict(p.Value));
                return _pendingBlobDeletes.Count + _pendingEvictions.Count;
            }
        }

        /// <summary>Startup sweep: removes raw photos and intermediates left by a crash mid-conversion.</summary>
        public int SweepDeviceTemp(string playerId)
        {
            IAvatarBlobStore temp = _stores[DataLocation.DeviceTemp];
            int n = 0;
            foreach (string k in temp.Keys(playerId)) if (temp.Delete(k)) n++;
            return n;
        }

        /// <summary>Every location still holding something photo-derived for the player (deletion verification).</summary>
        public IReadOnlyList<string> Leftovers(string playerId)
        {
            var list = new List<string>();
            foreach (IAvatarBlobStore s in _stores.Values) list.AddRange(s.Keys(playerId));
            foreach (IAvatarCache c in _caches) if (c.Holds(playerId)) list.Add(c.Location + "/" + playerId);
            return list;
        }
    }
}
