namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap status --json</c>.</summary>
/// <param name="Configured">The one question a caller deciding whether to run setup is asking: a
/// server is configured and this CLI can authenticate to it — with credentials the token store
/// still considers good, or because the server asks for none. It does not assert the server accepts
/// them — <c>kcap whoami</c> is what asks the server.</param>
public sealed record StatusJson(
    bool Configured, string Profile, StatusServerJson Server, StatusAuthJson Auth,
    StatusVersionJson Version, IReadOnlyList<StatusHarnessJson> Harnesses, StatusDaemonJson Daemon);
