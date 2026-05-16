using System.Text.Json;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHookCommandTests : IDisposable {
    // Shared NotInParallel group for every test that mutates global state —
    // Console.Out and the KAPACITOR_DAEMON_URL env var. Without a single
    // shared group, parallel tests against the same process-wide writer or
    // env var corrupt each other's assertions nondeterministically.
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

    // AI-648: Codex 'Stop' fires at turn end, not session end. The hook must
    // NOT POST /hooks/session-end/codex (that path is reserved for the
    // watcher's parent-exit fallback in WatchCommand.cs). It must still emit
    // {"continue":true} on stdout to satisfy Codex's stop-hook JSON parser
    // (AI-635 invariant) and must NOT leak WatcherManager chatter to stdout.
    //
    // Globally sequential: this test captures Console.Out for the duration.
    // NotInParallel with no group key forces it to run on its own to avoid
    // interleaving with other stdout-mutating tests under TUnit's scheduler.
    [Test, NotInParallel]
    public async Task Stop_is_turn_end_no_op_and_does_not_post_session_end() {
        // Stub the route so any (incorrect) POST is recorded — we assert zero.
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

            // Core invariant: Stop must NOT POST session-end.
            var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
            await Assert.That(endRequests.Count).IsEqualTo(0);

            // Defensive: also no other related routes.
            var wrong1 = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
            var wrong2 = _server.FindLogEntries(Request.Create().WithPath("/hooks/codex/stop").UsingPost());
            await Assert.That(wrong1.Count).IsEqualTo(0);
            await Assert.That(wrong2.Count).IsEqualTo(0);

            // AI-635 invariant: valid JSON object on stdout, no chatter.
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

    // Globally sequential alongside Stop_is_turn_end_no_op_and_does_not_post_session_end —
    // see that test for why ConsoleSerialGroup alone has been observed to
    // interleave in suite runs and drop captured Console output.
    [Test, NotInParallel]
    public async Task PermissionRequest_records_event_and_yields_decision_to_codex() {
        // The Codex permission-request hook must not silently auto-allow tool
        // calls; in the stub branch (no KAPACITOR_DAEMON_URL set) it records
        // the request server-side via /hooks/permission-record (the same
        // fire-and-forget endpoint Claude's terminal path uses) and emits an
        // empty hookSpecificOutput so Codex's normal in-CLI approval prompt
        // asks the user. Regression for AI-636.
        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);

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
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
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

    // AI-636 / Qodo finding: bounding only the POST left auth discovery
    // (GET /auth/config) running on HttpClient's 100 s default. Stub
    // /auth/config slow and assert the hook still returns under 5 s — the
    // shared 2 s CTS in CodexHookCommand covers discovery too.
    //
    // AI-628 follow-up: this test redirects Console.Out, so it must share
    // ConsoleSerialGroup with every other Console.Out-redirecting test in
    // the suite. Previously it lived in its own "CodexPermissionRequestStdout"
    // group and raced against the ConsoleSerialGroup tests, occasionally
    // causing both this test and the unrelated stdout-asserting ones to
    // observe a stale Console.Out writer.
    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_returns_quickly_when_auth_discovery_is_slow() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));
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
        var sw           = System.Diagnostics.Stopwatch.StartNew();
        try {
            Console.SetOut(stdoutWriter);
            var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));
            sw.Stop();
            await Assert.That(exit).IsEqualTo(0);
        } finally {
            Console.SetOut(originalOut);
        }

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

    // Fix #3 / AI-648: non-string session_id in a Stop payload must not crash.
    // session_id falls to null via the safe TryGetString helper, so HandleStop
    // short-circuits before EnsureWatcherRunning. No server POST is expected
    // (AI-648 made Stop a turn-end no-op), but we still stub /hooks/session-end/codex
    // so a regression that reintroduces the POST surfaces as a test failure
    // via the WireMock log assertion below.
    [Test]
    public async Task Stop_with_numeric_session_id_returns_zero_without_crash() {
        var payload = """{"hook_event_name": "Stop", "session_id": 12345, "transcript_path": "/tmp/r.jsonl"}""";

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var exit = await CodexHookCommand.Handle(_server.Url!, new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        // AI-648: Stop must never POST session-end, even with a malformed payload.
        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);
    }

    // ---- PermissionRequest daemon-bridge tests ----
    //
    // The no-daemon-URL "stub" branch is covered by the AI-636 regression test
    // PermissionRequest_records_event_and_yields_decision_to_codex above; it
    // asserts the new record-and-yield contract (no in-CLI decision, fall back
    // to Codex's own approval prompt). The tests below cover the new
    // daemon-bridge branch added in AI-68.

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_daemon_url_set_posts_to_bridge_and_forwards_response_to_stdout() {
        using var bridge = WireMockServer.Start();
        var token = "abc123";
        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
              .RespondWith(Response.Create()
                  .WithStatusCode(200)
                  .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));

        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle("http://server.example",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"allow\"");
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_daemon_url_emits_deny_and_exits_nonzero_on_500() {
        using var bridge = WireMockServer.Start();
        var token = "abc123";
        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(500));

        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle("http://server.example",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"deny\"");
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_daemon_url_emits_deny_on_connection_refused() {
        // Use a deliberately-unreachable port (e.g. 1) so the connection fails immediately.
        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", "http://127.0.0.1:1/abc");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle("http://server.example",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"deny\"");
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_non_loopback_daemon_url_emits_deny_without_posting() {
        using var bridge = WireMockServer.Start();
        var previousEnv  = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        // Non-loopback host should be rejected by DaemonBridgeUrl.TryParseLoopback
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://example.com:{bridge.Ports[0]}/abc");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle("http://server.example",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"deny\"");
            await Assert.That(bridge.LogEntries.Count()).IsEqualTo(0);
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_https_daemon_url_emits_deny_without_posting() {
        using var bridge = WireMockServer.Start();
        var previousEnv  = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"https://127.0.0.1:{bridge.Ports[0]}/abc");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle("http://server.example",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"deny\"");
            await Assert.That(bridge.LogEntries.Count()).IsEqualTo(0);
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(ConsoleSerialGroup)]
    public async Task PermissionRequest_with_daemon_url_does_not_double_post_to_server_hooks_endpoint() {
        using var bridge = WireMockServer.Start();
        using var server = WireMockServer.Start();
        var token = "abc123";
        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
              .RespondWith(Response.Create()
                  .WithStatusCode(200)
                  .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}"""));

        server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            await CodexHookCommand.Handle($"http://127.0.0.1:{server.Ports[0]}",
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(server.LogEntries.Count()).IsEqualTo(0); // server NOT touched
            await Assert.That(bridge.LogEntries.Count()).IsEqualTo(1);
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }
}
