namespace Capacitor.Cli.Core.LocalEdition;

public static class LocalServerConditions {
    /// <summary>The binary the unit runs, reached through <c>bin/current</c> so an upgrade switches it in place.</summary>
    public static string ServerBinary(LocalRoot root) => Path.Combine(root.Directory, "bin", "current", "kcap-server");

    /// <summary>Lock-free and write-free, like <c>kcap local status</c>: the lock is probed, never taken, so a reader
    /// cannot hold up the command it reports on.</summary>
    public static LocalServerCondition Read(LocalRoot root) {
        if (LifecycleLease.IsHeld(root)) return LocalServerCondition.Changing;

        switch (LocalStateFiles.ReadJournal(root)) {
            case StateRead<LifecycleJournalEntry>.Present:    return LocalServerCondition.Interrupted;
            case StateRead<LifecycleJournalEntry>.Unreadable: return LocalServerCondition.Unreadable;
        }

        return LocalStateFiles.ReadRegistry(root) switch {
            StateRead<InstallRegistry>.Unreadable => LocalServerCondition.Unreadable,
            StateRead<InstallRegistry>.Present { Value.Active: not null } when !Directory.Exists(Path.Combine(root.Directory, "bin"))
                => LocalServerCondition.Uninstalled,
            StateRead<InstallRegistry>.Present { Value.Active: not null } => File.Exists(ServerBinary(root))
                ? LocalServerCondition.Installed
                : LocalServerCondition.FilesMissing,
            StateRead<InstallRegistry>.Present { Value.Retired.Count: > 0 } => LocalServerCondition.Purged,
            _ => LocalServerCondition.NotInstalled,
        };
    }

    extension(LocalServerCondition condition) {
        /// <summary>The one line a client shows for a server it cannot reach, in the words <c>kcap local status</c>
        /// uses; null for <see cref="LocalServerCondition.Installed"/>, where only the caller knows whether it can start it.</summary>
        public string? Explanation => condition switch {
            LocalServerCondition.NotInstalled => "There is no local server on this computer. kcap setup --local installs one.",
            LocalServerCondition.Purged       => "The local server was removed with its data. kcap setup --local installs a new one.",
            LocalServerCondition.Uninstalled  => "The local server was uninstalled. Its data is kept, and kcap setup --local reinstalls it.",
            LocalServerCondition.FilesMissing => "The local server's files are missing. kcap setup --local restores them.",
            LocalServerCondition.Changing     => "Another kcap command is changing the local server. It reconnects when that finishes.",
            LocalServerCondition.Interrupted  => "An earlier local server command stopped part way. kcap local status says what to do.",
            LocalServerCondition.Unreadable   => "The local installation's records cannot be read. kcap local status says which file.",
            _                                 => null,
        };
    }
}
