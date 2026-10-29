namespace Capacitor.Cli.Core.Harness.Titles;

/// <summary>A store read and when it started: the value it returns was current at or after that instant, so that is
/// the time it is observed at, however late it finishes.</summary>
public sealed record HarnessTitleRead(Task<StoreTitle?> Reading, DateTimeOffset StartedAt);
