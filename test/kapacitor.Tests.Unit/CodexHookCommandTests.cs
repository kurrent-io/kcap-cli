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

        // AI-635: SessionStart also writes Codex's required JSON output to stdout.
        // We can't reliably capture stdout here because the test path spawns a
        // real watcher child process via Process.Start, which under TUnit's
        // Console capture corrupts subsequent stdout reads. The Stop test covers
        // the identical write pattern (Console.Write(SessionScopedOutputJson)),
        // and HandleSessionStart writes the same constant in the same way before
        // the watcher spawn.
    }

    [Test, NotInParallel(nameof(Stop_maps_to_session_end_codex_route))]
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

        var originalOut  = Console.Out;
        var stdoutWriter = new StringWriter();
        try {
            Console.SetOut(stdoutWriter);

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

            // AI-635: Stop must emit a valid JSON object on stdout so Codex's
            // hook output parser doesn't reject it as "invalid stop hook JSON
            // output". WatcherManager chatter must not leak onto the same channel.
            var stdout = stdoutWriter.ToString();
            var doc    = JsonDocument.Parse(stdout);
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
            await Assert.That(stdout.Contains("Watcher ")).IsFalse();
            await Assert.That(stdout.Contains("Inline drain")).IsFalse();
            await Assert.That(stdout.Contains("Spawned")).IsFalse();
        } finally {
            Console.SetOut(originalOut);
        }
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

    // Fix #3: non-string hook_event_name (e.g. a number) must not crash — return 0 silently.
    [Test]
    public async Task Hook_event_name_as_number_returns_zero_without_crash() {
        var payload = """{"hook_event_name": 99, "session_id": "abc", "transcript_path": "/tmp/r.jsonl"}""";

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count()).IsEqualTo(0);
    }

    // Fix #3: non-string session_id in a Stop payload must not crash.
    [Test]
    public async Task Stop_with_numeric_session_id_returns_zero_without_crash() {
        var payload = """{"hook_event_name": "Stop", "session_id": 12345, "transcript_path": "/tmp/r.jsonl"}""";

        // No server stub needed — session_id is null after safe extraction,
        // so KillWatcher is skipped and PostHookAsync is not called (no baseUrl stub).
        // But we DO need a stub for the post so it doesn't fail due to unreachable.
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
    }
}
