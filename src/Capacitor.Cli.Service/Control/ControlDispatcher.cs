using System.Text.Json;
using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Control;

/// <summary>
/// Turns one request line from a caller into one response line. The capabilities a client sees are the
/// keys of the same table that dispatches, so a verb cannot be advertised without a handler. The daemon's
/// verbs are bare; the local server's are the same verbs prefixed <see cref="ServerPrefix"/>
/// </summary>
public sealed class ControlDispatcher {
    public const int Protocol = 1;

    public const string ServerPrefix = "server-";

    public const string LeavingError = "kcap-service is removing itself; install it again";

    readonly Dictionary<string, Func<string, CancellationToken, Task<ControlResponse>>> _verbs = new(StringComparer.Ordinal);
    readonly IServiceLog _log;
    readonly IEnrollmentIndex _index;
    readonly Func<bool> _lastUserGone;
    readonly Lock _indexLock = new();
    bool _leaving; // under _indexLock

    /// <param name="lastUserGone">Starts the service's removal; false when it could not, and the service stays</param>
    public ControlDispatcher(
            Supervisor<Enrollment> daemons, Supervisor<ServerEnrollment> servers, IEnrollmentIndex index, Func<bool> lastUserGone,
            string version, IServiceLog log) {
        _log = log;
        _index = index;
        _lastUserGone = lastUserGone;
        _verbs["capabilities"] = (_, _) => Task.FromResult(new ControlResponse(true, Capabilities: Capabilities(version)));
        Map("", daemons, servers.HasEnrollment);
        Map(ServerPrefix, servers, daemons.HasEnrollment);
    }

    /// <param name="otherEnrolled">Whether the user still has the other kind of enrollment; null when that
    /// cannot be told</param>
    void Map<TEnrollment>(string prefix, Supervisor<TEnrollment> supervisor, Func<string, bool?> otherEnrolled)
        where TEnrollment : class, ISupervisedEnrollment {
        _verbs[prefix + "status"] = (sid, _) => Task.FromResult(Reply(supervisor.Status(sid)));
        _verbs[prefix + "start"] = (sid, _) => Task.FromResult(Reply(supervisor.Start(sid)));
        _verbs[prefix + "stop"] = async (sid, _) => Reply(await supervisor.StopAsync(sid).ConfigureAwait(false));
        _verbs[prefix + "enroll"] = (sid, _) => EnrollAsync(sid, supervisor);
        _verbs[prefix + "unenroll"] = (sid, _) => UnenrollAsync(sid, supervisor, otherEnrolled);
    }

    /// <summary>Indexed before the process starts, so a last unenroll running alongside cannot find the index
    /// empty and remove the service this enrollment is about to rely on</summary>
    async Task<ControlResponse> EnrollAsync<TEnrollment>(string sid, Supervisor<TEnrollment> supervisor)
        where TEnrollment : class, ISupervisedEnrollment {
        bool added;
        lock (_indexLock) {
            if (_leaving) return new ControlResponse(false, LeavingError);
            added = _index.Add(sid);
        }
        var status = await supervisor.EnrollAsync(sid).ConfigureAwait(false);
        if (status.State is DaemonState.NotEnrolled or DaemonState.NoSession) {
            if (added) {
                lock (_indexLock) {
                    if (!_leaving) _index.Remove(sid);
                }
            }
            return new ControlResponse(false, status.Error ?? "not enrolled", StatusDto.From(status));
        }
        return Reply(status);
    }

    /// <summary>
    /// Several callers unenroll during one app uninstall, each held behind the first one's stop. Removal starts
    /// once, and the index it deletes is not touched after it starts. The user is dropped from this supervisor
    /// whatever state their process was in, so the answer is always a success; they leave the index only once
    /// their other enrollment is known to be gone too.
    /// </summary>
    async Task<ControlResponse> UnenrollAsync<TEnrollment>(string sid, Supervisor<TEnrollment> supervisor, Func<string, bool?> otherEnrolled)
        where TEnrollment : class, ISupervisedEnrollment {
        var status = await supervisor.StopAsync(sid).ConfigureAwait(false);
        supervisor.Forget(sid);
        lock (_indexLock) {
            if (!_leaving) {
                // Read under the lock: an enroll of the other kind is either seen here or, once removal has
                // started, refused
                if (otherEnrolled(sid) == false) _index.Remove(sid);
                _index.PruneDeparted();
                if (_index.IsEmpty) _leaving = _lastUserGone();
            }
        }
        return new ControlResponse(true, Status: StatusDto.From(status));
    }

    /// <summary>Indexes a user whose daemon or server the service runs without having been asked to enroll them
    /// — after a reinstall, at their next sign-in — so the service is not removed from under them</summary>
    public void Supervised(string sid) {
        lock (_indexLock) {
            if (!_leaving) _index.Add(sid);
        }
    }

    CapabilitiesDto Capabilities(string version) => new(version, Protocol, [.. _verbs.Keys.Order(StringComparer.Ordinal)]);

    static ControlResponse Reply(DaemonStatus status) =>
        new(status.State is not (DaemonState.Failed or DaemonState.NotEnrolled), status.Error, StatusDto.From(status));

    public async Task<string> HandleAsync(string callerSid, string line, CancellationToken ct) {
        ControlResponse response;
        try {
            var request = JsonSerializer.Deserialize(line, ControlJsonContext.Default.ControlRequest);
            response = request?.Verb is { } verb && _verbs.TryGetValue(verb, out var handler)
                ? await handler(callerSid, ct).ConfigureAwait(false)
                : new ControlResponse(false, $"unknown verb '{request?.Verb}'");
            _log.Write($"pipe {request?.Verb} from {callerSid}: {(response.Ok ? "ok" : response.Error)}");
        } catch (JsonException) {
            response = new ControlResponse(false, "the request is not a JSON object with a verb");
        }
        return JsonSerializer.Serialize(response, ControlJsonContext.Default.ControlResponse);
    }
}
