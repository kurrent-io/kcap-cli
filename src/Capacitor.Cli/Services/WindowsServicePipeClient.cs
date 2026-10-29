using System.IO.Pipes;
using System.Text.Json;

namespace Capacitor.Cli.Services;

sealed class WindowsServicePipeClient(TimeProvider time) : IServicePipe {
    public const string PipeName = "kcap-service";

    public ServicePipeResponse? Send(string verb, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout, time);
        try {
            return SendAsync(verb, cts.Token).GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    static async Task<ServicePipeResponse?> SendAsync(string verb, CancellationToken ct) {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct).ConfigureAwait(false);

        await using var writer = new StreamWriter(pipe, leaveOpen: true);
        writer.AutoFlush = true;
        using var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new ServicePipeRequest(verb), ServicePipeJsonContext.Default.ServicePipeRequest)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize(line, ServicePipeJsonContext.Default.ServicePipeResponse);
    }
}
