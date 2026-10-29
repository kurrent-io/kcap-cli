using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

/// <summary>A probe that answers when the test says so. <see cref="Started"/> completes once the refresh
/// has taken its probe sequence number and called the probe.</summary>
sealed class FakeAccountProbe : IAccountProbe {
    readonly TaskCompletionSource<ProbeResult> _result  = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource              _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    /// <summary>Runs after the answer is known and before the probe returns it.</summary>
    public Action? BeforeReturn { get; init; }

    public void Complete(ProbeResult result) => _result.TrySetResult(result);

    public static FakeAccountProbe Returning(ProbeResult result) {
        var probe = new FakeAccountProbe();
        probe.Complete(result);
        return probe;
    }

    public async Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) {
        _started.TrySetResult();
        var result = await _result.Task.WaitAsync(ct);
        BeforeReturn?.Invoke();
        return result;
    }
}
