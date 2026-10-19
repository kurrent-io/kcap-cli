using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A <c>tools/call</c> runs alongside the rest of the protocol: a long call holds up neither a
/// later call nor a ping, and every request with an id is answered exactly once.</summary>
public class McpStdioLoopTests {
    /// Collects each written line as one response, safe to read while the loop writes.
    sealed class Responses : TextWriter {
        readonly ConcurrentQueue<JsonObject> _lines = new();
        readonly SemaphoreSlim _written = new(0);

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value) {
            _lines.Enqueue(JsonNode.Parse(value!)!.AsObject());
            _written.Release();
            return Task.CompletedTask;
        }

        public IReadOnlyList<JsonObject> All => [.. _lines];

        public IEnumerable<string> Ids => _lines.Select(l => l["id"]!.ToJsonString());

        public async Task WaitForAsync(int count) {
            while (_lines.Count < count)
                if (!await _written.WaitAsync(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException($"Expected {count} responses, saw {_lines.Count}.");
        }

        protected override void Dispose(bool disposing) {
            if (disposing) _written.Dispose();
            base.Dispose(disposing);
        }
    }

    static string Call(int id, string tool) => new JsonObject {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool },
    }.ToJsonString();

    static string Request(string id, string method) => $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":{{{method}}}}""";

    static string Tool(JsonObject request) => request["params"]!["name"]!.GetValue<string>();

    static string Answer(JsonNode id, string? method) =>
        new JsonObject { ["id"] = id.DeepClone(), ["result"] = new JsonObject { ["method"] = method } }.ToJsonString();

    static Task Run(string input, Responses output, Func<JsonNode, JsonObject, Task<string>> callTool) =>
        McpStdioLoop.RunAsync(new StringReader(input), output, callTool, (id, method, _) => Answer(id, method), "test");

    [Test]
    public async Task A_long_call_holds_up_neither_a_later_call_nor_a_ping() {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new Responses();
        var input = string.Join('\n', Call(1, "slow"), Call(2, "fast"), Request("3", "\"ping\""));

        var loop = Run(input, output, (id, request) =>
            Tool(request) == "slow" ? release.Task : Task.FromResult(Answer(id, "tools/call")));
        await output.WaitForAsync(2);

        await Assert.That(output.Ids).IsEquivalentTo(["2", "3"]);
        await Assert.That(loop.IsCompleted).IsFalse();

        release.SetResult(Answer(1, "tools/call"));
        await loop;

        await Assert.That(output.Ids.Last()).IsEqualTo("1");
    }

    [Test]
    public async Task Serial_calls_run_in_arrival_order_while_a_ping_is_still_answered() {
        var release     = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started     = new ConcurrentQueue<string>();
        using var output = new Responses();
        var input = string.Join('\n', Call(1, "slow"), Call(2, "fast"), Request("3", "\"ping\""));

        var loop = McpStdioLoop.RunAsync(new StringReader(input), output, (id, request) => {
            started.Enqueue(Tool(request));
            if (Tool(request) != "slow") return Task.FromResult(Answer(id, "tools/call"));

            slowStarted.SetResult();
            return release.Task;
        }, (id, method, _) => Answer(id, method), "test", serialCalls: true);
        await slowStarted.Task;
        await output.WaitForAsync(1);

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3");
        await Assert.That(string.Join(",", started)).IsEqualTo("slow");

        release.SetResult(Answer(1, "tools/call"));
        await loop;

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3,1,2");
    }

    /// Yields its lines, then fails the next read as a broken stdin would.
    sealed class FailingReader(params string[] lines) : TextReader {
        int _next;

        public override Task<string?> ReadLineAsync() =>
            _next < lines.Length ? Task.FromResult<string?>(lines[_next++]) : Task.FromException<string?>(new IOException("stdin broke"));
    }

    [Test]
    public async Task A_failing_read_still_waits_for_the_call_in_flight_before_failing() {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new Responses();

        var loop = McpStdioLoop.RunAsync(new FailingReader(Call(1, "slow")), output, (_, _) => release.Task,
            (id, method, _) => Answer(id, method), "test");
        await Task.Delay(50);

        await Assert.That(loop.IsCompleted).IsFalse();

        release.SetResult(Answer(1, "tools/call"));

        await Assert.That(async () => await loop).Throws<IOException>();
        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("1");
    }

    [Test]
    public async Task A_call_that_throws_is_answered_with_an_internal_error_and_the_loop_serves_on() {
        using var output = new Responses();
        var input = string.Join('\n', Call(1, "broken"), Request("2", "\"ping\""));

        await Run(input, output, (_, _) => throw new InvalidOperationException("boom"));

        var error = output.All.Single(r => r["id"]!.ToJsonString() == "1");
        await Assert.That(error["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(error.ToJsonString()).DoesNotContain("boom");
        await Assert.That(output.Ids).Contains("2");
    }

    [Test]
    public async Task Notifications_and_unreadable_lines_get_no_answer_and_a_non_string_method_reads_as_none() {
        // A duplicated key surfaces on the first property read, not in Parse.
        using var output = new Responses();
        var input = string.Join('\n',
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "not json",
            "[1,2]",
            """{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""",
            "",
            Request("\"a\"", "42"));

        await Run(input, output, (_, _) => throw new InvalidOperationException("no call expected"));

        var only = output.All.Single();
        await Assert.That(only["id"]!.GetValue<string>()).IsEqualTo("a");
        await Assert.That(only["result"]!["method"]).IsNull();
    }
}
