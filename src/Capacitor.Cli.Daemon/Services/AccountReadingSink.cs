using System.Threading.Channels;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Records what hosted agents observe about their account's limits. <see cref="Post"/> runs on
/// read loops that must never wait, so it only queues; one consumer writes, and when it falls behind the
/// oldest observations go first.</summary>
internal sealed partial class AccountReadingSink : IDisposable {
    const int Capacity = 256;

    static readonly TimeSpan WarnInterval = TimeSpan.FromMinutes(10);

    readonly AccountStore?                _store;
    readonly TimeProvider                 _time;
    readonly ILogger<AccountReadingSink>  _logger;
    readonly Channel<(ResolvedAccount Account, ReadingObservation Observation)> _queue =
        Channel.CreateBounded<(ResolvedAccount, ReadingObservation)>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    long? _lastWarnedAt;

    // Seam: tells a test each observation has been written (true) or skipped.
    internal Action<ReadingObservation, bool>? Processed { get; set; }

    public AccountReadingSink(AccountStore? store, TimeProvider time, ILogger<AccountReadingSink> logger) {
        _store  = store;
        _time   = time;
        _logger = logger;
        if (store is not null) _ = Task.Run(() => ConsumeAsync(store));
    }

    /// <summary>An unregistered default (no account id) records nothing.</summary>
    public void Post(ResolvedAccount? account, ReadingObservation observation) {
        if (_store is null || account?.AccountId is null) return;

        _queue.Writer.TryWrite((account, observation));
    }

    async Task ConsumeAsync(AccountStore store) {
        await foreach (var (account, observation) in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) {
            bool recorded;
            try {
                recorded = store.Readings.TryRecord(account.AccountId!, (f, entry) =>
                    observation.ProducedAt < entry.GenerationStartedAt ? null : f with { Reading = ReadingMerge.Apply(f.Reading, observation) });
            } catch (Exception ex) {
                recorded = false;
                LogRecordFailed(_logger, account.AccountId!, ex);
            }

            if (!recorded) WarnSkipped(account.AccountId!);
            Processed?.Invoke(observation, recorded);
        }
    }

    // An observation from before the current login or a contended lock skips by design; the warning only says it is happening.
    void WarnSkipped(string accountId) {
        if (_lastWarnedAt is { } last && _time.GetElapsedTime(last) < WarnInterval) return;

        _lastWarnedAt = _time.GetTimestamp();
        LogSkipped(_logger, accountId);
    }

    public void Dispose() => _queue.Writer.TryComplete();

    [LoggerMessage(Level = LogLevel.Debug, Message = "A reading for account {AccountId} was not recorded (contended, removed or re-logged); further skips are quiet for a while")]
    static partial void LogSkipped(ILogger logger, string accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a reading for account {AccountId} failed")]
    static partial void LogRecordFailed(ILogger logger, string accountId, Exception ex);
}
