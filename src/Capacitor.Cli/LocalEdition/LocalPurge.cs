using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Retires an installation, then deletes what belonged to it: the registry moves it to the retired list durably
/// before anything is deleted, so no reader sees its origin as neither active nor retired, and a purge that cannot
/// publish that deletes nothing. Every step after <see cref="Begin"/> is idempotent and runs again from the journal
/// after a crash.
/// </summary>
static class LocalPurge {
    /// <summary>Under the lease, with the unit stopped and nothing alive.</summary>
    /// <summary>Refuses, writing nothing, an installation whose journal this build could not act on again.</summary>
    public static LifecycleJournalEntry Begin(LifecycleLease lease, LocalInstallation installation, string configDir, string watcherDir) {
        var details = LocalPurgeJournal.Build(installation, Path.GetFullPath(configDir), Path.GetFullPath(watcherDir));
        var draft   = new LifecycleJournalEntry(LocalPurgeJournal.Operation, LocalPurgeJournal.Retiring, ServerStart.Deny, installation.Id,
            DateTimeOffset.MinValue, details);

        if (!LocalPurgeJournal.TryRead(draft, out _, out var error))
            throw new LocalLifecycleException(
                $"The installation registry {lease.Root.RegistryPath} names an installation this kcap cannot purge ({error}). Its service is stopped; nothing was removed.");

        return lease.Begin(LocalPurgeJournal.Operation, LocalPurgeJournal.Retiring, ServerStart.Deny, installation.Id, details);
    }

    /// <summary>From either phase to a cleared journal. A failure leaves the journal, so the next lifecycle command
    /// carries on from it.</summary>
    public static async Task FinishAsync(LifecycleLease lease, LifecycleJournalEntry entry, ILocalServerService service, UserHome home,
        CancellationToken ct) {
        var root = lease.Root;

        if (!LocalPurgeJournal.TryRead(entry, out var details, out var error))
            throw new LocalLifecycleException(
                $"The purge journal {root.JournalPath} cannot be acted on ({error}). Nothing was changed; fix or remove it, then run kcap local uninstall --purge again.");

        var unit = LocalServerUnit.ForInstallation(root, details.Port);

        // Again on a recovery: the journal denies the server a start, but a unit loaded since would still run.
        await LocalStackStop.RunAsync(root, unit, service, home, removeUnit: false, ct);

        lease.PublishRegistry(Retire(lease, details));
        FaultPoints.Hit("purge:after-retire");

        if (entry.Phase == LocalPurgeJournal.Retiring) {
            lease.Advance(entry, LocalPurgeJournal.Retired, ServerStart.Deny);
            FaultPoints.Hit("purge:after-retired");
        }

        await LocalStackStop.RunAsync(root, unit, service, home, removeUnit: true, ct);
        LocalStackStop.Delete(root, "bin", "run", "data", "logs", "models");
        FaultPoints.Hit("purge:after-data");

        var config = new ConfigRoot(details.ConfigDir);

        // Profiles first: a hook that still resolves the installation spools what it is refused, and once no profile
        // names it, nothing new resolves it.
        await ClearProfilesAsync(config, details, home);
        FaultPoints.Hit("purge:after-profile");

        ForgetServer(config, details.Port);
        FaultPoints.Hit("purge:after-caches");

        lease.Complete();
    }

    /// <summary>Republished even when it already names the installation as retired: a visible registry may not be a
    /// synced one.</summary>
    static InstallRegistry Retire(LifecycleLease lease, PurgeDetails details) {
        switch (lease.ReadRegistry()) {
            case StateRead<InstallRegistry>.Present(var registry) when registry.Active?.Id == details.InstallationId:
                return registry.RetireActive();
            case StateRead<InstallRegistry>.Present(var registry) when registry.Active is null && registry.Retired.Any(r => r.Id == details.InstallationId):
                return registry;
            case StateRead<InstallRegistry>.Unreadable(var path, var reason):
                throw new LocalLifecycleException($"The installation registry {path} cannot be read ({reason}). Nothing was removed; fix it, then run kcap local uninstall --purge again.");
            default:
                throw new LocalLifecycleException(
                    $"The installation registry {lease.Root.RegistryPath} does not name the installation {details.InstallationId} being purged. Nothing was removed.");
        }
    }

    /// <summary>The recordings spooled for it and the caches keyed by its identity. While an installation exists the
    /// machine records to nothing else, so the spools hold only its recordings.</summary>
    static void ForgetServer(ConfigRoot config, int port) {
        // Deleted outright rather than after an existence check, which reads an unreadable directory as absent.
        foreach (var dir in new[] { HookSpool.DirectoryName, TranscriptSpool.DirectoryName }) {
            try {
                Directory.Delete(config.Path(dir), recursive: true);
            } catch (DirectoryNotFoundException) {
                // Nothing was spooled.
            }
        }

        // Each store keys its entries by the URL as it was spelled, and the installation answers to three spellings.
        foreach (var serverUrl in new[] { $"http://localhost:{port}", $"http://127.0.0.1:{port}", $"http://[::1]:{port}" }) {
            ServerVersionStore.Forget(serverUrl, config);
            PlanEntitlementStore.Forget(serverUrl, config);
            SessionStartFlowsCatalogCache.Default(config).Forget(serverUrl);
        }
    }

    /// <summary>Clears the URL and the provider stamp of every profile naming the installation, in any loopback
    /// spelling. A profile under another config root keeps its URL and is refused as retired.</summary>
    static async Task ClearProfilesAsync(ConfigRoot config, PurgeDetails details, UserHome home) {
        if (!File.Exists(AppConfig.GetConfigPath(config))) return;

        var retired   = new RetiredInstallation(details.InstallationId, details.Port, details.ServerUrl);
        var endpoints = new LocalEndpoints(new StateRead<InstallRegistry>.Present(new(null, [retired])));

        bool Names(string? url) => endpoints.Classify(url) is LocalEndpointClass.Retired;

        try {
            await ConfigMutator.MutateStrictAsync(config, cfg => cfg with {
                Profiles = cfg.Profiles.ToDictionary(
                    p => p.Key,
                    p => Names(p.Value.ServerUrl) || Names(p.Value.AuthProvider?.ServerUrl)
                        ? p.Value with { ServerUrl = null, AuthProvider = null }
                        : p.Value),
            }, durable: true, home);
        } catch (ConfigUnreadableException ex) {
            throw new LocalLifecycleException(
                $"The configuration file {ex.ConfigPath} cannot be read, so its profile still points at {details.ServerUrl}. The purge journal is kept; fix the file, then run kcap local uninstall --purge again.", ex);
        }
    }
}
