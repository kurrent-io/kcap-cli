using System.Text.Json;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>Cursor's per-chat <c>meta.json</c> at <c>chats/&lt;workspace-hash&gt;/&lt;session-id&gt;/meta.json</c> —
/// the session id directory name matches the id under <see cref="CursorPaths.ProjectsDir"/>'s
/// agent-transcripts, confirmed against a live install. <c>title</c> is absent until the user (or the
/// IDE's default) names the chat; <c>"New Agent"</c> is the un-renamed default and reads as no title.
/// <c>updatedAtMs</c> tracks ordinary conversation activity as well as renames on this install
/// (it moves with the transcript's own last-write time), so it is not a reliable rename timestamp —
/// this reader never reports a change time.</summary>
public sealed class CursorChatTitle(string chatsDir, string dashedSessionId) : IHarnessTitleStore {
    /// <summary>The session id is the transcript file's own name (without extension) — the same id
    /// that names its directory under <c>agent-transcripts</c>.</summary>
    public static CursorChatTitle ForTranscript(string chatsDir, string transcriptPath) =>
        new(chatsDir, Path.GetFileNameWithoutExtension(transcriptPath));

    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        try {
            if (!Directory.Exists(chatsDir)) return null;

            foreach (var workspaceDir in Directory.EnumerateDirectories(chatsDir)) {
                var metaPath = Path.Combine(workspaceDir, dashedSessionId, "meta.json");
                if (!File.Exists(metaPath)) continue;

                try {
                    using var doc   = JsonDocument.Parse(File.ReadAllTextShared(metaPath));
                    var       title = doc.RootElement.Str("title");

                    if (string.IsNullOrWhiteSpace(title) || title == "New Agent") return null;

                    return new StoreTitle(title.Trim(), HarnessTitleKind.Rename, null);
                } catch (JsonException) {
                    return null;
                }
            }
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }

        return null;
    }
}
