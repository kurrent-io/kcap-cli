using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The stdio JSON-RPC loop every kcap MCP server runs. Each <c>tools/call</c> runs as its own task, so a long
/// poll never holds up a <c>ping</c> or another call; every other method is answered in order. Only the writer
/// is serialised, one response per line. A handler shared across calls must therefore be safe to run
/// concurrently, or give calls an <c>orderKey</c>: calls that share a key run one at a time in arrival
/// order, still without holding up anything else.
/// </summary>
static class McpStdioLoop {
    /// <param name="tools">The tools this server publishes. A call to one of them that passes an argument its schema
    /// does not declare is refused here, before <paramref name="callTool"/> runs.</param>
    /// <param name="callTool">Answers a <c>tools/call</c>; expected to turn its own failures into tool errors.</param>
    /// <param name="answer">Answers any other request that carries an id, given its method (null when absent or not a string).</param>
    /// <param name="orderKey">The key a <c>tools/call</c> is ordered under, or null for one that runs alongside everything.</param>
    public static async Task RunAsync(
            TextReader reader,
            TextWriter writer,
            IReadOnlyList<McpTool> tools,
            Func<JsonNode, JsonObject, Task<string>> callTool,
            Func<JsonNode, string?, JsonObject, string> answer,
            string serverName,
            Func<JsonObject, string?>? orderKey = null) {
        using var writeGate = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();
        // The last call queued under each key; read and written only by this loop.
        var tails = new Dictionary<string, Task>(StringComparer.Ordinal);

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
                response = McpToolArguments.UndeclaredArguments(tools, request) is { } refusal
                    ? ToolError(id, refusal)
                    : await callTool(id, request);
            } catch (Exception ex) {
                // Without an answer the client waits on this id forever.
                await LogAsync($"kcap mcp {serverName}: unexpected error handling tools/call: {ex}");
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

                // Notifications have no id and get no response.
                if (TryParseRequest(line) is not { } request || request["id"] is not { } id) continue;

                var method = request["method"] is JsonValue value && value.TryGetValue(out string? m) ? m : null;
                if (method == "tools/call") {
                    inFlight.RemoveAll(t => t.IsCompletedSuccessfully);
                    foreach (var done in tails.Where(t => t.Value.IsCompleted).Select(t => t.Key).ToList()) tails.Remove(done);

                    if (!TryOrderKey(orderKey, request, out var key, out var failure)) {
                        await LogAsync($"kcap mcp {serverName}: refused a tools/call whose order key could not be read: {failure!.Message}");
                        await WriteAsync(Error(id, -32600, "Invalid request"));
                        continue;
                    }

                    Task call;
                    if (key is not null) {
                        var prior = tails.GetValueOrDefault(key) ?? Task.CompletedTask;
                        call       = Task.Run(() => CallAfterAsync(prior, id, request));
                        tails[key] = call;
                    } else {
                        call = Task.Run(() => CallAsync(id, request));
                    }
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

    /// <summary>Null for a line the loop must not act on. A JsonObject fills its property table
    /// lazily, so a duplicated key surfaces on the first read rather than in Parse; forcing it here
    /// keeps that inside the guard instead of ending the server.</summary>
    internal static JsonObject? TryParseRequest(string line) {
        try {
            var request = JsonNode.Parse(line)?.AsObject();
            _ = request?.Count;

            return request;
        } catch {
            return null;
        }
    }

    /// <summary>Reads the call's order key, null when it has none. False when reading fails: a malformed nested
    /// property throws on its first read, and such a call can be placed in no order, so it is refused rather
    /// than run alongside the call it should wait for.</summary>
    static bool TryOrderKey(Func<JsonObject, string?>? orderKey, JsonObject request, out string? key, out Exception? failure) {
        try {
            key     = orderKey?.Invoke(request);
            failure = null;
            return true;
        } catch (Exception ex) {
            key     = null;
            failure = ex;
            return false;
        }
    }

    /// <summary>A diagnostic on stderr. Losing it never stops the server: a broken stderr is not a protocol fault.</summary>
    static async Task LogAsync(string line) {
        try {
            await Console.Error.WriteLineAsync(line);
        } catch {
            // stderr is gone; the JSON-RPC answer still goes out on stdout.
        }
    }

    /// <summary>An <c>orderKey</c> that runs every call in arrival order.</summary>
    public static string? InArrivalOrder(JsonObject request) => "";

    static string ToolError(JsonNode id, string text) => new JsonObject {
        ["jsonrpc"] = "2.0",
        ["id"]      = id.DeepClone(),
        ["result"]  = new JsonObject {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = true,
        },
    }.ToJsonString();

    static string Error(JsonNode id, int code, string message) => new JsonObject {
        ["jsonrpc"] = "2.0",
        ["id"]      = id.DeepClone(),
        ["error"]   = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();
}
