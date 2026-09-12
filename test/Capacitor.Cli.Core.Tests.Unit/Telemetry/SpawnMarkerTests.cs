using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class SpawnMarkerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>
    /// Reading the marker consumes it, so these mutate the real environment — the only place it
    /// lives. Bare, because a concurrent peer spawning a child would inherit whatever is set.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task The_marker_is_read_once_and_removed_from_the_environment() {
        using var marker = EnvScope.Exclusive(TelemetryStartup.SpawnNoTelemetryVar, "1");

        var startup = TelemetryStartup.FromEnvironment("login", serverUrl: null, AuthEndpoints.DefaultSignupUrl);

        await Assert.That(startup.Suppressed).IsTrue();
        await Assert.That(Environment.GetEnvironmentVariable(TelemetryStartup.SpawnNoTelemetryVar)).IsNull()
            .Because("nothing this process spawns may observe it");
    }

    [Test]
    [NotInParallel]
    public async Task No_marker_suppresses_nothing() {
        using var marker = EnvScope.Exclusive(TelemetryStartup.SpawnNoTelemetryVar, null);

        await Assert.That(TelemetryStartup.FromEnvironment("login", serverUrl: null, AuthEndpoints.DefaultSignupUrl).Suppressed).IsFalse();
    }

    /// <summary>The marker is ours; the opt-out is the user's, and consuming one must not touch the other.</summary>
    [Test]
    [NotInParallel]
    public async Task Consuming_the_marker_leaves_the_users_own_KCAP_TELEMETRY_alone() {
        using var marker = EnvScope.Exclusive(TelemetryStartup.SpawnNoTelemetryVar, "1");
        using var choice = EnvScope.Exclusive("KCAP_TELEMETRY", "1");

        TelemetryStartup.FromEnvironment("login", serverUrl: null, AuthEndpoints.DefaultSignupUrl);

        await Assert.That(Environment.GetEnvironmentVariable("KCAP_TELEMETRY")).IsEqualTo("1");
    }

    [Test]
    public async Task A_suppressed_startup_yields_a_facade_that_captures_nothing() {
        var probe = TelemetryProbe.Start("login", Config.Root, suppressed: true);

        probe.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Events).IsEmpty();
    }

    /// <summary>
    /// The marker is gone from the environment once read, so a second facade in the same process
    /// cannot re-take the decision — inheriting the value is the only way it survives, which is what
    /// an MCP server does when it re-derives its own under the "mcp-server" pseudo-command.
    /// </summary>
    [Test]
    public async Task Suppression_travels_to_a_second_facade_in_the_same_process() {
        var startup = new TelemetryStartup("mcp", ServerUrl: null, AuthEndpoints.DefaultSignupUrl,
                                    Suppressed: true, Debug: false);

        var probe = TelemetryProbe.Start(startup with { Command = "mcp-server" }, Config.Root);

        probe.Telemetry.Capture("mcp_tool_called", []);

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Events).IsEmpty();
    }
}
