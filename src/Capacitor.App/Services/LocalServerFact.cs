using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

/// The local server the uninstall would delete: the active installation's URL, or the local root
/// while a lifecycle journal stands (interrupted or unreadable), which the CLI also refuses to
/// remove without consent.
public static class LocalServerFact {
    public static string? Read(LocalRoot root) {
        if (LocalEndpoints.Read(root).Active?.ServerUrl is { } url) return url;

        return LocalStateFiles.ReadJournal(root) is StateRead<LifecycleJournalEntry>.Present or StateRead<LifecycleJournalEntry>.Unreadable
            ? root.Directory
            : null;
    }
}
