using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// The meta files beside a session name each subagent's starting call; a file not yet readable waits for a later read.
public class ClaudeSubagentFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Starts_reads_each_new_subagent_once_its_meta_names_a_call() {
        var parent = Tmp.PathTo("s1.jsonl");
        await Assert.That(ClaudeSubagentFiles.Starts(parent, new HashSet<string>())).IsEmpty();

        var dir = ClaudeSubagentFiles.DirectoryOf(parent);
        Tmp.CreateFile(["s1", "subagents", "agent-a1.meta.json"], """{"toolUseId":"toolu_1","requestShape":"foreground"}""");
        Tmp.CreateFile(["s1", "subagents", "agent-a2.meta.json"], """{"toolUseId":"toolu_2"}""");
        Tmp.CreateFile(["s1", "subagents", "agent-a3.meta.json"], """{"toolUse""");
        Tmp.CreateFile(["s1", "subagents", "agent-a1.jsonl"], "{}\n");

        var starts = ClaudeSubagentFiles.Starts(parent, new HashSet<string> { "a2" });

        await Assert.That(starts).IsEquivalentTo([("toolu_1", "a1")]);
        await Assert.That(ClaudeSubagentFiles.TranscriptPath(parent, "a1")).IsEqualTo(Path.Combine(dir, "agent-a1.jsonl"));
    }
}
