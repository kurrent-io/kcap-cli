namespace Capacitor.App.Services.Update;

/// The app's view of the updater. Unavailable outside a packed bundle (a `dotnet run` build), in
/// which case nothing else here may be called.
public interface IAppUpdater {
    bool IsAvailable { get; }
    string? InstalledVersion { get; }

    /// Whether prerelease builds are offered, read at each feed read.
    bool AllowsPrerelease { get; }

    /// A package already downloaded and waiting for a relaunch, or null.
    UpdateCandidate? PendingRestart { get; }

    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate candidate, CancellationToken ct, Action<int>? progress = null);

    /// Hands the swap to the updater, which waits for this process to exit; call it last in the
    /// shutdown sequence — its wait is bounded to 60 s.
    void ApplyOnExit(UpdateCandidate candidate);

    /// Applies immediately: exits this process and relaunches the new version.
    void ApplyNow(UpdateCandidate candidate);
}
