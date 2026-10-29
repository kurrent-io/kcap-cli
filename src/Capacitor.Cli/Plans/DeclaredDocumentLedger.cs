using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Plans;

/// <summary>The documents a session declared, by full local path, with the arguments that declared
/// them and the server that accepted them, so an edit can declare the file again: the server holds
/// only the snapshot it was sent. Best effort throughout: a lost entry costs one stale snapshot,
/// never the tool call or the hook.</summary>
sealed class DeclaredDocumentLedger(ConfigRoot config, TimeProvider time) {
    /// <summary>How long a session keeps its entries after it last declared or edited one. Nothing
    /// evicts them at session end, because a resumed Claude session keeps its id.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public void Record(string sessionId, DeclaredDocument document, string serverUrl) {
        try {
            Reap();
            var entry = Entry(sessionId, document.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            var json = document.ToJson();
            json["server_url"] = serverUrl;
            File.WriteAllText(entry, json.ToJsonString());
            Touch(sessionId);
        } catch { }
    }

    /// <summary>Null unless <paramref name="serverUrl"/> accepted the declaration: a document is never
    /// sent to a server selected since.</summary>
    public DeclaredDocument? Find(string sessionId, string path, string serverUrl) {
        try {
            var entry = Entry(sessionId, path);
            if (!File.Exists(entry) || JsonNode.Parse(File.ReadAllText(entry)) is not JsonObject json) return null;
            if (json["server_url"] is not JsonValue url || !url.TryGetValue<string>(out var accepted) || accepted != serverUrl) return null;
            Touch(sessionId);
            return DeclaredDocument.FromJson(json);
        } catch {
            return null;
        }
    }

    /// <summary>Holds one document's refresh across processes, or null when <paramref name="wait"/>
    /// runs out. Parallel tool calls run their hooks concurrently, so each refresh reads the file
    /// only once it holds this: whichever posts last sent the newest text.</summary>
    public async Task<IDisposable?> LockAsync(string sessionId, string path, TimeSpan wait) {
        var lockPath = Entry(sessionId, path) + ".lock";
        Directory.CreateDirectory(SessionDir(sessionId));
        var deadline = time.GetUtcNow() + wait;
        while (true) {
            try {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) when (time.GetUtcNow() < deadline) {
                await Task.Delay(LockPoll, time);
            } catch (IOException) {
                return null;
            }
        }
    }

    static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(25);

    void Touch(string sessionId) => Directory.SetLastWriteTimeUtc(SessionDir(sessionId), time.GetUtcNow().UtcDateTime);

    void Reap() {
        var root = config.Path(DirName);
        if (!Directory.Exists(root)) return;
        var cutoff = time.GetUtcNow().UtcDateTime - Retention;
        foreach (var dir in Directory.EnumerateDirectories(root)) {
            try {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true);
            } catch { }
        }
    }

    const string DirName = "declared-documents";

    string SessionDir(string sessionId) => config.Path(DirName, PolicySnapshotStore.Sanitize(sessionId));

    /// <summary>Case-folded where the default filesystem ignores case, so the path an edit names finds
    /// the declaration however it was cased; on a case-sensitive volume a collision costs one extra
    /// re-declaration of the declared file.</summary>
    string Entry(string sessionId, string path) {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsLinux()) full = full.ToUpperInvariant();
        return Path.Combine(SessionDir(sessionId), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32]);
    }
}
