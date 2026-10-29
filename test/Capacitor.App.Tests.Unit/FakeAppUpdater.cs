using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

/// Scripted IAppUpdater: every call is counted, the next check's answer is a settable field, and
/// downloads can be held on a TaskCompletionSource so a test can observe the in-flight state.
sealed class FakeAppUpdater : IAppUpdater {
    public bool IsAvailable { get; set; } = true;
    public string? InstalledVersion { get; set; } = "0.12.0-beta.2";
    public UpdateCandidate? PendingRestart { get; set; }
    public bool? BetaChoice;
    public bool AllowsPrerelease => BetaChoice ?? InstalledVersion?.Contains('-') == true;

    public UpdateCandidate? NextCheck;
    public Exception? CheckFailure;
    public Exception? DownloadFailure;
    public int CheckCalls;
    public int DownloadCalls;
    public TaskCompletionSource? HoldDownload;
    public TaskCompletionSource? HoldCheck;
    public Action<int>? ReportProgress;
    public readonly List<UpdateCandidate> ApplyOnExitCalls = [];
    public readonly List<UpdateCandidate> ApplyNowCalls = [];
    public Exception? ApplyNowFailure;

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        CheckCalls++;
        if (HoldCheck is { } hold) await hold.Task.WaitAsync(ct);
        if (CheckFailure is { } failure) throw failure;
        return NextCheck;
    }

    public async Task DownloadAsync(UpdateCandidate candidate, CancellationToken ct, Action<int>? progress = null) {
        ReportProgress = progress;
        DownloadCalls++;
        if (HoldDownload is { } hold) await hold.Task.WaitAsync(ct);
        if (DownloadFailure is { } failure) throw failure;
        PendingRestart = candidate;
    }

    public void ApplyOnExit(UpdateCandidate candidate) => ApplyOnExitCalls.Add(candidate);

    public void ApplyNow(UpdateCandidate candidate) {
        if (ApplyNowFailure is { } failure) throw failure;
        ApplyNowCalls.Add(candidate);
    }
}
