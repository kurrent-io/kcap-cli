using Capacitor.Cli.Core.Instructions;

namespace Capacitor.Cli.Tests.Unit;

public class AgentInstructionsWriterTests {
    [Test]
    public async Task Write_creates_file_with_marked_block() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");

        var change = AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body);

        await Assert.That(change).IsEqualTo(AgentInstructionsWriter.Change.Updated);
        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains(AgentInstructionsWriter.EndMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task Write_is_idempotent() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");

        await Assert.That(AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body))
            .IsEqualTo(AgentInstructionsWriter.Change.Updated);
        await Assert.That(AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body))
            .IsEqualTo(AgentInstructionsWriter.Change.Unchanged);
    }

    [Test]
    public async Task Write_preserves_user_content() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");
        await File.WriteAllTextAsync(path, "# My rules\n\nAlways use tabs.\n");

        AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body);

        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content.IndexOf("Always use tabs.", StringComparison.Ordinal))
            .IsLessThan(content.IndexOf(AgentInstructionsWriter.BeginMarker, StringComparison.Ordinal));
    }

    [Test]
    public async Task Write_refreshes_stale_block_in_place() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");
        var staleBlock = AgentInstructionsWriter.BeginMarker + "\nOLD kcap text\n" + AgentInstructionsWriter.EndMarker;
        await File.WriteAllTextAsync(path, "# My rules\n\n" + staleBlock + "\n\nMore user notes.\n");

        var change = AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body);

        await Assert.That(change).IsEqualTo(AgentInstructionsWriter.Change.Updated);
        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content).DoesNotContain("OLD kcap text");
        await Assert.That(content).Contains("Prefer kcap tools");
        await Assert.That(content).Contains("# My rules");
        await Assert.That(content).Contains("More user notes.");
        await Assert.That(CountOccurrences(content, AgentInstructionsWriter.BeginMarker)).IsEqualTo(1);
    }

    [Test]
    public async Task Remove_strips_block_but_keeps_user_content() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");
        await File.WriteAllTextAsync(path, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body);

        var change = AgentInstructionsWriter.Remove(path);

        await Assert.That(change).IsEqualTo(AgentInstructionsWriter.Change.Updated);
        var content = await File.ReadAllTextAsync(path);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).DoesNotContain("Prefer kcap tools");
    }

    [Test]
    public async Task Remove_deletes_file_when_only_our_block() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");
        AgentInstructionsWriter.Write(path, KcapAgentInstructions.Body);

        var change = AgentInstructionsWriter.Remove(path);

        await Assert.That(change).IsEqualTo(AgentInstructionsWriter.Change.Updated);
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Remove_is_unchanged_when_no_block() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "copilot-instructions.md");
        await File.WriteAllTextAsync(path, "# My rules\n");

        await Assert.That(AgentInstructionsWriter.Remove(path)).IsEqualTo(AgentInstructionsWriter.Change.Unchanged);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("# My rules\n");
    }

    [Test]
    public async Task Write_fails_closed_when_path_is_a_directory() {
        using var tmp = new TempDir();
        // Path points at an existing directory → the atomic move can't overwrite it → Failed, no throw.
        await Assert.That(AgentInstructionsWriter.Write(tmp.Path, KcapAgentInstructions.Body))
            .IsEqualTo(AgentInstructionsWriter.Change.Failed);
    }

    static int CountOccurrences(string haystack, string needle) {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kcap-instr-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
