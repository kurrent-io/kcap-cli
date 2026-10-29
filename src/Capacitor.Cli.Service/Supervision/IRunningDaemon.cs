namespace Capacitor.Cli.Service.Supervision;

public interface IRunningDaemon : IDisposable {
    int Pid { get; }

    /// <summary>False for a daemon adopted after its job was lost; stopping it then ends the daemon only</summary>
    bool HasJob { get; }

    /// <summary>Completes with the exit code</summary>
    Task<int> Exited { get; }

    /// <summary>Ends the daemon and, with a job, everything it started, with exit code 0 so the end reads
    /// as deliberate</summary>
    void Terminate();
}
