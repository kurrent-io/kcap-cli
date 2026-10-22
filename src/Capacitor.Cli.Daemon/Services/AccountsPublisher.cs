using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The host's accounts snapshot, kept current by polling <c>accounts.json</c>, which the CLI and the
/// desktop app write from other processes. A new snapshot is published only for a new revision or
/// host id, so a recording-state change reaches readers with the next registry revision.
/// </summary>
internal sealed partial class AccountsPublisher(
        DaemonConfig config, UserHome home, TimeProvider time, ILogger<AccountsPublisher> logger)
    : BackgroundService {
    static readonly TimeSpan PollInterval       = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ServerSendInterval = TimeSpan.FromSeconds(30);

    readonly SemaphoreSlim        _gate     = new(1, 1);
    readonly DaemonStatusNotifier _notifier = new();

    AccountsSnapshotDto?       _current;
    PeriodicTimer?             _timer;
    string?                    _hostId;
    (long Length, long Ticks)? _stat;
    bool                       _settled;
    bool                       _loadWarned;
    bool                       _hostWarned;

    readonly Lock _sendLock = new();
    ITimer?       _trailingSend;
    long?         _lastSentAt;
    bool          _trailingDue;
    bool          _sendStopped;

    public AccountsSnapshotDto? Current => Volatile.Read(ref _current);

    /// <summary>Raised under the rebuild gate so handlers see revisions in order. A handler must not
    /// throw, block, or wait on <see cref="RebuildAsync"/>; one that throws is logged and the rest still
    /// run. Every new snapshot is announced, the one start builds included; a poll that finds the
    /// revision unchanged announces nothing.</summary>
    public event Action<AccountsSnapshotDto>? Changed;

    // Seam: tests substitute a failing read.
    internal Func<AccountStore, AccountRegistry> LoadRegistry { get; set; } = store => store.Load();

    /// <summary>Takes a new snapshot to the server, at most once per <see cref="ServerSendInterval"/>: a change
    /// inside the interval waits for its end and sends whatever snapshot is current then.</summary>
    public Func<AccountsSnapshotDto, Task>? SendToServer { get; set; }

    public long Version => _notifier.Version;

    public Task WaitBeyondAsync(long seen, CancellationToken ct) => _notifier.WaitBeyondAsync(seen, ct);

    /// <summary>Reads the registry now, whether or not its file looks changed.</summary>
    public Task RebuildAsync(CancellationToken ct) => PollAsync(force: true, ct);

    public override async Task StartAsync(CancellationToken cancellationToken) {
        if (config.Accounts is not null) {
            await PollAsync(force: true, cancellationToken);
            _timer = new PeriodicTimer(PollInterval, time);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (_timer is not { } timer) return;

        using (timer) {
            try {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await PollAsync(force: false, stoppingToken);
            } catch (OperationCanceledException) { /* shutdown */ }
        }
    }

    async Task PollAsync(bool force, CancellationToken ct) {
        if (config.Accounts is not { } store) return;

        await _gate.WaitAsync(ct);
        try {
            if ((_hostId ??= TryHostId(store)) is not { } hostId) return;

            var stat      = Stat(store.RegistryPath);
            var unchanged = stat == _stat;
            if (unchanged && _settled && !force) return;

            // Two same-length writes inside one filesystem timestamp tick stat alike, so a stat is
            // trusted only once a later tick has read the file under it successfully.
            _settled = unchanged && !force;
            if (!unchanged) _loadWarned = false;
            _stat = stat;

            if (!Publish(store, hostId)) _settled = false;
        } finally {
            _gate.Release();
        }
    }

    /// <summary>False when the registry could not be read.</summary>
    bool Publish(AccountStore store, string hostId) {
        AccountRegistry registry;
        try {
            registry = LoadRegistry(store);
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            if (!_loadWarned) LogRegistryUnreadable(logger, store.RegistryPath, ex);
            _loadWarned = true;
            return false;
        }

        if (Current is { } current && current.Revision == registry.Revision && current.HostId == hostId) return true;

        var next = AccountSnapshotBuilder.Build(registry, hostId, config.DefaultDirectories, home);
        Volatile.Write(ref _current, next);
        _notifier.Pulse();
        Announce(next);
        QueueServerSend(next);
        return true;
    }

    void Announce(AccountsSnapshotDto snapshot) {
        if (Changed is not { } changed) return;

        foreach (var handler in changed.GetInvocationList()) {
            try {
                ((Action<AccountsSnapshotDto>) handler)(snapshot);
            } catch (Exception ex) {
                LogHandlerFailed(logger, ex);
            }
        }
    }

    // Runs under the rebuild gate, so it never waits on the send.
    void QueueServerSend(AccountsSnapshotDto snapshot) {
        lock (_sendLock) {
            if (_sendStopped || SendToServer is null || _trailingDue) return;

            var wait = _lastSentAt is { } last ? ServerSendInterval - time.GetElapsedTime(last) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero) {
                _trailingDue = true;
                if (_trailingSend is { } timer) timer.Change(wait, Timeout.InfiniteTimeSpan);
                else _trailingSend = time.CreateTimer(_ => SendTrailing(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastSentAt = time.GetTimestamp();
        }

        _ = SendAsync(snapshot);
    }

    void SendTrailing() {
        lock (_sendLock) {
            if (_sendStopped) return;
            _trailingDue = false;
            _lastSentAt  = time.GetTimestamp();
        }

        if (Current is { } snapshot) _ = SendAsync(snapshot);
    }

    async Task SendAsync(AccountsSnapshotDto snapshot) {
        if (SendToServer is not { } send) return;

        try {
            await send(snapshot);
        } catch (Exception ex) {
            LogServerSendFailed(logger, ex);
        }
    }

    public override void Dispose() {
        lock (_sendLock) {
            _sendStopped = true;
            _trailingSend?.Dispose();
        }

        base.Dispose();
    }

    // Any failure here leaves the publisher waiting for a host id rather than stopping the daemon.
    string? TryHostId(AccountStore store) {
        try {
            return store.HostId();
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            if (!_hostWarned) LogHostIdUnavailable(logger, ex);
            _hostWarned = true;
            return null;
        }
    }

    static (long Length, long Ticks)? Stat(string path) {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} could not be read; keeping the last accounts snapshot")]
    static partial void LogRegistryUnreadable(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host id could not be read; accounts are not published until it can")]
    static partial void LogHostIdUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An accounts snapshot subscriber threw; the others were still notified")]
    static partial void LogHandlerFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending the accounts snapshot to the server failed; the next connect carries it")]
    static partial void LogServerSendFailed(ILogger logger, Exception ex);
}
