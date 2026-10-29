using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli;

/// <summary>
/// A detached child that keeps hold of its own identity — the OS handle it was created with, not
/// its pid — alongside the stdin pipe its payload travels over. A pid is reusable the moment the
/// child exits, so terminating by pid can reach an unrelated process; the retained handle names
/// this child and nothing else, for as long as it is held.
///
/// <para>Disposing releases that handle and closes the pipe. It does not signal the child, which is
/// the point of spawning detached; <see cref="Terminate"/> is the deliberate kill.</para>
/// </summary>
public sealed class DetachedChild : IDisposable {
    readonly Stream             _standardInput;
    readonly Process?           _process; // Unix: the wrapper owns the handle
    readonly SafeProcessHandle? _handle;  // Windows: the handle CreateProcess returned
    int                         _disposed;

    DetachedChild(int pid, Stream standardInput, Process? process, SafeProcessHandle? handle) {
        Pid            = pid;
        _standardInput = standardInput;
        _process       = process;
        _handle        = handle;
    }

    public int    Pid           { get; }
    public Stream StandardInput => _standardInput;

    public static DetachedChild ForProcess(Process process, Stream standardInput) =>
        new(process.Id, standardInput, process, handle: null);

    public static DetachedChild ForHandle(int pid, SafeProcessHandle handle, Stream standardInput) =>
        new(pid, standardInput, process: null, handle);

    /// <summary>
    /// Kills the child through the retained handle. Best effort: a child that already exited is the
    /// outcome the caller wanted, and nothing here is worth failing a cleanup path over.
    /// </summary>
    public void Terminate() {
        try {
            if (_process is not null) {
                _process.Kill(entireProcessTree: true);
            } else if (_handle is { IsInvalid: false }) {
                ProcessHelpers.TerminateByHandle(_handle);
            }
        } catch { }
    }

    // Closing the pipe first is what delivers EOF to the child; the handle is released from a
    // finally because that close can throw — a child that already exited leaves buffered bytes with
    // nowhere to go — and the handle would then be abandoned until finalization.
    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) {
            return;
        }

        try {
            _standardInput.Dispose();
        } finally {
            _process?.Dispose();
            _handle?.Dispose();
        }
    }
}
