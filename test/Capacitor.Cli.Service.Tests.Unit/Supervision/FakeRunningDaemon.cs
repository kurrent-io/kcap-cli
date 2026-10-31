using Capacitor.Cli.Service.Supervision;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

sealed class FakeRunningDaemon(int pid, bool hasJob = true) : IRunningDaemon {
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pid => pid;
    public bool HasJob => hasJob;
    public Task<int> Exited => _exit.Task;
    public int Terminations { get; private set; }

    public void Exit(int code) => _exit.TrySetResult(code);

    public void Terminate() {
        Terminations++;
        _exit.TrySetResult(0);
    }

    public void Dispose() { }
}
