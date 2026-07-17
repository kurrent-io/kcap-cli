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
        File.WriteAllLines(parent, new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"{{agentName}}","prompt":"p"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        File.WriteAllText(
            Path.Combine(subDir, DashedSub + ".jsonl"),
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","kind":"subagent","directories":[]}""" + "\n");

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

    // ── EnumerateDescendantFiles (AI-1383 D3: recursive grandchild discovery) ──────────────

    const string DashedGrandsub = "8c1a2222-3333-4444-5555-666677778888";

    // Root (with an invoke_agent call spawning Sub) + Sub's OWN dir with a grandsub file
    // (chats/<sub>/<grandsub>.jsonl) whose invocation is recorded in SUB's transcript, not
    // the root's — Sub's own transcript needs its own invoke_agent tool call for the
    // grandchild, using a DIFFERENT agent_name than the root's call, so a test can assert
    // type resolution reads from the immediate parent, not the root.
    static string WriteRootSubGrandsub(string tmp, string subAgentName, string grandsubAgentName) {
        var chats = Path.Combine(tmp, "chats");
        Directory.CreateDirectory(chats);

        var root = Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl");
        File.WriteAllLines(root, new[] {
            $$"""{"sessionId":"{{DashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"{{subAgentName}}","prompt":"p"},"agentId":"{{DashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, DashedParent);
        Directory.CreateDirectory(subDir);
        var subFile = Path.Combine(subDir, DashedSub + ".jsonl");
        File.WriteAllLines(subFile, new[] {
            $$"""{"sessionId":"{{DashedSub}}","projectHash":"h","kind":"subagent","directories":[]}""",
            $$"""{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__y","name":"invoke_agent","args":{"agent_name":"{{grandsubAgentName}}","prompt":"p2"},"agentId":"{{DashedGrandsub}}","status":"success"}]}"""
        });

        // Sub's OWN nested dir (rooted under the ROOT chats/ dir, per Sub's own dashed id) —
        // this is where the grandsub transcript lives.
        var grandsubDir = Path.Combine(chats, DashedSub);
        Directory.CreateDirectory(grandsubDir);
        File.WriteAllText(
            Path.Combine(grandsubDir, DashedGrandsub + ".jsonl"),
            $$"""{"sessionId":"{{DashedGrandsub}}","projectHash":"h","kind":"subagent","directories":[]}""" + "\n");

        return root;
    }

    [Test]
    public async Task EnumerateDescendantFiles_FindsGrandchildUnderSubsOwnDir() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd-desc").FullName;
        try {
            var root = WriteRootSubGrandsub(tmp, "codebase_investigator", "test_runner");

            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
            await Assert.That(result.Files.Select(f => (f.DashedId, f.Depth)))
                .IsEquivalentTo(new[] { (DashedSub, 1), (DashedGrandsub, 2) });
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateDescendantFiles_ResolvesGrandchildTypeFromImmediateParent_NotRoot() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd-desc").FullName;
        try {
            var root = WriteRootSubGrandsub(tmp, "codebase_investigator", "test_runner");

            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            var grandsub = result.Files.Single(f => f.DashedId == DashedGrandsub);
            var sub      = result.Files.Single(f => f.DashedId == DashedSub);

            // The grandsub's immediate parent transcript is SUB's own file (not root's) —
            // resolving types from it must yield "test_runner", the distinct agent_name
            // recorded in Sub's own invoke_agent call, not the root's "codebase_investigator"
            // and not the "subagent" fallback a flat file list would produce.
            var grandsubTypes = GeminiSubagentDiscovery.ResolveAgentTypes(grandsub.ImmediateParentTranscriptPath);
            await Assert.That(grandsubTypes.GetValueOrDefault(DashedGrandsub)).IsEqualTo("test_runner");

            var subTypes = GeminiSubagentDiscovery.ResolveAgentTypes(sub.ImmediateParentTranscriptPath);
            await Assert.That(subTypes.GetValueOrDefault(DashedSub)).IsEqualTo("codebase_investigator");
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateDescendantFiles_EmptyWhenNoNestedDir() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd-desc").FullName;
        try {
            var chats = Path.Combine(tmp, "chats");
            Directory.CreateDirectory(chats);
            var parent = Path.Combine(chats, "session-x.jsonl");
            File.WriteAllText(parent, $$"""{"sessionId":"{{DashedParent}}","kind":"main"}""" + "\n");

            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(parent);

            await Assert.That(result.Files.Count).IsEqualTo(0);
            await Assert.That(result.DescendantsOmitted).IsEqualTo(0);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnumerateDescendantFiles_depth_9_is_omitted_with_diagnostic() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd-desc").FullName;
        try {
            var chats = Path.Combine(tmp, "chats");
            Directory.CreateDirectory(chats);

            var rootId = "00000000-0000-4000-8000-000000000000";
            var root   = Path.Combine(chats, "session-root.jsonl");
            File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

            // A 9-level chain of subagent dirs below the root. Each level's directory is
            // rooted directly under the shared chats/ dir (chats/&lt;thatLevel'sOwnId&gt;/),
            // matching EnumerateDescendantFiles's "always derived from the ROOT chats/ dir,
            // never relative to a nested dir" contract — never chats/&lt;root&gt;/&lt;n1&gt;/&lt;n2&gt;/....
            var prevId = rootId;
            for (var depth = 1; depth <= 9; depth++) {
                var id  = $"00000000-0000-4000-8000-{depth:D12}";
                var dir = Path.Combine(chats, prevId);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
                prevId = id;
            }

            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            await Assert.That(result.Files.Count).IsEqualTo(8);
            await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);
            await Assert.That(result.DescendantsOmitted).IsEqualTo(1);
            await Assert.That(result.OmittedDescendantIds.Count).IsEqualTo(1);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // AI-1383 D3 review fix #3: the walker used to stop AT the boundary child (depth 9) and
    // never look below it, so a chain continuing to depth 10 was still counted as ONE omitted
    // descendant. The walk must now continue (never importing) below the cap to count the
    // WHOLE omitted subtree.
    [Test]
    public async Task EnumerateDescendantFiles_depth_9_and_10_chain_reports_omitted_two_not_one() {
        var tmp = Directory.CreateTempSubdirectory("kcap-gsd-desc").FullName;
        try {
            var chats = Path.Combine(tmp, "chats");
            Directory.CreateDirectory(chats);

            var rootId = "00000000-0000-4000-8000-000000000000";
            var root   = Path.Combine(chats, "session-root.jsonl");
            File.WriteAllText(root, $$"""{"sessionId":"{{rootId}}","kind":"main"}""" + "\n");

            // A 10-level chain of subagent dirs below the root.
            var prevId = rootId;
            for (var depth = 1; depth <= 10; depth++) {
                var id  = $"00000000-0000-4000-8000-{depth:D12}";
                var dir = Path.Combine(chats, prevId);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, id + ".jsonl"), $$"""{"sessionId":"{{id}}","kind":"subagent"}""" + "\n");
                prevId = id;
            }

            var result = GeminiSubagentDiscovery.EnumerateDescendantFiles(root);

            await Assert.That(result.Files.Count).IsEqualTo(8);
            await Assert.That(result.Files.Max(f => f.Depth)).IsEqualTo(8);
            // Depths 9 AND 10 are omitted — TWO, not one.
            await Assert.That(result.DescendantsOmitted).IsEqualTo(2);
            await Assert.That(result.OmittedDescendantIds.Count).IsEqualTo(2);
        } finally {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
