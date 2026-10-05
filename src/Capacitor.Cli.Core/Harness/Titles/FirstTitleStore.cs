namespace Capacitor.Cli.Core.Harness.Titles;

/// <summary>Reads each store in order and returns the first title found — for a harness that keeps one session's
/// title in more than one place.</summary>
public sealed class FirstTitleStore(params IHarnessTitleStore[] stores) : IHarnessTitleStore {
    public bool RecordsChangeTime => stores.Any(s => s.RecordsChangeTime);

    public StoreTitle? Read() {
        foreach (var store in stores) {
            if (store.Read() is { } title) return title;
        }

        return null;
    }
}
