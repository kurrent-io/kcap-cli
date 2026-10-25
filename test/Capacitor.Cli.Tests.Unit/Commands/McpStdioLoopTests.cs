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
        McpStdioLoop.RunAsync(new StringReader(input), output, [], callTool, (id, method, _) => Answer(id, method), "test");

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
    public async Task Calls_in_arrival_order_run_one_at_a_time_while_a_ping_is_still_answered() {
        var release     = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started     = new ConcurrentQueue<string>();
        using var output = new Responses();
        var input = string.Join('\n', Call(1, "slow"), Call(2, "fast"), Request("3", "\"ping\""));

        var loop = McpStdioLoop.RunAsync(new StringReader(input), output, [], (id, request) => {
            started.Enqueue(Tool(request));
            if (Tool(request) != "slow") return Task.FromResult(Answer(id, "tools/call"));

            slowStarted.SetResult();
            return release.Task;
        }, (id, method, _) => Answer(id, method), "test", McpStdioLoop.InArrivalOrder);
        await slowStarted.Task;
        await output.WaitForAsync(1);

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3");
        await Assert.That(string.Join(",", started)).IsEqualTo("slow");

        release.SetResult(Answer(1, "tools/call"));
        await loop;

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3,1,2");
    }

    [Test]
    public async Task Calls_sharing_a_key_run_in_arrival_order_while_other_keys_run_alongside() {
        var release     = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeld   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started     = new ConcurrentQueue<string>();
        using var output = new Responses();
        var input = string.Join('\n', Call(1, "a-first"), Call(2, "a-second"), Call(3, "b"));

        var loop = McpStdioLoop.RunAsync(new StringReader(input), output, [], (id, request) => {
            started.Enqueue(Tool(request));
            if (Tool(request) != "a-first") return Task.FromResult(Answer(id, "tools/call"));

            firstHeld.SetResult();
            return release.Task;
        }, (id, method, _) => Answer(id, method), "test", request => Tool(request)[..1]);
        await firstHeld.Task;
        await output.WaitForAsync(1);

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3");
        await Assert.That(started).DoesNotContain("a-second");

        release.SetResult(Answer(1, "tools/call"));
        await loop;

        await Assert.That(string.Join(",", output.Ids)).IsEqualTo("3,1,2");
    }

    /// <summary>A call whose order key cannot be read cannot be placed in order, so it is refused as an invalid
    /// request rather than run alongside the call it should have waited for.</summary>
    [Test]
    public async Task A_call_whose_order_key_cannot_be_read_is_refused_and_the_loop_serves_on() {
        using var output = new Responses();
        var ran = 0;
        // A duplicated key nested in arguments throws when the order key first reads it.
        var input = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"x","arguments":{"a":1,"a":2}}}""",
            Request("2", "\"ping\""));

        await McpStdioLoop.RunAsync(new StringReader(input), output, [], (id, _) => {
                Interlocked.Increment(ref ran);
                return Task.FromResult(Answer(id, "tools/call"));
            },
            (id, method, _) => Answer(id, method), "test",
            request => ((request["params"] as JsonObject)?["arguments"] as JsonObject)?["a"]?.ToJsonString());

        var refused = output.All.Single(r => r["id"]!.ToJsonString() == "1");
        await Assert.That(refused["error"]!["code"]!.GetValue<int>()).IsEqualTo(-32600);
        await Assert.That(ran).IsEqualTo(0);
        await Assert.That(output.Ids).Contains("2");
    }

    static readonly McpTool[] Filtered = [
        new("list", "Lists.", new("object", new() { ["repo"] = new("string", "A repo."), ["limit"] = new("integer", "A cap.") }, []),
            McpToolAnnotations.Read),
    ];

    static string CallWith(int id, string tool, string arguments) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";

    /// <summary>An argument the tool does not declare would be dropped, and a dropped filter reads as an applied one, so
    /// the call is refused before the tool runs, naming what it was passed and what it takes. A declared-only call, and
    /// a call to a tool the list does not hold, reach the server as before.</summary>
    [Test]
    public async Task A_call_passing_an_argument_its_tool_does_not_declare_is_refused_before_it_runs() {
        using var output = new Responses();
        var ran = new ConcurrentBag<string>();
        var input = string.Join('\n',
            CallWith(1, "list", """{"repo":"a/b","from":"2026-01-01","to":"2026-02-01"}"""),
            CallWith(2, "list", """{"repo":"a/b","limit":5}"""),
            CallWith(3, "other", """{"anything":1}"""));

        await McpStdioLoop.RunAsync(new StringReader(input), output, Filtered, (id, _) => {
                ran.Add(id.ToJsonString());
                return Task.FromResult(Answer(id, "tools/call"));
            },
            (id, method, _) => Answer(id, method), "test");

        var refused = output.All.Single(r => r["id"]!.ToJsonString() == "1")["result"]!;
        await Assert.That(refused["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(refused["content"]![0]!["text"]!.GetValue<string>())
            .IsEqualTo("list does not take from, to: it takes repo, limit. Nothing was done.");
        await Assert.That(ran).IsEquivalentTo(new[] { "2", "3" });
    }

    /// <summary>A duplicated key cannot be read as a set of names, so the check leaves the call to the tool's own
    /// handler rather than answering it as an internal error.</summary>
    [Test]
    public async Task A_call_whose_arguments_repeat_a_key_still_reaches_its_tool() {
        using var output = new Responses();
        var ran = 0;

        await McpStdioLoop.RunAsync(new StringReader(CallWith(1, "list", """{"repo":"a","repo":"b"}""")), output, Filtered,
            (id, _) => {
                Interlocked.Increment(ref ran);
                return Task.FromResult(Answer(id, "tools/call"));
            },
            (id, method, _) => Answer(id, method), "test");

        await Assert.That(ran).IsEqualTo(1);
        await Assert.That(output.All.Single()["error"]).IsNull();
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

        var loop = McpStdioLoop.RunAsync(new FailingReader(Call(1, "slow")), output, [], (_, _) => release.Task,
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
