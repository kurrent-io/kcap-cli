using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// <see cref="IAcpProcess"/> over a real <see cref="Process"/> — the <c>cursor-agent acp</c> child
/// spawned by <see cref="Services.AcpHostedAgentRuntimeFactory"/> (AI-684 Task 10). Mirrors the
/// terminate/wait semantics of <c>Pty.Unix.UnixPtyProcess</c>/<c>WinPtyProcess</c> (SIGTERM-then-kill
/// via <see cref="Process.Kill(bool)"/>, bounded waits that return silently on timeout) but owns no
/// terminal I/O — stdin/stdout carry ACP JSON-RPC frames, consumed by <see cref="AcpConnection"/>.
/// </summary>
internal sealed class AcpChildProcess(Process process) : IAcpProcess {
    int _disposed;

    public int Pid {
        get {
            try {
                return process.HasExited ? 0 : process.Id;
            } catch {
                // Process already disposed/exited under us — never let a PID read throw.
                return 0;
            }
        }
    }

    public bool HasExited {
        get {
            try {
                return process.HasExited;
            } catch {
                return true;
            }
        }
    }

    public int? ExitCode {
        get {
            try {
                return process.HasExited ? process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t);

                try {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Timed out — return silently, matching IPtyProcess's contract.
                }
            } else {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Process already exited/disposed — nothing to wait for.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (process.HasExited) return;

            process.Kill(entireProcessTree: true);
        } catch {
            // Already exited/disposed, or the kill raced the exit — either way there's
            // nothing left to terminate.
            return;
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        } catch {
            // Best-effort — already exited or inaccessible.
        }

        try {
            process.Dispose();
        } catch {
            // Best-effort.
        }

        await Task.CompletedTask;
    }
}
