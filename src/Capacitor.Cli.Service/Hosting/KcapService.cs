using System.Runtime.Versioning;
using System.ServiceProcess;
using Capacitor.Cli.Service.Control;
using Capacitor.Cli.Service.Installation;
using Capacitor.Cli.Service.Logging;
using Capacitor.Cli.Service.Sessions;
using Capacitor.Cli.Service.Supervision;
using Capacitor.Cli.Service.Windows;

namespace Capacitor.Cli.Service.Hosting;

/// <summary>The SCM-facing service. Stopping it detaches: users' daemons and local servers keep running, and
/// the next start adopts them</summary>
[SupportedOSPlatform("windows")]
sealed class KcapService : ServiceBase {
    static readonly TimeSpan DaemonStopWait = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ServerStopWait = TimeSpan.FromSeconds(100);

    readonly Supervisor<Enrollment> _daemons;
    readonly Supervisor<ServerEnrollment> _servers;
    readonly ControlPipeServer _pipe;
    readonly IServiceLog _log;

    public KcapService(IServiceLog log, string version) {
        ServiceName = ServiceIdentity.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        _log = log;
        ControlDispatcher? dispatcher = null;
        _daemons = new Supervisor<Enrollment>(new WindowsDaemonHost(), TimeProvider.System, log, DaemonStopWait, sid => dispatcher?.Supervised(sid));
        _servers = new Supervisor<ServerEnrollment>(new WindowsServerHost(), TimeProvider.System, log, ServerStopWait, sid => dispatcher?.Supervised(sid));
        dispatcher = new ControlDispatcher(_daemons, _servers, new RegistryEnrollmentIndex(), RemoveSelf, version, log);
        _pipe = new ControlPipeServer(dispatcher, TimeProvider.System, log);
        _log.Write($"kcap-service {version}");
    }

    protected override void OnStart(string[] args) {
        _log.Write("service starting");
        _pipe.Start();
        _ = Task.Run(() => {
            Scan("daemon", _daemons.Start);
            Scan("server", _servers.Start);
        });
    }

    protected override void OnStop() => Detach("service stopping; daemons and servers left running");

    protected override void OnShutdown() => Detach("system shutting down");

    protected override void OnSessionChange(SessionChangeDescription change) {
        var session = change.SessionId;
        var reason = change.Reason;
        _log.Write($"session {session}: {reason}");
        _ = Task.Run(() => {
            Route(_daemons, session, reason);
            Route(_servers, session, reason);
        });
    }

    void Route<TEnrollment>(Supervisor<TEnrollment> supervisor, int session, SessionChangeReason reason)
        where TEnrollment : class, ISupervisedEnrollment {
        try {
            switch (reason) {
                case SessionChangeReason.SessionLogon:  supervisor.OnLogon(session);  break;
                case SessionChangeReason.SessionLogoff: supervisor.OnLogoff(session); break;
                case SessionChangeReason.SessionUnlock: supervisor.OnUnlock(session); break;
            }
        } catch (Exception ex) {
            _log.Write($"session {session} {reason}: {ex.Message}");
        }
    }

    void Scan(string what, Action start) {
        try {
            start();
        } catch (Exception ex) {
            _log.Write($"initial {what} session scan failed: {ex.Message}");
        }
    }

    void Detach(string why) {
        _pipe.Stop();
        _daemons.Detach();
        _servers.Detach();
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
