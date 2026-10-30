using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Explains the state of this root's installation without changing it: no write, and no probe that could
/// keep a starting server from running. Every state a crash, a failed stop or a taken port can leave behind reads as
/// a verdict and what to do about it.
/// </summary>
sealed class LocalStatusReader(LocalStack stack, string cliVersion, Func<int, PortHolder?> findHolder) {
    const string Logs = "kcap local logs";

    public async Task<LocalStatusReport> ReadAsync(CancellationToken ct) {
        var root = stack.Root.Directory;

        return stack.Read() switch {
            LocalStackState.NotInstalled => NotInstalled(root),

            LocalStackState.Unreadable(var path, var reason) => new(false, $"{path} cannot be read ({reason}).", [],
                ["No kcap local command acts on the installation until that file is fixed or removed."]),

            LocalStackState.Interrupted(var operation, var phase) when LifecycleLease.IsHeld(stack.Root) => new(false,
                $"Local {operation} in progress ({phase}).", [new("Root", root)], ["Wait for it to finish before running another local command."]),

            LocalStackState.Interrupted(var operation, var phase) => new(false,
                $"An interrupted {operation} of the local installation stopped at \"{phase}\".", [new("Root", root)],
                ["The next kcap local start, stop, restart or upgrade, or kcap setup --local, finishes or undoes it first."]),

            LocalStackState.Installed installed => await InstalledAsync(installed, ct),

            var other => throw new InvalidOperationException($"Unexpected state {other}."),
        };
    }

    /// <summary>A purge leaves the retired URL in the registry; that is the only evidence it ever existed, and nothing
    /// about it is offered to start.</summary>
    LocalStatusReport NotInstalled(string root) =>
        LocalStateFiles.ReadRegistry(stack.Root) is StateRead<InstallRegistry>.Present { Value.Retired: [.., var retired] }
            ? new(false, $"Purged: no local installation in {root}.", [new("Retired", retired.ServerUrl)],
                [$"Its data was deleted and {retired.ServerUrl} is retired, so nothing is sent there again. kcap setup --local installs a new one on a new port."])
            : new(false, $"No local installation in {root}.", [], ["Run kcap setup --local to install one."]);

    async Task<LocalStatusReport> InstalledAsync(LocalStackState.Installed state, CancellationToken ct) {
        var (installation, unit) = (state.Installation, state.Unit);
        var service = stack.Service;
        var notes   = new List<string>();

        if (PrereleaseSemver.IsNewer(cliVersion, installation.Version))
            notes.Add($"The stack is behind this kcap ({cliVersion}). Run kcap local upgrade to upgrade it.");
        else if (PrereleaseSemver.IsNewer(installation.Version, cliVersion))
            notes.Add($"The stack is newer than this kcap ({cliVersion}); this kcap keeps working with it, and kcap local upgrade leaves it as it is.");

        var recorded = LocalEmbeddingFile.Read(stack.Root);
        var pending  = recorded is { Pending: true } ? " (not yet applied; kcap setup --local applies it)" : "";

        var fields = new List<KeyValuePair<string, string>> {
            new("URL", installation.ServerUrl),
            new("Version", installation.Version),
            new("Installation", installation.Id),
            new("Root", stack.Root.Directory),
            new("Service", $"{service.Describe} {Label(unit)} ({service.UnitFile(unit)})"),
            new("Search", (unit.Embedding is { Provider: LocalEmbeddingProvider.Ollama, Model: { } model }
                ? $"by meaning through Ollama with {model} while it answers, and by keyword"
                : unit.Embedding is { Provider: LocalEmbeddingProvider.Builtin }
                ? "by meaning with the built-in model, and by keyword"
                : "by keyword only; kcap setup --local --builtin-embeddings adds search by meaning")
              + pending),
        };

        // A start, stop, restart or uninstall in flight passes through every state below on its way.
        var held = LifecycleLease.IsHeld(stack.Root);
        if (held && state.UnitAbsent) return Changing(fields, notes);

        if (state.UnitAbsent)
            return Directory.Exists(Path.Combine(stack.Root.Directory, "bin"))
                ? Down($"Not running: no {service.Describe} is installed for it.", fields, notes, StoppedAdvice(unit, "installs and starts it"), Leftovers())
                : Down($"Uninstalled: its {service.Describe} and the server's files were removed.", fields, notes,
                    [$"Its data in {Path.Combine(stack.Root.Directory, "data")} and its URL are kept. kcap setup --local reinstalls it on the same port; "
                   + "kcap local start cannot run it until then."], Leftovers());

        switch (state.Owner) {
            case OwnerDecision.NotOurs:
                var others = Roots(state.Readings).ToList();
                return Down($"Not running: the {service.Describe} {Label(unit)} serves another installation.", fields, notes,
                    [$"It runs the installation in {string.Join(" and ", others)}. One user has one unit, so only one installation runs at a time; "
                  + $"manage that one with KCAP_LOCAL_ROOT={others.FirstOrDefault() ?? "<its root>"} kcap local …."]);

            case OwnerDecision.Unknown:
                var unreadable = Unreadable(state.Readings).ToList();
                return unreadable.Count > 0
                    ? Down($"Unknown: the {service.Describe}'s state could not be read.", fields, notes, unreadable)
                    : Down($"Unknown: the {service.Describe} names {string.Join(" and ", Roots(state.Readings))}, which may be this root by another spelling.",
                        fields, notes,
                        ["Telling the two apart writes a probe file into the root, which status never does. kcap local start, stop or restart decides it under the lifecycle lock."]);
        }

        var activity = service.Activity(unit);
        if (held && activity is not UnitActivity.Running) return Changing(fields, notes);

        switch (activity) {
            case UnitActivity.Stopped:
                fields.Add(new("Postgres", PostgresField()));
                var blocked = await TakenAsync(unit, "Stop that process, then run kcap local start; kcap never stops a process it did not start.", ct);
                return blocked.Count > 0
                    ? Down($"Stopped, and port {installation.Port} is taken by another process.", fields, notes, blocked, Leftovers())
                    : Down("Stopped.", fields, notes, StoppedAdvice(unit, "starts it"), Leftovers());

            case UnitActivity.Running(var pid):
                fields.Add(new("Process", $"pid {pid}"));
                return await RunningAsync(installation.Port, pid, unit, fields, notes, pending, ct);

            case UnitActivity.Restarting(var code):
                fields.Add(new("Postgres", PostgresField()));
                return Down($"Not running: the server exited{ExitCode(code)} and the {service.Describe} is starting it again.", fields, notes,
                    await TakenAsync(unit, KeepsTrying, ct), [$"A server that cannot start a store or listen on its port exits and is started again; {Logs} shows why."]);

            case UnitActivity.Exited(var code):
                fields.Add(new("Postgres", PostgresField()));
                return Down($"Not running: the server exited{ExitCode(code)} and will not be started again on its own.", fields, notes,
                    await TakenAsync(unit, "Stop that process, then run kcap local restart; kcap never stops a process it did not start.", ct),
                    [$"kcap local restart starts it; {Logs} shows why it exited."]);

            case UnitActivity.Unknown(var reason):
                return Down($"Unknown: {reason}.", fields, notes);

            default:
                throw new InvalidOperationException("Unexpected unit activity.");
        }
    }

    async Task<LocalStatusReport> RunningAsync(
            int port, int pid, LocalServerUnit unit, List<KeyValuePair<string, string>> fields, List<string> notes, string pending, CancellationToken ct) {
        var taken = Taken(port, pid).ToList();

        switch (await LocalServerHealth.ReadAsync(stack.Local, unit, stack.Time, ct)) {
            case ServerHealth.Answered { Ready: true } ready:
                if (taken.Count > 0) return Down($"Not ready: something else answers on port {port}.", fields, notes, taken);

                AddStores(fields, ready.Stores);
                if (ready.UpdatePending) notes.Add("The server can serve requests while its update finishes. A planned restart is still pending.");
                await ApplyEmbeddingHealthAsync(fields, unit, pending, ct);

                return await LocalServerProbe.AuthConfigAsync(stack.Local, unit.BaseUrl, ct) switch {
                    AuthConfigAnswer.Lifted => new(true, $"Ready on {unit.BaseUrl}.", fields, notes),
                    AuthConfigAnswer.Pending => Down("Running fenced: the server answers as an installation setup has not committed.", fields, notes,
                        ["It accepts no writes until setup commits. Run kcap setup --local to finish it."]),
                    _ => Down($"Not ready: what answers on port {port} does not answer as this installation.", fields, notes,
                        [$"Another server may hold the port. The port is part of this installation's identity, so the stack cannot move to another; stop that process. {Logs} shows what this server saw."]),
                };

            case ServerHealth.Answered starting:
                if (taken.Count > 0) return Down($"Not ready: something else answers on port {port}.", fields, notes, taken);

                AddStores(fields, starting.Stores);
                await ApplyEmbeddingHealthAsync(fields, unit, pending, ct);
                return Down("Starting, or a store is down.", fields, notes,
                    [$"The server reports ready once both stores are up and its schema is migrated. {Logs} shows its progress."]);

            case ServerHealth.Foreign(var what):
                fields.Add(new("Postgres", PostgresField()));
                return Down($"Not ready: something else answers on port {port}.", fields, notes, taken.Count > 0 ? taken : Answers(port, what, KeepsTrying).AsEnumerable());

            case ServerHealth.Unreachable:
                fields.Add(new("Postgres", PostgresField()));
                if (taken.Count > 0) return Down($"Not ready: port {port} is taken.", fields, notes, taken);

                return Down($"Starting: the server does not answer on port {port} yet.", fields, notes,
                    [$"It answers once both stores are up and its schema is migrated; a first start, or one after an upgrade, can take minutes. {Logs} shows its progress."]);

            default:
                throw new InvalidOperationException("Unexpected health reading.");
        }
    }

    /// <summary>Replaces the file-derived Search line only for a built-in model the server reports on; everything else
    /// keeps the recorded wording.</summary>
    async Task ApplyEmbeddingHealthAsync(List<KeyValuePair<string, string>> fields, LocalServerUnit unit, string pending, CancellationToken ct) {
        if (await LocalServerProbe.EmbeddingsAsync(stack.Local, unit.BaseUrl, stack.Time, ct) is not { Provider: "builtin" } health) return;

        var index = fields.FindIndex(f => f.Key == "Search");
        if (index < 0) return;

        fields[index] = new("Search", (health switch {
            { Usable: true }  => "by meaning with the built-in model, and by keyword",
            { Reason: null }  => "by keyword only: the built-in model is not loaded yet",
            { Reason: var r } => $"by keyword only: the built-in model is not usable ({r}); {Remedy(r)}"
        }) + pending);
    }

    /// <summary>Setup restores files that are missing or altered; it finds verified files present and restarts nothing,
    /// so it cannot fix a model that fails to load.</summary>
    static string Remedy(string reason) => reason is "model_missing" or "digest_mismatch"
        ? "run kcap setup --local"
        : "run kcap local restart, and see kcap local logs if it persists";

    const string KeepsTrying = "Stop that process; while the service runs, the server starts on its own once the port is free.";

    /// <summary>A listener on the port that is not the server's own process; <paramref name="ours"/> null when the
    /// server is not running, so any listener is a stranger.</summary>
    IEnumerable<string> Taken(int port, int? ours, string then = KeepsTrying) {
        if (findHolder(port) is not { } holder || holder.Pid == ours) yield break;

        yield return $"Port {port} is taken by {holder.Command} (pid {holder.Pid}), so the server cannot listen on it. "
                   + "The port is part of this installation's identity, so the stack cannot move to another. "
                   + then;
    }

    /// <summary>With the server not running, a stranger the OS names, else one whose answer the kcap server never
    /// gives. Without root, <c>lsof</c> sees only this user's processes, so the answer is the only evidence of
    /// another user's.</summary>
    async Task<List<string>> TakenAsync(LocalServerUnit unit, string then, CancellationToken ct) {
        var named = Taken(unit.Port, null, then).ToList();
        if (named.Count > 0) return named;

        return await LocalServerHealth.ReadAsync(stack.Local, unit, stack.Time, ct) is ServerHealth.Foreign(var what)
            ? [.. Answers(unit.Port, what, then)]
            : [];
    }

    static string[] Answers(int port, string what, string then) => [
        $"Port {port} answers as {what}, not as the kcap server, so the server cannot listen on it. "
      + "The port is part of this installation's identity, so the stack cannot move to another. " + then,
        OperatingSystem.IsLinux()
            ? $"sudo ss -ltnp 'sport = :{port}' names the process that holds it."
            : $"sudo lsof -nP -iTCP:{port} -sTCP:LISTEN names the process that holds it.",
    ];

    static LocalStatusReport Changing(List<KeyValuePair<string, string>> fields, List<string> notes) =>
        Down("Changing: another kcap command holds the installation, so the server may be starting, stopping or restarting.", fields, notes,
            ["Wait for it to finish; kcap local status then says where it left the server. A start now would only wait behind it."]);

    static void AddStores(List<KeyValuePair<string, string>> fields, IReadOnlyDictionary<string, StoreHealth> stores) {
        foreach (var (key, label) in new[] { ("kurrentdb", "KurrentDB"), ("postgres", "Postgres") }) {
            if (!stores.TryGetValue(key, out var store)) continue;

            var value = store.Up ? "up" : store.Status.ToLowerInvariant();
            if (!store.Up && !string.IsNullOrWhiteSpace(store.Description)) value += $": {store.Description}";

            fields.Add(new(label, value));
        }
    }

    string PostgresField() => LocalStackProbe.FindLivePostgres(stack.Root) is null ? "down" : "up";

    /// <summary>Never the server's instance lock: a probe holds it for a moment, and a concurrent start could be
    /// bringing the server up in that moment.</summary>
    string[] Leftovers() =>
        LocalStackProbe.FindLivePostgres(stack.Root) is { } live
            ? [$"Though nothing runs the server, {live}. kcap local start stops what an earlier server left behind and refuses what it did not start; {Logs} says which."]
            : [];

    static string[] StoppedAdvice(LocalServerUnit unit, string startDoes) =>
        File.Exists(unit.ServerBinary)
            ? [$"kcap local start {startDoes}."]
            : [$"The server's files are missing ({unit.ServerBinary}). Run kcap setup --local to restore them."];

    static string Label(LocalServerUnit unit) => OperatingSystem.IsLinux() ? unit.SystemdName : unit.Label;

    static string ExitCode(int? code) => code is { } c ? $" with code {c}" : "";

    static IEnumerable<string> Roots(UnitOwner readings) =>
        new[] { readings.Loaded, readings.OnDisk }.OfType<OwnerReading.Root>().Select(r => r.Path).Distinct(StringComparer.Ordinal);

    static IEnumerable<string> Unreadable(UnitOwner readings) =>
        new[] { readings.Loaded, readings.OnDisk }.OfType<OwnerReading.Unreadable>().Select(r => r.Reason);

    static LocalStatusReport Down(string headline, List<KeyValuePair<string, string>> fields, List<string> notes, params IEnumerable<string>[] more) =>
        new(false, headline, fields, [.. more.SelectMany(m => m), .. notes]);
}
