using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>An OpenAI-compatible chat-completions endpoint on loopback, scripted one reply per
/// request. Records every request body verbatim, so the tool surface, the system prompt and the
/// model id a test asserts on are read off the wire Pi actually sent — not off a model's account of
/// itself.</summary>
internal sealed class PiScriptedProvider : IDisposable {
    readonly WireMockServer                  _server = WireMockServer.Start();
    readonly ConcurrentQueue<PiScriptedStep> _script  = new();
    readonly List<JsonElement>               _requests = [];
    readonly Lock                            _gate = new();

    public PiScriptedProvider() {
        _server
            .Given(Request.Create().WithPath(p => p.EndsWith("/chat/completions", StringComparison.Ordinal)).UsingPost())
            .RespondWith(Response.Create().WithCallback(request => {
                using var doc = JsonDocument.Parse(request.Body ?? "{}");
                lock (_gate) _requests.Add(doc.RootElement.Clone());

                var step  = _script.TryDequeue(out var next) ? next : new PiScriptedStep(Text: "done");
                var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "?" : "?";

                return new WireMock.ResponseMessage {
                    StatusCode = 200,
                    Headers    = new Dictionary<string, WireMock.Types.WireMockList<string>> {
                        ["Content-Type"] = new("text/event-stream")
                    },
                    BodyData = new WireMock.Util.BodyData {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString     = Sse(model, step)
                    }
                };
            }));
    }

    public string BaseUrl => $"{_server.Url}/v1";

    public IReadOnlyList<JsonElement> Requests { get { lock (_gate) return [.. _requests]; } }

    public void Script(params PiScriptedStep[] steps) { foreach (var s in steps) _script.Enqueue(s); }

    // Hand-built via JsonObject rather than string interpolation: an OpenAI streaming chunk nests
    // enough literal braces around each interpolated hole that a raw interpolated string needs an
    // unreadable dollar-sign count to disambiguate them (mirrors PiRpc's own command builders).
    static string Sse(string model, PiScriptedStep step) {
        JsonNode delta;
        string   finish;

        if (step.Tool is null) {
            delta  = new JsonObject { ["role"] = "assistant", ["content"] = step.Text ?? "done" };
            finish = "stop";
        } else {
            delta = new JsonObject {
                ["role"]    = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(new JsonObject {
                    ["index"]    = 0,
                    ["id"]       = step.Id,
                    ["type"]     = "function",
                    ["function"] = new JsonObject {
                        ["name"]      = step.Tool,
                        ["arguments"] = step.ArgsJson ?? "{}",
                    },
                }),
            };
            finish = "tool_calls";
        }

        JsonObject Base() => new() { ["id"] = "probe", ["object"] = "chat.completion.chunk", ["created"] = 0, ["model"] = model };

        var first = Base();
        first["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = null });

        var last = Base();
        last["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = finish });

        var usage = Base();
        usage["choices"] = new JsonArray();
        usage["usage"]   = new JsonObject { ["prompt_tokens"] = 1, ["completion_tokens"] = 1, ["total_tokens"] = 2 };

        return $"data: {first.ToJsonString()}\n\ndata: {last.ToJsonString()}\n\ndata: {usage.ToJsonString()}\n\ndata: [DONE]\n\n";
    }

    public void Dispose() => _server.Stop();
}
