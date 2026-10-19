using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The daemon's hub negotiation names its kcap version, which a local server holds to its client floor.</summary>
public class ServerConnectionVersionHeaderTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class SessionsHub : Hub;

    sealed class TestServerConnection(DaemonConfig config)
        : ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
                           NullLogger<ServerConnection>.Instance, TimeProvider.System);

    [Test]
    public async Task The_negotiation_carries_the_cli_version() {
        string? negotiated = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.Use(async (ctx, next) => {
            if (ctx.Request.Path.Value?.EndsWith("/negotiate", StringComparison.Ordinal) == true)
                negotiated = ctx.Request.Headers[HttpClientExtensions.CliVersionHeader].ToString();
            await next();
        });
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var config = new DaemonConfig {
            Name       = "test",
            ServerUrl  = app.Urls.Single(),
            ConfigRoot = Config.Root,
            Profiles   = Resolutions.None(Config.Root),
        };
        await using var conn = new TestServerConnection(config);
        using var cts = new CancellationTokenSource(HangGuard);
        await conn.StartHubAsync(cts.Token);

        await Assert.That(HttpClientExtensions.CliVersion).IsNotNull();
        await Assert.That(negotiated).IsEqualTo(HttpClientExtensions.CliVersion);
    }
}
