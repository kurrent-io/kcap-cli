using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>What a service PATH refresh did, with the service manager's own account of a failure.</summary>
/// <param name="Reinstall">The command that reinstalls this unit, flags included.</param>
sealed record PathRefresh(UnitRefresh Outcome, string? Error = null, string? Reinstall = null);
