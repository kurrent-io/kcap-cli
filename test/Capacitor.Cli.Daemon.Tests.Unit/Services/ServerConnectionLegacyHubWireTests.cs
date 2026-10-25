using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A real SignalR hub with no <c>AgentRegisteredAcked</c> method, which a server older than the daemon can still be:
/// the missing-method answer is the one its dispatcher actually sends, not a string a test double chose. Detailed
/// errors stay off, as on a tenant.
/// </summary>
public class ServerConnectionLegacyHubWireTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class Received {
        public TaskCompletionSource<AgentRegistered> Legacy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The registration method of a server without the acknowledged one.</summary>
    sealed class LegacySessionsHub(Received received) : Hub {
        public Task AgentRegistered(AgentRegistered registration) {
            received.Legacy.TrySetResult(registration);

            return Task.CompletedTask;
        }
    }

    sealed class TestServerConnection(DaemonConfig config)
        : ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
                           NullLogger<ServerConnection>.Instance, TimeProvider.System);

    [Test]
    public async Task A_registration_against_a_server_without_the_acked_method_lands_on_the_legacy_one() {
        var received = new Received();
        var builder  = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(received);
        builder.Services.AddSignalR().AddJsonProtocol(o => ServerConnection.ConfigureHubPayload(o.PayloadSerializerOptions));

        await using var app = builder.Build();
        app.MapHub<LegacySessionsHub>("/hubs/sessions");
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

        var response = await conn.AgentRegisteredAsync("agent-1", "prompt", "model", null, "/repo", runtimeTransport: "pty")
            .WaitAsync(HangGuard);

        await Assert.That(response.Registered).IsTrue();
        var legacy = await received.Legacy.Task.WaitAsync(HangGuard);
        await Assert.That(legacy.AgentId).IsEqualTo("agent-1");

        await app.StopAsync(cts.Token);
    }
}
