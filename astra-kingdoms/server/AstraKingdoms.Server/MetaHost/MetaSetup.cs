using AstraKingdoms.Meta.Ads;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Server;
using AstraKingdoms.Server.Matches;
using Microsoft.Extensions.Options;

namespace AstraKingdoms.Server.MetaHost;

/// <summary>Service registration for the hosted meta services.</summary>
public static class MetaSetup
{
    private const string GoogleApisClient = "google-apis";

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        IServiceCollection s = builder.Services;
        s.AddHttpClient(GoogleApisClient, c => c.Timeout = TimeSpan.FromSeconds(15));
        s.AddSingleton(sp => new MetaSqliteStore(sp.GetRequiredService<IOptions<ServerOptions>>().Value.Storage.SqlitePath));
        s.AddSingleton<IMatchPresence>(sp => new RegistryMatchPresence(sp.GetRequiredService<MatchRegistry>()));
        s.AddSingleton<IHttpTransport>(sp => new SystemNetHttpTransport(sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleApisClient)));
        s.AddSingleton<IAdVerifierKeyProvider>(sp => new HttpAdVerifierKeyProvider(sp.GetRequiredService<IHttpTransport>(),
            new TimeProviderClock(sp.GetRequiredService<TimeProvider>()), sp.GetRequiredService<IOptions<ServerOptions>>().Value.Meta.Ads.SsvKeysUrl));
        s.AddSingleton(CreateVerifier);
        s.AddSingleton<MetaServices>();
        s.AddSingleton<MetaSettlement>();
        s.AddSingleton<IMatchSettlementHook>(sp => sp.GetRequiredService<MetaSettlement>());
        s.AddHostedService(sp => sp.GetRequiredService<MetaSettlement>());
        s.AddSingleton<AccountDeletionProcessor>();
        s.AddSingleton<AnalyticsIngestion>();
        s.AddSingleton<MetaJobs>();
        s.AddHostedService(sp => sp.GetRequiredService<MetaJobs>());
    }

    /// <summary>
    /// The Play purchase verifier from configuration. The fake one is refused outside Development
    /// (like Dev authentication); Google Play needs the service-account key file and the account-id
    /// salt, and startup fails without them rather than granting unverified purchases.
    /// </summary>
    private static IPurchaseVerifier CreateVerifier(IServiceProvider sp)
    {
        MetaBillingOptions o = sp.GetRequiredService<IOptions<ServerOptions>>().Value.Meta.Billing;
        IHostEnvironment env = sp.GetRequiredService<IHostEnvironment>();
        switch (o.Verifier)
        {
            case PurchaseVerifierMode.Fake:
                if (!env.IsDevelopment() && !o.AllowFakeOutsideDevelopment)
                    throw new InvalidOperationException("The fake purchase verifier is refused outside the Development environment.");
                return new FakePurchaseVerifier();
            case PurchaseVerifierMode.GooglePlay:
                if (string.IsNullOrWhiteSpace(o.AccountIdSalt))
                    throw new InvalidOperationException("AstraServer:Meta:Billing:AccountIdSalt is required for Google Play billing.");
                if (string.IsNullOrWhiteSpace(o.ServiceAccountKeyPath) || !File.Exists(o.ServiceAccountKeyPath))
                    throw new InvalidOperationException("AstraServer:Meta:Billing:ServiceAccountKeyPath must name the service-account JSON key file.");
                IHttpTransport http = sp.GetRequiredService<IHttpTransport>();
                var key = ServiceAccountKey.Parse(File.ReadAllText(o.ServiceAccountKeyPath));
                var tokens = new GoogleServiceAccountTokenProvider(key, http, new TimeProviderClock(sp.GetRequiredService<TimeProvider>()));
                return new GooglePlayPurchaseVerifier(o.PackageName, tokens, http);
            default:
                return new DisabledPurchaseVerifier();
        }
    }

    /// <summary>Resolves what must fail at startup rather than on the first purchase.</summary>
    public static void ValidateAtStartup(IServiceProvider services)
    {
        services.GetRequiredService<IPurchaseVerifier>();
        services.GetRequiredService<MetaServices>();
    }
}
