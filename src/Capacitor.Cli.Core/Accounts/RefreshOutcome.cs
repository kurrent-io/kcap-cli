namespace Capacitor.Cli.Core.Accounts;

public sealed record RefreshOutcome(bool Found, IReadOnlyList<WiringStep> Steps, long Revision);
