using System.Globalization;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexSessionIndexTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Id = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    [Test]
    public async Task Last_line_for_the_session_wins_with_its_time() {
        Tmp.CreateFile("session_index.jsonl", string.Join('\n',
            $$"""{"id":"{{Id}}","thread_name":"First","updated_at":"2026-09-29T10:00:00Z"}""",
            """{"id":"00000000-0000-0000-0000-000000000001","thread_name":"Other","updated_at":"2026-09-29T11:00:00Z"}""",
            $$"""{"id":"{{Id}}","thread_name":"Renamed","updated_at":"2026-09-29T12:00:00Z"}""") + "\n");

        var title = new CodexSessionIndexTitle(Tmp.Path, Id.Replace("-", "")).Read();

        await Assert.That(title).IsEqualTo(new StoreTitle("Renamed", HarnessTitleKind.Rename, DateTimeOffset.Parse("2026-09-29T12:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Test]
    public async Task Missing_file_or_session_reads_null() {
        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, "ffffffffffffffffffffffffffffffff").Read()).IsNull();
    }

    [Test]
    public async Task Malformed_lines_and_blank_names_are_skipped() {
        Tmp.CreateFile("session_index.jsonl", $$"""{"id":"{{Id}}","thread_name":"Good","updated_at":"2026-09-29T10:00:00Z"}""" + "\n{oops\n"
            + $$"""{"id":"{{Id}}","thread_name":"  ","updated_at":"2026-09-29T11:00:00Z"}""" + "\n");

        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, Id.Replace("-", "")).Read()!.Title).IsEqualTo("Good");
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Tmp.CreateFile("session_index.jsonl", $$"""{"id":"{{Id}}","thread_name":"Live","updated_at":"2026-09-29T10:00:00Z"}""" + "\n");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, Id.Replace("-", "")).Read()!.Title).IsEqualTo("Live");
    }

    [Test]
    public async Task Records_change_time_is_true() {
        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, Id.Replace("-", "")).RecordsChangeTime).IsTrue();
    }

    [Test]
    public async Task Dashed_session_id_is_accepted() {
        Tmp.CreateFile("session_index.jsonl", $$"""{"id":"{{Id}}","thread_name":"Dashed","updated_at":"2026-09-29T10:00:00Z"}""" + "\n");

        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, Id).Read()!.Title).IsEqualTo("Dashed");
    }

    [Test]
    [Arguments("")]
    [Arguments("abc")]
    [Arguments("not-a-guid-but-long-enough-to-slice")]
    public async Task Short_or_non_guid_id_reads_null_without_throwing(string sessionId) {
        Tmp.CreateFile("session_index.jsonl", $$"""{"id":"{{Id}}","thread_name":"Any","updated_at":"2026-09-29T10:00:00Z"}""" + "\n");

        await Assert.That(new CodexSessionIndexTitle(Tmp.Path, sessionId).Read()).IsNull();
    }

    /// <summary>A same-length rewrite with the original write time restored is invisible to the memo, so reading the
    /// old value back proves the unchanged file was not parsed again; the append then proves a real change is.</summary>
    [Test]
    public async Task Unchanged_file_is_not_re_parsed() {
        var path  = Tmp.CreateFile("session_index.jsonl", $$"""{"id":"{{Id}}","thread_name":"AAAA","updated_at":"2026-09-29T10:00:00Z"}""" + "\n");
        var store = new CodexSessionIndexTitle(Tmp.Path, Id);

        await Assert.That(store.Read()!.Title).IsEqualTo("AAAA");

        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("AAAA", "BBBB"));
        File.SetLastWriteTimeUtc(path, stamp);

        await Assert.That(store.Read()!.Title).IsEqualTo("AAAA");

        File.AppendAllText(path, $$"""{"id":"{{Id}}","thread_name":"Appended","updated_at":"2026-09-29T11:00:00Z"}""" + "\n");

        await Assert.That(store.Read()!.Title).IsEqualTo("Appended");
    }

    [Test]
    public async Task ReadAll_keeps_the_last_named_line_per_session_and_skips_malformed_ones() {
        const string other = "00000000-0000-0000-0000-000000000001";
        Tmp.CreateFile("session_index.jsonl", string.Join('\n',
            $$"""{"id":"{{Id}}","thread_name":"First","updated_at":"2026-09-29T10:00:00Z"}""",
            "{oops",
            """["not","an","object"]""",
            """{"id":"not-a-guid","thread_name":"Nobody"}""",
            $$"""{"id":"{{other}}","thread_name":"Other"}""",
            $$"""{"id":"{{Id}}","thread_name":"Renamed","updated_at":"2026-09-29T12:00:00Z"}""",
            $$"""{"id":"{{Id}}","thread_name":"  ","updated_at":"2026-09-29T13:00:00Z"}""") + "\n");

        var all = CodexSessionIndexTitle.ReadAll(Tmp.Path);

        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all[Id.Replace("-", "")]).IsEqualTo(new StoreTitle("Renamed", HarnessTitleKind.Rename, DateTimeOffset.Parse("2026-09-29T12:00:00Z", CultureInfo.InvariantCulture)));
        await Assert.That(all[other.Replace("-", "")]).IsEqualTo(new StoreTitle("Other", HarnessTitleKind.Rename, null));
    }

    [Test]
    public async Task ReadAll_is_empty_without_an_index() {
        await Assert.That(CodexSessionIndexTitle.ReadAll(Tmp.Path).Count).IsEqualTo(0);
    }
}
