using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>The turns a watched Codex session declares: only acknowledged human lines, each named by the hash of
/// the line as the watcher sent it, and none at all when that cannot be known.</summary>
public class CodexJudgeDeclarationReaderTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly string Sid = Guid.NewGuid().ToString("N");

    WatchAckCursor Cursor => new(Config.Root);

    static string User(string text, string turn, params string[] kinds) => JsonSerializer.Serialize(new {
        timestamp = "2026-10-21T02:10:01.206Z", type = "response_item",
        payload = new {
            type = "message", role = "user", content = new[] { new { type = "input_text", text } },
            internal_chat_message_metadata_passthrough = new { turn_id = turn, content_item_kinds = kinds }
        }
    });

    const string Assistant = """{"timestamp":"2026-10-21T02:10:02.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}""";

    string Rollout(params string[] lines) => Tmp.CreateFile("rollout.jsonl", string.Join('\n', lines) + "\n");

    Task<CodexDeclaration> Read(string path, TimeSpan? budget = null) =>
        CodexJudgeDeclarationReader.ReadAsync(path, Sid, Cursor, TimeProvider.System, budget ?? TimeSpan.Zero, CancellationToken.None);

    [Test]
    public async Task Declares_only_acknowledged_human_lines_and_never_a_prelude() {
        var first  = User("fix the build", "t1", "user.text");
        var second = User("now ship it", "t2", "user.text");
        var path = Rollout(User("# AGENTS.md instructions for /repo", "t1", "agents_md.instructions"), first, Assistant, second);
        Cursor.Write(Sid, 4);

        var declared = await Read(path);

        await Assert.That(declared.WindowlessReason).IsNull();
        await Assert.That(declared.Turns!.UserMessages.Select(m => m.Id))
            .IsEquivalentTo(new[] { TranscriptIds.CodexRecord(first).ToString(), TranscriptIds.CodexRecord(second).ToString() });
        await Assert.That(declared.Turns.UserMessages.Select(m => m.PromptId)).IsEquivalentTo(new string?[] { "t1", "t2" });
    }

    /// The server cannot see a line it has not stored, so declaring only the older turns would let a stale prompt
    /// stand in for the newest one.
    [Test]
    public async Task An_unacknowledged_newest_human_line_makes_the_set_windowless_once_the_wait_is_spent() {
        var path = Rollout(User("fix the build", "t1", "user.text"), User("now ship it", "t2", "user.text"));
        Cursor.Write(Sid, 1);

        var declared = await Read(path);

        await Assert.That(declared.Turns).IsNull();
        await Assert.That(declared.WindowlessReason).IsEqualTo("newest_turn_unacknowledged");
    }

    /// A human line written while the reader waits counts too: the count covering the line the wait began on is not enough.
    [Test]
    public async Task A_human_line_appended_during_the_wait_makes_the_set_windowless_until_acknowledged() {
        var path = Rollout(User("fix the build", "t1", "user.text"), User("now ship it", "t2", "user.text"));
        Cursor.Write(Sid, 1);

        var reading = Read(path, TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        await File.AppendAllTextAsync(path, User("actually, stop", "t3", "user.text") + "\n");
        Cursor.Write(Sid, 2);

        await Assert.That((await reading).WindowlessReason).IsEqualTo("newest_turn_unacknowledged");
    }

    [Test]
    public async Task Waits_for_the_count_to_cover_the_newest_human_line() {
        var path = Rollout(User("fix the build", "t1", "user.text"), User("now ship it", "t2", "user.text"));
        Cursor.Write(Sid, 1);

        var reading = Read(path, TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        Cursor.Write(Sid, 2);

        await Assert.That((await reading).Turns!.UserMessages.Length).IsEqualTo(2);
    }

    /// A cursor being replaced cannot be read for a moment; the wait has to outlast that rather than end as if the
    /// session had no watcher. An unreadable file stands in for the replace, which only Windows makes observable.
    [Test]
    public async Task A_cursor_that_cannot_be_read_for_a_moment_is_polled_again() {
        Skip.Unless(!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess, "needs POSIX permissions and an unprivileged user");
        if (OperatingSystem.IsWindows()) return;

        var path = Rollout(User("fix the build", "t1", "user.text"), User("now ship it", "t2", "user.text"));
        Cursor.Write(Sid, 2);
        var file = Cursor.Path(Sid);
        File.SetUnixFileMode(file, UnixFileMode.None);

        var reading = Read(path, TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.That((await reading).Turns!.UserMessages.Length).IsEqualTo(2);
    }

    [Test]
    public async Task A_redacted_line_is_declared_by_the_hash_of_its_redacted_bytes() {
        var raw  = User("use ghp_0123456789abcdefghijklmnopqrstuvwxyz to push", "t1", "user.text");
        var path = Rollout(raw);
        Cursor.Write(Sid, 1);

        var id = (await Read(path)).Turns!.UserMessages.Single().Id;

        await Assert.That(id).IsEqualTo(TranscriptIds.CodexRecord(SecretRedactor.RedactLine(raw)).ToString());
        await Assert.That(id).IsNotEqualTo(TranscriptIds.CodexRecord(raw).ToString());
    }

    [Test]
    public async Task A_line_the_redactor_could_not_finish_makes_the_set_windowless() {
        var path = Rollout(User(new string('x', SecretRedactor.MaxRecordBytes + 1), "t1", "user.text"));
        Cursor.Write(Sid, 1);

        await Assert.That((await Read(path)).WindowlessReason).IsEqualTo("redaction_incomplete");
    }

    /// The reader must find a human line however its JSON is spaced, or it declares a stale set.
    [Test]
    public async Task A_spaced_json_line_is_read_like_a_compact_one() {
        var spaced = User("fix the build", "t1", "user.text").Replace("\":", "\": ", StringComparison.Ordinal);
        var path   = Rollout(spaced);
        Cursor.Write(Sid, 1);

        await Assert.That((await Read(path)).Turns!.UserMessages.Single().Id).IsEqualTo(TranscriptIds.CodexRecord(spaced).ToString());
    }

    [Test]
    public async Task No_count_is_windowless() =>
        await Assert.That((await Read(Rollout(User("fix", "t1", "user.text")))).WindowlessReason).IsEqualTo("no_ack_cursor");
}
