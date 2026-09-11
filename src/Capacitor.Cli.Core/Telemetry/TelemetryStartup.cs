namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// What a process knows about telemetry before it builds a facade: which command it is running,
/// which server decides the organization group, and whether the desktop app spawned it.
///
/// <para>Carrying suppression as a value is what lets a second facade in the same process honour
/// it. <see cref="FromEnvironment"/> REMOVES the marker so nothing this process spawns can observe
/// it, which also means nothing can re-read it — an MCP server re-deriving its own startup with
/// <c>this with { Command = "mcp-server" }</c> inherits the decision instead of re-taking it.</para>
/// </summary>
public sealed record TelemetryStartup(string Command, string? ServerUrl, bool Suppressed, bool Debug) {
    /// <summary>
    /// The app-spawned-child marker: consumed for telemetry suppression and removed from the
    /// process environment before command dispatch, so nothing this process spawns (a detached
    /// daemon, hosted children) can observe it. Never touches the user's own KCAP_TELEMETRY choice.
    /// </summary>
    public const string SpawnNoTelemetryVar = "KCAP_APP_SPAWN_NO_TELEMETRY";

    public static TelemetryStartup FromEnvironment(string command, string? serverUrl) {
        var debug = Environment.GetEnvironmentVariable("KCAP_TELEMETRY_DEBUG") == "1";

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SpawnNoTelemetryVar)))
            return new TelemetryStartup(command, serverUrl, Suppressed: false, debug);

        Environment.SetEnvironmentVariable(SpawnNoTelemetryVar, null);

        return new TelemetryStartup(command, serverUrl, Suppressed: true, debug);
    }
}
