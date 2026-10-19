using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The stdio JSON-RPC loop every kcap MCP server runs. Each <c>tools/call</c> runs as its own task, so a long
/// poll never holds up a <c>ping</c> or another call; every other method is answered in order. Only the writer
/// is serialised, one response per line. A handler shared across calls must therefore be safe to run
/// concurrently, or ask for <c>serialCalls</c>: calls then run one at a time in arrival order, still
/// without holding up anything else.
/// </summary>
static class McpStdioLoop {
    /// <param name="callTool">Answers a <c>tools/call</c>; expected to turn its own failures into tool errors.</param>
    /// <param name="answer">Answers any other request that carries an id, given its method (null when absent or not a string).</param>
    public static async Task RunAsync(
            TextReader reader,
            TextWriter writer,
            Func<JsonNode, JsonObject, Task<string>> callTool,
            Func<JsonNode, string?, JsonObject, string> answer,
            string serverName,
            bool serialCalls = false) {
        using var writeGate = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();
        var previous = Task.CompletedTask;

        async Task WriteAsync(string response) {
            await writeGate.WaitAsync();
            try {
                await writer.WriteLineAsync(response);
            } finally {
                writeGate.Release();
            }
        }

        async Task CallAsync(JsonNode id, JsonObject request) {
            string response;
            try {
                response = await callTool(id, request);
            } catch (Exception ex) {
                // Without an answer the client waits on this id forever.
                await Console.Error.WriteLineAsync($"kcap mcp {serverName}: unexpected error handling tools/call: {ex}");
                response = Error(id, -32603, "Internal error");
            }
            await WriteAsync(response);
        }

        async Task CallAfterAsync(Task prior, JsonNode id, JsonObject request) {
            try {
                await prior;
            } catch {
                // The prior call's own failure is its own; this call still runs.
            }
            await CallAsync(id, request);
        }

        // However the loop ends, a call already running still answers, and nothing it uses is disposed under it.
        try {
            while (await reader.ReadLineAsync() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;
                try {
                    request = JsonNode.Parse(line) as JsonObject;
                } catch {
                    continue;
                }

                // Notifications have no id and get no response.
                if (request?["id"] is not { } id) continue;

                var method = request["method"] is JsonValue value && value.TryGetValue(out string? m) ? m : null;
                if (method == "tools/call") {
                    inFlight.RemoveAll(t => t.IsCompletedSuccessfully);
                    var prior = previous;
                    var call = serialCalls
                        ? Task.Run(() => CallAfterAsync(prior, id, request))
                        : Task.Run(() => CallAsync(id, request));
                    previous = call;
                    inFlight.Add(call);
                } else {
                    await WriteAsync(answer(id, method, request));
                }
            }
        } catch {
            try {
                await Task.WhenAll(inFlight);
            } catch {
                // The loop's own failure is the one to report.
            }
            throw;
        }

        await Task.WhenAll(inFlight);
    }

    static string Error(JsonNode id, int code, string message) => new JsonObject {
        ["jsonrpc"] = "2.0",
        ["id"]      = id.DeepClone(),
        ["error"]   = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();
}
