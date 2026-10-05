namespace Capacitor.Cli.Core.Harness.Titles;

/// <summary>A harness's current title for one session, read from its local store.</summary>
public interface IHarnessTitleStore {
    /// <summary>True when the store records when the title changed (sent even on a first read).</summary>
    bool RecordsChangeTime { get; }

    /// <summary>The current title, or null when none or unreadable. Never throws.</summary>
    StoreTitle? Read();
}
