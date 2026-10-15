using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Plans;

/// <summary>The documents a session declared, by full local path, with the arguments that declared
/// them, so an edit can declare the file again: the server holds only the snapshot it was sent. Best
/// effort throughout: a lost entry costs one stale snapshot, never the tool call or the hook.</summary>
sealed class DeclaredDocumentLedger(ConfigRoot config, TimeProvider time) {
    /// <summary>Session end evicts a Claude session's entries; a harness with no hook that does is
    /// reaped by age instead.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public void Record(string sessionId, DeclaredDocument document) {
        try {
            Reap();
            var entry = Entry(sessionId, document.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, document.ToJson().ToJsonString());
        } catch { }
    }

    public DeclaredDocument? Find(string sessionId, string path) {
        try {
            var entry = Entry(sessionId, path);
            return File.Exists(entry) ? DeclaredDocument.FromJson(JsonNode.Parse(File.ReadAllText(entry)) as JsonObject) : null;
        } catch {
            return null;
        }
    }

    public void Evict(string sessionId) {
        try { Directory.Delete(SessionDir(sessionId), recursive: true); } catch { }
    }

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

    string Entry(string sessionId, string path) =>
        Path.Combine(SessionDir(sessionId), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..32]);
}
