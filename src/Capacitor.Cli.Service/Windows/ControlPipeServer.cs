using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Capacitor.Cli.Service.Control;
using Capacitor.Cli.Service.Logging;

namespace Capacitor.Cli.Service.Windows;

/// <summary>
/// <c>\\.\pipe\kcap-service</c>: any signed-in user may connect, and each connection acts only on its
/// caller's own daemon and local server, identified by impersonating the pipe client
/// </summary>
[SupportedOSPlatform("windows")]
sealed class ControlPipeServer(ControlDispatcher dispatcher, TimeProvider time, IServiceLog log) : IDisposable {
    const int MaxRequestChars = 4096;

    /// <summary>A client writes its one line as soon as it connects; one that does not is let go, so idle
    /// connections cannot pile up</summary>
    static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(10);

    readonly CancellationTokenSource _stop = new();

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Stop() => _stop.Cancel();

    static PipeSecurity Security() {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    async Task AcceptLoopAsync() {
        while (!_stop.IsCancellationRequested) {
            NamedPipeServerStream pipe;
            try {
                pipe = NamedPipeServerStreamAcl.Create(ServiceIdentity.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            } catch (IOException ex) {
                log.Write($"pipe: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), time).ConfigureAwait(false);
                continue;
            }

            try {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe) {
        await using (pipe.ConfigureAwait(false)) {
            try {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true);
                writer.AutoFlush = true;

                using var wait = new CancellationTokenSource(RequestWait, time);
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, _stop.Token);
                var buffer = new char[MaxRequestChars];
                var line = await ReadLineAsync(reader, buffer, bound.Token).ConfigureAwait(false);
                if (line is null) return;

                string? sid = null;
                pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
                if (sid is null) return;

                await writer.WriteLineAsync(await dispatcher.HandleAsync(sid, line, _stop.Token).ConfigureAwait(false)).ConfigureAwait(false);
            } catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) {
                log.Write($"pipe serve: {ex.Message}");
            }
        }
    }

    /// <summary>One line, refused past the cap rather than buffered without bound</summary>
    static async Task<string?> ReadLineAsync(StreamReader reader, char[] buffer, CancellationToken ct) {
        var length = 0;
        while (length < buffer.Length) {
            var read = await reader.ReadAsync(buffer.AsMemory(length, 1), ct).ConfigureAwait(false);
            if (read == 0) return length == 0 ? null : new string(buffer, 0, length);
            if (buffer[length] == '\n') return new string(buffer, 0, length).TrimEnd('\r');
            length++;
        }
        return null;
    }

    public void Dispose() => _stop.Dispose();
}
