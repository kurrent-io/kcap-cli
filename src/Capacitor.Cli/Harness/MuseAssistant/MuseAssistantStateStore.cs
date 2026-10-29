using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>The files under one profile's scope directory. Every write goes to a temp file, is flushed to
/// disk and renamed over the target, so a crash mid-write leaves the previous state rather than a torn
/// one. A file whose stored server or user disagrees with the directory is refused: it was copied here,
/// and running it would post another account's intents.</summary>
public class MuseAssistantStateStore(string scopeDir, MuseAssistantPrincipal principal) {
    public string ScopeDir => scopeDir;

    MuseAssistantScopeFile EmptyScope => new(principal.ServerUrl, principal.UserId, MuseAssistantRoles.Selected, []);

    public MuseAssistantScopeFile LoadScope() {
        var path = MuseAssistantPaths.ScopeFile(scopeDir);
        if (!File.Exists(path)) return EmptyScope;

        try {
            var scope = JsonSerializer.Deserialize(File.ReadAllText(path), MuseAssistantJsonContext.Default.MuseAssistantScopeFile);

            return scope is not null && scope.ServerUrl == principal.ServerUrl && scope.UserId == principal.UserId && scope.Chats is not null
                ? scope
                : EmptyScope;
        } catch (JsonException) {
            return EmptyScope;
        }
    }

    public void SaveScope(MuseAssistantScopeFile scope) =>
        WriteAtomic(
            MuseAssistantPaths.ScopeFile(scopeDir),
            JsonSerializer.Serialize(scope with { ServerUrl = principal.ServerUrl, UserId = principal.UserId, Roles = MuseAssistantRoles.Selected }, MuseAssistantJsonContext.Default.MuseAssistantScopeFile));

    public (MuseAssistantChatState? State, string? Problem) LoadState(string chatId) {
        var path = MuseAssistantPaths.ChatState(scopeDir, chatId);
        if (!File.Exists(path)) return (null, null);

        MuseAssistantChatState? state;
        try {
            state = JsonSerializer.Deserialize(File.ReadAllText(path), MuseAssistantJsonContext.Default.MuseAssistantChatState);
        } catch (JsonException e) {
            return (null, $"the state file for this chat is unreadable: {e.Message}");
        }

        if (state is null) return (null, "the state file for this chat is empty");
        if (state.ChatId != chatId || state.ServerUrl != principal.ServerUrl || state.UserId != principal.UserId)
            return (null, "the state file for this chat belongs to another chat, tenant or account");
        if (!IsWellFormed(state.Grant))
            return (null, "the state file for this chat holds a grant `scope add` could not have written");

        return (state, null);
    }

    // Any shape but the ones scope add writes has no floor, so it would read as consent to the whole chat.
    static bool IsWellFormed(MuseAssistantGrant? grant) =>
        grant is { Id.Length: > 0 } && grant.Boundary switch {
            MuseAssistantGrantBoundaries.AfterSeq                                          => grant.AfterSeq is >= 0,
            MuseAssistantGrantBoundaries.EmptyChat or MuseAssistantGrantBoundaries.History => grant.AfterSeq is null,
            _                                                                              => false
        };

    /// <summary>Renames an unusable state file out of the <c>*.json</c> set so the chat can be granted
    /// afresh while the file stays as evidence. Returns the new file name; the caller holds the chat lock.</summary>
    public string SetAsideState(string chatId, long nowMs) => SetAside(MuseAssistantPaths.ChatState(scopeDir, chatId), nowMs);

    /// <inheritdoc cref="SetAsideState"/>
    public string SetAside(string path, long nowMs) {
        for (var stamp = nowMs;; stamp++) {
            var aside = $"{path}.unusable-{stamp}";
            if (File.Exists(aside)) continue;

            File.Move(path, aside);
            return Path.GetFileName(aside);
        }
    }

    public virtual void SaveState(MuseAssistantChatState state) {
        if (state.ServerUrl != principal.ServerUrl || state.UserId != principal.UserId)
            throw new InvalidOperationException("state for another tenant or account");

        WriteAtomic(MuseAssistantPaths.ChatState(scopeDir, state.ChatId), JsonSerializer.Serialize(state, MuseAssistantJsonContext.Default.MuseAssistantChatState));
    }

    /// <summary>Every readable state file that belongs here, by the name its own chat id would give it.</summary>
    public IEnumerable<MuseAssistantChatState> EnumerateStates() =>
        ScanStateFiles().Where(f => f.State is not null).Select(f => f.State!);

    /// <summary>Every other <c>*.json</c> beside <c>scope.json</c>: unreadable, malformed, another account's, or misnamed.</summary>
    public IEnumerable<string> EnumerateUnusableStateFiles() =>
        ScanStateFiles().Where(f => f.State is null).Select(f => f.File);

    IEnumerable<(string File, MuseAssistantChatState? State)> ScanStateFiles() {
        if (!Directory.Exists(scopeDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(scopeDir, "*.json").Order(StringComparer.Ordinal)) {
            var name = Path.GetFileName(file);
            if (name == "scope.json") continue;

            MuseAssistantChatState? state;
            try {
                state = JsonSerializer.Deserialize(File.ReadAllText(file), MuseAssistantJsonContext.Default.MuseAssistantChatState);
            } catch (JsonException) {
                state = null;
            }

            var usable = state is not null && state.ServerUrl == principal.ServerUrl && state.UserId == principal.UserId
                      && name == MuseAssistantPaths.ChatKey(state.ChatId) + ".json" && IsWellFormed(state.Grant);

            yield return (file, usable ? state : null);
        }
    }

    const int ReplaceMaxAttempts   = 10;
    const int ReplaceBackoffBaseMs = 20;

    static void WriteAtomic(string path, string json) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";

        try {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(true);
            }

            // Some reads take no lock (the ingest's scope check, a busy remove's state check), and on
            // Windows a reader holding the target open fails the replace until it closes.
            for (var attempt = 1; ; attempt++) {
                try {
                    File.Move(temp, path, overwrite: true);
                    return;
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < ReplaceMaxAttempts) {
                    Thread.Sleep(ReplaceBackoffBaseMs * attempt + Random.Shared.Next(ReplaceBackoffBaseMs));
                }
            }
        } finally {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
