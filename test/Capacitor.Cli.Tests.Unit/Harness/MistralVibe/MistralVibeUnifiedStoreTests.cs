using Capacitor.Cli.Harness.MistralVibe;

namespace Capacitor.Cli.Tests.Unit.Harness.MistralVibe;

public class MistralVibeUnifiedStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Layout and record shapes as vibe 2.26.0's store writes them: snake_case store documents,
    // camelCase history entries.
    const string Generation = "0000000000000001";

    static string Message(string id, string role, string text, string status = "completed") =>
        "{\"type\":\"message\",\"id\":\"" + id + "\",\"role\":\"" + role + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text
      + "\"}],\"createdAt\":1000,\"generationStatus\":\"" + status + "\"}";

    static string Effect(string id, string status, string state) =>
        "{\"type\":\"effect\",\"id\":\"" + id + "\",\"createdAt\":2000,\"generationStatus\":\"" + status
      + "\",\"detail\":{\"kind\":\"shell\",\"toolName\":\"file_system.bash\",\"input\":{\"command\":\"ls\"}},\"state\":" + state + "}";

    static string Delta(long sequence, params string[] ops) =>
        "{\"type\":\"projection_delta\",\"sequence\":" + sequence + ",\"payload\":{\"watermark\":1,\"delta\":[" + string.Join(",", ops) + "]}}";

    static string Append(string entry)              => "{\"op\":\"append_entry\",\"entry\":" + entry + "}";
    static string Replace(string id, string entry)  => "{\"op\":\"replace_entry\",\"id\":\"" + id + "\",\"entry\":" + entry + "}";
    static string Remove(string id)                 => "{\"op\":\"remove_entry\",\"id\":\"" + id + "\"}";
    const string Envelope = """{"op":"set_envelope","state":{}}""";

    void Publish(string session, string projectionState, string journalSegment = "journal/0000000000000002.jsonl", long firstSequence = 2) {
        Tmp.CreateFile($"{session}/CURRENT", "{\"generation\":\"" + Generation + "\",\"snapshot_sequence\":" + (firstSequence - 1) + "}");
        Tmp.CreateFile($"{session}/generations/{Generation}/manifest.json",
            "{\"projection_state\":" + projectionState + ",\"recovery_journal_segment\":{\"path\":\"" + journalSegment + "\",\"first_sequence\":" + firstSequence + "}}");
    }

    void PublishInline(string session, params string[] entries) {
        Publish(session, """{"path":"projection-state.json","sha256":"x"}""");
        Tmp.CreateFile($"{session}/generations/{Generation}/projection-state.json",
            "{\"snapshot\":{\"history\":{\"entries\":[" + string.Join(",", entries) + "]}}}");
    }

    [Test]
    public async Task Replays_the_journal_over_the_published_snapshot_and_yields_the_settled_effect() {
        PublishInline("s", Message("m1", "user", "list files"));
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Append(Effect("effect-1", "in_progress", """{"status":"running","outputText":""}""")), Envelope),
            Delta(3, Replace("effect-1", Effect("effect-1", "completed", """{"status":"completed","outputText":"a.txt"}"""))),
        });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("list files");
        await Assert.That(lines[1]).Contains("\"outputText\":\"a.txt\"");
    }

    [Test]
    public async Task An_entry_still_generating_is_withheld() {
        PublishInline("s", Message("m1", "user", "go"));
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Append(Message("m2", "assistant", "half a sent", "in_progress"))),
        });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"m1\"");
    }

    [Test]
    public async Task A_chunked_snapshot_reads_only_the_chunks_its_manifest_names() {
        Publish("s", """{"path":"projection-state.json","sha256":"x","chunks":["aaaa","bbbb"]}""");
        Tmp.CreateFile($"s/generations/{Generation}/projection-state.json", """{"snapshot":{"history":{"entries":[]}}}""");
        Tmp.CreateFile("s/chunks/aaaa.json", $"[{Message("m1", "user", "first")}]");
        Tmp.CreateFile("s/chunks/bbbb.json", $"[{Message("m2", "assistant", "second")}]");
        // The pool also holds the model checkpoint transcript and older generations' chunks.
        Tmp.CreateFile("s/chunks/cccc.json", $"[{Message("stale", "user", "from an older generation")}]");

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("first");
        await Assert.That(lines[1]).Contains("second");
    }

    [Test]
    public async Task Only_the_current_generations_journal_segment_replays() {
        PublishInline("s", Message("m1", "user", "kept"));
        Tmp.CreateFile("s/journal/0000000000000001.jsonl", new[] { Delta(1, Append(Message("old", "user", "folded already"))) });
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] { Delta(2, Append(Message("m2", "user", "new"))) });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(string.Join("\n", lines)).DoesNotContain("folded already");
    }

    [Test]
    public async Task Remove_and_set_history_ops_reshape_the_history() {
        PublishInline("s", Message("m1", "user", "one"), Message("m2", "user", "two"));
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Remove("m1")),
            Delta(3, """{"op":"set_history_entries","entries":[""" + Message("m2", "user", "two") + "," + Message("m3", "user", "three") + "]}"),
        });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("two");
        await Assert.That(lines[1]).Contains("three");
    }

    [Test]
    public async Task Without_a_published_generation_every_segment_replays_in_sequence_order() {
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] { Delta(2, Append(Message("a", "user", "two"))) });
        Tmp.CreateFile("s/journal/0000000000000010.jsonl", new[] { Delta(10, Append(Message("b", "user", "ten"))), "{\"type\":\"projection_del" });

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("two");
        await Assert.That(lines[1]).Contains("ten");
    }

    [Test]
    public async Task An_indented_snapshot_entry_still_becomes_one_line() {
        Publish("s", """{"path":"projection-state.json","sha256":"x"}""");
        Tmp.CreateFile($"s/generations/{Generation}/projection-state.json",
            "{\"snapshot\":{\"history\":{\"entries\":[\n  {\n    \"type\": \"message\", \"id\": \"m1\", \"role\": \"user\",\n    \"content\": [{\"type\": \"text\", \"text\": \"hi\"}]\n  }\n]}}}");

        var lines = MistralVibeUnifiedStore.ReadLines(Tmp.PathTo("s"));

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).DoesNotContain("\n");
    }

    [Test]
    public async Task Detects_the_unified_store_shape() {
        Tmp.CreateFile("u/CURRENT", "{}");
        await Assert.That(MistralVibeUnifiedStore.IsUnifiedSession(Tmp.PathTo("u"))).IsTrue();
        await Assert.That(MistralVibeUnifiedStore.IsUnifiedSession(Tmp.PathTo("missing"))).IsFalse();
    }

    [Test]
    public async Task Reads_cwd_from_nested_environment_working_directory() {
        Tmp.CreateFile("m/meta.json", """{"environment":{"working_directory":"/work/repo"},"origin_directory":"/work/repo"}""");
        await Assert.That(MistralVibeUnifiedStore.ReadCwd(Tmp.PathTo("m"))).IsEqualTo("/work/repo");
    }

    // ── the live transcript the watcher tails ──────────────────────────────────────────────────

    [Test]
    public async Task Live_sync_appends_each_entry_once_as_it_finishes() {
        var live = Tmp.PathTo("live/s.jsonl");
        PublishInline("s", Message("m1", "user", "go"));
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Append(Effect("effect-1", "in_progress", """{"status":"running","outputText":""}"""))),
        });

        MistralVibeLiveTranscript.Sync(Tmp.PathTo("s"), live, model: null);
        MistralVibeLiveTranscript.Sync(Tmp.PathTo("s"), live, model: null);
        await Assert.That(File.ReadAllLines(live).Length).IsEqualTo(1);

        File.AppendAllLines(Tmp.PathTo("s/journal/0000000000000002.jsonl"), [
            Delta(3, Replace("effect-1", Effect("effect-1", "completed", """{"status":"completed","outputText":"done"}"""))),
        ]);
        MistralVibeLiveTranscript.Sync(Tmp.PathTo("s"), live, model: null);

        var lines = File.ReadAllLines(live);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("\"m1\"");
        await Assert.That(lines[1]).Contains("\"outputText\":\"done\"");
    }

    [Test]
    public async Task Discard_deletes_kcaps_copy_and_never_a_transcript_vibe_owns() {
        var copy = MistralVibeLiveTranscript.PathFor(Config.Root, "s1");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "{}");
        var vibeOwned = Tmp.CreateFile("session_1/messages.jsonl", "{}");

        MistralVibeLiveTranscript.Discard(Config.Root, "s1", copy);
        MistralVibeLiveTranscript.Discard(Config.Root, "s1", vibeOwned);

        await Assert.That(File.Exists(copy)).IsFalse();
        await Assert.That(File.Exists(vibeOwned)).IsTrue();
    }

    [Test]
    public async Task A_copy_rebuilt_after_discard_stamps_only_the_tokens_counted_since() {
        var copy = MistralVibeLiveTranscript.PathFor(Config.Root, "s1");
        Publish("s", """{"path":"projection-state.json","sha256":"x"}""");
        Tmp.CreateFile($"s/generations/{Generation}/projection-state.json",
            "{\"snapshot\":{\"session\":{\"tokenUsage\":{\"inputTokens\":100,\"outputTokens\":10,\"cachedInputTokens\":0}},\"history\":{\"entries\":["
          + Message("m1", "user", "go") + "," + Message("a1", "assistant", "first") + "]}}}");

        MistralVibeLiveTranscript.Sync(Tmp.PathTo("s"), copy, model: null);
        MistralVibeLiveTranscript.Discard(Config.Root, "s1", copy);

        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Append(Message("a2", "assistant", "resumed")),
                     """{"op":"set_envelope","state":{"session":{"tokenUsage":{"inputTokens":150,"outputTokens":15,"cachedInputTokens":0}}}}"""),
        });
        MistralVibeLiveTranscript.Sync(Tmp.PathTo("s"), copy, model: null);

        var resumed = File.ReadAllLines(copy).Single(line => line.Contains("\"a2\""));
        await Assert.That(resumed).Contains("\"kcapUsage\":{\"inputTokens\":50,\"outputTokens\":5");
    }

    [Test]
    public async Task Refresh_follows_the_store_after_the_last_hook() {
        // The turn's closing message often completes after post_agent, when no hook is left to sync it.
        var live = Tmp.PathTo("live/s.jsonl");
        PublishInline("s", Message("m1", "user", "go"));
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] { Delta(2, Append(Message("m2", "assistant", "partial", "in_progress"))) });

        MistralVibeLiveTranscript.Follow(Tmp.PathTo("s"), live, model: null);
        await Assert.That(File.ReadAllLines(live).Length).IsEqualTo(1);

        File.AppendAllLines(Tmp.PathTo("s/journal/0000000000000002.jsonl"), [Delta(3, Replace("m2", Message("m2", "assistant", "done")))]);
        MistralVibeLiveTranscript.Refresh(live);

        var lines = File.ReadAllLines(live);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[1]).Contains("done");
    }

    [Test]
    public async Task Read_takes_the_running_token_total_and_the_spawned_subagents() {
        Publish("s", """{"path":"projection-state.json","sha256":"x"}""");
        Tmp.CreateFile($"s/generations/{Generation}/projection-state.json",
            "{\"snapshot\":{\"session\":{\"tokenUsage\":{\"inputTokens\":10,\"outputTokens\":2,\"cachedInputTokens\":0}},\"history\":{\"entries\":[" + Message("m1", "user", "go") + "]}}}");
        Tmp.CreateFile("s/journal/0000000000000002.jsonl", new[] {
            Delta(2, Append("""{"type":"effect","id":"effect-s","generationStatus":"completed","detail":{"kind":"subagent","toolName":"subagent.spawn","childSessionId":"child-abc123","input":{"task":"look","agent":"explore"}},"state":{"status":"completed","outputText":""}}"""),
                     """{"op":"set_envelope","state":{"session":{"tokenUsage":{"inputTokens":90,"outputTokens":7,"cachedInputTokens":40}}}}"""),
        });

        var session = MistralVibeUnifiedStore.Read(Tmp.PathTo("s"));

        await Assert.That(session.Usage).IsEqualTo(new MistralVibeTokenUsage(90, 7, 40));
        await Assert.That(session.Subagents.Single()).IsEqualTo(new MistralVibeSubagent("child-abc123", "explore"));
    }
}
