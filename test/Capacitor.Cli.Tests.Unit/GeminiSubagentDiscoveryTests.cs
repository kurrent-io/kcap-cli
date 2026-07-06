using Capacitor.Cli.Core.Gemini;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="GeminiSubagentDiscovery"/> (AI-900) — the shared discovery
/// used by both the import path and the live watcher: locate nested subagent files under
/// <c>chats/&lt;dashedParent&gt;/</c>, resolve each subagent's type from the parent's
/// <c>invoke_agent</c> call, and canonicalize the (dashed) subId for the server.
/// </summary>
public class GeminiSubagentDiscoveryTests {
    const string DashedParent = "0a900000-0000-4000-8000-000000000903";
    const string DashedSub    = "57d9b498-2705-4af5-b060-ebaba4878c96";

    static string WriteParentWithSubagent(string tmp, string agentName) {
        var chats = Path.Combine(tmp, "chats");
        Directory.CreateDirectory(chats);

        var parent = Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl");

        File.WriteAllLines(
            parent,
            [
                $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","kind":"main"}""",
                $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"{{agentName}}","prompt":"p"},"agentId":"{{DashedSub}}","status":"success"}]}"""
            ]
        );

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);

        File.WriteAllText(
            Path.Combine(subDir, DashedSub + ".jsonl"),
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","kind":"subagent","directories":[]}""" + "\n"
        );

        return parent;
    }

    [Test]
    public async Task EnumerateSubagentFiles_FindsNestedFile() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd").FullName;

        try {
            var parent = WriteParentWithSubagent(tmp, "codebase_investigator");

            var files = GeminiSubagentDiscovery.EnumerateSubagentFiles(parent);

            await Assert.That(files.Count).IsEqualTo(1);
            await Assert.That(Path.GetFileNameWithoutExtension(files[0])).IsEqualTo(DashedSub);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateSubagentFiles_EmptyWhenNoNestedDir() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd").FullName;

        try {
            var chats = Path.Combine(tmp, "chats");
            Directory.CreateDirectory(chats);
            var parent = Path.Combine(chats, "session-x.jsonl");
            File.WriteAllText(parent, $$"""{"sessionId":"{{DashedParent}}","kind":"main"}""" + "\n");

            await Assert.That(GeminiSubagentDiscovery.EnumerateSubagentFiles(parent).Count).IsEqualTo(0);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task ResolveAgentTypes_MapsSubIdToParentAgentName() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd").FullName;

        try {
            var parent = WriteParentWithSubagent(tmp, "codebase_investigator");

            var types = GeminiSubagentDiscovery.ResolveAgentTypes(parent);

            await Assert.That(types.GetValueOrDefault(DashedSub)).IsEqualTo("codebase_investigator");
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    [Arguments("57d9b498-2705-4af5-b060-ebaba4878c96", "57d9b49827054af5b060ebaba4878c96")]
    [Arguments("alreadydashless", "alreadydashless")]
    public async Task CanonicalAgentId_StripsDashes(string input, string expected) {
        await Assert.That(GeminiSubagentDiscovery.CanonicalAgentId(input)).IsEqualTo(expected);
    }
}
