using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Client;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.Platform
{
    /// <summary>The guest profile file under persistentDataPath, written atomically (temp file, then replace).</summary>
    public sealed class FilePersistence : ILocalPersistence
    {
        private readonly string _path;

        public FilePersistence(string path) => _path = path;

        public static FilePersistence Default() => new FilePersistence(Path.Combine(Application.persistentDataPath, "meta", "guest-profile.json"));

        public string Load()
        {
            try
            {
                return File.Exists(_path) ? File.ReadAllText(_path) : null;
            }
            catch (IOException ex)
            {
                Debug.LogWarning("[Meta] Could not read the guest profile: " + ex.Message);
                return null;
            }
        }

        public void Save(string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (IOException ex)
            {
                Debug.LogWarning("[Meta] Could not save the guest profile: " + ex.Message);
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
            }
            catch (IOException ex)
            {
                Debug.LogWarning("[Meta] Could not delete the guest profile: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Store used when no billing adapter is installed (Unity IAP absent): no products, every purchase
    /// fails as unavailable. The shop then shows "price unavailable" instead of guessing.
    /// </summary>
    public sealed class UnavailableStoreBridge : IStoreBridge
    {
        public bool IsInitialized { get; private set; }
        public IReadOnlyList<StoreProductInfo> Products => Array.Empty<StoreProductInfo>();
        public event Action<StoreTransaction> PurchaseUpdated { add { } remove { } }
        public event Action<string, StoreFailure> PurchaseFailed;

        public Task<bool> InitializeAsync(IEnumerable<string> skus)
        {
            IsInitialized = true;
            return Task.FromResult(false);
        }

        public void Purchase(string sku, string obfuscatedAccountId) => PurchaseFailed?.Invoke(sku, StoreFailure.StoreUnavailable);
        public void FinishTransaction(StoreTransaction transaction) { }
        public Task<IReadOnlyList<StoreTransaction>> QueryOwnedAsync() => Task.FromResult<IReadOnlyList<StoreTransaction>>(Array.Empty<StoreTransaction>());
    }

    /// <summary>
    /// Rewarded-ad provider used until an ad network is chosen and its Families-certified adapter is
    /// written (BLOCKED on that owner decision). It never has an ad, so no offer can be shown.
    /// </summary>
    public sealed class UnavailableRewardedAdProvider : IRewardedAdProvider
    {
        public bool IsReady => false;
        public Task<AdLoadResult> LoadAsync(AdRequestOptions options, CancellationToken ct = default) => Task.FromResult(AdLoadResult.Error);
        public Task<AdShowOutcome> ShowAsync(string ssvUserId, string ssvCustomData, CancellationToken ct = default) => Task.FromResult(AdShowOutcome.NotLoaded);
    }

    /// <summary>
    /// Analytics transport placeholder: no collection endpoint has been chosen (BLOCKED on the
    /// privacy/vendor decision), so batches stay in the client's bounded queue. Development builds
    /// log the batch size so instrumentation can be checked in the editor.
    /// </summary>
    public sealed class PendingEndpointAnalyticsSink : IAnalyticsSink
    {
        private readonly bool _log;

        public PendingEndpointAnalyticsSink(bool log) => _log = log;

        public bool Send(IReadOnlyList<AnalyticsEvent> batch)
        {
            if (_log) Debug.Log("[Meta] analytics batch of " + batch.Count + " event(s) held: no collection endpoint configured");
            return false;
        }
    }

    /// <summary>Crash-reporter placeholder (no vendor chosen); only records whether collection is allowed.</summary>
    public sealed class ConsentAwareCrashReporter : ICrashReporter
    {
        public bool Enabled { get; private set; }
        public void SetCollectionEnabled(bool enabled) => Enabled = enabled;

        public void RecordCrash(bool critical, string stage)
        {
            if (Enabled) Debug.LogWarning("[Meta] crash recorded (critical=" + critical + ", stage=" + stage + ")");
        }
    }
}
