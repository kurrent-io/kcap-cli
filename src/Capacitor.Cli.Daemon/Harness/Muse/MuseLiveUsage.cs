namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>The last <c>session/tokenUsage</c> counters. Live status only: the session log is the record of usage.</summary>
internal sealed record MuseLiveUsage(string? ModelId, long InputTokens, long OutputTokens, long CachedTokens, long ReasoningTokens);
