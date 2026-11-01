using System.Collections.Concurrent;
using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Runs the server's headless prompts on this machine's harnesses. At most <see cref="MaxConcurrent"/> run at
/// once, so background work cannot starve the hosted agents this daemon runs. A request still queued at its deadline
/// never starts; a running one is killed there. A SignalR client-result handler gets no cancellation token, so the
/// server cancels by request id.</summary>
sealed partial class HeadlessPromptRunner : IDisposable {
    public const int MaxConcurrent = 2;

    readonly FrozenDictionary<string, IHeadlessHarness>                 _harnesses;
    readonly TimeProvider                                              _time;
    readonly ILogger<HeadlessPromptRunner>                             _logger;
    readonly CancellationToken                                         _shutdown;
    readonly SemaphoreSlim                                             _slots    = new(MaxConcurrent, MaxConcurrent);
    readonly ConcurrentDictionary<string, CancellationTokenSource>     _requests = new(StringComparer.Ordinal);

    public HeadlessPromptRunner(
            IEnumerable<IHeadlessHarness> harnesses,
            TimeProvider                  time,
            ILogger<HeadlessPromptRunner> logger,
            CancellationToken             shutdown
        ) {
        _harnesses = harnesses.ToFrozenDictionary(h => h.Harness, StringComparer.OrdinalIgnoreCase);
        _time      = time;
        _logger    = logger;
        _shutdown  = shutdown;
    }

    /// <summary>The harnesses whose CLI resolves now, in ordinal order.</summary>
    public string[] AdvertisedHarnesses() =>
        [.. _harnesses.Values.Where(h => h.IsAvailable()).Select(h => h.Harness).Order(StringComparer.Ordinal)];

    public async Task<HeadlessPromptResponse> RunAsync(RunHeadlessPromptArgs args) {
        if (!_harnesses.TryGetValue(args.Harness, out var harness)) return HeadlessPromptResponses.NotStarted(HeadlessPromptResponses.NoHarness);
        var left = args.Deadline - _time.GetUtcNow();
        if (left <= TimeSpan.Zero) return HeadlessPromptResponses.NotStarted(HeadlessPromptResponses.DeadlinePassed);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        if (!_requests.TryAdd(args.RequestId, cancel)) return HeadlessPromptResponses.NotStarted(null, "request id already in flight");

        try {
            // One clock read for the check and the timer: a second read can land past the deadline and throw.
            using (var deadline = new CancellationTokenSource(left, _time))
            using (var queued = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, deadline.Token)) {
                try {
                    await _slots.WaitAsync(queued.Token);
                } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
                    return HeadlessPromptResponses.NotStarted(HeadlessPromptResponses.DeadlinePassed);
                } catch (OperationCanceledException) {
                    return HeadlessPromptResponses.NotStarted(null, "cancelled");
                }
            }

            try {
                return await RunInSlotAsync(harness, args, cancel);
            } finally {
                _slots.Release();
            }
        } finally {
            _requests.TryRemove(new(args.RequestId, cancel));
        }
    }

    public void Dispose() => _slots.Dispose();

    public Task CancelAsync(CancelHeadlessPromptArgs args) {
        if (_requests.TryGetValue(args.RequestId, out var cancel)) {
            try {
                cancel.Cancel();
            } catch (ObjectDisposedException) {
                // The run ended between the lookup and the cancel.
            }
        }

        return Task.CompletedTask;
    }

    async Task<HeadlessPromptResponse> RunInSlotAsync(IHeadlessHarness harness, RunHeadlessPromptArgs args, CancellationTokenSource cancel) {
        var left = args.Deadline - _time.GetUtcNow();
        if (left <= TimeSpan.Zero) return HeadlessPromptResponses.NotStarted(HeadlessPromptResponses.DeadlinePassed);
        if (cancel.IsCancellationRequested) return HeadlessPromptResponses.NotStarted(null, "cancelled");
        if (!harness.IsAvailable()) return HeadlessPromptResponses.NotStarted(HeadlessPromptResponses.BinaryMissing);

        try {
            var outcome = await harness.RunAsync(args, left, cancel.Token);
            if (outcome.Failure is { } failure) LogRunFailed(_logger, args.RequestId, harness.Harness, failure.ToString(), outcome.Subtype);

            return HeadlessPromptResponses.From(outcome, structured: !string.IsNullOrWhiteSpace(args.JsonSchema));
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
            return HeadlessPromptResponses.Failed(HeadlessPromptResponses.ProcessFailure, "cancelled");
        } catch (Exception e) {
            LogRunThrew(_logger, e, args.RequestId, harness.Harness);

            return HeadlessPromptResponses.Failed(HeadlessPromptResponses.ProcessFailure, e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Headless prompt {RequestId} on {Harness} failed: {Failure} ({Subtype})")]
    static partial void LogRunFailed(ILogger logger, string requestId, string harness, string failure, string? subtype);

    [LoggerMessage(Level = LogLevel.Error, Message = "Headless prompt {RequestId} on {Harness} threw")]
    static partial void LogRunThrew(ILogger logger, Exception exception, string requestId, string harness);
}
