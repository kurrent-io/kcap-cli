using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using DynamicData;

namespace Capacitor.App.Services.Notifications;

public sealed class DesktopNotificationCoordinator : IDisposable {
    sealed class Notice(string id, PendingPermissionRequest? request, AgentRow row) {
        public string Id { get; } = id;
        public PendingPermissionRequest? Request { get; set; } = request;
        public AgentRow Row { get; set; } = row;
        public bool Busy { get; set; }
    }

    readonly IPermissionService _permissions;
    readonly IObservableCache<PendingPermissionRequest, string> _pendingCache;
    readonly IDesktopNotificationSink _sink;
    readonly Func<bool> _isForeground;
    readonly Action<AgentRow> _openAgent;
    readonly IAppNotifier _notifier;
    readonly IScheduler _scheduler;
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly Dictionary<string, Notice> _notices = new(StringComparer.Ordinal);
    readonly HashSet<string> _seenRequests = new(StringComparer.Ordinal);
    Dictionary<string, AgentRow> _rows = new(StringComparer.Ordinal);
    IReadOnlyList<PendingPermissionRequest> _pending = [];
    NotificationPreferences _preferences = new();
    bool _localOnAppServer;
    bool _remoteStale;
    bool _disposed;

    public DesktopNotificationCoordinator(IPermissionService permissions, IAgentDirectory directory,
            IObservable<NotificationPreferences> preferences, IDesktopNotificationSink sink,
            Func<bool> isForeground, Action<AgentRow> openAgent, IAppNotifier notifier, IScheduler scheduler) {
        _permissions = permissions;
        _pendingCache = permissions.Pending.AsObservableCache();
        _sink = sink;
        _isForeground = isForeground;
        _openAgent = openAgent;
        _notifier = notifier;
        _scheduler = scheduler;
        _subscriptions.Add(preferences.ObserveOn(scheduler).Subscribe(OnPreferences));
        _subscriptions.Add(directory.LocalDaemonOnAppServer.ObserveOn(scheduler).Subscribe(value => {
            _localOnAppServer = value;
            ReconcilePending();
        }));
        _subscriptions.Add(directory.RemoteStale.ObserveOn(scheduler).Subscribe(value => {
            _remoteStale = value;
            ReconcilePending();
        }));
        _subscriptions.Add(directory.Rows.Connect()
            .QueryWhenChanged(q => (IReadOnlyList<AgentRow>)[.. q.Items])
            .ObserveOn(scheduler).Subscribe(OnRows));
        _subscriptions.Add(_pendingCache.Connect()
            .QueryWhenChanged(q => (IReadOnlyList<PendingPermissionRequest>)[.. q.Items])
            .ObserveOn(scheduler).Subscribe(items => {
                _pending = items;
                ReconcilePending();
                WithdrawIdle();
            }));
    }

    void OnPreferences(NotificationPreferences preferences) {
        if (_disposed) return;
        _preferences = preferences;
        foreach (var notice in _notices.Values.ToArray())
            if (!Enabled(notice.Request)) Close(notice.Id);
    }

    void OnRows(IReadOnlyList<AgentRow> rows) {
        if (_disposed) return;
        var previous = _rows;
        _rows = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        ReconcilePending();
        WithdrawIdle();
        foreach (var row in rows) {
            if (!previous.TryGetValue(row.Key, out var before) || before.CreatedAt != row.CreatedAt ||
                !SessionStatusDots.IsWorking(before.Status, before.AwaitingInput, before.LiveSubagents) || !Idle(row) ||
                HasPending(row) || !_preferences.Idle || _isForeground()) continue;
            Show(new Notice(Guid.NewGuid().ToString("N"), null, row));
        }
    }

    void WithdrawIdle() {
        foreach (var notice in _notices.Values.Where(n => n.Request is null).ToArray()) {
            if (!_rows.TryGetValue(notice.Row.Key, out var row) || !Idle(row) || HasPending(row)) Close(notice.Id);
            else notice.Row = row;
        }
    }

    void ReconcilePending() {
        if (_disposed) return;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notice in _notices.Values.Where(n => n.Request is not null).ToArray()) {
            var current = _pending.Where(p => SameRequest(p, notice.Request!)).OrderBy(p => p.Lane).FirstOrDefault();
            if (current is null && notice.Request!.SubscriptionLost)
                foreach (var alias in Aliases(notice.Request)) _seenRequests.Remove(alias);
            if (current is null || RowFor(current, allowStale: true) is not { } row || !Enabled(current) || !retained.Add(current.Key)) {
                Close(notice.Id);
                continue;
            }
            notice.Request = current;
            notice.Row = row;
        }

        foreach (var request in _pending) {
            var aliases = Aliases(request).ToArray();
            var known = aliases.Any(_seenRequests.Contains) ||
                _notices.Values.Any(n => n.Request is not null && SameRequest(request, n.Request));
            if (!Enabled(request) || _isForeground()) {
                foreach (var alias in aliases) _seenRequests.Add(alias);
                continue;
            }
            // A request can precede its directory row; wait for a navigable agent before consuming it.
            if (RowFor(request) is not { } row) continue;
            foreach (var alias in aliases) _seenRequests.Add(alias);
            if (known) continue;
            Show(new Notice(Guid.NewGuid().ToString("N"), request, row));
        }
    }

    AgentRow? RowFor(PendingPermissionRequest request, bool allowStale = false) {
        if (request.Lane == PermissionLane.Local) {
            if (_rows.TryGetValue($"local:{request.AgentId}", out var local)) return local;
            // Directory rows can switch lanes before the pending permission feed does.
            return _localOnAppServer && (!_remoteStale || allowStale)
                ? _rows.Values.FirstOrDefault(r => r.Origin == AgentOrigin.Remote &&
                    r.Id == request.AgentId && r.SessionId == request.SessionId)
                : null;
        }
        if (_remoteStale && !allowStale) return null;
        return _rows.Values.Where(r => r.SessionId == request.SessionId &&
                (r.Origin == AgentOrigin.Remote || r.Origin == AgentOrigin.Local && _localOnAppServer))
            .OrderBy(r => r.Origin).FirstOrDefault();
    }

    bool HasPending(AgentRow row) => _pending.Any(p => RowFor(p)?.Key == row.Key);

    static bool Idle(AgentRow row) => row.Status == "Running" && SessionStatusDots.WaitsOnUser(row) && row.LiveSubagents is not > 0;

    static bool Question(PendingPermissionRequest request) => request.IsQuestion || request.ToolName == ClaudeElicitation.ToolName;

    bool Enabled(PendingPermissionRequest? request) => request is null ? _preferences.Idle
        : Question(request) ? _preferences.Questions : _preferences.Permissions;

    IEnumerable<string> Aliases(PendingPermissionRequest request) {
        yield return request.Key;
        if (_localOnAppServer && request.Lane == PermissionLane.Local && request.ServerRequestId is { Length: > 0 } serverId)
            yield return PendingPermissionRequest.KeyFor(PermissionLane.Server, serverId);
    }

    bool SameRequest(PendingPermissionRequest left, PendingPermissionRequest right) =>
        left.Key == right.Key || _localOnAppServer && left.SessionId == right.SessionId && left.Lane != right.Lane &&
        (Aliases(left).Intersect(Aliases(right), StringComparer.Ordinal).Any() || left.SameQuestionAs(right));

    static string AgentLabel(AgentRow row) => row.Title is { Length: > 0 } title ? title : $"{row.Vendor} · {row.RepoGroupLabel}";

    void Show(Notice notice) {
        var request = notice.Request;
        var title = request is null ? "Agent is idle" : Question(request) ? "Agent has a question" : "Permission requested";
        var detail = request is null ? "Ready for your next message."
            : Question(request) ? request.AcpQuestion?.Prompt ?? request.Questions?.Questions.FirstOrDefault()?.Question ?? "Respond in the app."
            : $"{request.ToolName}: {ToolDetail.From(request.ToolInputJson, notice.Row.WorktreePath ?? notice.Row.RepoPath)}";
        var body = $"{AgentLabel(notice.Row)}\n{detail}";
        if (body.Length > 300) body = body[..299] + "…";
        _notices.Add(notice.Id, notice);
        try {
            _sink.Show(new DesktopNotification(notice.Id, title, body, Actions(request)),
                action => _scheduler.Schedule(() => { _ = ActivateAsync(notice.Id, action); }));
        } catch (Exception ex) {
            Close(notice.Id);
            Console.Error.WriteLine($"kcap: could not show desktop notification: {ex.Message}");
        }
    }

    static IReadOnlyList<DesktopNotificationAction> Actions(PendingPermissionRequest? request) {
        if (request is null) return [new("open", "Open agent")];
        if (Question(request)) return [new("open", "Respond in app")];
        var actions = new List<DesktopNotificationAction>();
        if (!CanAnswer(request, "allow")) actions.Add(new("open", "Open agent"));
        foreach (var (id, label) in new[] { ("allow", "Allow"), ("always", "Always"), ("decline", "Decline") })
            if (CanAnswer(request, id)) {
                var scopeLabel = id == "always" ? request.Options?.FirstOrDefault(o => o.Kind == "allow_always")?.Label : null;
                actions.Add(new(id, string.IsNullOrWhiteSpace(scopeLabel) ? label : scopeLabel));
            }
        return actions.Count > 0 ? actions : [new("open", "Open agent")];
    }

    static string? OptionFor(PendingPermissionRequest request, string action) {
        var kind = action switch { "allow" => "allow_once", "always" => "allow_always", "decline" => "reject_once", _ => null };
        if (action == "always" && request.Options?.Count(o => o.Kind == kind) != 1) return null;
        return kind is null ? null : request.Options?.FirstOrDefault(o => o.Kind == kind)?.OptionId;
    }

    static bool CanAnswer(PendingPermissionRequest request, string action) => !Question(request) &&
        (request.Options is not null ? OptionFor(request, action) is { Length: > 0 }
            : action == "decline" || action == "allow" && request.CanAllowOnce || action == "always" && request.CanAllowAlways);

    async Task ActivateAsync(string id, string? action) {
        if (_disposed || !_notices.TryGetValue(id, out var notice) || notice.Busy || !Enabled(notice.Request)) return;
        if (!_rows.TryGetValue(notice.Row.Key, out var row)) { Close(id); return; }
        if (notice.Request is { } pending) {
            var live = _pendingCache.Lookup(pending.Key);
            if (!live.HasValue || !ReferenceEquals(live.Value, pending)) { Close(id); return; }
        }
        if (action is null or "open") {
            Close(id);
            _openAgent(row);
            return;
        }
        if (notice.Request is not { } request || !CanAnswer(request, action)) return;
        notice.Busy = true;
        try {
            var outcome = request.Options is not null
                ? await _permissions.PickOptionAsync(request, OptionFor(request, action)!, _lifetime.Token)
                : await _permissions.ResolveAsync(request, action switch {
                    "allow" => PermissionAnswer.Allow, "always" => PermissionAnswer.AllowAlways, _ => PermissionAnswer.Deny,
                }, _lifetime.Token);
            if (_disposed) return;
            Close(id);
            if (outcome.Kind == PermissionResolveKind.TransportFailure) {
                _notifier.Notify("Could not answer the permission request. Try again in the app.");
                _openAgent(row);
            }
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            if (_disposed) return;
            Close(id);
            _notifier.Notify($"Could not answer the permission request: {ex.Message}");
            _openAgent(row);
        }
    }

    void Close(string id) {
        if (!_notices.Remove(id)) return;
        try { _sink.Close(id); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: could not withdraw desktop notification: {ex.Message}"); }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        _pendingCache.Dispose();
        _lifetime.Cancel();
        foreach (var id in _notices.Keys.ToArray()) Close(id);
        _lifetime.Dispose();
    }
}
