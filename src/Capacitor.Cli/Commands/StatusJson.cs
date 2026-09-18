using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>The configured server and whether it answered.</summary>
/// <param name="Url">Null when no server is configured, which is what an unset-up machine looks like.</param>
/// <param name="Reachable">Null when there was nothing to probe. Reachability, not authorization.</param>
/// <param name="StatusCode">Set only when the server answered with a failure status.</param>
public sealed record StatusServerJson(string? Url, bool? Reachable, int? StatusCode);

/// <summary>What this CLI authenticates as.</summary>
/// <param name="State">
/// <c>machine</c> — both machine variables set, so this CLI records as the machine;
/// <c>machine_incomplete</c> — one set, which diverts auth off the token store and then fails,
/// so nothing records and <c>login</c> is not the fix;
/// <c>valid</c> — a stored token, bound to the configured server;
/// <c>wrong_server</c> — a token issued by a different server, withheld before any request;
/// <c>expired</c>; or <c>none</c>.
/// </param>
public sealed record StatusAuthJson(string State, string? Identity, DateTimeOffset? ExpiresAt);

/// <param name="UpdateAvailable">The version to move to, or null when current.</param>
/// <param name="ServerCapped">The target was held back to the connected server's version.</param>
public sealed record StatusVersionJson(
    string Current, string? UpdateAvailable, bool ServerCapped, bool Bundled);

/// <param name="InstallCommand">What to run to wire this one up, when it is installed and unwired.</param>
public sealed record StatusHarnessJson(
    string Vendor, bool Installed, bool Wired, string? InstallCommand);

public sealed record StatusDaemonEntryJson(string Name, int Pid);

/// <param name="StalePidFiles">PID files with no live process behind them — `kcap daemon doctor
/// --clean` removes these.</param>
public sealed record StatusDaemonJson(
    bool Running, IReadOnlyList<StatusDaemonEntryJson> Daemons, bool StalePidFiles);

/// <summary>Machine-readable payload for <c>kcap status --json</c>.</summary>
/// <param name="Configured">The one question a caller deciding whether to run setup is asking: a
/// server is configured and this CLI has credentials the token store still considers good. It does
/// not assert the server accepts them — <c>kcap whoami</c> is what asks the server.</param>
public sealed record StatusJson(
    bool Configured, string Profile, StatusServerJson Server, StatusAuthJson Auth,
    StatusVersionJson Version, IReadOnlyList<StatusHarnessJson> Harnesses, StatusDaemonJson Daemon);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusJson))]
public partial class StatusJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the status payload — kept separate from I/O so it's directly testable.</summary>
internal static class StatusJsonRender {
    public static string Render(StatusJson status) =>
        JsonSerializer.Serialize(status, StatusJsonContext.Default.StatusJson);

    /// <summary>A machine is set up when it knows a server and holds credentials for it. An
    /// unreachable server does not make it unconfigured — that is a network fact, not a setup one.</summary>
    public static bool IsConfigured(string? serverUrl, string authState) =>
        serverUrl is not null && authState is "valid" or "machine";
}
