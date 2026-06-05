using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Exercises <see cref="ClaudeHookCommand.Handle"/> end-to-end against a
/// WireMock server and captures stdout to validate the SessionStart
/// <c>hookSpecificOutput</c> envelope shape — including the
/// single-envelope invariant when both <c>top_clusters</c> and
/// <c>version</c> are present (AI-768).
///
/// Test payloads deliberately OMIT <c>transcript_path</c> so the
/// session-start path short-circuits before
/// <c>WatcherManager.EnsureWatcherRunning</c>; spawning the watcher's
/// child process corrupts TUnit's <c>Console</c> capture (see
/// <c>test/Capacitor.Cli.Tests.Unit/Codex/CodexHookCommandTests.cs:47-53</c>).
/// </summary>
public class ClaudeHookStdoutTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static string SessionStartPayloadWithoutTranscriptPath() =>
        // No transcript_path, no session_id → WatcherManager spawn is skipped.
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

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

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_nudge_envelope_when_server_returns_newer_version_only() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{ "version": "999.0.0" }""")
            );

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        var trimmed = stdout.Trim();
        await Assert.That(trimmed).IsNotEmpty();
        var json = JsonNode.Parse(trimmed);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");

        var ctx = json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("npm install -g @kurrent/kcap");
        await Assert.That(ctx).DoesNotContain("Recurring lessons");
    }

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_combined_envelope_when_server_returns_top_clusters_and_newer_version() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(
                        """
                        {
                          "version": "999.0.0",
                          "top_clusters": [
                            { "category": "safety", "text": "always close the writer" }
                          ]
                        }
                        """
                    )
            );

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        var trimmed = stdout.Trim();

        // Single-envelope invariant.
        await Assert.That(trimmed).IsNotEmpty();
        var firstClose = trimmed.LastIndexOf('}');
        var afterClose = trimmed[(firstClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");

        var json = JsonNode.Parse(trimmed);
        var ctx  = json!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("Recurring lessons");
        await Assert.That(ctx).Contains("- always close the writer");
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("npm install -g @kurrent/kcap");
    }

    [Test, NotInParallel("Console_Out")]
    public async Task Emits_nothing_when_server_returns_empty_object() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var stdout = await CaptureStdoutAsync(() =>
            ClaudeHookCommand.Handle(_server.Url!, new StringReader(SessionStartPayloadWithoutTranscriptPath()))
        );

        await Assert.That(stdout.Trim()).IsEqualTo("");
    }
}
