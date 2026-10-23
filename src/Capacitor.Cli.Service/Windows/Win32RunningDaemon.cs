using System.Runtime.Versioning;
using Capacitor.Cli.Service.Supervision;
using Microsoft.Win32.SafeHandles;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Windows;

/// <summary>A daemon process the service holds a handle to, and its job when it has one. Owns both</summary>
[SupportedOSPlatform("windows")]
sealed class Win32RunningDaemon : IRunningDaemon {
    readonly IntPtr _process;
    readonly IntPtr _job;
    readonly ManualResetEvent _wait;
    readonly RegisteredWaitHandle _registration;
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _disposed;

    public Win32RunningDaemon(int pid, IntPtr process, IntPtr job) {
        Pid = pid;
        _process = process;
        _job = job;
        _wait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        _registration = ThreadPool.RegisterWaitForSingleObject(_wait, (_, _) => {
            _exit.TrySetResult(GetExitCodeProcess(_process, out var code) ? code : -1);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int Pid { get; }

    public bool HasJob => _job != IntPtr.Zero;

    public Task<int> Exited => _exit.Task;

    public void Terminate() {
        var ended = HasJob ? TerminateJobObject(_job, 0) : TerminateProcess(_process, 0);
        if (!ended) throw Failure(HasJob ? "TerminateJobObject" : "TerminateProcess");
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration.Unregister(null);
        _wait.Dispose();
        CloseHandle(_process);
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }
}
