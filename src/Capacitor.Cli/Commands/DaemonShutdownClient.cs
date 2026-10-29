using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>Sends one shutdown request over a daemon's control socket.</summary>
static class DaemonShutdownClient {
    static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The daemon's reply, or null when it closed the socket without one. An unreachable socket
    /// throws <see cref="SocketException"/> or <see cref="IOException"/>.</summary>
    public static async Task<LocalFrame?> RequestAsync(
            DaemonStore store, string name, ShutdownRequestDto request, CancellationToken ct) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(name)), ct);

        await using var stream = new NetworkStream(sock, ownsSocket: false);
        await FrameCodec.WriteAsync(stream, LocalFrame.Shutdown(request), ct);

        return await FrameCodec.ReadAsync(stream, ct);
    }

    /// <summary>True only when the daemon acknowledged the request and the process its PID file names
    /// was gone within <paramref name="exitWait"/>.</summary>
    public static async Task<bool> StopAndWaitAsync(
            DaemonStore store, string name, ShutdownRequestDto request, TimeSpan exitWait, TimeProvider time,
            CancellationToken ct) =>
        await StopAsync(store, name, request, exitWait, time, ct) == ShutdownOutcome.Exited;

    /// <summary>Sends the request and waits for the process its PID file names to exit;
    /// <paramref name="exitWait"/> bounds the request and the wait together.</summary>
    public static async Task<ShutdownOutcome> StopAsync(
            DaemonStore store, string name, ShutdownRequestDto request, TimeSpan exitWait, TimeProvider time,
            CancellationToken ct) {
        if (DaemonPidProbe.ReadPidFile(store, name) is not { } entry) return ShutdownOutcome.NoAnswer;

        using var deadline = new CancellationTokenSource(exitWait, time);
        using var bound    = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        LocalFrame? reply;
        try {
            reply = await RequestAsync(store, name, request, bound.Token);
        } catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
            return ShutdownOutcome.NoAnswer;
        }

        if (reply is { Type: FrameType.Error, Text: { } error } && error.StartsWith("busy:", StringComparison.Ordinal))
            return ShutdownOutcome.Busy;
        if (reply is null or { Type: FrameType.Error }) return ShutdownOutcome.Refused;
        if (reply is not { Type: FrameType.ShutdownAck, Text: "stopping" or "queued" }) return ShutdownOutcome.NoAnswer;

        try {
            while (DaemonPidProbe.IsOurDaemon(entry.Pid, entry.StartToken))
                await Task.Delay(ExitPollInterval, time, bound.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return ShutdownOutcome.Overran;
        }

        return ShutdownOutcome.Exited;
    }
}
