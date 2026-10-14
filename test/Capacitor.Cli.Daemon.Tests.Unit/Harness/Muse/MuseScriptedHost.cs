using System.Globalization;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

/// <summary>
/// A <see cref="FakeMuseMspProcess"/> that answers the runtime's requests the way a healthy
/// <c>muse serve</c> does, and lets a test await a specific write instead of sleeping.
/// </summary>
internal sealed class MuseScriptedHost {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _syncId = 9000;

    public MuseScriptedHost() => Process.OnWrite = OnWrite;

    public FakeMuseMspProcess Process { get; } = new();

    /// <summary>Returns the whole line to push for a request, or null to fall through to the default answer.</summary>
    public Func<JsonObject, string?>? Override { get; set; }

    /// <summary>Methods left unanswered.</summary>
    public HashSet<string> Silent { get; } = [];

    public IReadOnlyList<JsonObject> WritesOf(string method) =>
        Process.Writes.Select(w => JsonNode.Parse(w)!.AsObject())
            .Where(o => (string?)o["method"] == method)
            .ToList();

    public async Task<JsonObject> WaitForWriteAsync(string method, int occurrence = 1) {
        using var cts = new CancellationTokenSource(HangGuard);

        while (true) {
            var signal  = Volatile.Read(ref _written);
            var matches = WritesOf(method);
            if (matches.Count >= occurrence) return matches[occurrence - 1];

            await signal.Task.WaitAsync(cts.Token);
        }
    }

    /// <summary>Proves the runtime's pump has consumed every line pushed before this call: a server
    /// request goes on stdout and its receipt is awaited on stdin.</summary>
    public async Task SyncAsync() {
        var id = Interlocked.Increment(ref _syncId);
        Process.Push("""{"jsonrpc":"2.0","id":@0@,"method":"approval/request","params":{}}""".Replace("@0@", id.ToString(CultureInfo.InvariantCulture)));

        using var cts = new CancellationTokenSource(HangGuard);

        while (true) {
            var signal = Volatile.Read(ref _written);
            if (Process.Writes.Any(w => IsReceipt(w, id))) return;

            await signal.Task.WaitAsync(cts.Token);
        }
    }

    static bool IsReceipt(string line, long id) {
        var o = JsonNode.Parse(line)!.AsObject();
        return o["method"] is null && o["id"] is { } i && (long)i == id && o["result"] is JsonObject;
    }

    void OnWrite(string json) {
        var request = JsonNode.Parse(json)!.AsObject();

        if (request["method"] is { } m && request["id"] is { } id && !Silent.Contains((string)m!)) {
            var line = Override?.Invoke(request) ?? DefaultAnswer((string)m!, (long)id, request["params"]!.AsObject());
            if (line is not null) Process.Push(line);
        }

        Interlocked.Exchange(ref _written, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    static string? DefaultAnswer(string method, long id, JsonObject p) {
        var commandId = (string?)p["commandId"];

        string? result = method switch {
            "session/start" or "session/resume" =>
                MuseMspWire.SessionStartResult.Replace(MuseMspWire.WireSessionId, (string)p["sessionId"]!),
            "turn/start" =>
                """{"commandId":"@0@","status":"accepted","turnId":"@0@","startedNewTurn":true,"disposition":"started"}""".Replace("@0@", commandId),
            "turn/steer" =>
                """{"commandId":"@0@","status":"accepted","turnId":"@1@"}""".Replace("@0@", commandId).Replace("@1@", (string)p["expectedTurnId"]!),
            "initialize" => null,
            _ => """{"commandId":"@0@","status":"accepted"}""".Replace("@0@", commandId),
        };

        return result is null ? null : """{"jsonrpc":"2.0","id":@0@,"result":@1@}""".Replace("@0@", id.ToString(CultureInfo.InvariantCulture)).Replace("@1@", result);
    }

    public static string ErrorResponse(long id, string message) =>
        """{"jsonrpc":"2.0","id":@0@,"error":{"code":-32030,"message":"@1@","data":{"kind":"commandRejected","retryable":false}}}""".Replace("@0@", id.ToString(CultureInfo.InvariantCulture)).Replace("@1@", message);
}
