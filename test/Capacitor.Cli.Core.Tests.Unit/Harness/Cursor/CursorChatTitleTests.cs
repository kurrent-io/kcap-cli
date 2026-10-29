using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorChatTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionId = "7fa8b9da-fb08-481c-8cbf-6b528542babd";

    [Test]
    public async Task Real_title_is_read_as_a_rename() {
        Tmp.CreateFile(["ws", SessionId, "meta.json"], """{"title":"Real"}""");

        var title = new CursorChatTitle(Tmp.Path, SessionId).Read();

        await Assert.That(title).IsEqualTo(new StoreTitle("Real", HarnessTitleKind.Rename, null));
    }

    [Test]
    public async Task Default_title_reads_null() {
        Tmp.CreateFile(["ws", SessionId, "meta.json"], """{"title":"New Agent"}""");

        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Missing_title_reads_null() {
        Tmp.CreateFile(["ws", SessionId, "meta.json"], "{}");

        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Missing_file_reads_null() {
        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).Read()).IsNull();
    }

    [Test]
    public async Task Finds_the_session_under_whichever_workspace_hash_holds_it() {
        Tmp.CreateFile(["other-ws", "some-other-id", "meta.json"], """{"title":"Not this one"}""");
        Tmp.CreateFile(["ws-with-it", SessionId, "meta.json"], """{"title":"Found it"}""");

        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).Read()!.Title).IsEqualTo("Found it");
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).RecordsChangeTime).IsFalse();
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Tmp.CreateFile(["ws", SessionId, "meta.json"], """{"title":"Live"}""");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(new CursorChatTitle(Tmp.Path, SessionId).Read()!.Title).IsEqualTo("Live");
    }

    [Test]
    public async Task ForTranscript_derives_the_session_id_from_the_transcript_file_name() {
        Tmp.CreateFile(["ws", SessionId, "meta.json"], """{"title":"From transcript"}""");
        var transcriptPath = Path.Combine("agent-transcripts", SessionId, $"{SessionId}.jsonl");

        var title = CursorChatTitle.ForTranscript(Tmp.Path, transcriptPath).Read();

        await Assert.That(title!.Title).IsEqualTo("From transcript");
    }
}
