using System.Text.Json;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class CodexHookCommandTests : IDisposable {
    // All PermissionRequest tests that manipulate KAPACITOR_DAEMON_URL and
    // Console.Out must not run in parallel with each other — both are
    // process-level globals and parallel access corrupts the assertions.
    const string PermissionRequestGroup = "PermissionRequest_SerialGroup";

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

    [Test, NotInParallel(PermissionRequestGroup)]
    public async Task PermissionRequest_returns_default_allow_decision() {
        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);

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

        var originalOut  = Console.Out;
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
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
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

    // ---- PermissionRequest daemon-bridge tests ----

    [Test, NotInParallel(PermissionRequestGroup)]
    public async Task PermissionRequest_without_daemon_url_still_uses_legacy_stub() {
        var previousEnv = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");
        Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", null);

        _server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var stdoutCapture = new StringWriter();
        var originalOut   = Console.Out;
        Console.SetOut(stdoutCapture);

        try {
            var exit = await CodexHookCommand.Handle(_server.Url!,
                new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}"""));

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(stdoutCapture.ToString()).Contains("\"behavior\":\"allow\"");

            var entries = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost());
            await Assert.That(entries.Count).IsEqualTo(1); // informational POST recorded
        } finally {
            Console.SetOut(originalOut);
            Environment.SetEnvironmentVariable("KAPACITOR_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel(PermissionRequestGroup)]
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

    [Test, NotInParallel(PermissionRequestGroup)]
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

    [Test, NotInParallel(PermissionRequestGroup)]
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

    [Test, NotInParallel(PermissionRequestGroup)]
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

    [Test, NotInParallel(PermissionRequestGroup)]
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

    [Test, NotInParallel(PermissionRequestGroup)]
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
