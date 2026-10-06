using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Server;
using AstraKingdoms.Server.Identity;
using AstraKingdoms.Server.Lobby;
using AstraKingdoms.Server.Matches;
using AstraKingdoms.Server.Ops;
using AstraKingdoms.Server.Realtime;
using AstraKingdoms.Server.Storage;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ServerSetup.ConfigureServices(builder);
WebApplication app = builder.Build();
ServerSetup.ConfigurePipeline(app);
app.Run();

/// <summary>Entry point type (also the anchor for WebApplicationFactory in tests).</summary>
public partial class Program
{
}

namespace AstraKingdoms.Server
{
    /// <summary>Service registration and the HTTP pipeline, shared by the executable and the tests.</summary>
    public static class ServerSetup
    {
        private const string GoogleKeysClient = "google-signing-keys";

        public static void ConfigureServices(WebApplicationBuilder builder)
        {
            builder.Services.AddOptions<ServerOptions>().Bind(builder.Configuration.GetSection(ServerOptions.Section));
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
            if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);

            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(sp =>
            {
                ServerOptions o = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
                return new SqliteStore(o.Storage.SqlitePath, sp.GetRequiredService<TimeProvider>(), o.Storage.MatchRecordRetentionDays);
            });
            builder.Services.AddSingleton<IMatchRepository>(sp => sp.GetRequiredService<SqliteStore>());
            builder.Services.AddSingleton<IRewardLedger>(sp => sp.GetRequiredService<SqliteStore>());
            builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<SqliteStore>());
            builder.Services.AddSingleton<IGrievanceStore>(sp => sp.GetRequiredService<SqliteStore>());

            builder.Services.AddHttpClient(GoogleKeysClient, c => c.Timeout = TimeSpan.FromSeconds(10));
            builder.Services.AddSingleton<ISigningKeySource>(sp => new GoogleX509KeySource(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleKeysClient), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<ServerOptions>>().Value.Auth.SigningKeysUrl));
            builder.Services.AddSingleton<IIdentityVerifier>(sp =>
            {
                ServerOptions o = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
                IHostEnvironment env = sp.GetRequiredService<IHostEnvironment>();
                if (o.Auth.Mode == AuthMode.Dev)
                {
                    if (!env.IsDevelopment() && !o.Auth.AllowDevOutsideDevelopment)
                        throw new InvalidOperationException("Dev authentication is refused outside the Development environment.");
                    return new DevIdentityVerifier();
                }
                return new FirebaseIdTokenVerifier(sp.GetRequiredService<ISigningKeySource>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IOptions<ServerOptions>>());
            });

            builder.Services.AddSingleton<ConnectionRegistry>();
            builder.Services.AddSingleton<IPlayerChannel>(sp => sp.GetRequiredService<ConnectionRegistry>());
            builder.Services.AddSingleton<MatchRegistry>();
            builder.Services.AddSingleton<LobbyService>();
            builder.Services.AddSingleton<RealtimeEndpoint>();
            builder.Services.AddSingleton<DrainState>();
            builder.Services.AddSingleton<ServiceMetrics>();
            builder.Services.AddSingleton(sp => new CheckpointWriter(sp.GetRequiredService<IMatchRepository>(),
                sp.GetRequiredService<ILogger<CheckpointWriter>>()));
            builder.Services.AddSingleton<HttpLimiters>();
            builder.Services.AddSingleton<LifecycleService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<LifecycleService>());
            builder.Services.AddHostedService<RetentionService>();
        }

        public static void ConfigurePipeline(WebApplication app)
        {
            // Resolve the verifier eagerly so a misconfigured auth mode fails at startup, not on the first player.
            app.Services.GetRequiredService<IIdentityVerifier>();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.Map(OnlineProtocol.WebSocketPath, (HttpContext http, RealtimeEndpoint endpoint) => endpoint.HandleAsync(http));
            HttpEndpoints.Map(app);
        }
    }
}
