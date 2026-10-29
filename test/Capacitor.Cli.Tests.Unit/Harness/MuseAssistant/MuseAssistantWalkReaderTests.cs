using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.MuseAssistant;
using static Capacitor.Cli.Tests.Unit.Harness.MuseAssistant.WalkBuilder;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>A walk is accepted only when the manifest, the files and Muse's own cursor chain agree, and
/// every selected item carries what the server needs. Anything else is a named problem and nothing is read
/// as data.</summary>
public class MuseAssistantWalkReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const long T0 = 1_760_000_000_000;

    WalkBuilder TwoPages() => new WalkBuilder(Tmp)
        .AddPage(null, "c1", true, User(22, T0 + 410_000, "Yes, send it.", "t2"), Assistant(25, T0 + 420_000, "Sent.", "t2"))
        .AddPage("c1", null, false, Developer(10, T0), User(12, T0 + 5_000, "Plan my week"), Commentary(13, T0 + 7_000), Assistant(15, T0 + 12_000, "Thursday is blocked."));

    [Test]
    public async Task A_cursor_chained_walk_loads_items_in_seq_order_with_coverage_facts() {
        var (walk, problem) = MuseAssistantWalkReader.Load(TwoPages().Write());

        await Assert.That(problem).IsNull();
        await Assert.That(walk!.ChatId).IsEqualTo("chat-main");
        await Assert.That(walk.IsMain).IsTrue();
        await Assert.That(walk.FetchedAtMs).IsEqualTo(1_760_000_500_000);
        await Assert.That(walk.Items.Select(i => i.Seq)).IsEquivalentTo([10L, 12, 13, 15, 22, 25]);
        await Assert.That(walk.OldestHasMore).IsFalse();
        await Assert.That(walk.OldestPageMinSeq).IsEqualTo(10);
        await Assert.That(MuseAssistantWalkReader.ReadChatId(Tmp.PathTo("walk"))).IsEqualTo("chat-main");
    }

    [Test]
    public async Task Lines_keep_the_item_verbatim_and_add_chat_and_turn_ids() {
        var content = "She said \"done\" — {braces}, \\ backslash, 日本語, and a fake \"chat_id\": \"x\"";
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(12, T0, content, "7c1e")).Write();

        var (walk, _) = MuseAssistantWalkReader.Load(dir);
        var line = JsonDocument.Parse(walk!.Items[0].Line).RootElement;

        await Assert.That(line.GetProperty("chat_id").GetString()).IsEqualTo("chat-main");
        await Assert.That(line.GetProperty("turn_id").GetString()).IsEqualTo("7c1e");
        await Assert.That(line.GetProperty("content").GetString()).IsEqualTo(content);
        await Assert.That(line.GetProperty("seq").GetInt64()).IsEqualTo(12);
        await Assert.That(line.GetProperty("occurred_at").GetString()).IsNotNull();
    }

    [Test]
    public async Task A_compact_item_becomes_the_prefix_plus_its_own_text_byte_for_byte() {
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(12, T0, "She said \"done\" — 日本語", "7c1e")).Write();

        using var page = JsonDocument.Parse(File.ReadAllBytes(Path.Join(dir, "page-000.json")));
        var raw = page.RootElement.GetProperty("turns")[0].GetProperty("items")[0].GetRawText();

        await Assert.That(MuseAssistantWalkReader.Load(dir).Walk!.Items[0].Line).IsEqualTo("{\"chat_id\":\"chat-main\",\"turn_id\":\"7c1e\"," + raw[1..]);
    }

    [Test]
    public async Task A_pretty_printed_page_still_yields_one_line_per_item() {
        var content = "two\nlines, \"quoted\", 日本語";
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(12, T0, content)).Write(tamper: d => {
            d.CreateFile("page-000.json", JsonNode.Parse(File.ReadAllText(d.PathTo("page-000.json")))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        });

        var (walk, problem) = MuseAssistantWalkReader.Load(dir);
        var line = walk!.Items[0].Line;

        await Assert.That(problem).IsNull();
        await Assert.That(line.Contains('\n') || line.Contains('\r')).IsFalse();
        await Assert.That(JsonDocument.Parse(line).RootElement.GetProperty("content").GetString()).IsEqualTo(content);
        await Assert.That(JsonDocument.Parse(line).RootElement.GetProperty("chat_id").GetString()).IsEqualTo("chat-main");
    }

    [Test]
    public async Task A_page_whose_cursor_is_not_the_previous_next_cursor_is_refused() {
        var dir = TwoPages().Write(tamper: d => Rewrite(d, "manifest.json", m => m["pages"]![1]!["cursor"] = "stale"));

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).Contains("next_cursor");
    }

    [Test]
    public async Task A_chain_that_skips_a_page_is_refused() {
        var b = new WalkBuilder(Tmp)
            .AddPage(null, "c1", true, User(30, T0 + 30))
            .AddPage("c2", null, false, User(10, T0));

        await Assert.That(MuseAssistantWalkReader.Load(b.Write()).Problem).Contains("next_cursor");
    }

    [Test]
    public async Task A_page_following_has_more_false_is_refused() {
        var b = new WalkBuilder(Tmp)
            .AddPage(null, "c1", false, User(30, T0 + 30))
            .AddPage("c1", null, false, User(10, T0));

        await Assert.That(MuseAssistantWalkReader.Load(b.Write()).Problem).Contains("has_more");
    }

    [Test]
    public async Task Page_000_fetched_with_a_cursor_is_refused() {
        var dir = TwoPages().Write(tamper: d => Rewrite(d, "manifest.json", m => m["pages"]![0]!["cursor"] = "c0"));

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).Contains("page-000");
    }

    [Test]
    public async Task A_missing_page_file_or_an_extra_file_is_refused() {
        var missing  = TwoPages().Write("missing", d => File.Delete(d.PathTo("page-001.json")));
        var extra    = TwoPages().Write("extra", d => d.CreateFile("notes.txt", "x"));
        var unlisted = TwoPages().Write("unlisted", d => d.CreateFile("page-002.json", File.ReadAllText(d.PathTo("page-001.json"))));

        await Assert.That(MuseAssistantWalkReader.Load(missing).Problem).Contains("missing");
        await Assert.That(MuseAssistantWalkReader.Load(extra).Problem).Contains("notes.txt");
        await Assert.That(MuseAssistantWalkReader.Load(unlisted).Problem).Contains("page-002.json");
    }

    [Test]
    public async Task Manifest_without_fetched_at_or_is_main_or_with_a_non_positive_fetched_at_is_refused() {
        var noFetch = new WalkBuilder(Tmp) { FetchedAtMs = null }.AddPage(null, null, false, User(1, T0)).Write("a");
        var zero    = new WalkBuilder(Tmp) { FetchedAtMs = 0 }.AddPage(null, null, false, User(1, T0)).Write("b");
        var noMain  = new WalkBuilder(Tmp) { IsMain = null }.AddPage(null, null, false, User(1, T0)).Write("c");

        await Assert.That(MuseAssistantWalkReader.Load(noFetch).Problem).Contains("fetched_at_ms");
        await Assert.That(MuseAssistantWalkReader.Load(zero).Problem).Contains("fetched_at_ms");
        await Assert.That(MuseAssistantWalkReader.Load(noMain).Problem).Contains("is_main");
    }

    [Test]
    public async Task A_selected_item_with_missing_empty_or_non_string_content_is_refused_while_a_developer_item_may_lack_it() {
        var empty   = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0, "")).Write("empty");
        var missing = new WalkBuilder(Tmp).AddPage(null, null, false, new Item("t1", "assistant", 1, T0, null)).Write("missing");
        var array   = new WalkBuilder(Tmp).AddPage(null, null, false, new Item("t1", "commentary", 1, T0, new JsonArray("a", "b"))).Write("array");
        var dev     = new WalkBuilder(Tmp).AddPage(null, null, false, new Item("t1", "developer", 1, T0, null), User(2, T0 + 1)).Write("dev");

        await Assert.That(MuseAssistantWalkReader.Load(empty).Problem).Contains("content");
        await Assert.That(MuseAssistantWalkReader.Load(missing).Problem).Contains("content");
        await Assert.That(MuseAssistantWalkReader.Load(array).Problem).Contains("content");
        await Assert.That(MuseAssistantWalkReader.Load(dev).Problem).IsNull();
    }

    [Test]
    public async Task A_seq_that_is_not_an_integer_or_reaches_the_untrackable_range_is_refused() {
        var huge = new WalkBuilder(Tmp).AddPage(null, null, false, User(3_000_000_000L, T0)).Write("huge");
        var atCeiling = new WalkBuilder(Tmp).AddPage(null, null, false, User(1_000_000_000L, T0)).Write("ceiling");
        var belowCeiling = new WalkBuilder(Tmp).AddPage(null, null, false, User(999_999_999L, T0)).Write("below");
        var frac = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write("frac", d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["seq"] = 1.5));
        var text = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write("text", d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["seq"] = "12"));

        await Assert.That(MuseAssistantWalkReader.Load(huge).Problem).Contains("seq");
        await Assert.That(MuseAssistantWalkReader.Load(atCeiling).Problem).Contains("is at or above 1000000000, which the server cannot track");
        await Assert.That(MuseAssistantWalkReader.Load(belowCeiling).Problem).IsNull();
        await Assert.That(MuseAssistantWalkReader.Load(frac).Problem).Contains("seq");
        await Assert.That(MuseAssistantWalkReader.Load(text).Problem).Contains("seq");
    }

    [Test]
    public async Task An_item_without_a_role_or_an_integer_occurred_at_ms_is_refused_even_when_it_is_not_selected() {
        var noRole = new WalkBuilder(Tmp).AddPage(null, null, false, Developer(1, T0)).Write("role", d => Rewrite(d, "page-000.json", p => ((JsonObject)p["turns"]![0]!["items"]![0]!).Remove("role")));
        var noAt   = new WalkBuilder(Tmp).AddPage(null, null, false, Developer(1, T0)).Write("at", d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["occurred_at_ms"] = "soon"));

        await Assert.That(MuseAssistantWalkReader.Load(noRole).Problem).Contains("role");
        await Assert.That(MuseAssistantWalkReader.Load(noAt).Problem).Contains("occurred_at_ms");
    }

    [Test]
    public async Task An_occurred_at_ms_outside_the_representable_time_range_is_refused() {
        static string At(WalkBuilder b, string name, long at) =>
            b.AddPage(null, null, false, Developer(1, T0)).Write(name, d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["occurred_at_ms"] = at));

        var late  = At(new WalkBuilder(Tmp), "late", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() + 1);
        var early = At(new WalkBuilder(Tmp), "early", DateTimeOffset.MinValue.ToUnixTimeMilliseconds() - 1);
        var max   = At(new WalkBuilder(Tmp), "max", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());

        await Assert.That(MuseAssistantWalkReader.Load(late).Problem).Contains("occurred_at_ms").And.Contains("outside");
        await Assert.That(MuseAssistantWalkReader.Load(early).Problem).Contains("occurred_at_ms").And.Contains("outside");
        await Assert.That(MuseAssistantWalkReader.Load(max).Problem).IsNull();
    }

    [Test]
    public async Task Out_of_order_or_duplicate_items_and_overlapping_pages_are_refused() {
        var dup     = new WalkBuilder(Tmp).AddPage(null, null, false, User(5, T0), Assistant(5, T0 + 1)).Write("dup");
        var time    = new WalkBuilder(Tmp).AddPage(null, null, false, User(5, T0 + 10), Assistant(6, T0)).Write("time");
        var overlap = new WalkBuilder(Tmp).AddPage(null, "c1", true, User(20, T0 + 20), User(30, T0 + 30)).AddPage("c1", null, false, User(25, T0 + 25)).Write("overlap");

        await Assert.That(MuseAssistantWalkReader.Load(dup).Problem).Contains("twice");
        await Assert.That(MuseAssistantWalkReader.Load(time).Problem).Contains("earlier");
        await Assert.That(MuseAssistantWalkReader.Load(overlap).Problem).Contains("overlap");
    }

    [Test]
    public async Task A_page_whose_chat_id_differs_or_lacks_page_or_turns_is_refused() {
        var chat  = TwoPages().Write("chat", d => Rewrite(d, "page-001.json", p => p["chat_id"] = "other"));
        var page  = TwoPages().Write("page", d => Rewrite(d, "page-000.json", p => p.Remove("page")));
        var turns = TwoPages().Write("turns", d => Rewrite(d, "page-000.json", p => p.Remove("turns")));

        await Assert.That(MuseAssistantWalkReader.Load(chat).Problem).Contains("chat_id");
        await Assert.That(MuseAssistantWalkReader.Load(page).Problem).Contains("page object");
        await Assert.That(MuseAssistantWalkReader.Load(turns).Problem).Contains("turns");
    }

    [Test]
    public async Task An_empty_middle_page_does_not_hide_an_overlap_between_its_neighbours() {
        var b = new WalkBuilder(Tmp)
            .AddPage(null, "c1", true, User(20, T0 + 20), User(30, T0 + 30))
            .AddPage("c1", "c2", true)
            .AddPage("c2", null, false, User(25, T0 + 25));

        await Assert.That(MuseAssistantWalkReader.Load(b.Write()).Problem).Contains("overlap");
    }

    [Test]
    public async Task A_lone_surrogate_escape_in_a_page_is_a_problem_not_an_exception() {
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write(tamper: d => {
            d.CreateFile("page-000.json", File.ReadAllText(d.PathTo("page-000.json")).Replace("\"content\":\"hello\"", "\"content\":\"\\ud800\""));
        });

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).Contains("UTF-16");
    }

    [Test]
    public async Task A_page_that_is_not_valid_utf8_is_refused_instead_of_being_repaired() {
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write(tamper: d => {
            var path  = d.PathTo("page-000.json");
            var bytes = File.ReadAllBytes(path);
            var at    = bytes.AsSpan().IndexOf("hello"u8);
            bytes[at] = 0xFF;
            File.WriteAllBytes(path, bytes);
        });

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).Contains("UTF-8");
    }

    [Test]
    public async Task A_null_page_entry_in_the_manifest_is_a_problem() {
        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write(tamper: d =>
            d.CreateFile("manifest.json", """{"chat_id":"chat-main","is_main":true,"fetched_at_ms":1760000500000,"pages":[null]}"""));

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).IsNotNull();
    }

    [Test]
    public async Task An_item_carrying_its_own_chat_or_turn_id_is_refused() {
        var chat = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write("chat", d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["chat_id"] = "other"));
        var turn = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write("turn", d => Rewrite(d, "page-000.json", p => p["turns"]![0]!["items"]![0]!["turn_id"] = "other"));

        await Assert.That(MuseAssistantWalkReader.Load(chat).Problem).Contains("must not carry");
        await Assert.That(MuseAssistantWalkReader.Load(turn).Problem).Contains("must not carry");
    }

    [Test]
    public async Task A_directory_that_cannot_be_listed_is_a_problem_not_an_exception() {
        Skip.Unless(!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess, "needs POSIX permissions and an unprivileged user");
        if (OperatingSystem.IsWindows()) return;

        var dir = new WalkBuilder(Tmp).AddPage(null, null, false, User(1, T0)).Write();
        File.SetUnixFileMode(dir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try {
            await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).IsNotNull();
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task An_item_newer_than_the_fetch_time_validates() {
        var dir = new WalkBuilder(Tmp) { FetchedAtMs = T0 }.AddPage(null, null, false, User(1, T0 + 60_000)).Write();

        await Assert.That(MuseAssistantWalkReader.Load(dir).Problem).IsNull();
    }

    [Test]
    public async Task An_empty_chat_is_a_valid_walk_with_no_items() {
        var (walk, problem) = MuseAssistantWalkReader.Load(new WalkBuilder(Tmp).AddPage(null, null, false).Write());

        await Assert.That(problem).IsNull();
        await Assert.That(walk!.Items).IsEmpty();
        await Assert.That(walk.OldestPageMinSeq).IsNull();
    }

    [Test]
    public async Task A_directory_that_is_not_a_walk_has_no_chat_id_and_a_problem() {
        await Assert.That(MuseAssistantWalkReader.ReadChatId(Tmp.PathTo("nope"))).IsNull();
        await Assert.That(MuseAssistantWalkReader.Load(Tmp.PathTo("nope")).Problem).IsNotNull();
    }

    static void Rewrite(TempDirHandle dir, string file, Action<JsonObject> edit) {
        var node = JsonNode.Parse(File.ReadAllText(dir.PathTo(file)))!.AsObject();
        edit(node);
        dir.CreateFile(file, node.ToJsonString());
    }
}
