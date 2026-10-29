using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// The subset of Capacitor.Cli.Commands.ServiceStatusJson the app reads; snake_case on the wire.
/// `LoadedSpawnType` is launchd's word for the loaded job, null when the label is not loaded or the
/// CLI predates the field; read it through SpawnTypes. The `Txn*` details of a leftover marker are
/// null from a CLI that predates them.
public sealed record ServiceSnapshot(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath, string? InstallBinaryPath,
    int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive, string? LoadedSpawnType = null,
    string? TxnOperation = null, string? TxnPhase = null, DateTimeOffset? TxnMarkedAt = null,
    string? Supervisor = null, bool? ServiceUpgradeAvailable = null, bool? WindowsServiceInstallable = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServiceSnapshot))]
public partial class KcapCliJsonContext : JsonSerializerContext;

/// Domain classification of ServiceSnapshot.State's wire string, with an explicit Unknown arm —
/// positive-evidence-only: an unrecognized/future/typo'd value must never silently read
/// as NotInstalled and enter a positive-mutation path.
public enum ServiceState { Running, Installed, NotInstalled, Unknown }

public static class ServiceStateClassifier {
    public static ServiceState Parse(string state) => state switch {
        "running"       => ServiceState.Running,
        "installed"     => ServiceState.Installed,
        "not_installed" => ServiceState.NotInstalled,
        _               => ServiceState.Unknown,
    };
}

public enum ImportScopeChoice { Everything, Org, Repo }

/// <param name="Repos">With <see cref="ImportScopeChoice.Repo"/>, every repository to import; overrides
/// <paramref name="OrgOrRepo"/>.</param>
/// <param name="Since">Only sessions started on or after this day.</param>
/// <param name="Private">Mark every imported session as visible to its owner only.</param>
/// <param name="SkipTitle">Leave titling to the server rather than this machine's own agent.</param>
public sealed record ImportRequest(
    ImportScopeChoice Scope, string? OrgOrRepo, IReadOnlyList<string> VendorFlags,
    IReadOnlyList<string>? Repos = null, DateOnly? Since = null, bool Private = false, bool SkipTitle = false);

/// Typed facade over every CLI call the app shells out to — every daemon mutation goes through
/// the CLI. Consumed by the lifecycle controller and faked in tests behind
/// IProcessRunner.
public interface IKcapCli {
    string? CliPath { get; }

    /// Runs `--version --no-update-check`; null on a non-zero exit, a timeout, or a malformed
    /// version string (CliResolver.ParseVersion).
    Task<string?> VersionAsync(CancellationToken ct);

    /// Runs <c>daemon service status --name &lt;name&gt; --json</c>; null = unknown (non-zero exit, a
    /// timeout, or a parse failure) — never a fabricated snapshot.
    Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct);

    /// The same read for another daemon name's service.
    Task<ServiceSnapshot?> ServiceStatusAsync(string daemonName, CancellationToken ct);

    Task<bool> SupportsServiceRetireAsync(CancellationToken ct);

    Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct);

    /// <c>daemon service refresh --name &lt;name&gt; --force</c>: reloads this daemon's launchd job to Standard, ending
    /// whatever it hosts. One <c>refresh_outcome=&lt;token&gt;</c> line on stderr; exit 0 only for reloaded/current.
    Task<ProcessResult> ServiceReloadAsync(CancellationToken ct);

    Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct, string? retireServiceId = null);

    /// `daemon service install --system --verify`: installs or updates the machine's Windows service behind the
    /// UAC prompt, then hands this daemon to it. Bounded by the time a person may take to answer the prompt.
    Task<ProcessResult> ServiceInstallWindowsServiceAsync(CancellationToken ct);

    /// <c>daemon start -d --name &lt;name&gt;</c>, bounded + ProcessOnly-kill, stamped with a boot-attempt id
    /// for the daemon's own boot-carrier correlation — the lane always mints a fresh one per action.
    Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct);

    /// `plugin install` (+ vendorFlag, when non-null, then <paramref name="options"/>); null = the
    /// flagless Claude default.
    Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct, IReadOnlyList<string>? options = null);

    Task<ProcessResult> PluginRemoveAsync(string? vendorFlag, CancellationToken ct);

    /// <c>accounts add|remove|rename|rewire … --json</c>, which prints one AccountActionResultDto line.
    /// No server overlay: the account verbs touch nothing on the server.
    Task<ProcessResult> AccountsAsync(MutationVerb verb, AccountArgs args, CancellationToken ct);

    /// `import` with scope/vendor flags, streamed live via onLine — unbounded internal timeout
    /// (imports are long; ct cancellation is the only bound).
    Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct);

    /// `setup --local --json --single-user --yes` and the embedding choice's flag, streamed live via onLine. No
    /// internal timeout: <paramref name="ct"/> bounds only the work before the spawn; once running, the child stops
    /// only through <paramref name="interrupt"/>, which lets the CLI roll back before a tree kill.
    Task<StreamingResult> SetupLocalAsync(
        LocalEmbeddingChoice embeddings, Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct);

    /// `local upgrade --json --no-update-check`, streamed live via onLine, with the same cancellation
    /// contract as <see cref="SetupLocalAsync"/>.
    Task<StreamingResult> LocalUpgradeAsync(Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct);

    /// `uninstall --yes --force --keep-path-links` (and `--purge-local`), streamed live via onLine. No internal
    /// timeout; <paramref name="ct"/> bounds only the work before the spawn, so a running uninstall is never killed.
    Task<StreamingResult> UninstallAsync(bool purgeLocal, Action<StreamedLine> onLine, CancellationToken ct);

    /// `local start`, bounded above the CLI's own readiness budget.
    Task<ProcessResult> LocalStartAsync(CancellationToken ct);

    /// `import --discover --json` for these vendors; null when the run failed or printed no report.
    Task<ImportDiscoveryReport?> ImportDiscoverAsync(IReadOnlyList<string> vendorFlags, CancellationToken ct);
}

public sealed class KcapCli : IKcapCli {
    // Covers forward/rollback budgets, lock wait, crash recovery and the manual-owner kill wait.
    static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(60);

    /// The UAC prompt waits on a person; past this the install is reported unconfirmed, not failed.
    static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(10);
    /// The forced refresh may wait 10 s for the service transaction lock before its own 85 s deadline starts.
    static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(105);
    // Discovery reads every vendor's history off disk; a large Claude projects tree takes seconds.
    static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(120);
    // A rename adds, on top of the install's own budgets: the old id's 10s lock wait, its 5s label
    // query, the 5s target probe, the old daemon's fence (two connects and exchanges of 3s each to
    // acquire, then a 3s commit or abort), the old daemon's 10s end-agents wait and the retire's own 20s
    // forward budget — about 128s in all.
    static readonly TimeSpan RenameTimeout = TimeSpan.FromSeconds(150);
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    // Same tier as VersionTimeout — also a read-only query — so a hung `launchctl print` can
    // never block the per-mutation gate forever once the lifecycle controller polls this.
    static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    // Above MutationTimeout's 60s — bounds the wrapper without killing a still-forking detach (ProcessOnly).
    static readonly TimeSpan DetachedStartTimeout = TimeSpan.FromSeconds(75);
    // Sits above the CLI's ten-minute readiness budget so the CLI reports its own failure first.
    static readonly TimeSpan LocalStartTimeout = TimeSpan.FromMinutes(11);
    // A cancelled setup still finishes recovery and commit, which may run back to back: two 10 min readiness waits,
    // a 210 s service stop, two 60 s unit-lock waits, two 10 s lift polls and a 10 s lease wait, 26 min in all.
    static readonly TimeSpan SetupInterruptGrace = TimeSpan.FromMinutes(30);
    // Only the download stops for an interrupt. A resumed upgrade and this one back to back: two 10 min readiness
    // waits, three 105 s service stops, five 60 s unit-lock waits and a 10 s lease wait, about 31 min in all.
    static readonly TimeSpan UpgradeInterruptGrace = TimeSpan.FromMinutes(35);

    // Overlay variable names every app-initiated spawn carries — the lane and KcapCliTests reference these.
    public const string SpawnNoTelemetryVar   = "KCAP_APP_SPAWN_NO_TELEMETRY";
    public const string ConsentSeedDefaultVar = "KCAP_CONSENT_SEED_DEFAULT";
    public const string ExpectServerUrlVar    = "KCAP_EXPECT_SERVER_URL";
    public const string BootAttemptVar        = "KCAP_BOOT_ATTEMPT";

    readonly IProcessRunner _runner;
    readonly string _daemonName;
    readonly string _profileName;
    // Bound once at construction (one KcapCli per owned action); null is fine until a mutation runs.
    readonly string? _canonicalServer;
    // Lazy, not a static ctor value: the terminal PATH is only known once the async probe has run,
    // and the probe itself caches — so resolving it per spawn is cheap and always current.
    readonly Func<CancellationToken, Task<string?>>? _terminalPathAsync;

    public KcapCli(
            IProcessRunner runner, string? cliPath, string daemonName, string profileName,
            Func<CancellationToken, Task<string?>>? terminalPathAsync, string? canonicalServer = null) {
        _runner            = runner;
        CliPath            = cliPath;
        _daemonName        = daemonName;
        _profileName       = profileName;
        _terminalPathAsync = terminalPathAsync;
        _canonicalServer   = canonicalServer;
    }

    public string? CliPath { get; }

    /// A no-CLI (broken KCAP_APP_CLI_PATH — CliResolver.ResolvePath returned null) result the app
    /// treats identically to "unknown"/"nothing resolved": these two read-only queries return
    /// null instead of throwing, the same degradation an unparseable or timed-out response
    /// already models.
    public async Task<string?> VersionAsync(CancellationToken ct) {
        if (CliPath is not { } cliPath) return null;

        var result = await Run(cliPath, ["--version", "--no-update-check"],
                new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: VersionTimeout), ct)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && !result.TimedOut ? CliResolver.ParseVersion(result.Stdout) : null;
    }

    public Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) => ServiceStatusAsync(_daemonName, ct);

    public async Task<ServiceSnapshot?> ServiceStatusAsync(string daemonName, CancellationToken ct) {
        if (CliPath is not { } cliPath) return null;

        var result = await Run(cliPath,
                ["daemon", "service", "status", "--name", daemonName, "--json"],
                new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: StatusTimeout), ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut) return null;

        try {
            return JsonSerializer.Deserialize(result.Stdout, KcapCliJsonContext.Default.ServiceSnapshot);
        } catch (JsonException) {
            return null;
        }
    }

    // Older CLIs ignore unknown install flags, so their ordinary version floor is insufficient.
    public async Task<bool> SupportsServiceRetireAsync(CancellationToken ct) {
        if (CliPath is not { } cliPath) return false;
        var help = await Run(cliPath, ["daemon", "--help", "--no-update-check"],
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: VersionTimeout), ct).ConfigureAwait(false);
        return !help.TimedOut && help.ExitCode == 0 &&
            (help.Stdout.Contains("--retire", StringComparison.Ordinal) || help.Stderr.Contains("--retire", StringComparison.Ordinal));
    }

    public async Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);
        return await Run(cliPath, ["daemon", "service", "start", "--name", _daemonName, "--verify"],
            new RunOptions(EnvOverlay: await EnvWithTerminalPathAsync(env, ct).ConfigureAwait(false), Timeout: MutationTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult> ServiceReloadAsync(CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);
        return await Run(cliPath, ["daemon", "service", "refresh", "--name", _daemonName, "--force"],
            new RunOptions(EnvOverlay: await EnvWithTerminalPathAsync(env, ct).ConfigureAwait(false), Timeout: ReloadTimeout), ct).ConfigureAwait(false);
    }

    // The profile flag and environment overlay must agree with the identity the lane verifies.
    public async Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct, string? retireServiceId = null) {
        if (retireServiceId is not null && !replace)
            throw new ArgumentException("Retiring a service requires replacement.", nameof(retireServiceId));
        var mutation = MutationEnv(); // throws before any spawn if the instance carries no server
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        if (retireServiceId is not null && !await SupportsServiceRetireAsync(ct).ConfigureAwait(false))
            return new ProcessResult(VerifyExitCodes.RetireRefused, "", "retire_reason=cli_unsupported", false);

        List<string> args = ["daemon", "service", "install", "--name", _daemonName, "--profile", _profileName, "--verify"];
        if (replace) args.Add("--replace");
        if (retireServiceId is not null) args.AddRange(["--retire", retireServiceId]);

        var env = await EnvWithTerminalPathAsync(mutation, ct).ConfigureAwait(false);
        return await Run(cliPath, args.ToArray(), new RunOptions(EnvOverlay: env,
            Timeout: retireServiceId is null ? MutationTimeout : RenameTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult> ServiceInstallWindowsServiceAsync(CancellationToken ct) {
        var mutation = MutationEnv();
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        string[] args = ["daemon", "service", "install", "--system", "--name", _daemonName, "--profile", _profileName, "--verify"];
        var env = await EnvWithTerminalPathAsync(mutation, ct).ConfigureAwait(false);
        return await Run(cliPath, args, new RunOptions(EnvOverlay: env, Timeout: ElevationTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        env[BootAttemptVar] = bootAttemptId;
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        return await Run(cliPath, ["daemon", "start", "-d", "--name", _daemonName],
            new RunOptions(
                EnvOverlay: await EnvWithTerminalPathAsync(env, ct).ConfigureAwait(false), Timeout: DetachedStartTimeout,
                CancelMode: CancelMode.AbandonWait, TimeoutKill: TimeoutKillScope.ProcessOnly),
            ct).ConfigureAwait(false);
    }

    // Neither this nor ImportAsync overlays MutationEnv — non-daemon shelling keeps lenient
    // classification; the vendor flag itself is the caller's exclusive-flag choice.
    public async Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct, IReadOnlyList<string>? options = null) {
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        List<string> args = ["plugin", "install"];
        if (vendorFlag is not null) args.Add(vendorFlag);
        if (options is not null) args.AddRange(options);

        return await Run(cliPath, args.ToArray(),
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: MutationTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult> PluginRemoveAsync(string? vendorFlag, CancellationToken ct) {
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);
        string[] args = vendorFlag is null ? ["plugin", "remove"] : ["plugin", "remove", vendorFlag];
        return await Run(cliPath, args,
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: MutationTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult> AccountsAsync(MutationVerb verb, AccountArgs args, CancellationToken ct) {
        string[] argv = verb switch {
            MutationVerb.AccountAdd    => ["accounts", "add", Required(args.Vendor), Required(args.Directory), "--json"],
            MutationVerb.AccountRemove => ["accounts", "remove", Required(args.AccountId), "--json"],
            MutationVerb.AccountRename => ["accounts", "rename", Required(args.AccountId), Required(args.Label), "--json"],
            MutationVerb.AccountRewire => args.AccountId is { } id ? ["accounts", "rewire", id, "--json"] : ["accounts", "rewire", "--json"],
            MutationVerb.AccountRefresh => ["accounts", "refresh", Required(args.AccountId), "--json"],
            _                          => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not an account verb"),
        };
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        return await Run(cliPath, argv,
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: MutationTimeout), ct).ConfigureAwait(false);

        static string Required(string? value) =>
            value ?? throw new ArgumentException("The account verb is missing one of its arguments.", nameof(args));
    }

    public async Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct) {
        if (CliPath is not { } cliPath)
            return new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]);

        List<string> args = ["import"];
        args.Add(request.Scope switch {
            ImportScopeChoice.Everything => "--all",
            ImportScopeChoice.Org        => "--org",
            ImportScopeChoice.Repo       => "--repo",
            _                            => throw new ArgumentOutOfRangeException(nameof(request)),
        });
        if (request is { Scope: ImportScopeChoice.Repo, Repos: { Count: > 0 } repos }) {
            args.Add(repos[0]);
            foreach (var repo in repos.Skip(1)) {
                args.Add("--repo");
                args.Add(repo);
            }
        } else if (request.Scope != ImportScopeChoice.Everything) {
            args.Add(request.OrgOrRepo!);
        }
        if (request.Since is { } since) {
            args.Add("--since");
            args.Add(since.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (request.Private) args.Add("--private");
        if (request.SkipTitle) args.Add("--skip-title");
        args.Add("--yes");
        args.AddRange(request.VendorFlags);

        return await _runner.RunStreamingAsync(cliPath, args.ToArray(),
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false)), onLine, ct).ConfigureAwait(false);
    }

    public async Task<StreamingResult> SetupLocalAsync(
            LocalEmbeddingChoice embeddings, Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct) {
        if (CliPath is not { } cliPath)
            return new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]);

        return await _runner.RunStreamingAsync(cliPath, ["setup", "--local", "--json", "--single-user", "--yes", .. embeddings.Args],
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Interrupt: interrupt, InterruptGrace: SetupInterruptGrace),
            onLine, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<StreamingResult> LocalUpgradeAsync(Action<StreamedLine> onLine, CancellationToken interrupt, CancellationToken ct) {
        if (CliPath is not { } cliPath)
            return new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]);

        return await _runner.RunStreamingAsync(cliPath, ["local", "upgrade", "--json", "--no-update-check"],
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Interrupt: interrupt, InterruptGrace: UpgradeInterruptGrace),
            onLine, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<StreamingResult> UninstallAsync(bool purgeLocal, Action<StreamedLine> onLine, CancellationToken ct) {
        if (CliPath is not { } cliPath)
            return new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]);

        string[] args = purgeLocal
            ? ["uninstall", "--yes", "--force", "--keep-path-links", "--purge-local"]
            : ["uninstall", "--yes", "--force", "--keep-path-links"];

        return await _runner.RunStreamingAsync(cliPath, args,
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false)), onLine, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<ProcessResult> LocalStartAsync(CancellationToken ct) {
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        return await _runner.RunAsync(cliPath, ["local", "start"],
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: LocalStartTimeout), ct).ConfigureAwait(false);
    }

    public async Task<ImportDiscoveryReport?> ImportDiscoverAsync(IReadOnlyList<string> vendorFlags, CancellationToken ct) {
        if (CliPath is not { } cliPath) return null;

        string[] args = ["import", "--discover", "--json", .. vendorFlags];
        var result = await Run(cliPath, args,
            new RunOptions(EnvOverlay: await SpawnEnvAsync(ct).ConfigureAwait(false), Timeout: DiscoverTimeout), ct).ConfigureAwait(false);

        return result is { ExitCode: 0, TimedOut: false } ? ImportDiscoveryReport.Parse(result.Stdout) : null;
    }

    // Deterministic exit 127 ("command not found") rather than throwing on a null CliPath — a
    // broken KCAP_APP_CLI_PATH must degrade the same way every other no-CLI case does, not crash
    // whichever lifecycle mutation happens to call it.
    static Task<ProcessResult> NoCliResult() => Task.FromResult(new ProcessResult(127, "", "kcap CLI not found", false));

    Task<ProcessResult> Run(string cliPath, string[] args, RunOptions options, CancellationToken ct) =>
        _runner.RunAsync(cliPath, args, options, ct);

    // Every child carries the pinned profile and the app-spawn telemetry-suppression marker.
    Dictionary<string, string> Env() => new() {
        [ProfileOverrides.ProfileVar]      = _profileName,
        [SpawnNoTelemetryVar] = "1",
    };

    // The login shell can resolve kcap to a shim whose interpreter sits outside a GUI app's PATH, so
    // every spawn carries the terminal's.
    Task<Dictionary<string, string>> SpawnEnvAsync(CancellationToken ct) => EnvWithTerminalPathAsync(Env(), ct);

    // Null canonicalServer here is a construction bug (mutations are action-scoped) — fail loudly, don't spawn.
    Dictionary<string, string> MutationEnv() {
        if (_canonicalServer is not { } server)
            throw new InvalidOperationException("KcapCli: mutation spawn requires a non-null canonicalServer.");

        var env = Env();
        env[ConsentSeedDefaultVar] = "prompt";
        env[ExpectServerUrlVar]    = server;
        return env;
    }

    // An unknown probe result (null) leaves PATH out rather than overlaying a value that is not the
    // user's; install then bakes whatever the app itself inherited into the unit. Resolved against
    // the spawn's own token, never a detached one.
    async Task<Dictionary<string, string>> EnvWithTerminalPathAsync(Dictionary<string, string> env, CancellationToken ct) {
        var terminalPath = _terminalPathAsync is null ? null : await _terminalPathAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(terminalPath)) env["PATH"] = terminalPath; // an empty answer would unset PATH for the child

        return env;
    }
}
