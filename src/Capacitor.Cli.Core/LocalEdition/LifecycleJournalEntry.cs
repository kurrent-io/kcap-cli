namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The lifecycle operation in flight and how far it got. The next lifecycle command hands it to the
/// recovery registered for <see cref="Operation"/>, which finishes or undoes it.
/// </summary>
/// <param name="ServerStart">How the server starts while this entry stands. The server reads only
/// this and never interprets <see cref="Phase"/>.</param>
/// <param name="Details">Whatever the operation needs to recover from this phase.</param>
public sealed record LifecycleJournalEntry(
    string Operation,
    string Phase,
    ServerStart ServerStart,
    string? InstallationId,
    DateTimeOffset StartedAt,
    IReadOnlyDictionary<string, string> Details);
