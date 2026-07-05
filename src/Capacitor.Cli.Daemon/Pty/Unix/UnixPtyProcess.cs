using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Capacitor.Cli.Daemon.Pty.Unix;

public sealed class UnixPtyProcess : IPtyProcess {
    readonly int                     _masterFd;
    readonly CancellationTokenSource _cts = new();
    bool                             _disposed;

    public int  Pid       { get; }
    public bool HasExited { get; private set; }
    public int? ExitCode  { get; private set; }

    UnixPtyProcess(int masterFd, int childPid) {
        _masterFd = masterFd;
        Pid = childPid;
    }

    public static UnixPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        // Console app inherits full PATH — no resolution needed
        // Precompute ALL strings before fork (fork safety)
        var colsStr = cols.ToString();
        var rowsStr = rows.ToString();
        var argv    = new string[args.Length + 2];
        argv[0] = command;
        Array.Copy(args, 0, argv, 1, args.Length);
        argv[^1] = null!;

        string[]? extraEnvKeys   = null;
        string[]? extraEnvValues = null;

        if (extraEnv is { Count: > 0 }) {
            extraEnvKeys   = extraEnv.Keys.ToArray();
            extraEnvValues = extraEnv.Values.ToArray();
        }

        var ws  = new UnixPtyInterop.WinSize { ws_row = rows, ws_col = cols };
        var pid = UnixPtyInterop.forkpty(out var masterFd, IntPtr.Zero, IntPtr.Zero, ref ws);

        switch (pid) {
            case < 0:
                throw new InvalidOperationException($"forkpty failed: errno {Marshal.GetLastPInvokeError()}");
            case 0: {
                // Child process — set terminal vars, unset Claude vars, apply extraEnv
                UnixPtyInterop.setenv("TERM", "xterm-256color", 1);
                UnixPtyInterop.setenv("LANG", "en_US.UTF-8", 1);
                UnixPtyInterop.setenv("COLUMNS", colsStr, 1);
                UnixPtyInterop.setenv("LINES", rowsStr, 1);
                UnixPtyInterop.unsetenv("CLAUDECODE");
                UnixPtyInterop.unsetenv("CLAUDE_CODE_ENTRYPOINT");
                UnixPtyInterop.unsetenv("ANTHROPIC_API_KEY");
                // A daemon started from inside a Claude Code session inherits that
                // session's identity vars. CLAUDE_CODE_CHILD_SESSION=1 makes the spawned
                // interactive claude behave as a child session and never write its own
                // transcript .jsonl, so `kcap watch` has nothing to stream and the hosted
                // agent's web chat stays empty forever ("Waiting for conversation
                // data...") while the PTY works fine. Scrub the whole inherited-session
                // surface — parity with ConPtyProcess.BuildEnvironmentBlock.
                UnixPtyInterop.unsetenv("CLAUDE_CODE_CHILD_SESSION");
                UnixPtyInterop.unsetenv("CLAUDE_CODE_SESSION_ID");
                UnixPtyInterop.unsetenv("CLAUDE_ENV_FILE");
                UnixPtyInterop.unsetenv("ANTHROPIC_API_KEY_CLAUDE_CODE_BACKUP");
                // Clear any hosted-agent identity/routing the daemon may have inherited (e.g.
                // it was started from inside a kcap-tracked session) so the spawned agent gets
                // ONLY what extraEnv sets: hosted launches re-add these below; private local
                // launches deliberately leave them unset (no mis-tag, native permissions).
                UnixPtyInterop.unsetenv("KCAP_AGENT_ID");
                UnixPtyInterop.unsetenv("KCAP_RENDERED_AGENT");
                UnixPtyInterop.unsetenv("KCAP_DAEMON_URL");
                // Never leak daemon supervision state into hosted agents — otherwise a
                // `kcap daemon start` run from inside an agent could inherit a supervised
                // classification and later take the exit-for-relaunch path with no supervisor.
                UnixPtyInterop.unsetenv("KCAP_DAEMON_SUPERVISED");
                UnixPtyInterop.unsetenv("XPC_SERVICE_NAME");
                UnixPtyInterop.unsetenv("INVOCATION_ID");
                UnixPtyInterop.unsetenv("SYSTEMD_EXEC_PID");

                if (extraEnvKeys is not null) {
                    for (var i = 0; i < extraEnvKeys.Length; i++) {
                        UnixPtyInterop.setenv(extraEnvKeys[i], extraEnvValues![i], 1);
                    }
                }

                UnixPtyInterop.chdir(cwd);
                UnixPtyInterop.execvp(command, argv);
                UnixPtyInterop._exit(127); // execvp failed

                break;
            }
        }

        // Parent process — winsize already set via forkpty ref ws parameter
        return new UnixPtyProcess(masterFd, pid);
    }

    public async IAsyncEnumerable<byte[]> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        ) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var       buf    = new byte[4096];

        while (!linked.Token.IsCancellationRequested && !HasExited) {
            var bytesRead = await Task.Run(
                () => {
                    var pfd = new UnixPtyInterop.PollFd { fd = _masterFd, events = UnixPtyInterop.POLLIN };

                    while (!linked.Token.IsCancellationRequested) {
                        var pollResult = UnixPtyInterop.poll(ref pfd, 1, 200);

                        switch (pollResult) {
                            case > 0 when (pfd.revents & UnixPtyInterop.POLLIN) != 0: {
                                var n = UnixPtyInterop.read(_masterFd, buf, buf.Length);

                                return (int)n;
                            }
                            case > 0 when (pfd.revents & (UnixPtyInterop.POLLHUP | UnixPtyInterop.POLLERR)) != 0:
                                // Slave side closed or errored without buffered data — EOF
                                return 0;
                            case < 0:
                                return -1;
                        }
                    }

                    return 0;
                },
                CancellationToken.None
            );

            if (bytesRead <= 0) {
                CheckExited();

                yield break;
            }

            var data = new byte[bytesRead];
            Array.Copy(buf, data, bytesRead);

            yield return data;
        }
    }

    public Task WriteAsync(string input) {
        var bytes = Encoding.UTF8.GetBytes(input);

        return Task.Run(() => UnixPtyInterop.write(_masterFd, bytes, bytes.Length));
    }

    public Task WriteAsync(byte[] data) {
        return Task.Run(() => UnixPtyInterop.write(_masterFd, data, data.Length));
    }

    public void Resize(ushort cols, ushort rows) {
        UnixPtyInterop.SetWinSize(_masterFd, rows, cols);
    }

    public void SendInterrupt() {
        if (!HasExited) {
            UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGINT);
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGTERM);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (!HasExited && DateTime.UtcNow < deadline) {
            CheckExited();
            if (!HasExited) {
                await Task.Delay(100);
            }
        }

        if (!HasExited) {
            UnixPtyInterop.kill(Pid, UnixPtyInterop.SIGKILL);
            CheckExited();
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (HasExited) {
            return;
        }

        var sw    = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        while (!HasExited && sw.Elapsed < limit) {
            CheckExited();

            if (!HasExited) {
                await Task.Delay(50);
            }
        }
    }

    void CheckExited() {
        var result = UnixPtyInterop.waitpid(Pid, out var status, UnixPtyInterop.WNOHANG);

        if (result == Pid) {
            HasExited = true;
            ExitCode  = (status >> 8) & 0xFF;
        }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        await _cts.CancelAsync();

        if (!HasExited) {
            await TerminateAsync();
        }

        UnixPtyInterop.close(_masterFd);
        _cts.Dispose();
    }
}

public class UnixPtyProcessFactory : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        )
        => UnixPtyProcess.Spawn(command, args, cwd, extraEnv, cols, rows);
}
