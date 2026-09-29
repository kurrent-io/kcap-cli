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
}
