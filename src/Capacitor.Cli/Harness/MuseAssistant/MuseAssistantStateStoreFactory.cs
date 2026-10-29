namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Opens the store for a profile's directory. A collaborator rather than a <c>new</c> at the
/// call site so a test can hand the ingest a store that fails a chosen write.</summary>
public class MuseAssistantStateStoreFactory {
    public virtual MuseAssistantStateStore Create(string scopeDir, MuseAssistantPrincipal principal) => new(scopeDir, principal);
}
