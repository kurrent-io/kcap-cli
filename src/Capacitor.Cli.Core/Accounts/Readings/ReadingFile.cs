namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>One account's reading on disk. <see cref="RefreshOrigins"/> holds the <c>ProducedAt</c> of the
/// last observations a refresh merged, so any daemon on the host can tell refresh-produced block
/// evidence from an agent's. Local only; never published.</summary>
public sealed record ReadingFile(AccountReadingDto Reading, ReadingSession[] Sessions, DateTimeOffset[]? RefreshOrigins = null) {
    public static ReadingFile Empty(long generation) => new(ReadingMerge.Empty(generation), []);
}
