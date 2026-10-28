namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>Reading access for a caller that already holds <c>readings.lock</c> inside
/// <see cref="AccountStore.MutateWithReadings{T}"/>.</summary>
public sealed class HeldReadings {
    readonly ReadingStore _store;

    internal HeldReadings(ReadingStore store) => _store = store;

    /// <summary>Missing, unparseable, or of another generation reads as an empty reading of
    /// <paramref name="generation"/>.</summary>
    public ReadingFile Read(string accountId, long generation) => _store.ReadNormalized(accountId, generation);

    public void Write(string accountId, ReadingFile file) => _store.WriteFile(accountId, file);

    public void Delete(string accountId) => _store.DeleteFile(accountId);
}
