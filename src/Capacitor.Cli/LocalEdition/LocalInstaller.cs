using System.Security.Cryptography;
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
    static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(10);

    IProgress<LocalInstallEvent> _progress = new SyncProgress<LocalInstallEvent>(_ => { });

    LocalInstallEvent.Committed? _rolledForward;

    /// <summary>The version to install. Defaults to the override or this CLI's own.</summary>
    public string Version { get; init; } = LocalBundleClient.RequestedVersion();

    public string RootDirectory => root.Directory;

    /// <summary>Replaces the platform's socket-path limit.</summary>
    public int? SocketLimit { get; init; }

    /// <summary>How long it waits for another root's hold on the per-user unit lock.</summary>
    public TimeSpan UnitLockWait { get; init; } = LocalUnitLock.Wait;

    /// <summary>The other copies of kcap the preflight holds to the client floor; none are looked for when null.</summary>
    public ILocalClientCopies? ClientCopies { get; init; }

    /// <summary>Required when the answers carry an embedding answer.</summary>
    public OllamaClient? Ollama { get; init; }

    /// <summary>What setup recorded about semantic search, or null when it never asked. Read without the lease.</summary>
    public LocalEmbeddingSetting? EmbeddingSetting => LocalEmbeddingFile.Read(root);

    /// <summary>Reports to whichever <see cref="InstallAsync"/> is running, so a recovery built before the call can
    /// report into it.</summary>
    public IProgress<LocalInstallEvent> Progress => new SyncProgress<LocalInstallEvent>(Report);

    void Report(LocalInstallEvent e) => _progress.Report(e);

    /// <summary>Records the commit a recovery's roll-forward produced, for <see cref="AfterRollForwardAsync"/>.</summary>
    public void RolledForward(LocalInstallEvent.Committed committed) => _rolledForward = committed;

    /// <summary>The plan is still decided from the state the roll-forward left, but a cancellation, which the
    /// roll-forward ignored, ends the setup with that commit rather than as cancelled.</summary>
    async Task<LocalInstallEvent.Committed> AfterRollForwardAsync(LifecycleLease lease, LocalInstallAnswers answers, LocalInstallEvent.Committed done,
        CancellationToken ct) {
        if (!ct.IsCancellationRequested) {
            try {
                return await UnderLeaseAsync(lease, answers, ct);
            } catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or LocalInstallFailure { Code: LocalInstallFailureCode.Cancelled }) {
                // The installation is committed; only the reuse pass over it was cut short.
            }
        }

        _progress.TryReport(done);

        return done;
    }

    LocalPreflight Checks => new(root, config, platform, service, SocketLimit, ClientCopies);

    /// <summary>No lock, no network, no writes.</summary>
    public LocalPlan Preflight() => Checks.Check(Version);

    public async Task<LocalInstallEvent.Committed> InstallAsync(LocalInstallAnswers answers, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        _progress      = progress;
        _rolledForward = null;

        var hint      = Checks.Check(Version).Hint;
        var lifecycle = new LocalLifecycle(root, recoveries(this), time);
        var recovered = false;

        try {
            return await lifecycle.RunAsync(LeaseWait, (lease, token) => {
                recovered = true;

                return _rolledForward is { } done ? AfterRollForwardAsync(lease, answers, done, token) : UnderLeaseAsync(lease, answers, token);
            }, ct);
        } catch (LocalLifecycleException ex) when (ex.InnerException is IOException) {
            throw new LocalInstallFailure(LocalInstallFailureCode.LifecycleBusy, ex.Message);
        } catch (LocalLifecycleException ex) {
            var code = hint == PlanHint.Recover && !recovered ? LocalInstallFailureCode.RecoveryFailed : LocalInstallFailureCode.StartFailed;
            throw new LocalInstallFailure(code, ex.Message);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw new LocalInstallFailure(LocalInstallFailureCode.Cancelled, "Setup was cancelled.");
        } catch (Exception ex) when (ex is not LocalInstallFailure) {
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

            if (plan.Hint == PlanHint.Reuse) {
                var existing = plan.Existing!;

                return await ReuseAsync(existing, unitLock!, answers, ct);
            }

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
            // Before the unit is written, so the first start already has the model; a rollback deletes it with data/.
            var previous = LocalEmbeddingFile.Read(root);
            var setting  = await DecideEmbeddingsAsync(answers, new LocalModelSetDownloader(root, bundles), ct);
            var recorded = setting is not null && LocalEmbeddingStep.Record(root, setting, _progress);

            entry = lease.Advance(entry, LocalSetupJournal.Unit, ServerStart.Fenced);

            var unit = LocalServerUnit.ForInstallation(root, details.Port);

            service.Install(unit);
            unitLock.Dispose();
            unitLock = null;

            if (recorded) LocalEmbeddingStep.Applied(previous, setting!, _progress);

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

        _progress.TryReport(committed);

        return committed;
    }

    /// <summary>Takes over the unit lock. No port is taken and no journal is written: the profile is published only
    /// once the installation's own server answers unfenced. No bundle is downloaded unless an uninstall removed the
    /// binaries; then the installed version is staged again over the data it kept. A changed embedding setting, or a
    /// built-in model set put back, rewrites the unit, which restarts the server.</summary>
    async Task<LocalInstallEvent.Committed> ReuseAsync(LocalInstallation existing, LocalUnitLock unitLock, LocalInstallAnswers answers,
        CancellationToken ct) {
        var unit    = LocalServerUnit.ForInstallation(root, existing.Port);
        var details = new SetupDetails(existing.Id, existing.Port, existing.Version, existing.ServerUrl, Path.GetFullPath(config.Directory), ActiveProfile());

        LocalInstallFailure NotOurs() => new(LocalInstallFailureCode.NotOurServer,
            $"The {service.Describe} {unit.Label} ({service.UnitFile(unit)}) is not provably this installation's, so it was left alone and the profile was not changed.");

        try {
            if (!File.Exists(unit.ServerBinary)) {
                // Decided before bin/ is touched: a refusal must leave the installation as it found it.
                var owner = service.Owner(unit);
                if (owner is not { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent } && LocalRootIdentity.Decide(root, owner) != OwnerDecision.Ours)
                    throw NotOurs();

                await RestageAsync(details, ct);
            }

            unit = await ReuseEmbeddingsAsync(unit, answers, NotOurs, ct);
        } finally {
            unitLock.Dispose();
        }

        _progress.Report(new LocalInstallEvent.Starting(unit.Label));

        await LocalServerReadiness.WaitAsync(local, unit, LocalServerReadiness.StartBudget, time, ct);
        _progress.Report(new LocalInstallEvent.Ready(unit.BaseUrl));

        if (await LocalServerProbe.AuthConfigAsync(local, unit.BaseUrl, ct) != AuthConfigAnswer.Lifted)
            throw new LocalInstallFailure(LocalInstallFailureCode.NotOurServer,
                $"Something other than this installation's local server answers on {unit.BaseUrl}, so the profile was not changed.");

        ct.ThrowIfCancellationRequested();

        try {
            await LocalSetupCommit.PublishProfileAsync(details, "Fix the configuration, then run kcap setup --local again.");
        } catch (Exception ex) when (ex is not LocalInstallFailure) {
            throw new LocalInstallFailure(LocalInstallFailureCode.CommitFailed, $"The profile could not be published ({ex.Message}).");
        }

        _progress.Report(new LocalInstallEvent.Reused(existing.ServerUrl, existing.Version));

        var committed = new LocalInstallEvent.Committed(existing.ServerUrl, existing.Id, existing.Port, existing.Version);
        _progress.TryReport(committed);

        return committed;
    }

    /// <summary>Brings the recorded embedding setting and the unit together. The setting is marked pending before the
    /// unit changes and cleared after, so a setup that stops anywhere in between rewrites the unit next time; a refused
    /// rewrite restores what was recorded before. Files fetched for the recorded built-in model set are a repair: the
    /// server loads the model only once, so the files land only once the record is marked pending, and the unit is
    /// rewritten to restart it.</summary>
    /// <returns>The unit as installed.</returns>
    async Task<LocalServerUnit> ReuseEmbeddingsAsync(LocalServerUnit unit, LocalInstallAnswers answers, Func<LocalInstallFailure> notOurs,
        CancellationToken ct) {
        // Before the decision, which may pull a model: a refused setup changes nothing, Ollama included.
        var owner = service.Owner(unit);
        if (owner is not { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent } && LocalRootIdentity.Decide(root, owner) != OwnerDecision.Ours)
            throw notOurs();

        var previous  = LocalEmbeddingFile.Read(root);
        var repairing = false;
        var models    = new LocalModelSetDownloader(root, bundles) {
            Publishing = m => {
                if (previous is not { Provider: LocalEmbeddingProvider.Builtin } || previous.ModelSetId != m.ModelSet) return true;

                return repairing = LocalEmbeddingStep.Record(root, previous with { Pending = true }, _progress);
            },
        };
        var target  = await DecideEmbeddingsAsync(answers, models, ct) ?? (previous is { Pending: true } || repairing ? previous! with { Pending = false } : null);
        var rewrite = target is not null && (previous is { Pending: true } || repairing || !unit.Serves(target));

        if (rewrite && !LocalEmbeddingStep.Record(root, target! with { Pending = true }, _progress)) (rewrite, target) = (false, null);

        if (rewrite) unit = unit with { Embedding = target };

        try {
            LocalOwnUnit.Ensure(root, unit, service, notOurs, startPresent: true, replace: rewrite);
        } catch (LocalInstallFailure refused) when (rewrite) {
            try {
                LocalEmbeddingStep.Restore(root, previous);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                throw new LocalInstallFailure(refused.Code,
                    $"{refused.Message} {LocalEmbeddingFile.PathFor(root)} could not be restored ({ex.Message}).");
            }

            throw;
        }

        if (target is null) return unit;

        if (rewrite && !repairing) LocalEmbeddingStep.Applied(previous, target, _progress);

        LocalEmbeddingStep.Record(root, target, _progress);

        return unit;
    }

    /// <returns>The embedding setting to record, or null when the recorded one stands.</returns>
    async Task<LocalEmbeddingSetting?> DecideEmbeddingsAsync(LocalInstallAnswers answers, LocalModelSetDownloader models, CancellationToken ct) =>
        answers.Embeddings is { } answer
            ? await LocalEmbeddingStep.DecideAsync(root, answer,
                Ollama ?? throw new InvalidOperationException("An embedding answer needs an Ollama client."),
                models, answers.BuiltinModel, _progress, ct)
            : null;

    /// <summary>The built-in model set <paramref name="version"/>'s signed manifest names. No lease, no writes.</summary>
    /// <returns>Null when the manifest names none, or cannot be fetched or verified: built-in is then not offered.</returns>
    /// <exception cref="LocalInstallFailure"><paramref name="version"/> is not a release version, as the install refuses it.</exception>
    public async Task<LocalEmbeddingModelManifest?> FetchEmbeddingManifestAsync(string version, CancellationToken ct) {
        try {
            return await new LocalEmbeddingOffer(bundles, manifestKey, platform.Rid).LookupAsync(version, ct);
        } catch (ArgumentException ex) {
            throw new LocalInstallFailure(LocalInstallFailureCode.InvalidArguments, ex.Message);
        }
    }

    /// <summary>Stages the bundle, then points <c>bin/current</c> at it. Every step is durable before the journal can
    /// say <c>unit</c>.</summary>
    async Task StageAsync(SetupDetails details, CancellationToken ct) {
        await new LocalBundleStager(root, bundles, manifestKey, platform).StageAsync(details.Version, details.InstallationId, _progress, ct);
        LocalBin.Switch(root, details.Version);
    }

    /// <summary>Whatever an interrupted earlier attempt left in <c>bin/</c> is replaced, which is safe only while
    /// nothing of the stack runs: the registry still names the installation, so a crash here is retried by the next
    /// setup rather than journaled.</summary>
    async Task RestageAsync(SetupDetails details, CancellationToken ct) {
        if (LocalStackProbe.FindLive(root) is { } live)
            throw new LocalInstallFailure(LocalInstallFailureCode.StartFailed,
                $"The local installation's binaries are missing, but {live} under {root.Directory}. Nothing was changed.");

        LocalStackStop.Delete(root, "bin");

        try {
            await StageAsync(details, ct);
        } catch (LocalBundleException bundle) {
            throw new LocalInstallFailure(bundle.Code, bundle.Message);
        }
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
            _ when ct.IsCancellationRequested => new(LocalInstallFailureCode.Cancelled, "Setup was cancelled."),
            LocalInstallFailure failure       => failure,
            LocalBundleException bundle       => new(bundle.Code, bundle.Message),
            _                                 => new(LocalInstallFailureCode.StartFailed, ex.Message),
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
