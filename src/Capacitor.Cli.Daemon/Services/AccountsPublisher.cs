using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The host's accounts snapshot, kept current by polling <c>accounts.json</c> and the reading files, which
/// the CLI, the desktop app and agents write from other processes. A snapshot is published only when it
/// differs by value from the last one, which a reading change can do under the same revision.
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
    (string AccountId, long Length, long Ticks)[] _readingsStat = [];
    (string AccountId, string Path, long Length, long Ticks)[]? _wiringStat;
    (string AccountId, string Path)[] _wiringPaths = [];
    bool                       _settled;
    bool                       _loadWarned;
    bool                       _hostWarned;
    bool                       _pollWarned;
    Dictionary<string, DateTimeOffset?>? _blockedSeen;

    readonly Lock _sendLock = new();
    ITimer?       _trailingSend;
    long?         _lastSentAt;
    bool          _trailingDue;
    bool          _sendStopped;

    public AccountsSnapshotDto? Current => Volatile.Read(ref _current);

    /// <summary>Raised under the rebuild gate so handlers see revisions in order. A handler must not
    /// throw, block, or wait on <see cref="RebuildAsync"/>; one that throws is logged and the rest still
    /// run. Every new snapshot is announced, the one start builds included; a rebuild equal by value to the
    /// current snapshot announces nothing.</summary>
    public event Action<AccountsSnapshotDto>? Changed;

    // Seam: tests substitute a failing read.
    internal Func<AccountStore, AccountRegistry> LoadRegistry { get; set; } = store => store.Load();

    /// <summary>Takes a new snapshot to the server, at most once per <see cref="ServerSendInterval"/>: a change
    /// inside the interval waits for its end and sends whatever snapshot is current then.</summary>
    public Func<AccountsSnapshotDto, Task>? SendToServer { get; set; }

    /// <summary>Asks for a refresh of an account whose block advanced since the last snapshot. An advance a
    /// refresh wrote is skipped, whichever daemon ran it: it records its origin in the same replace as the
    /// block, so a refresh never triggers another. Called under the rebuild gate; it must not block.</summary>
    public Action<string>? RequestRefresh { get; set; }

    /// <summary>Asks for a prompt refresh of a Codex account whose <c>hooks.json</c> or <c>config.toml</c> changed: its
    /// trust answer is bound to those files, so the account reads Needs approval until a probe asks again. Called
    /// under the rebuild gate; it must not block.</summary>
    public Action<string>? RequestWiringRefresh { get; set; }

    public long Version => _notifier.Version;

    public Task WaitBeyondAsync(long seen, CancellationToken ct) => _notifier.WaitBeyondAsync(seen, ct);

    /// <summary>Reads the registry now, whether or not its file looks changed.</summary>
    public Task RebuildAsync(CancellationToken ct) => TryPollAsync(force: true, ct);

    public override async Task StartAsync(CancellationToken cancellationToken) {
        if (config.Accounts is not null) {
            await TryPollAsync(force: true, cancellationToken);
            _timer = new PeriodicTimer(PollInterval, time);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (_timer is not { } timer) return;

        using (timer) {
            try {
                while (await timer.WaitForNextTickAsync(stoppingToken)) await TryPollAsync(force: false, stoppingToken);
            } catch (OperationCanceledException) { /* shutdown */ }
        }
    }

    /// <summary>An unreadable registry is logged once and left for the next poll: a throw here would
    /// stop the daemon, at boot as much as from the timer.</summary>
    async Task TryPollAsync(bool force, CancellationToken ct) {
        try {
            await PollAsync(force, ct);
            _pollWarned = false;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            if (!_pollWarned) LogPollFailed(logger, ex);
            _pollWarned = true;
        }
    }

    async Task PollAsync(bool force, CancellationToken ct) {
        if (config.Accounts is not { } store) return;

        await _gate.WaitAsync(ct);
        try {
            if ((_hostId ??= TryHostId(store)) is not { } hostId) return;

            var stat         = Stat(store.RegistryPath);
            var readingsStat = store.Readings.Stat();
            var wiringStat   = WiringStat(_wiringPaths);
            var unchanged    = stat == _stat && readingsStat.SequenceEqual(_readingsStat) && _wiringStat is not null && wiringStat.SequenceEqual(_wiringStat);
            if (unchanged && _settled && !force) return;

            // Two same-length writes inside one filesystem timestamp tick stat alike, so a stat is
            // trusted only once a later tick has read the file under it successfully.
            _settled = unchanged && !force;
            if (stat != _stat) _loadWarned = false;
            _stat         = stat;
            _readingsStat = readingsStat;

            var read = false;
            try {
                read = Publish(store, hostId, readingsStat);
            } finally {
                if (!read) _settled = false;
            }

            if (!read) return;

            // A registry change can add or move accounts, so the stats are taken again over the paths it loaded.
            var previous = _wiringStat;
            var current  = WiringStat(_wiringPaths);
            _wiringStat = current;
            RequestWiringRefreshes(previous ?? wiringStat, current, baseline: previous is null);
        } finally {
            _gate.Release();
        }
    }

    /// <summary>False when the registry could not be read.</summary>
    bool Publish(AccountStore store, string hostId, (string AccountId, long Length, long Ticks)[] readingsStat) {
        AccountRegistry registry;
        try {
            registry = LoadRegistry(store);
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            if (!_loadWarned) LogRegistryUnreadable(logger, store.RegistryPath, ex);
            _loadWarned = true;
            return false;
        }

        _wiringPaths = [
            .. registry.Accounts
                .Where(a => a.Vendor == HarnessId.Codex)
                .SelectMany(a => {
                    var paths = AccountLayouts.Codex(home, a.Directory);
                    return new[] { (a.Id, paths.UserHooksJson), (a.Id, paths.ConfigToml) };
                }),
        ];

        var readings = KeepUnreadable(store.Readings.LoadAll(), readingsStat, registry);
        var next     = AccountSnapshotBuilder.Build(registry, hostId, config.DefaultDirectories, home, readings);
        if (Current is { } current && AccountSnapshotBuilder.Equivalent(current, next)) return true;

        Volatile.Write(ref _current, next);
        _notifier.Pulse();
        Announce(next);
        RequestBlockedRefreshes(next, readings);
        QueueServerSend(next);
        return true;
    }

    // A file that exists but did not parse is mid-write or damaged, not absent: the account keeps the
    // reading it last published until the file reads again or is removed.
    IReadOnlyDictionary<string, ReadingFile> KeepUnreadable(
        IReadOnlyDictionary<string, ReadingFile>      loaded,
        (string AccountId, long Length, long Ticks)[] readingsStat,
        AccountRegistry                               registry
    ) {
        if (Current is not { } current) return loaded;

        Dictionary<string, ReadingFile>? kept = null;
        foreach (var account in registry.Accounts) {
            if (loaded.ContainsKey(account.Id) || !readingsStat.Any(s => s.AccountId == account.Id)) continue;
            if (current.Accounts.FirstOrDefault(v => v.Id == account.Id)?.Reading is not { } previous || previous.Generation != account.Generation) continue;

            kept ??= new Dictionary<string, ReadingFile>(loaded, StringComparer.Ordinal);
            kept[account.Id] = new ReadingFile(previous, []);
        }

        return kept ?? loaded;
    }

    // The first snapshot is the baseline: a block it already carries is the startup refresh's to look at.
    void RequestBlockedRefreshes(AccountsSnapshotDto snapshot, IReadOnlyDictionary<string, ReadingFile> readings) {
        var seen = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var account in snapshot.Accounts) {
            var blockedAt = account.Reading?.Blocked?.BlockedAt;
            seen[account.Id] = blockedAt;

            if (_blockedSeen is null || blockedAt is not { } at) continue;
            if (_blockedSeen.GetValueOrDefault(account.Id) is { } previous && at <= previous) continue;
            if (readings.GetValueOrDefault(account.Id)?.RefreshOrigins?.Contains(at) == true) continue;

            try {
                RequestRefresh?.Invoke(account.Id);
            } catch (Exception ex) {
                LogHandlerFailed(logger, ex);
            }
        }

        _blockedSeen = seen;
    }

    // Accounts new to the stat, and the first stat, are baselines.
    void RequestWiringRefreshes(
        (string AccountId, string Path, long Length, long Ticks)[] previous,
        (string AccountId, string Path, long Length, long Ticks)[] current,
        bool                                                       baseline
    ) {
        if (baseline) return;

        var changed = current.Where(c => previous.Any(p => p.AccountId == c.AccountId && p.Path == c.Path && p != c))
            .Select(c => c.AccountId)
            .Distinct(StringComparer.Ordinal);

        foreach (var id in changed) {
            try {
                RequestWiringRefresh?.Invoke(id);
            } catch (Exception ex) {
                LogHandlerFailed(logger, ex);
            }
        }
    }

    static (string AccountId, string Path, long Length, long Ticks)[] WiringStat((string AccountId, string Path)[] paths) =>
        [
            .. paths.Select(p => {
                var info = new FileInfo(p.Path);
                return info.Exists ? (p.AccountId, p.Path, info.Length, info.LastWriteTimeUtc.Ticks) : (p.AccountId, p.Path, -1L, 0L);
            }),
        ];

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling the accounts registry failed; retrying on the next tick")]
    static partial void LogPollFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host id could not be read; accounts are not published until it can")]
    static partial void LogHostIdUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An accounts snapshot subscriber threw; the others were still notified")]
    static partial void LogHandlerFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending the accounts snapshot to the server failed; the next connect carries it")]
    static partial void LogServerSendFailed(ILogger logger, Exception ex);
}
