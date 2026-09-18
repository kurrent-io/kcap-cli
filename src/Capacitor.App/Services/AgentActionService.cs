using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// Shared by the tray menu and the main-window rows (spec §7: one code path) — the single place
/// that calls ILocalControlOps.StopAgentAsync and builds the open-in-web URL, so both surfaces
/// get identical in-flight gating, toast text, and link construction. No local cache mutation:
/// a stopped agent's disappearance comes only from the next daemon snapshot (spec §7).
public sealed class AgentActionService {
    const string DaemonUnreachableReason = "daemon_unreachable";
    const string UnreachableCopy         = "The daemon is not reachable";

    readonly ILocalControlOps _ops;
    readonly IAppNotifier _notifier;
    readonly IUrlOpener _opener;
    readonly CancellationToken _shutdownToken;
    readonly Func<string, Task<bool>> _confirmForceStop;
    readonly IServerLane? _lane;

    // ONE lock guards both the in-flight set and the latest server URL — both are cheap,
    // occasional writes, never held across the async stop call itself.
    readonly Lock _lock = new();
    ImmutableHashSet<string> _inFlight = ImmutableHashSet<string>.Empty;
    string? _serverUrl;
    // Never touched by a snapshot: OpenInWebRemote's fixed server, distinct from _serverUrl's
    // local-snapshot-fed one — a remote row's daemon can live behind a different server than
    // this machine's local one, so the two must never share one mutable URL.
    readonly string? _remoteServerUrl;

    readonly BehaviorSubject<IReadOnlySet<string>> _stopsInFlight;

    /// <param name="confirmForceStop">
    /// The confirm-then-force seam for a protected kind (decision 5): invoked with the agent's
    /// label, resolves true to proceed with force:true, false to no-op. The composed delegate is
    /// UI glue (a dialog Window) — this service only awaits it, never marshals to the UI thread
    /// itself; that is the caller's job (App.axaml.cs, via Dispatcher.UIThread.InvokeAsync).
    /// </param>
    /// <param name="fallbackServerUrl">
    /// OpenInWeb's server URL before any local snapshot has ever arrived, AND the fixed server
    /// OpenInWebRemote always uses. Null leaves both without a server URL for a caller with no
    /// resolved profile.
    /// </param>
    public AgentActionService(
            ILocalControlOps ops, IAppNotifier notifier, IUrlOpener opener,
            IObservable<DaemonStatusDto> snapshots, CancellationToken shutdownToken,
            Func<string, Task<bool>> confirmForceStop, string? fallbackServerUrl = null,
            IServerLane? lane = null) {
        _ops = ops;
        _notifier = notifier;
        _opener = opener;
        _shutdownToken = shutdownToken;
        _confirmForceStop = confirmForceStop;
        _lane = lane;
        _stopsInFlight = new BehaviorSubject<IReadOnlySet<string>>(_inFlight);
        _serverUrl = fallbackServerUrl;
        _remoteServerUrl = fallbackServerUrl;

        // Held for the service's lifetime, same as TrayViewModel's constructor-scoped
        // subscriptions — this service is a singleton for the app's lifetime, never disposed
        // mid-run, so there is no unsubscribe seam to wire up.
        snapshots.Subscribe(s => { lock (_lock) _serverUrl = s.Daemon.ServerUrl; });
    }

    /// Replay-1, starts empty. Consumed by the tray menu and the workspace headers to disable a
    /// Stop control while its op is pending. Membership is StopKey, never a bare agent id.
    public IObservable<IReadOnlySet<string>> StopsInFlight => _stopsInFlight.AsObservable();

    /// How a stop is named in StopsInFlight. The two lanes allocate agent ids independently, so
    /// the same id on each is two different agents and neither may gate the other.
    public static string StopKey(AgentOrigin origin, string agentId) => $"{origin}:{agentId}";

    /// A kind other than exactly "agent" is protected (KindText vocabulary: agent|review|
    /// review-flow). Any non-"agent" value — including one this build doesn't recognise — fails
    /// safe as protected, mirroring the daemon's own `Kind != LaunchKind.Default` check and the
    /// CLI's `IsProtectedKind`.
    internal static bool IsProtectedKind(string kind) => kind is not "agent";

    /// Per-agent gating: a second Stop for the same agent no-ops while one is pending — including
    /// while a protected kind's confirm-then-force dialog is still open, since the agent stays
    /// in-flight for the whole RunStopAsync call — other agents run concurrently.
    /// Never throws — this is a UI command target, not a Task the caller awaits.
    public void RequestStop(string agentId, string label, string kind, AgentOrigin origin = AgentOrigin.Local) {
        var key = StopKey(origin, agentId);
        lock (_lock) {
            if (_inFlight.Contains(key)) return;
            _inFlight = _inFlight.Add(key);
            _stopsInFlight.OnNext(_inFlight);
        }
        _ = Task.Run(() => origin == AgentOrigin.Remote ? RunRemoteStopAsync(agentId, label, key) : RunStopAsync(agentId, label, kind, key));
    }

    /// A remote row's stop never reaches ILocalControlOps — it goes to the server hub, which
    /// forwards it to the owning daemon. Ok is silent: the row's disappearance comes from the
    /// next registry update, same as a local stop's confirmation-by-absence.
    async Task RunRemoteStopAsync(string agentId, string label, string key) {
        try {
            if (_lane is null) { _notifier.Notify("Not signed in to a server"); return; }
            var outcome = await _lane.RequestStopAgentAsync(agentId, _shutdownToken).ConfigureAwait(false);
            switch (outcome.Result) {
                case HubCallResult.Ok: break;
                case HubCallResult.NotConnected: _notifier.Notify("Not connected to the server"); break;
                case HubCallResult.Denied: _notifier.Notify($"The server declined to stop {label}"); break;
                default: _notifier.Notify($"Couldn't stop {label}: {outcome.Reason}"); break;
            }
        } catch (OperationCanceledException) {
            // Deliberate shutdown: absorbed quietly, no toast, no log.
        } catch (Exception ex) {
            _notifier.Notify($"Couldn't stop {label}: {ex.Message}");
        } finally {
            lock (_lock) {
                _inFlight = _inFlight.Remove(key);
                _stopsInFlight.OnNext(_inFlight);
            }
        }
    }

    async Task RunStopAsync(string agentId, string label, string kind, string key) {
        try {
            var force = false;
            if (IsProtectedKind(kind)) {
                // false (dialog cancelled) is a quiet no-op — the finally below still clears the
                // in-flight entry, but nothing is sent to the daemon and no toast is shown.
                if (!await _confirmForceStop(label).ConfigureAwait(false)) return;
                force = true;
            }

            var result = await _ops.StopAgentAsync(agentId, force, _shutdownToken).ConfigureAwait(false);
            switch (result.Status) {
                case "stopped": break; // no toast — disappearance from the next snapshot is the confirmation
                case "failed":  _notifier.Notify($"Couldn't stop {label}"); break;
                case "skipped": _notifier.Notify($"The daemon declined to stop {label}"); break;
                case "error":
                    // The daemon's Error text may name an id or CLI-speak ("Pass --force…") the
                    // app cannot act on (spec §7) — never surfaced verbatim in the UI. The full
                    // text goes to stderr only; the toast stays generic. With force-after-confirm
                    // above, a legitimate protected-refusal Error should no longer occur — this
                    // now covers unknown-id/stale cases.
                    Console.Error.WriteLine($"kcap: stop {agentId} failed: {result.Error}");
                    _notifier.Notify($"Couldn't stop {label}");
                    break;
            }
        } catch (OperationCanceledException) {
            // Deliberate shutdown: absorbed quietly, no toast, no log.
        } catch (LocalControlOpsException ex) {
            _notifier.Notify(ex.Reason == DaemonUnreachableReason ? UnreachableCopy : $"Couldn't stop {label}: {ex.Message}");
        } catch (Exception ex) {
            // An unmapped exception still gets a toast (never a silent drop) — AppNotifier.Notify
            // covers both the toast AND stderr (spec §11), so this one call satisfies both
            // without a separate Console.Error write. The finally below still runs either way.
            _notifier.Notify($"Couldn't stop {label}: {ex.Message}");
        } finally {
            // A completion into an already-vanished row/entry is naturally a no-op — the set
            // just loses a member nothing is rendering against anymore.
            lock (_lock) {
                _inFlight = _inFlight.Remove(key);
                _stopsInFlight.OnNext(_inFlight);
            }
        }
    }

    /// Opens {ServerUrl trimmed of trailing '/'}/agents/{Uri.EscapeDataString(id)} from the
    /// latest snapshot in the default browser. Never throws.
    public void OpenInWeb(string agentId) {
        string? serverUrl;
        lock (_lock) serverUrl = _serverUrl;
        OpenUrl(serverUrl, AgentPath(agentId), "Not connected to a daemon yet"); // cannot happen from live UI; defensive only
    }

    /// Same URL shape as OpenInWeb, but always against the app profile's own server rather than
    /// the local daemon's latest snapshot — a remote row's daemon can live behind a different
    /// server than this machine's local one. Never throws.
    public void OpenInWebRemote(string agentId) =>
        OpenUrl(_remoteServerUrl, AgentPath(agentId), "Not signed in to a server");

    /// The work item's page, always on the app profile's own server: that is the server whose read
    /// named the id, and a daemon snapshot's server may be a different one. Never throws.
    public void OpenWorkItemInWeb(string workItemId) {
        if (WorkContextIds.ValidWorkItemId(workItemId) is not { } id) {
            _notifier.Notify("This work item has no usable id");
            return;
        }
        OpenUrl(_remoteServerUrl, $"/work-items/{Uri.EscapeDataString(id)}", "Not signed in to a server");
    }

    static string AgentPath(string agentId) => $"/agents/{Uri.EscapeDataString(agentId)}";

    void OpenUrl(string? serverUrl, string path, string missingServerMessage) {
        if (serverUrl is null) {
            _notifier.Notify(missingServerMessage);
            return;
        }

        try {
            _opener.Open(serverUrl.TrimEnd('/') + path);
        } catch (Exception ex) {
            _notifier.Notify($"Couldn't open the browser: {ex.Message}");
        }
    }
}
