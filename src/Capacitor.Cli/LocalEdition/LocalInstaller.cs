using System.Security.Cryptography;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Installs the local edition all-or-nothing, under the lifecycle lease. Until the <c>committing</c> journal write is
/// attempted any failure or cancellation unloads the unit and deletes what this attempt created; from that write on
/// nothing is deleted and the journal is left for the next run to finish.
///
/// <para>Lock order: the lifecycle lease, then the unit lock, then the config lock inside the commit boundary.</para>
/// </summary>
sealed class LocalInstaller(
    LocalRoot root, ConfigRoot config, UserHome home, ILocalServerService service, LocalBundleClient bundles,
    HttpClient local, ECDsa manifestKey, LocalPlatform platform, TimeProvider time,
    Func<LocalInstaller, IEnumerable<ILifecycleRecovery>> recoveries) {
    static readonly TimeSpan LeaseWait    = TimeSpan.FromSeconds(10);
    static readonly TimeSpan UnitLockWait = TimeSpan.FromMinutes(1);

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    IProgress<LocalInstallEvent> _progress = new SyncProgress<LocalInstallEvent>(_ => { });

    /// <summary>The version to install. Defaults to the override or this CLI's own.</summary>
    public string Version { get; init; } = LocalBundleClient.RequestedVersion();

    /// <summary>Replaces the platform's socket-path limit.</summary>
    public int? SocketLimit { get; init; }

    /// <summary>Reports to whichever <see cref="InstallAsync"/> is running, so a recovery built before the call can
    /// report into it.</summary>
    public IProgress<LocalInstallEvent> Progress => new SyncProgress<LocalInstallEvent>(Report);

    void Report(LocalInstallEvent e) => _progress.Report(e);

    LocalPreflight Checks => new(root, config, platform, service, SocketLimit);

    /// <summary>No lock, no network, no writes.</summary>
    public LocalPlan Preflight() => Checks.Check(Version);

    public async Task<LocalInstallEvent.Committed> InstallAsync(LocalInstallAnswers answers, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        _progress = progress;

        var hint      = Checks.Check(Version).Hint;
        var lifecycle = new LocalLifecycle(root, recoveries(this), time);

        try {
            return await lifecycle.RunAsync(LeaseWait, (lease, token) => UnderLeaseAsync(lease, answers, token), ct);
        } catch (LocalLifecycleException ex) when (ex.InnerException is IOException) {
            throw new LocalInstallFailure(LocalInstallFailureCode.LifecycleBusy, ex.Message);
        } catch (LocalLifecycleException ex) {
            throw new LocalInstallFailure(hint == PlanHint.Recover ? LocalInstallFailureCode.RecoveryFailed : LocalInstallFailureCode.StartFailed, ex.Message);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw new LocalInstallFailure(LocalInstallFailureCode.Cancelled, "Setup was cancelled.");
        } catch (Exception ex) when (ex is not (LocalInstallFailure or NotImplementedException)) {
            throw new LocalInstallFailure(LocalInstallFailureCode.StartFailed, ex.Message);
        }
    }

    async Task<LocalInstallEvent.Committed> UnderLeaseAsync(LifecycleLease lease, LocalInstallAnswers answers, CancellationToken ct) {
        LocalPlan            plan;
        LocalUnitLock?       unitLock = null;
        SetupDetails         details;
        LifecycleJournalEntry entry;

        try {
            // Held from the empty-slot check of the unit through Install, so two roots cannot both pass it.
            unitLock = await LocalUnitLock.AcquireAsync(home, UnitLockWait, TimeProvider.System, ct);
            plan     = Checks.Recheck(Version);

            _progress.Report(new LocalInstallEvent.PreflightPassed(plan.Hint, plan.Version, root.Directory));

            if (plan.Hint == PlanHint.Reuse) throw new NotImplementedException("Reusing an existing local installation.");

            if (!answers.SingleUser || !answers.Confirmed)
                throw new LocalInstallFailure(LocalInstallFailureCode.AnswerRequired,
                    "A local installation needs both answers: that it serves only you (--single-user), and that you confirm it (--yes).");

            var port = LocalPortPicker.Choose(Registry(lease));
            _progress.Report(new LocalInstallEvent.PortChosen(port));

            details = new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), port, plan.Version,
                LocalServerUnit.ForInstallation(root, port).BaseUrl, Path.GetFullPath(config.Directory), ActiveProfile());

            entry = lease.Begin(LocalSetupJournal.Operation, LocalSetupJournal.Pending, ServerStart.Fenced, details.InstallationId,
                LocalSetupJournal.Build(details));
        } catch {
            unitLock?.Dispose();
            throw;
        }

        try {
            await StageAsync(details, ct);

            entry = lease.Advance(entry, LocalSetupJournal.Unit, ServerStart.Fenced);

            var unit = LocalServerUnit.ForInstallation(root, details.Port);

            service.Install(unit);
            unitLock.Dispose();
            unitLock = null;

            _progress.Report(new LocalInstallEvent.Starting(unit.Label));

            await LocalServerReadiness.WaitAsync(local, unit, LocalServerReadiness.StartBudget, time, ct);
            _progress.Report(new LocalInstallEvent.Ready(unit.BaseUrl));

            // A stranger answering on the port is never committed: only this pending installation carries the header.
            if (await LocalServerProbe.AuthConfigAsync(local, unit.BaseUrl, ct) != AuthConfigAnswer.Pending)
                throw new LocalInstallFailure(LocalInstallFailureCode.NotOurServer,
                    $"Something other than the new local server answers on {unit.BaseUrl}. Nothing was kept.");

            FaultPoints.Hit("setup:before-committing");
            ct.ThrowIfCancellationRequested();

            _progress.Report(new LocalInstallEvent.Committing());
        } catch (Exception ex) {
            unitLock?.Dispose();

            var failure = Classify(ex, ct);

            await RollBackAsync(lease, entry, failure);

            throw failure;
        }

        LocalInstallEvent.Committed committed;

        // From the attempt on, a throw is ambiguous: the rename may have landed even if the sync did not.
        try {
            entry = lease.Advance(entry, LocalSetupJournal.Committing, ServerStart.Fenced);
            FaultPoints.Hit("setup:after-committing");

            committed = await LocalSetupCommit.RollForwardAsync(lease, entry, details, service, home, local, time);
        } catch (LocalInstallFailure) {
            throw;
        } catch (Exception ex) {
            throw new LocalInstallFailure(LocalInstallFailureCode.CommitFailed,
                $"Setup could not finish its commit ({ex.Message}). Nothing was removed; run kcap setup --local again to finish it.");
        }

        _progress.ReportFinished(committed);

        return committed;
    }

    /// <summary>Downloads, verifies and unpacks the bundle into <c>bin/&lt;version&gt;</c>, then points <c>bin/current</c>
    /// at it. Every step is durable before the journal can say <c>unit</c>.</summary>
    async Task StageAsync(SetupDetails details, CancellationToken ct) {
        var bin     = Path.Combine(root.Directory, "bin");
        var staging = Path.Combine(bin, $".staging-{details.InstallationId}");
        var rid     = platform.Rid;

        DurableDirectory.Ensure(staging, root.Directory, OwnerOnly);

        byte[] bytes;
        string signature;

        try {
            (bytes, signature) = await bundles.FetchManifestAsync(details.Version, rid, ct);
        } catch (ArgumentException ex) {
            throw new LocalInstallFailure(LocalInstallFailureCode.InvalidArguments, ex.Message);
        }

        var manifest           = LocalBundleManifest.Verify(bytes, signature, manifestKey, details.Version, rid);

        _progress.Report(new LocalInstallEvent.Downloading($"{bundles.BaseUrl}/{rid}/{manifest.Archive}", manifest.Size));

        var archive = Path.Combine(staging, "archive.tar.gz");
        await bundles.DownloadAsync(manifest, archive,
            new SyncProgress<long>(received => _progress.Report(new LocalInstallEvent.DownloadProgress(received, manifest.Size))), ct);

        _progress.Report(new LocalInstallEvent.Verified(manifest.Sha256));

        var tree = Path.Combine(staging, "tree");
        await LocalBundleUnpacker.UnpackAsync(archive, tree, ct);

        var versionDir = Path.Combine(bin, details.Version);
        Directory.Move(tree, versionDir);
        DurableDirectory.Sync(bin);

        ScriptUpdater.SwitchLink(Path.Combine(bin, "current"), details.Version);
        DurableDirectory.Sync(bin);

        Directory.Delete(staging, recursive: true);
        DurableDirectory.Sync(bin);

        _progress.Report(new LocalInstallEvent.Unpacked(versionDir));
    }

    async Task RollBackAsync(LifecycleLease lease, LifecycleJournalEntry entry, LocalInstallFailure failure) {
        try {
            await LocalSetupRollback.RunAsync(lease, entry, service, home, _progress, failure.Message);
        } catch (Exception ex) {
            var reason = ex is LocalInstallFailure ? ex.Message : $"Setup could not be undone ({ex.Message}).";

            throw new LocalInstallFailure(LocalInstallFailureCode.RecoveryFailed,
                $"{failure.Message} {reason} The setup journal is kept; the next kcap setup --local retries the cleanup.");
        }
    }

    static LocalInstallFailure Classify(Exception ex, CancellationToken ct) =>
        ex switch {
            LocalInstallFailure failure                                    => failure,
            LocalBundleException bundle                                    => new(bundle.Code, bundle.Message),
            OperationCanceledException when ct.IsCancellationRequested     => new(LocalInstallFailureCode.Cancelled, "Setup was cancelled."),
            _                                                              => new(LocalInstallFailureCode.StartFailed, ex.Message),
        };

    static InstallRegistry Registry(LifecycleLease lease) =>
        lease.ReadRegistry() switch {
            StateRead<InstallRegistry>.Present(var registry) => registry,
            StateRead<InstallRegistry>.Unreadable(var path, var reason) =>
                throw new LocalInstallFailure(LocalInstallFailureCode.RegistryUnreadable,
                    $"The installation registry {path} cannot be read ({reason}). Fix or remove it, then run setup again."),
            _ => InstallRegistry.Empty,
        };

    string ActiveProfile() {
        var path = AppConfig.GetConfigPath(config);

        if (!ConfigMutator.TryLoadPure(path, out var cfg))
            throw new LocalInstallFailure(LocalInstallFailureCode.ConfigUnreadable,
                $"The configuration file {path} cannot be read. Fix or remove it, then run setup again.");

        return cfg.ActiveName;
    }
}
