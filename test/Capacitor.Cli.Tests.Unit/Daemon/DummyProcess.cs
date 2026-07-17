using System.Diagnostics;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Phase B: a real, isolated child process a test fully owns — the ONLY thing the D4 reap
/// tests ever signal (spec §2/§8: no live daemon, no live flows). Sleeps for a while so the test can
/// probe/kill it, and can carry custom <c>KCAP_*</c> env markers for the env-based reap paths.
/// </summary>
internal sealed class DummyProcess : IDisposable {
    readonly Process _proc;

    DummyProcess(Process proc) => _proc = proc;

    public int  Pid       => _proc.Id;
    public bool HasExited => _proc.HasExited;

    public static DummyProcess StartSleep(int seconds, IDictionary<string, string>? env = null) {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c timeout /t {seconds} >NUL")
            : new ProcessStartInfo("sleep", seconds.ToString());

        psi.UseShellExecute = false;

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        return new DummyProcess(Process.Start(psi) ?? throw new InvalidOperationException("failed to start dummy process"));
    }

    public void Kill() {
        try { _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
    }

    public bool WaitForExit(TimeSpan timeout) => _proc.WaitForExit((int) timeout.TotalMilliseconds);
    public void WaitForExit()                 => _proc.WaitForExit();

    public void Dispose() {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        _proc.Dispose();
    }
}
