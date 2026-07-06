using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Wire-contract test for Gemini subagent import (AI-900). Gemini records subagents
/// in nested files (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>); the parent's
/// <c>invoke_agent</c> tool call persists <c>agentId == subId</c> + <c>args.agent_name</c>.
/// This drives the full discover → classify → import path and asserts the subagent
/// lifecycle the server expects: <c>/hooks/subagent-start</c> and <c>/hooks/subagent-stop</c>
/// carry the CANONICAL (dashless) <c>agent_id</c> and the <c>agent_type</c> resolved from
/// the parent <c>agent_name</c>, and the subagent transcript batch is tagged
/// <c>vendor=gemini</c> with that <c>agent_id</c> so the server routes it to
/// <c>AgentSubsession-*</c>.
/// </summary>
public class GeminiSubagentImportTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kcap-gemini-sub-it").FullName;

    const string DashedParent   = "0a900000-0000-4000-8000-000000000903";
    const string DashlessParent = "0a900000000040008000000000000903";
    const string DashedSub      = "57d9b498-2705-4af5-b060-ebaba4878c96";
    const string DashlessSub    = "57d9b49827054af5b060ebaba4878c96";

    public void Dispose() {
        _server.Stop();

        try { Directory.Delete(_tempDir, recursive: true); } catch {
            /* best effort */
        }
    }

    // Writes a parent main session (with an invoke_agent call) + the nested subagent file,
    // and returns the tmp-dir override (the dir that holds per-project subdirectories).
    void WriteFixture() {
        var chats = Path.Combine(_tempDir, "proj", "chats");
        Directory.CreateDirectory(chats);

        File.WriteAllLines(
            Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl"),
            [
                $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","lastUpdated":"2026-06-22T14:31:00.000Z","kind":"main"}""",
                """{"id":"u1","timestamp":"2026-06-22T14:31:01.000Z","type":"user","content":[{"text":"delegate it"}]}""",
                $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","tokens":{"input":5,"output":2,"total":7},"model":"gemini-3-flash-preview","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"codebase_investigator","prompt":"list files"},"agentId":"{{DashedSub}}","status":"success"}]}"""
            ]
        );

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);

        File.WriteAllLines(
            Path.Combine(subDir, DashedSub + ".jsonl"),
            [
                $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","startTime":"2026-06-22T14:31:06.000Z","lastUpdated":"2026-06-22T14:31:06.000Z","kind":"subagent","directories":[]}""",
                """{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"gemini","content":"calc.py, README.md","tokens":{"input":3,"output":4,"total":7},"model":"gemini-3-flash-preview"}"""
            ]
        );
    }

    [Test]
    public async Task ImportSession_imports_nested_subagent_with_canonical_id_and_resolved_type() {
        WriteFixture();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        foreach (var route in new[] {
                     "/hooks/session-start/gemini", "/hooks/transcript",
                     "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/gemini"
                 }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var       source = new GeminiImportSource(tmpDirOverride: _tempDir);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        // The nested subagent file must NOT be discovered as its own session.
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(DashlessParent);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None
        );

        var outcome = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();

        // Main lifecycle + the subagent lifecycle + two transcript batches (main + subagent).
        await Assert.That(posts)
            .IsEquivalentTo(
                [
                    "/hooks/session-start/gemini", "/hooks/transcript", "/hooks/subagent-start",
                    "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/gemini"
                ]
            );

        // subagent-start lands before subagent-stop (fail-closed lifecycle ordering).
        await Assert.That(posts.IndexOf("/hooks/subagent-start"))
            .IsLessThan(posts.IndexOf("/hooks/subagent-stop"));

        // subagent-start carries the canonical (dashless) id + the agent_name-derived type.
        var startBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains($"\"agent_id\":\"{DashlessSub}\"");
        await Assert.That(startBody).Contains("\"agent_type\":\"codebase_investigator\"");

        // The subagent transcript batch is tagged vendor=gemini and carries the agent_id.
        var subTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains(DashlessSub));
        await Assert.That(subTranscript).Contains("\"vendor\":\"gemini\"");

        var stopBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-stop").RequestMessage.Body!;
        await Assert.That(stopBody).Contains($"\"agent_id\":\"{DashlessSub}\"");
    }
}
