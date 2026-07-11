using System.Text;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-701: <see cref="ValidatePlanCommand"/>'s two-call flow — <c>GET .../plan-artifacts?chain=true</c>
/// for the discovered plan set, then the existing <c>GET .../recap?chain=true</c> for current-session
/// work rows and "what's done" summaries. A 404 on the artifacts route (old server / non-visible
/// session) falls back to the original recap-only rendering unchanged.
///
/// Every test is <c>[NotInParallel]</c> with NO group key (globally sequential), mirroring
/// <c>ClaudeHookStdoutTests</c>: the SUT writes to the process-global <see cref="Console.Out"/>,
/// and a group key alone would not stop a different file's test from leaking into the capture.
/// </summary>
public class ValidatePlanCommandTests : IDisposable {
    const string SessionId = "9dc2775376454e4691ecc2d69973c152";

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        var original = Console.Out;
        var sw       = new StringWriter();
        Console.SetOut(sw);
        try {
            await action();
        } finally {
            Console.SetOut(original);
        }
        return sw.ToString();
    }

    static string RecapJson(string extraEntries = "") => $$"""
        [
          {"type":"write","session_id":"{{SessionId}}","agent_id":null,"agent_type":null,"content":"","file_path":"src/Foo.cs","timestamp":"2026-07-01T00:00:00Z"},
          {"type":"whats_done","session_id":null,"agent_id":null,"agent_type":null,"content":"Implemented Foo.","file_path":null,"timestamp":"2026-07-01T00:05:00Z"}{{extraEntries}}
        ]
        """;

    [Test, NotInParallel]
    public async Task Primary_artifact_renders_under_plan_and_recap_supplies_work_and_whats_done() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-1",
                    "kind": "plan",
                    "title": "Plan A",
                    "source": "native_plan",
                    "session_id": "{{SessionId}}",
                    "content": "Step 1\nStep 2",
                    "content_state": "ok",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "content_hash": "abc123",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "high",
                    "reason": "native plan tool",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout).Contains("## Plan");
        await Assert.That(stdout).Contains("Step 1\nStep 2");
        await Assert.That(stdout).Contains("## What's Done");
        await Assert.That(stdout).Contains("Implemented Foo.");
        await Assert.That(stdout).Contains("- Write: src/Foo.cs");

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1); // recap is still consulted for work/whats_done
    }

    [Test, NotInParallel]
    public async Task Empty_response_prints_no_plan_found_and_skips_recap() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""
                { "primary": null, "artifacts": [], "diagnostics": [] }
                """));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout.Trim()).IsEqualTo("No plan found for this session.");

        var recapHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(recapHits.Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task NotFound_falls_back_to_legacy_recap_only_rendering() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // Legacy path: a single recap call carrying a "plan" entry plus the usual work/whats_done.
        var extra = $$""", {"type":"plan","session_id":"{{SessionId}}","agent_id":null,"agent_type":null,"content":"Legacy plan text","file_path":null,"timestamp":"2026-06-30T00:00:00Z"}""";
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson(extra)));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout).Contains("## Plan");
        await Assert.That(stdout).Contains("Legacy plan text");
        await Assert.That(stdout).Contains("## What's Done");
        await Assert.That(stdout).Contains("Implemented Foo.");
        await Assert.That(stdout).DoesNotContain("plan truncated");
        await Assert.That(stdout).DoesNotContain("plan content unavailable");

        // Both routes were attempted: plan-artifacts first (404), then the recap fallback.
        var artifactHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet());
        await Assert.That(artifactHits.Count).IsEqualTo(1);
        var recapHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(recapHits.Count).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task NotFound_with_no_plan_entries_prints_no_plan_found() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout.Trim()).IsEqualTo("No plan found for this session.");
    }

    [Test, NotInParallel]
    public async Task Truncated_primary_prefixes_the_plan_section_with_a_byte_count_marker() {
        const string content = "Truncated prefix of the plan...";
        var byteCount        = Encoding.UTF8.GetByteCount(content);

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-2",
                    "kind": "plan",
                    "title": "Plan B",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": "{{content}}",
                    "content_state": "truncated",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": true,
                    "original_bytes": 5000,
                    "content_hash": "def456",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "medium",
                    "reason": "repo file over size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout).Contains($"[plan truncated: first {byteCount} of 5000 bytes]");
        await Assert.That(stdout).Contains(content);
    }

    [Test, NotInParallel]
    public async Task Unavailable_primary_renders_placeholder_and_states_validation_is_not_possible() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-3",
                    "kind": "plan",
                    "title": "Plan C",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": null,
                    "content_state": "unavailable",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "original_bytes": 999999,
                    "content_hash": "ghi789",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "low",
                    "reason": "content exceeds hard size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        using var client = new HttpClient();
        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(client, _server.Url!, SessionId));

        await Assert.That(stdout).Contains("[plan content unavailable due to size bounds]");
        await Assert.That(stdout).Contains("Validation is not possible");
    }
}
