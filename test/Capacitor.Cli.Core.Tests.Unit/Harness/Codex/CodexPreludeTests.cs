using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexPreludeTests {
    /// The server pins the same file: a turn this side declares as human must be one the server counts as human,
    /// or a declared turn set never verifies.
    [Test]
    public async Task Codex_user_lines_classify_as_the_fixture_says() {
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", "codex-user-lines.jsonl"))) {
            using var doc     = JsonDocument.Parse(line);
            var       payload = doc.RootElement.GetProperty("payload");
            var       text    = payload.GetProperty("content")[0].GetProperty("text").GetString()!;
            IReadOnlyList<string>? kinds = payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var pass)
                ? pass.GetProperty("content_item_kinds").EnumerateArray().Select(k => k.GetString()!).ToList()
                : null;

            await Assert.That(CodexPrelude.IsHuman(text, kinds)).IsEqualTo(doc.RootElement.GetProperty("expect_human").GetBoolean()).Because(line);
        }
    }
}
