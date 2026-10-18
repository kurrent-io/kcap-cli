using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// A setup past its point of no return, finished in the server contract's order: registry, profile, allow, proof that
/// the fence lifted, then the journal cleared. Every step is idempotent, so an interrupted run is finished by running
/// it again from the phase the journal shows. Nothing here deletes or is cancelled; a failure throws
/// <see cref="LocalInstallFailure"/> with <see cref="LocalInstallFailureCode.CommitFailed"/> and leaves the journal.
/// </summary>
static class LocalSetupCommit {
    static readonly TimeSpan LiftBudget  = TimeSpan.FromSeconds(10);
    static readonly TimeSpan LiftPollGap = TimeSpan.FromMilliseconds(250);

    /// <summary>From a <c>committing</c> or <c>allowed</c> entry to a cleared journal.</summary>
    public static async Task<LocalInstallEvent.Committed> RollForwardAsync(LifecycleLease lease, LifecycleJournalEntry entry, SetupDetails details,
        ILocalServerService service, HttpClient local, TimeProvider time, IProgress<LocalInstallEvent> progress) {
        if (entry.Phase == LocalSetupJournal.Committing) await PublishAsync(lease, entry, details);
        else if (entry.Phase != LocalSetupJournal.Allowed) throw new ArgumentException($"A {entry.Phase} setup has nothing to roll forward.", nameof(entry));

        return await ConfirmAsync(lease, details, service, local, time, progress);
    }

    /// <summary>Registry, profile, then <c>allowed</c>: the server may start unfenced only once both are durable.</summary>
    public static async Task<LifecycleJournalEntry> PublishAsync(LifecycleLease lease, LifecycleJournalEntry entry, SetupDetails details) {
        // Republished even when it already names the installation: a visible registry may not be a synced one.
        lease.PublishRegistry(Registry(lease).WithActive(new(details.InstallationId, details.Port, details.ServerUrl, details.Version)));
        FaultPoints.Hit("setup:after-registry");

        await PublishProfileAsync(details);
        FaultPoints.Hit("setup:after-profile");

        var allowed = lease.Advance(entry, LocalSetupJournal.Allowed, ServerStart.Allow);
        FaultPoints.Hit("setup:after-allowed");

        return allowed;
    }

    /// <summary>Proves the lift, restarting the unit once when polling proves nothing, then clears the journal.</summary>
    public static async Task<LocalInstallEvent.Committed> ConfirmAsync(LifecycleLease lease, SetupDetails details, ILocalServerService service,
        HttpClient local, TimeProvider time, IProgress<LocalInstallEvent> progress) {
        if (!await LiftedAsync(local, details.ServerUrl, time)) {
            // The restart is not itself proof: only an unfenced answer counts.
            var unit = LocalServerUnit.ForInstallation(lease.Root, details.Port);

            await service.StopAsync(unit, CancellationToken.None);
            service.Start(unit);
            await LocalServerReadiness.WaitAsync(local, unit, LocalServerReadiness.StartBudget, time, CancellationToken.None);

            if (!await LiftedAsync(local, details.ServerUrl, time))
                throw Failed($"The local server at {details.ServerUrl} did not confirm it left setup mode, even after a restart. The setup journal is kept; run kcap setup --local again to finish it.");
        }

        FaultPoints.Hit("setup:before-complete");
        lease.Complete();

        var committed = new LocalInstallEvent.Committed(details.ServerUrl, details.InstallationId, details.Port, details.Version);
        progress.Report(committed);

        return committed;
    }

    static InstallRegistry Registry(LifecycleLease lease) =>
        lease.ReadRegistry() switch {
            StateRead<InstallRegistry>.Present(var registry) => registry,
            StateRead<InstallRegistry>.Unreadable(var path, var reason) =>
                throw Failed($"The installation registry {path} cannot be read ({reason}). Fix or remove it, then run kcap setup --local again."),
            _ => InstallRegistry.Empty,
        };

    static async Task PublishProfileAsync(SetupDetails details) {
        var url = details.ServerUrl;

        var request = new CommitRequest(
            [new AuthIdentity(details.Profile, url)], AuthProvider.None, details.Profile, url,
            ConfigMutation: c => CommitBoundary.PointProfileAtServer(c, details.Profile, url),
            PublishTokens: null, Precondition: new CommitPrecondition.OnlyServer(url), Durable: true);

        var result = await CommitBoundary.CommitAsync(new ConfigRoot(details.ConfigDir), request, null, NoAuthProgress.Instance, CancellationToken.None);

        if (result is AuthResult.Failed(var message, _))
            throw Failed($"The profile \"{details.Profile}\" in {details.ConfigDir} could not be pointed at {url}: {message}. The setup journal is kept; fix the configuration, then run kcap setup --local again.");
    }

    static async Task<bool> LiftedAsync(HttpClient local, string url, TimeProvider time) {
        var deadline = time.GetUtcNow() + LiftBudget;

        while (true) {
            if (await LocalServerProbe.AuthConfigAsync(local, url, CancellationToken.None) == AuthConfigAnswer.Lifted) return true;
            if (time.GetUtcNow() >= deadline) return false;

            await Task.Delay(LiftPollGap, time);
        }
    }

    static LocalInstallFailure Failed(string message) => new(LocalInstallFailureCode.CommitFailed, message);
}
