using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Which transport new Codex work takes, resolved from the <c>codex-app-server</c> switch and the
/// installed Codex. Routing, the capability advertisement and the status snapshot read one committed
/// instance, so a version, a policy and a transport from different passes are never published together.
/// </summary>
internal sealed record CodexTransportResolution(long Generation, bool SwitchOn, string? CliVersion, bool Active, string? InactiveReason) {
    public static CodexTransportResolution Unresolved { get; } = new(0, false, null, false, null);

    public string InteractiveTransport => Active ? CodexTransportDecision.AppServer : CodexTransportDecision.Pty;

    public string LauncherPolicyVersion =>
        Active ? DaemonRunner.CodexAppServerLauncherPolicyVersion : DaemonRunner.CodexLauncherPolicyVersion;

    /// <summary>Probes only when the switch is on: a daemon with it off never pays for the spawn.</summary>
    public static CodexTransportResolution Resolve(long generation, bool switchOn, Func<string?> probe) {
        if (!switchOn) return new(generation, false, null, false, null);

        var version = probe();
        if (CodexTransportDecision.MeetsFloor(version)) return new(generation, true, version, true, null);

        return new(generation, true, version, false, version is null
            ? "Codex version could not be read"
            : $"Codex {version} is below {CodexTransportDecision.VersionFloor}");
    }

    /// <summary>Resolves against the saved switch, re-reading it after the probe and resolving again
    /// when it moved meanwhile. An unreadable file reads as off, with the file's problem as the reason.</summary>
    public static CodexTransportResolution Read(long generation, Func<ExperimentalSettingsRead> settings, Func<string?> probe) {
        var id = ExperimentalFeatures.CodexAppServer.Id;
        for (var pass = 1; ; pass++) {
            var before     = settings();
            var resolution = Resolve(generation, before.IsEnabled(id), probe);
            if (!before.Readable) resolution = resolution with { InactiveReason = before.Problem };

            // Bounded so a file rewritten faster than Codex answers cannot hold the pass forever; the
            // watcher's next trigger picks up whatever it settles on.
            if (settings().IsEnabled(id) == before.IsEnabled(id) || pass == MaxPasses) return resolution;
        }
    }

    const int MaxPasses = 5;

    /// <summary>The same answer, whatever generation either carries.</summary>
    public bool SameStateAs(CodexTransportResolution other) => this with { Generation = other.Generation } == other;
}
