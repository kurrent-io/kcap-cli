using Velopack;
using Velopack.Sources;

namespace Capacitor.App.Services.Update;

/// IAppUpdater over Velopack's UpdateManager. Prereleases are offered when the user opted in, or,
/// with no choice made, when the installed version is itself a prerelease.
public sealed class VelopackAppUpdater : IAppUpdater {
    readonly UpdateManager _manager;
    readonly Func<bool?> _betaChoice;
    UpdateInfo? _lastCheck;

    public VelopackAppUpdater(Func<string, string?> getEnv, Func<bool?> betaChoice) {
        _betaChoice = betaChoice;
        var source = new PrereleaseFilteringSource(new SimpleWebSource(UpdateFeed.Resolve(getEnv)), () => AllowsPrerelease);
        _manager = new UpdateManager(source);
    }

    public bool IsAvailable => _manager.IsInstalled;
    public string? InstalledVersion => _manager.CurrentVersion?.ToString();
    public bool AllowsPrerelease => _betaChoice() ?? _manager.CurrentVersion?.IsPrerelease == true;

    public UpdateCandidate? PendingRestart =>
        _manager.UpdatePendingRestart is { } asset ? new UpdateCandidate(asset.Version.ToString(), asset.Version.IsPrerelease) : null;

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        _lastCheck = info;
        if (info is null) return null;
        var target = info.TargetFullRelease.Version;
        return new UpdateCandidate(target.ToString(), target.IsPrerelease);
    }

    public Task DownloadAsync(UpdateCandidate candidate, CancellationToken ct) {
        var info = _lastCheck;
        if (info is null || info.TargetFullRelease.Version.ToString() != candidate.Version)
            throw new InvalidOperationException($"No check offered {candidate.Version}; check before downloading.");
        return _manager.DownloadUpdatesAsync(info, null, ct);
    }

    public void ApplyOnExit(UpdateCandidate candidate) =>
        _manager.WaitExitThenApplyUpdates(AssetFor(candidate), silent: true, restart: true);

    public void ApplyNow(UpdateCandidate candidate) =>
        _manager.ApplyUpdatesAndRestart(AssetFor(candidate));

    VelopackAsset AssetFor(UpdateCandidate candidate) {
        if (_manager.UpdatePendingRestart is { } pending && pending.Version.ToString() == candidate.Version) return pending;
        if (_lastCheck is { } info && info.TargetFullRelease.Version.ToString() == candidate.Version) return info.TargetFullRelease;
        throw new InvalidOperationException($"No downloaded package for {candidate.Version}.");
    }
}
