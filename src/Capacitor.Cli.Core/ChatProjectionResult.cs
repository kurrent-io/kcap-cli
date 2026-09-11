namespace Capacitor.Cli.Core;

/// A local slash command can acknowledge input even when its transcript wrappers remain hidden.
public sealed record ChatProjectionResult(IReadOnlyList<AcpEventEnvelope> Envelopes, IReadOnlyList<string> SubmittedInputs);
