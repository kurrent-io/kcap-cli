namespace Capacitor.Cli.Core.LocalEdition;

public static class LocalServerConditions {
    /// <summary>The binary the unit runs, reached through <c>bin/current</c> so an upgrade switches it in place.</summary>
    public static string ServerBinary(LocalRoot root) => Path.Combine(root.Directory, "bin", "current", ServerFileName);

    public static string ServerFileName => OperatingSystem.IsWindows() ? "kcap-server.exe" : "kcap-server";

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

    static HttpClient Probe => field ??= new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });

    /// <summary><see cref="Read(LocalRoot)"/>, then, for an installation a start could bring up, who answers on its
    /// port. Still write-free and lock-free; it costs one request, and a process lookup only when the port is not
    /// plainly free or plainly the server's.</summary>
    public static Task<LocalServerReading> ReadAsync(LocalRoot root, CancellationToken ct) =>
        ReadAsync(root, Probe, PortHolder.FindAsync, TimeProvider.System, ct);

    /// <summary>An answer the kcap server never gives is another program's whatever the OS says; a connection with no
    /// answer is another program's only when the OS names a listener that is not the server. Anything less, a probe or
    /// lookup that fails included, stays <see cref="LocalServerCondition.Installed"/>: not running as far as anyone
    /// knows, and never taken as healthy. Only the installation's own records make a reading unreadable.</summary>
    public static async Task<LocalServerReading> ReadAsync(
            LocalRoot root, HttpClient http, Func<int, CancellationToken, Task<PortHolder?>> findHolder, TimeProvider time, CancellationToken ct) {
        var condition = Read(root);
        if (condition != LocalServerCondition.Installed || LocalEndpoints.Read(root).Active is not { Port: var port })
            return new(condition);

        try {
            var answer = await LocalPortProbe.ReadAsync(http, port, time, ct);
            if (answer is not (LocalPortAnswer.Foreign or LocalPortAnswer.Silent)) return new(condition, port);

            var holder = await findHolder(port, ct) is { IsServerProcess: false } named ? named : null;

            return answer switch {
                LocalPortAnswer.Foreign(var what) => new(LocalServerCondition.PortTaken, port, holder, what),
                _ when holder is not null         => new(LocalServerCondition.PortTaken, port, holder),
                _                                 => new(condition, port),
            };
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            return new(condition, port);
        }
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
            LocalServerCondition.PortTaken    => "Another program holds the local server's port, so it cannot start. Stop that program; kcap never stops a program it did not start.",
            LocalServerCondition.Restarting   => "Its service is starting it again, and it reconnects once the server answers. kcap local logs shows why it stopped.",
            LocalServerCondition.ServesAnother => "Its service runs another local installation, so this one cannot start. kcap local status names it.",
            _                                 => null,
        };

        /// <summary>The name <c>kcap local status --json</c> gives the condition.</summary>
        public string Wire => condition switch {
            LocalServerCondition.NotInstalled => "not_installed",
            LocalServerCondition.Purged       => "purged",
            LocalServerCondition.Uninstalled  => "uninstalled",
            LocalServerCondition.FilesMissing => "files_missing",
            LocalServerCondition.Changing     => "changing",
            LocalServerCondition.Interrupted  => "interrupted",
            LocalServerCondition.Unreadable   => "unreadable",
            LocalServerCondition.Installed    => "installed",
            LocalServerCondition.PortTaken    => "port_taken",
            LocalServerCondition.Restarting   => "restarting",
            LocalServerCondition.ServesAnother => "serves_another",
            _                                 => throw new ArgumentOutOfRangeException(nameof(condition), condition, null),
        };
    }

    /// <summary>Null for a name this build does not know, as a newer CLI may print.</summary>
    public static LocalServerCondition? FromWire(string? wire) =>
        Enum.GetValues<LocalServerCondition>().Cast<LocalServerCondition?>().FirstOrDefault(c => c!.Value.Wire == wire);
}
