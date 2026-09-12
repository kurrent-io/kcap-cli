using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Collaborators from a facade that is off, for a test that drives a lane which reports telemetry
/// but does not observe it. Naming it at the call site is the point: a test reading
/// <c>NoTelemetry.Funnel</c> is saying it expects nothing to be emitted, where a probe would say
/// the opposite.
/// </summary>
public static class NoTelemetry {
    public static CliTelemetry Facade => CliTelemetry.Disabled();

    /// <summary>A suppressed startup, so anything that starts its own facade from it comes up off.</summary>
    public static TelemetryStartup Startup =>
        new("test", ServerUrl: null, AuthEndpoints.DefaultSignupUrl, Suppressed: true, Debug: false);
    public static SetupFunnel  Funnel => Facade.Funnel;
    public static SetupJoin    Join   => Facade.Join;
}
