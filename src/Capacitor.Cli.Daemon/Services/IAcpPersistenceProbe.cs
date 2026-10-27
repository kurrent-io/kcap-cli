namespace Capacitor.Cli.Daemon.Services;

/// <summary>Which of a hosted session's user messages, by vendor item id, the server has stored.</summary>
internal interface IAcpPersistenceProbe {
    /// <returns>Each given item already stored, with the seq it was stored under; an item not stored within
    /// <paramref name="budget"/>, or never seen, is left out.</returns>
    Task<IReadOnlyDictionary<string, long>> AwaitPersistedAsync(IReadOnlyCollection<string> itemIds, TimeSpan budget, CancellationToken ct);
}
