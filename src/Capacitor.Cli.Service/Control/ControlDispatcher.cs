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
    readonly Lock _indexLock = new();
    bool _leaving; // under _indexLock

    /// <param name="lastUserGone">Starts the service's removal; false when it could not, and the service stays</param>
    public ControlDispatcher(Supervisor supervisor, IEnrollmentIndex index, Func<bool> lastUserGone, string version, IServiceLog log) {
        _log = log;
        _verbs = new(StringComparer.Ordinal) {
            ["capabilities"] = (_, _) => Task.FromResult(new ControlResponse(true, Capabilities: Capabilities(version))),
            ["status"] = (sid, _) => Task.FromResult(Reply(supervisor.Status(sid))),
            ["start"] = (sid, _) => Task.FromResult(Reply(supervisor.Start(sid))),
            ["stop"] = async (sid, ct) => Reply(await supervisor.StopAsync(sid, ct).ConfigureAwait(false)),
            ["enroll"] = (sid, _) => {
                lock (_indexLock) {
                    if (_leaving) return Task.FromResult(new ControlResponse(false, LeavingError));
                }
                var status = supervisor.Start(sid);
                if (status.State is DaemonState.NotEnrolled or DaemonState.NoSession)
                    return Task.FromResult(new ControlResponse(false, status.Error ?? "not enrolled", StatusDto.From(status)));
                lock (_indexLock) index.Add(sid);
                return Task.FromResult(Reply(status));
            },
            // Several callers unenroll during one app uninstall, each held behind the first one's stop. Removal
            // starts once, and the index it deletes is not touched after it starts.
            ["unenroll"] = async (sid, ct) => {
                var status = await supervisor.StopAsync(sid, ct).ConfigureAwait(false);
                supervisor.Forget(sid);
                lock (_indexLock) {
                    if (_leaving) return Reply(status);
                    index.Remove(sid);
                    index.PruneDeparted();
                    if (index.IsEmpty) _leaving = lastUserGone();
                }
                return Reply(status);
            },
        };
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
