using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Writes a walk directory as the cron would: numbered pages in the real
/// <c>chat.read_messages</c> envelope plus the manifest naming each page's cursor.</summary>
sealed class WalkBuilder(TempDir tmp) {
    public sealed record Item(string Turn, string Role, long Seq, long At, object? Content);
    public sealed record Page(string? Cursor, string? NextCursor, bool HasMore, Item[] Items);

    public string ChatId = "chat-main";
    public bool? IsMain = true;
    public long? FetchedAtMs = 1_760_000_500_000;
    public readonly List<Page> Pages = [];

    public WalkBuilder AddPage(string? cursor, string? nextCursor, bool hasMore, params Item[] items) {
        Pages.Add(new(cursor, nextCursor, hasMore, items));
        return this;
    }

    public static Item User(long seq, long at, string content = "hello", string turn = "t1") => new(turn, "user", seq, at, content);
    public static Item Assistant(long seq, long at, string content = "hi there", string turn = "t1") => new(turn, "assistant", seq, at, content);
    public static Item Commentary(long seq, long at, string content = "thinking", string turn = "t1") => new(turn, "commentary", seq, at, content);
    public static Item Developer(long seq, long at, string content = "runtime instruction", string turn = "t1") => new(turn, "developer", seq, at, content);

    public string Write(string name = "walk", Action<TempDirHandle>? tamper = null) {
        var dir   = tmp.CreateDir(name);
        var pages = new JsonArray();

        for (var i = 0; i < Pages.Count; i++) {
            var page  = Pages[i];
            var turns = new JsonArray();

            foreach (var turn in page.Items.GroupBy(x => x.Turn)) {
                var items = new JsonArray();
                foreach (var item in turn) {
                    var o = new JsonObject { ["role"] = item.Role, ["seq"] = item.Seq, ["occurred_at_ms"] = item.At,
                        ["occurred_at"] = DateTimeOffset.FromUnixTimeMilliseconds(item.At).ToString("O") };
                    if (item.Content is string s) o["content"] = s;
                    else if (item.Content is JsonNode n) o["content"] = n;
                    items.Add(o);
                }
                turns.Add(new JsonObject { ["id"] = turn.Key, ["items"] = items });
            }

            var body = new JsonObject {
                ["chat_id"] = ChatId,
                ["page"]    = new JsonObject { ["has_more"] = page.HasMore, ["next_cursor"] = page.NextCursor, ["limit"] = 20, ["order"] = "newest_first" },
                ["turns"]   = turns
            };
            dir.CreateFile($"page-{i:000}.json", body.ToJsonString());
            pages.Add(new JsonObject { ["file"] = $"page-{i:000}.json", ["cursor"] = page.Cursor });
        }

        var manifest = new JsonObject { ["chat_id"] = ChatId, ["pages"] = pages };
        if (IsMain is { } main) manifest["is_main"] = main;
        if (FetchedAtMs is { } at) manifest["fetched_at_ms"] = at;
        dir.CreateFile("manifest.json", manifest.ToJsonString());

        tamper?.Invoke(dir);
        return dir.Path;
    }
}
