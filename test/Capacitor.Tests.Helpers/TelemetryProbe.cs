using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A telemetry facade and the sink it writes to, built together so a test reads back only its own
/// events. Nothing here is shared: two probes in the same process see nothing of each other, which
/// is why tests that assert on telemetry need no parallelism constraint.
/// </summary>
public sealed class TelemetryProbe {
    TelemetryProbe(CliTelemetry telemetry, RecordingTelemetrySink sink) {
        Telemetry = telemetry;
        Sink      = sink;
    }

    public CliTelemetry           Telemetry { get; }
    public RecordingTelemetrySink Sink      { get; }

    public SetupJoin   Join   => Telemetry.Join;
    public SetupFunnel Funnel => Telemetry.Funnel;

    public IReadOnlyList<TelemetryEvent> Events => Sink.Events;
    public IReadOnlyList<string>         Names  => Sink.Names;

    public static TelemetryProbe Start(
            string command, ConfigRoot config, string? serverUrl = null, bool loggedIn = false,
            bool suppressed = false, bool debug = false, string? signupUrl = null) =>
        Start(new TelemetryStartup(command, serverUrl, signupUrl ?? AuthEndpoints.DefaultSignupUrl,
                                   suppressed, debug), config, loggedIn);

    public static TelemetryProbe Start(TelemetryStartup startup, ConfigRoot config, bool loggedIn = false) {
        var sink      = new RecordingTelemetrySink();
        var telemetry = CliTelemetry.Start(startup, config, () => sink);

        // Program.cs completes the bag before announcing, so a probe does too — every event a test
        // reads back then carries what a real run's would.
        telemetry.AddSharedProperty("logged_in", loggedIn);
        telemetry.Announce(config);

        return new TelemetryProbe(telemetry, sink);
    }

    /// <summary>
    /// A facade that must come up live, with <c>cli_first_run</c> already dropped — it says nothing
    /// about what the test goes on to do. Throws a diagnosis rather than recording nothing when
    /// telemetry resolves off, since an empty sink otherwise surfaces as an opaque "sequence
    /// contains no elements" from whatever assertion runs next.
    /// </summary>
    public static TelemetryProbe Live(
            string command, ConfigRoot config, string? serverUrl = null, bool loggedIn = false,
            bool debug = false, string? signupUrl = null) {
        var probe = Start(command, config, serverUrl, loggedIn, suppressed: false, debug, signupUrl);

        TelemetryTestGuards.AssertEnabled(command, config, probe.Telemetry);
        probe.Sink.Discard();

        return probe;
    }

    /// <summary>The events captured under <paramref name="name"/>, in the order they were captured.</summary>
    public IReadOnlyList<TelemetryEvent> Captured(string name) => [.. Events.Where(e => e.Name == name)];

    /// <summary>The one event captured under <paramref name="name"/>; throws when there is not exactly one.</summary>
    public TelemetryEvent Only(string name) => Captured(name).Single();
}
