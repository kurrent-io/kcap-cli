namespace Capacitor.Cli.Core.Handoff;

/// <summary>The server's answer to beginning a takeover: exactly one of <paramref name="Token"/> and
/// <paramref name="Refusal"/>. The token is renewed within <paramref name="LeaseSeconds"/>, zero on a refusal.</summary>
public sealed record HandoffTakeoverAdmission(string? Token, string? Refusal, int LeaseSeconds);
