using System.Runtime.Versioning;
using System.ServiceProcess;
using Capacitor.Cli.Service.Control;
using Capacitor.Cli.Service.Installation;
using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Supervision;
using Capacitor.Cli.Service.Windows;

namespace Capacitor.Cli.Service.Hosting;

/// <summary>The SCM-facing service. Stopping it detaches: users' daemons keep running, and the next start
/// adopts them</summary>
[SupportedOSPlatform("windows")]
sealed class KcapService : ServiceBase {
    readonly Supervisor _supervisor;
    readonly ControlPipeServer _pipe;
    readonly IServiceLog _log;

    public KcapService(IServiceLog log, string version) {
        ServiceName = ServiceIdentity.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        _log = log;
        ControlDispatcher? dispatcher = null;
        _supervisor = new Supervisor(new WindowsDaemonHost(), TimeProvider.System, log, sid => dispatcher?.Supervised(sid));
        dispatcher = new ControlDispatcher(_supervisor, new RegistryEnrollmentIndex(), RemoveSelf, version, log);
        _pipe = new ControlPipeServer(dispatcher, TimeProvider.System, log);
        _log.Write($"kcap-service {version}");
    }

    protected override void OnStart(string[] args) {
        _log.Write("service starting");
        _pipe.Start();
        _ = Task.Run(() => {
            try {
                _supervisor.Start();
            } catch (Exception ex) {
                _log.Write($"initial session scan failed: {ex.Message}");
            }
        });
    }

    protected override void OnStop() => Detach("service stopping; daemons left running");

    protected override void OnShutdown() => Detach("system shutting down");

    protected override void OnSessionChange(SessionChangeDescription change) {
        var session = change.SessionId;
        var reason = change.Reason;
        _log.Write($"session {session}: {reason}");
        _ = Task.Run(() => {
            try {
                switch (reason) {
                    case SessionChangeReason.SessionLogon:  _supervisor.OnLogon(session);  break;
                    case SessionChangeReason.SessionLogoff: _supervisor.OnLogoff(session); break;
                    case SessionChangeReason.SessionUnlock: _supervisor.OnUnlock(session); break;
                }
            } catch (Exception ex) {
                _log.Write($"session {session} {reason}: {ex.Message}");
            }
        });
    }

    void Detach(string why) {
        _pipe.Stop();
        _supervisor.Detach();
        _log.Write(why);
    }

    bool RemoveSelf() {
        _log.Write("the last enrolled user has gone; removing the service");
        try {
            ServiceInstaller.RemoveSelf();
        } catch (Exception ex) {
            _log.Write($"self-removal failed: {ex.Message}");
            return false;
        }
        _ = Task.Run(Stop);
        return true;
    }

    protected override void Dispose(bool disposing) {
        if (disposing) _pipe.Dispose();
        base.Dispose(disposing);
    }
}
