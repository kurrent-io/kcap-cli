using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Local;

/// <summary>
/// The dumb-pipe end of the local control socket: connects, sends an opening frame
/// (Spawn or Attach), puts the terminal in raw mode, and pumps bytes both ways until the
/// agent exits or the user detaches. Returns the agent's exit code (or 0 on detach).
/// </summary>
internal static class LocalAgentClient {
    static readonly TimeSpan ResizePollGap = TimeSpan.FromMilliseconds(300);

    public static Task<int> RunAsync(string socketPath, LocalFrame opening, TimeProvider time, CancellationToken outerCt) =>
        RunAsync(socketPath, opening, time, ConsoleAgentTerminal.Instance, reconnect: null, outerCt);

    /// <param name="reconnect">How to ride out a dropped connection; null ends the session on the first drop.</param>
    public static async Task<int> RunAsync(
            string socketPath, LocalFrame opening, TimeProvider time, IAgentTerminal terminal,
            AttachReconnect? reconnect, CancellationToken outerCt) {
        NetworkStream? first = null;

        try {
            first = await ConnectAsync(socketPath, outerCt);
        } catch (Exception ex) when (ex is SocketException or IOException) {
            // A ledgered agent's daemon may be restarting: the reconnect window covers the opening connection too.
            if (reconnect?.Ledger() is null) {
                await Console.Error.WriteLineAsync($"kcap: cannot connect to daemon at {socketPath}: {ex.Message}");

                return 1;
            }
        }

        using var cts     = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var       ct      = cts.Token;
        var       current = (NetworkStream?)first;

        if (first is not null) await FrameCodec.WriteAsync(first, opening, ct);

        // Touch the size getter once BEFORE enabling raw mode, so .NET's lazy console init can't
        // re-cook the terminal after we set raw.
        _ = terminal.Size();

        using var raw         = terminal.EnterRawMode();
        var       writeLock   = new SemaphoreSlim(1, 1);
        var       exitCode    = 0;
        var       readOnly    = false; // daemon attached us to a protected agent; input is dropped
        var       attached    = false; // some connection got an Attached reply
        var       refusedWhileResuming = false; // the opening attach was refused before the resume published

        async Task Send(LocalFrame f) {
            await writeLock.WaitAsync(ct);

            try {
                if (Volatile.Read(ref current) is { } stream) await FrameCodec.WriteAsync(stream, f, ct);
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                // A dropped connection; the reader sees it too and decides what follows.
            } finally {
                writeLock.Release();
            }
        }

        async Task SwapConnectionAsync(NetworkStream? next) {
            NetworkStream? previous;

            await writeLock.WaitAsync(CancellationToken.None);
            try { previous = current; Volatile.Write(ref current, next); } finally { writeLock.Release(); }

            if (previous is not null && previous != next) await previous.DisposeAsync();
        }

        // True when the frame ends the session.
        async Task<bool> HandleAsync(LocalFrame f) {
            switch (f.Type) {
                case FrameType.Stdout:
                    terminal.Write(f.Bytes, f.Bytes.Length);

                    return false;
                case FrameType.Attached:
                    attached = true;
                    Volatile.Write(ref readOnly, false);
                    var (_, snapshot) = FrameCodec.Attached(f);
                    if (snapshot.Length > 0) terminal.Write(snapshot, snapshot.Length);

                    await Send(SizeFrame()); // nudge a clean repaint at our size

                    return false;
                case FrameType.AttachedReadOnly:
                    attached = true;
                    Volatile.Write(ref readOnly, true);
                    var (_, reason, roSnapshot) = FrameCodec.AttachedReadOnly(f);
                    if (roSnapshot.Length > 0) terminal.Write(roSnapshot, roSnapshot.Length);

                    var banner = "\r\n— read-only: " + reason + ".\r\n"
                               + "  Input is not delivered — address it with the flow tools."
                               + " Ctrl-Q d to detach. —\r\n";
                    var bannerBytes = System.Text.Encoding.UTF8.GetBytes(banner);
                    terminal.Write(bannerBytes, bannerBytes.Length);

                    // No SizeFrame nudge: the daemon ignores our size for a protected
                    // agent, so asking for a repaint at our dimensions would mislead.
                    return false;
                case FrameType.Exited:
                    exitCode = f.ExitCode;

                    return true;
                case FrameType.Error:
                    // Only this refusal means the daemon has not published the agent yet; any other is final.
                    if (!attached
                     && f.Text?.StartsWith(LocalFrame.NoSuchAgentPrefix, StringComparison.Ordinal) == true
                     && reconnect?.Ledger() is { State: not ResumeState.Interrupted }) {
                        refusedWhileResuming = true;

                        return true;
                    }

                    // A refused attach to an agent a restart left interrupted: its reason says more.
                    if (attached || reconnect is null || !await ReportInterruptedAsync(reconnect))
                        await Console.Error.WriteLineAsync($"\r\nkcap: {f.Text}");
                    exitCode = 1;

                    return true;
                default:
                    return false;
            }
        }

        // True when the connection dropped rather than ending the session.
        async Task<bool> PumpAsync(NetworkStream stream) {
            try {
                while (!ct.IsCancellationRequested) {
                    var f = await FrameCodec.ReadAsync(stream, ct);
                    if (f is null) return true;
                    if (await HandleAsync(f)) {
                        var refused = refusedWhileResuming;
                        refusedWhileResuming = false;

                        return refused;
                    }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or InvalidDataException) {
                /* connection closed */
            }

            return true;
        }

        // A connection on which the agent answered the attach, or null when this attempt failed.
        async Task<NetworkStream?> TryAttachAsync(TimeSpan budget) {
            using var limit  = new CancellationTokenSource(budget, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            NetworkStream? stream = null;

            try {
                stream = await ConnectAsync(socketPath, linked.Token);
                await FrameCodec.WriteAsync(stream, opening, linked.Token);

                if (await FrameCodec.ReadAsync(stream, linked.Token) is { Type: FrameType.Attached or FrameType.AttachedReadOnly } reply) {
                    await SwapConnectionAsync(stream);
                    await HandleAsync(reply);

                    return stream;
                }
            } catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or InvalidDataException
                                          || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
                /* not back yet */
            }

            if (stream is not null) await stream.DisposeAsync();

            return null;
        }

        async Task<bool> ReportInterruptedAsync(AttachReconnect r) {
            if (r.Ledger() is not { State: ResumeState.Interrupted } entry) return false;

            await Console.Error.WriteLineAsync(
                $"\r\nkcap: {r.AgentId} was interrupted ({entry.LastError ?? "no reason recorded"}). Retry with `kcap agent resume {r.AgentId}`.");

            return true;
        }

        // The resumed agent's connection, or null after reporting why it will not come back.
        async Task<NetworkStream?> ReconnectAsync(AttachReconnect r) {
            var deadline = time.GetUtcNow() + r.Window;

            while (true) {
                if (r.Ledger() is null) {
                    await Console.Error.WriteLineAsync($"\r\nkcap: {r.AgentId} was not resumed — see `kcap agent ls`.");

                    return null;
                }

                if (await ReportInterruptedAsync(r)) return null;

                var remaining = deadline - time.GetUtcNow();

                if (remaining <= TimeSpan.Zero) {
                    await Console.Error.WriteLineAsync("\r\nkcap: lost connection to the daemon");

                    return null;
                }

                if (await TryAttachAsync(remaining) is { } stream) return stream;

                await Task.Delay(r.RetryGap, time, ct);
            }
        }

        // SIGWINCH isn't in .NET's PosixSignal enum, so poll the window size and send a
        // Resize frame when it changes.
        _ = Task.Run(async () => {
            var last = terminal.Size();
            try {
                while (!ct.IsCancellationRequested) {
                    await Task.Delay(ResizePollGap, time, ct);
                    var cur = terminal.Size();
                    if (cur != last) { last = cur; if (!Volatile.Read(ref readOnly)) await Send(SizeFrame()); }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException) {
                /* shutting down */
            }
        }, ct);

        // client → daemon, with detach-sequence interception. read() is blocking and not
        // cancellable; on detach we break, and on agent exit the process exits (the loop below
        // returns), abandoning this thread — which is fine. It outlives a reconnect, so input
        // typed while the daemon restarts is dropped rather than read by a stale pump.
        var stdinPump = Task.Run(async () => {
            var scanner = new DetachScanner();
            var buf     = new byte[4096];
            try {
                while (!ct.IsCancellationRequested) {
                    var n = terminal.Read(buf);
                    if (n <= 0) break;

                    var (forward, detach) = scanner.Process(buf.AsSpan(0, n));
                    if (forward.Length > 0 && !Volatile.Read(ref readOnly)) await Send(LocalFrame.Stdin(forward));
                    if (detach) { await Send(LocalFrame.Detach()); break; }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException) {
                /* shutting down */
            }
        }, ct);

        var connection = first;
        var announced  = false;

        // A null connection is an opening connect the daemon refused: the loop starts at the reconnect.
        while (true) {
            if (connection is not null) {
                var pump = PumpAsync(connection);
                if (await Task.WhenAny(pump, stdinPump) == stdinPump) break;

                var dropped = await pump;
                await SwapConnectionAsync(null);
                if (!dropped) break;
            }

            exitCode = 1;

            if (reconnect is null || reconnect.Ledger() is null) {
                await Console.Error.WriteLineAsync("\r\nkcap: lost connection to the daemon");

                break;
            }

            if (await ReportInterruptedAsync(reconnect)) break;

            if (!announced) {
                announced = true;
                await Console.Error.WriteLineAsync($"\r\ndaemon restarting; waiting for {reconnect.AgentId} to resume…");
            }

            var attempt = ReconnectAsync(reconnect);
            if (await Task.WhenAny(attempt, stdinPump) == stdinPump) { exitCode = 0; break; }
            if (await attempt is not { } next) break;

            exitCode   = 0;
            connection = next;
        }

        await cts.CancelAsync();
        await SwapConnectionAsync(null);

        // raw mode is restored by `using raw` on return.
        return exitCode;

        LocalFrame SizeFrame() { var (c, r) = terminal.Size(); return LocalFrame.Resize(c, r); }
    }

    static async Task<NetworkStream> ConnectAsync(string socketPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);

            return new NetworkStream(sock, ownsSocket: true);
        } catch {
            sock.Dispose();
            throw;
        }
    }
}
