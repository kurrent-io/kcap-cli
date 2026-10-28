namespace Capacitor.Cli.Core.Accounts;

/// <summary>The one line <c>kcap accounts add|remove|rename|rewire|refresh --json</c> prints. <see cref="Revision"/>
/// is the registry revision the verb committed after its wiring attempt (null when it committed nothing),
/// so a snapshot at or past it was built after that attempt. <see cref="AccountId"/> is null when the
/// verb acted on no single account.</summary>
public sealed record AccountActionResultDto(string Action, string? AccountId, bool Succeeded, WiringStepDto[] Steps, long? Revision, string? Error);
