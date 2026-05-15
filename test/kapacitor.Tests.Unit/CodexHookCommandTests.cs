using System.Text.Json;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHookCommandTests : IDisposable {
    // Shared NotInParallel group for every test that mutates the global
    // Console.Out. Without a single shared group, tests using SetOut would run
    // concurrently against the same process-wide writer and interfere with each
    // other's stdout capture (nondeterministic failures depending on schedule).
    const string ConsoleSerialGroup = nameof(CodexHookCommandTests) + ".Console";

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

    [Test, NotInParallel(ConsoleSerialGroup)]
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

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_records_event_and_yields_decision_to_codex() {
        // The Codex permission-request hook must not silently auto-allow tool
        // calls; it records the request server-side via /hooks/permission-record
        // (the same fire-and-forget endpoint Claude's terminal path uses) and
        // emits an empty hookSpecificOutput so Codex's normal in-CLI approval
        // prompt asks the user. Regression for AI-636.
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
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

        var originalOut  = Console.Out;
        var stdoutWriter = new StringWriter();
        try {
            Console.SetOut(stdoutWriter);

            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

            await Assert.That(exit).IsEqualTo(0);

            // stdout must NOT carry a hook decision — Codex falls back to its
            // own approval prompt when hookSpecificOutput.decision is absent.
            var stdout = stdoutWriter.ToString();
            var doc    = JsonDocument.Parse(stdout);
            var hasDecision = doc.RootElement.TryGetProperty("hookSpecificOutput", out var hso)
                && hso.TryGetProperty("decision", out _);
            await Assert.That(hasDecision).IsFalse();
        } finally {
            Console.SetOut(originalOut);
        }

        // Recording must land on /hooks/permission-record, not on the
        // long-poll /hooks/permission-request/{vendor} route.
        var recorded = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-record").UsingPost());
        await Assert.That(recorded.Count).IsEqualTo(1);

        var wrong = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost());
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    // AI-636: the hook must return well under Codex's 30 s timeout even when
    // the recording endpoint is slow. We cap the HTTP client at 2 s and
    // swallow the resulting TaskCanceledException — guard against future
    // regressions where someone reintroduces an unbounded await.
    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_returns_quickly_when_server_is_slow() {
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

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

        var originalOut  = Console.Out;
        var stdoutWriter = new StringWriter();
        var sw           = System.Diagnostics.Stopwatch.StartNew();
        try {
            Console.SetOut(stdoutWriter);
            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
            sw.Stop();
            await Assert.That(exit).IsEqualTo(0);
        } finally {
            Console.SetOut(originalOut);
        }

        // 2 s HTTP timeout + a generous slack for build/CI jitter.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
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

    [Test]
    [Arguments("""{"generate_whats_done":true}""",  true)]
    [Arguments("""{"generate_whats_done":false}""", false)]
    [Arguments("""{"other_field":"x"}""",           false)]
    [Arguments("""{"generate_whats_done":"yes"}""", false)] // wrong type — fall through
    [Arguments("not json",                          false)]
    [Arguments("",                                  false)]
    public async Task ShouldSpawnWhatsDone_ParsesGenerateFlag(string responseBody, bool expected) {
        var result = CodexHookCommand.ShouldSpawnWhatsDone(responseBody);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ShouldSpawnWhatsDone_NullBody_ReturnsFalse() {
        var result = CodexHookCommand.ShouldSpawnWhatsDone(null);
        await Assert.That(result).IsFalse();
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
