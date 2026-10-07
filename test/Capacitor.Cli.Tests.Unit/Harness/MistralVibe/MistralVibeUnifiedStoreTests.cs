using Capacitor.Cli.Harness.MistralVibe;

namespace Capacitor.Cli.Tests.Unit.Harness.MistralVibe;

public class MistralVibeUnifiedStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // Shapes taken from a real vibe 2.26.0 unified session: the journal is projection_delta records,
    // and each transcript entry rides inside an append_entry op with a numeric epoch-ms createdAt.
    const string CoreInput = """{"type":"core_input","payload":{}}""";
    static string Delta(string entry) =>
        "{\"type\":\"projection_delta\",\"payload\":{\"delta\":[{\"op\":\"set_envelope\",\"state\":{}},{\"op\":\"append_entry\",\"entry\":" + entry + "}]}}";
    static string Entry(string id, string role, string text, long createdAt) =>
        "{\"type\":\"message\",\"role\":\"" + role + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"createdAt\":" + createdAt + ",\"id\":\"" + id + "\"}";

    [Test]
    public async Task Unwraps_append_entry_ops_ordered_by_created_at() {
        Tmp.CreateFile("s1/journal/0000000000000001.jsonl", new[] {
            CoreInput,                                        // skipped: not a transcript entry
            Delta(Entry("e1", "user", "first", 1000)),
            Delta(Entry("e2", "assistant", "second", 2000)),
        });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s1"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("first");
        await Assert.That(lines[1]).Contains("second");
    }

    [Test]
    public async Task Dedups_an_entry_present_in_both_a_chunk_and_the_journal_by_id() {
        // Once a generation is published the same entry is pooled into a chunk AND still in the journal.
        Tmp.CreateFile("s2/chunks/aaa.json", $"[{Entry("dup", "user", "once", 100)}]");
        Tmp.CreateFile("s2/journal/0000000000000001.jsonl", new[] { Delta(Entry("dup", "user", "once", 100)) });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s2"));
        await Assert.That(lines.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Skips_non_transcript_chunk_entries() {
        // Real chunks carry a session-config entry (agent_types/skills/…) with no transcript type.
        Tmp.CreateFile("s3/chunks/aaa.json", """[{"agent_types":[],"skills":[],"tool_groups":[]}]""");
        await Assert.That(MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s3")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Journal_files_ordered_by_numeric_sequence_when_untimed() {
        Tmp.CreateFile("s4/journal/0000000000000002.jsonl", new[] { """{"type":"projection_delta","payload":{"delta":[{"op":"append_entry","entry":{"type":"message","role":"user","content":[{"type":"text","text":"two"}],"id":"a"}}]}}""" });
        Tmp.CreateFile("s4/journal/0000000000000010.jsonl", new[] { """{"type":"projection_delta","payload":{"delta":[{"op":"append_entry","entry":{"type":"message","role":"user","content":[{"type":"text","text":"ten"}],"id":"b"}}]}}""" });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s4"));
        await Assert.That(lines[0]).Contains("two");
        await Assert.That(lines[1]).Contains("ten");
    }

    [Test]
    public async Task Detects_the_unified_store_shape() {
        Tmp.CreateFile("u/chunks/x.json", "[]");
        await Assert.That(MistralVibeUnifiedStore.IsUnifiedSession(Tmp.PathTo("u"))).IsTrue();
        await Assert.That(MistralVibeUnifiedStore.IsUnifiedSession(Tmp.PathTo("missing"))).IsFalse();
    }

    [Test]
    public async Task Reads_cwd_from_nested_environment_working_directory() {
        Tmp.CreateFile("m/meta.json", """{"environment":{"working_directory":"/work/repo"},"origin_directory":"/work/repo"}""");
        await Assert.That(MistralVibeUnifiedStore.ReadCwd(Tmp.PathTo("m"))).IsEqualTo("/work/repo");
    }
}
