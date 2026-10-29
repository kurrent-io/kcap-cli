using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Continues folding run signals after the workspace releases its transcript and terminal.
internal sealed class BackgroundCommandMonitor : IDisposable {
    SessionRuns _runs;
    TranscriptFeedHandoff? _handoff;
    readonly Action<int> _report;
    readonly Action _ended;
    readonly TimeProvider _time;
    readonly SessionAccessService _access;
    readonly SessionDetailReader _readDetail;
    readonly IServerLane _lane;
    readonly bool _remote;
    readonly string _agentId;
    readonly CompositeDisposable _subscriptions = new();
    readonly SerialDisposable _accessLease = new();
    readonly ITimer _timer;
    IChatTranscriptFeed? _feed;
    string? _key;
    int _generation;
    bool _reading;
    bool _seeded;
    bool _disposed;

    public BackgroundCommandMonitor(string rowKey, Action<int> report, Action ended, IDaemonClientService daemon,
            IAgentDirectory directory, SessionAccessService access, SessionDetailReader readDetail,
            IServerLane lane, TimeProvider time, TranscriptFeedHandoff? handoff = null) {
        _handoff = handoff;
        _report = report;
        _ended = ended;
        _time = time;
        _access = access;
        _readDetail = readDetail;
        _lane = lane;
        _remote = rowKey.StartsWith("remote:", StringComparison.Ordinal);
        _agentId = rowKey[(rowKey.IndexOf(':') + 1)..];
        _runs = new SessionRuns(time);
        _timer = time.CreateTimer(_ => Dispatcher.UIThread.Post(Read), null,
            ChatTabViewModel.PollInterval, ChatTabViewModel.PollInterval);
        _subscriptions.Add(_accessLease);
        if (_remote) {
            directory.Rows.Connect().Filter(row => row.Key == rowKey)
                .QueryWhenChanged(query => query.Items.FirstOrDefault())
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(row => Follow(row is null ? ChatSessionInfo.Gone : ChatSessionInfo.FromRemote(row, false), null))
                .DisposeWith(_subscriptions);
        } else {
            daemon.Agents.Connect().Filter(dto => dto.Id == _agentId)
                .QueryWhenChanged(query => query.Items.FirstOrDefault())
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(dto => Follow(dto is null ? ChatSessionInfo.Gone : ChatSessionInfo.FromLocal(dto, false),
                    dto is null ? null : ChatTranscriptSource.Resolve(dto).Projection))
                .DisposeWith(_subscriptions);
        }
    }

    void Follow(ChatSessionInfo info, IChatTranscriptProjection? projection) {
        if (_disposed) return;
        if (info.Ended || !_remote && projection is null) {
            _ended();
            return;
        }
        if (_key == info.FeedKey) return;
        _generation++;
        _feed?.Dispose();
        _accessLease.Disposable = Disposable.Empty;
        _key = info.FeedKey;
        _seeded = false;
        var handoff = _handoff;
        _handoff = null;
        if (!_remote && handoff is not null && handoff.Key == _key) {
            _feed = handoff.Feed;
            _runs = handoff.Runs;
            _report(_runs.RunningCommandCount);
            Read();
            return;
        }
        handoff?.Feed.Dispose();
        _runs.Clear();
        if (_key is null) { _feed = null; _report(0); return; }
        if (_remote) {
            var lease = _access.Acquire(_key);
            var denied = new SingleAssignmentDisposable();
            _accessLease.Disposable = new CompositeDisposable(denied, lease);
            _feed = new RemoteTranscriptFeed(_key, info.Vendor, lease.State, _readDetail, _lane, _time, Log);
            // A replayed denial can dispose this monitor inside Subscribe.
            denied.Disposable = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Where(state => state == SessionAccessState.Denied).Subscribe(_ => _ended());
        } else {
            _feed = new LocalTranscriptFeed(_key, projection!, _agentId, _time, Log);
        }
        Read();
    }

    void Read() {
        if (_disposed || _reading || _feed is not { } feed) return;
        _reading = true;
        PendingReadForTesting = ReadAsync(feed, _generation);
    }

    internal Task? PendingReadForTesting { get; private set; }

    async Task ReadAsync(IChatTranscriptFeed feed, int generation) {
        try {
            var read = await Task.Run(feed.ReadAppended).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => {
                if (_disposed || generation != _generation) return;
                // A remote feed stops on either verdict; a local one retries on the next poll.
                if (_remote && read.Status is FeedStatus.Missing or FeedStatus.Failed) { _ended(); return; }
                if (read.Status == FeedStatus.Missing) { _report(0); return; }
                if (read.Status == FeedStatus.Failed) return;
                // The seed may arrive after this read drained the feed; only a consumed Reset counts.
                if (_remote && !_seeded && read.Status != FeedStatus.Reset) return;
                if (read.Status == FeedStatus.Reset) {
                    _seeded = true;
                    _runs.Clear();
                }
                _runs.ApplyAll(read.Lines.Select(line => line.Projection));
                _report(_runs.RunningCommandCount);
            });
        } catch (Exception ex) {
            Log(ex.Message);
        } finally {
            await Dispatcher.UIThread.InvokeAsync(() => _reading = false);
        }
    }

    static void Log(string message) => Console.Error.WriteLine($"kcap: background commands: {message}");

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _timer.Dispose();
        _subscriptions.Dispose();
        _feed?.Dispose();
        _feed = null;
        _handoff?.Feed.Dispose();
        _handoff = null;
    }
}
