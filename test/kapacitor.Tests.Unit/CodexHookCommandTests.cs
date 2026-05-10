using System.Text.Json;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHookCommandTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SessionStart_posts_to_session_start_codex_with_normalized_session_id() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "SessionStart",
              "session_id": "019e0322-05fc-7570-be65-75719c3ea861",
              "transcript_path": "/tmp/rollout.jsonl",
              "cwd": "/tmp",
              "model": "gpt-5"
            }
            """;

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
        await Assert.That(root.GetProperty("home_dir").GetString()).IsNotNull();
    }

    [Test]
    public async Task Stop_maps_to_session_end_codex_route() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "Stop",
              "session_id": "abc",
              "transcript_path": "/tmp/rollout.jsonl",
              "cwd": "/tmp"
            }
            """;

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        // Confirm Stop is NOT posted to /hooks/stop or /hooks/codex/stop —
        // this guards the URL-mapping decision against future regressions.
        var wrong1 = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
        var wrong2 = _server.FindLogEntries(Request.Create().WithPath("/hooks/codex/stop").UsingPost());
        await Assert.That(wrong1.Count).IsEqualTo(0);
        await Assert.That(wrong2.Count).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(PermissionRequest_returns_default_allow_decision))]
    public async Task PermissionRequest_returns_default_allow_decision() {
        _server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
            {
              "hook_event_name": "PermissionRequest",
              "session_id": "abc",
              "transcript_path": "/tmp/r.jsonl",
              "cwd": "/tmp",
              "tool_name": "shell",
              "tool_input": { "command": "ls" }
            }
            """;

        var originalOut = Console.Out;
        var stdoutWriter = new StringWriter();
        try {
            Console.SetOut(stdoutWriter);

            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

            await Assert.That(exit).IsEqualTo(0);
            var stdout = stdoutWriter.ToString();
            var doc    = JsonDocument.Parse(stdout);
            await Assert.That(doc.RootElement
                .GetProperty("hookSpecificOutput")
                .GetProperty("decision")
                .GetProperty("behavior")
                .GetString())
                .IsEqualTo("allow");
        } finally {
            Console.SetOut(originalOut);
        }
    }

    [Test]
    public async Task UserPromptSubmit_PreToolUse_PostToolUse_are_swallowed() {
        // v1: pass-through events not consumed server-side. CLI should
        // exit 0 without making any HTTP request.
        foreach (var evt in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse" }) {
            var payload = $$"""
                {
                  "hook_event_name": "{{evt}}",
                  "session_id": "abc",
                  "transcript_path": "/tmp/r.jsonl",
                  "cwd": "/tmp"
                }
                """;

            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);
        }

        // No HTTP requests should have been issued.
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_event_returns_zero_and_no_request() {
        var payload = """{"hook_event_name": "BogusEvent", "session_id": "abc"}""";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_hook_event_name_returns_zero_silently() {
        var payload = """{"session_id": "abc"}""";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_json_returns_zero_silently() {
        var payload = "{not json";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
    }
}
