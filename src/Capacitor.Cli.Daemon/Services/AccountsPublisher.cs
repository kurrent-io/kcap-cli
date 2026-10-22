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
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    readonly SemaphoreSlim        _gate     = new(1, 1);
    readonly DaemonStatusNotifier _notifier = new();

    AccountsSnapshotDto?       _current;
    PeriodicTimer?             _timer;
    string?                    _hostId;
    (long Length, long Ticks)? _stat;
    bool                       _settled;
    bool                       _loadWarned;
    bool                       _hostWarned;

    public AccountsSnapshotDto? Current => Volatile.Read(ref _current);

    /// <summary>Raised under the rebuild gate so handlers see revisions in order; a handler must not
    /// wait on <see cref="RebuildAsync"/>. The snapshot built at start is not announced.</summary>
    public event Action<AccountsSnapshotDto>? Changed;

    public long Version => _notifier.Version;

    public Task WaitBeyondAsync(long seen, CancellationToken ct) => _notifier.WaitBeyondAsync(seen, ct);

    /// <summary>Reads the registry now, whether or not its file looks changed.</summary>
    public Task RebuildAsync(CancellationToken ct) => PollAsync(force: true, announce: true, ct);

    public override async Task StartAsync(CancellationToken cancellationToken) {
        if (config.Accounts is not null) {
            await PollAsync(force: true, announce: false, cancellationToken);
            _timer = new PeriodicTimer(PollInterval, time);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (_timer is not { } timer) return;

        using (timer) {
            try {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await PollAsync(force: false, announce: true, stoppingToken);
            } catch (OperationCanceledException) { /* shutdown */ }
        }
    }

    async Task PollAsync(bool force, bool announce, CancellationToken ct) {
        if (config.Accounts is not { } store) return;

        await _gate.WaitAsync(ct);
        try {
            if ((_hostId ??= TryHostId(store)) is not { } hostId) return;

            var stat      = Stat(store.RegistryPath);
            var unchanged = stat == _stat;
            if (unchanged && _settled && !force) return;

            // Two same-length writes inside one filesystem timestamp tick stat alike, so a stat is
            // trusted only once a later tick has read the file under it again.
            _settled = unchanged && !force;
            if (!unchanged) _loadWarned = false;
            _stat = stat;

            Publish(store, hostId, announce);
        } finally {
            _gate.Release();
        }
    }

    void Publish(AccountStore store, string hostId, bool announce) {
        AccountRegistry registry;
        try {
            registry = store.Load();
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            if (!_loadWarned) LogRegistryUnreadable(logger, store.RegistryPath, ex);
            _loadWarned = true;
            return;
        }

        if (Current is { } current && current.Revision == registry.Revision && current.HostId == hostId) return;

        var next = AccountSnapshotBuilder.Build(registry, hostId, config.DefaultDirectories, home);
        Volatile.Write(ref _current, next);
        _notifier.Pulse();
        if (announce) Changed?.Invoke(next);
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
}
