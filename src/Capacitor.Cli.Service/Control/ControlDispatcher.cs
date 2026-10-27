using System.Text.Json;
using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Control;

/// <summary>
/// Turns one request line from a caller into one response line. The capabilities a client sees are the
/// keys of the same table that dispatches, so a verb cannot be advertised without a handler
/// </summary>
public sealed class ControlDispatcher {
    public const int Protocol = 1;

    public const string LeavingError = "kcap-service is removing itself; install it again";

    readonly Dictionary<string, Func<string, CancellationToken, Task<ControlResponse>>> _verbs;
    readonly IServiceLog _log;
    readonly IEnrollmentIndex _index;
    readonly Lock _indexLock = new();
    bool _leaving; // under _indexLock

    /// <param name="lastUserGone">Starts the service's removal; false when it could not, and the service stays</param>
    public ControlDispatcher(Supervisor supervisor, IEnrollmentIndex index, Func<bool> lastUserGone, string version, IServiceLog log) {
        _log = log;
        _index = index;
        _verbs = new(StringComparer.Ordinal) {
            ["capabilities"] = (_, _) => Task.FromResult(new ControlResponse(true, Capabilities: Capabilities(version))),
            ["status"] = (sid, _) => Task.FromResult(Reply(supervisor.Status(sid))),
            ["start"] = (sid, _) => Task.FromResult(Reply(supervisor.Start(sid))),
            ["stop"] = async (sid, _) => Reply(await supervisor.StopAsync(sid).ConfigureAwait(false)),
            // Indexed before the daemon starts, so a last unenroll running alongside cannot find the index empty
            // and remove the service this enrollment is about to rely on
            ["enroll"] = async (sid, _) => {
                bool added;
                lock (_indexLock) {
                    if (_leaving) return new ControlResponse(false, LeavingError);
                    added = index.Add(sid);
                }
                var status = await supervisor.EnrollAsync(sid).ConfigureAwait(false);
                if (status.State is DaemonState.NotEnrolled or DaemonState.NoSession) {
                    if (added) {
                        lock (_indexLock) {
                            if (!_leaving) index.Remove(sid);
                        }
                    }
                    return new ControlResponse(false, status.Error ?? "not enrolled", StatusDto.From(status));
                }
                return Reply(status);
            },
            // Several callers unenroll during one app uninstall, each held behind the first one's stop. Removal
            // starts once, and the index it deletes is not touched after it starts. The user is dropped whatever
            // state their daemon was in, so the answer is always a success.
            ["unenroll"] = async (sid, _) => {
                var status = await supervisor.StopAsync(sid).ConfigureAwait(false);
                supervisor.Forget(sid);
                lock (_indexLock) {
                    if (!_leaving) {
                        index.Remove(sid);
                        index.PruneDeparted();
                        if (index.IsEmpty) _leaving = lastUserGone();
                    }
                }
                return new ControlResponse(true, Status: StatusDto.From(status));
            },
        };
    }

    /// <summary>Indexes a user whose daemon the service runs without having been asked to enroll them — after
    /// a reinstall, at their next sign-in — so the service is not removed from under them</summary>
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
