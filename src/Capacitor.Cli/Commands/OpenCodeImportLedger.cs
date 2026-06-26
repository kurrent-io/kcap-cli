using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-machine record of OpenCode sessions fully imported to a given server, used
/// to skip already-loaded sessions on re-run (the server exposes no completeness
/// signal). Shape: { "&lt;serverUrl&gt;": { "&lt;sessionId&gt;": &lt;reconstructedLineCount&gt; } }.
/// Keyed by server URL so it never claims a session is loaded on the wrong server.
/// </summary>
internal sealed class OpenCodeImportLedger {
    readonly string _path;
    readonly Dictionary<string, Dictionary<string, int>> _byServer;

    OpenCodeImportLedger(string path, Dictionary<string, Dictionary<string, int>> data) {
        _path = path; _byServer = data;
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".cache", "kcap", "opencode-imported.json");

    public static OpenCodeImportLedger Load(string path) {
        try {
            if (File.Exists(path)) {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize(json, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringInt32);
                if (data is not null) return new(path, data);
            }
        } catch { /* missing/corrupt → empty ledger */ }
        return new(path, new(StringComparer.Ordinal));
    }

    public bool IsComplete(string serverUrl, string sessionId, int lineCount) =>
        _byServer.TryGetValue(serverUrl, out var sessions)
        && sessions.TryGetValue(sessionId, out var recorded)
        && recorded == lineCount;

    public void MarkComplete(string serverUrl, string sessionId, int lineCount) {
        if (!_byServer.TryGetValue(serverUrl, out var sessions)) {
            sessions = new(StringComparer.Ordinal);
            _byServer[serverUrl] = sessions;
        }
        sessions[sessionId] = lineCount;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_byServer, OpenCodeLedgerJsonContext.Default.DictionaryStringDictionaryStringInt32);
            File.WriteAllText(_path, json);
        } catch { /* best effort — a ledger write failure must not fail the import */ }
    }
}

[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
internal partial class OpenCodeLedgerJsonContext : JsonSerializerContext { }
