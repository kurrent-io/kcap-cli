using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>The last flow catalogue each server returned, reduced to its proactive entries, so a session
/// whose listing fetch fails or runs out of budget can still be told what it may offer. Every operation
/// fails open: an unreadable or unwritable cache behaves as an empty one.</summary>
internal sealed class SessionStartFlowsCatalogCache(string root) {
    const int MaxBytes = 64 * 1024;

    public static SessionStartFlowsCatalogCache Default(ConfigRoot config) => new(config.Path("cache", "session-start-flows-v1"));

    public JsonNode? Read(string baseUrl) {
        try {
            var path = PathFor(baseUrl);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;

            return JsonNode.Parse(File.ReadAllText(path));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    public void Write(string baseUrl, JsonNode? catalogue) {
        var proactive = new JsonArray();
        if (catalogue is JsonObject { } obj && obj["definitions"] is JsonArray definitions) {
            foreach (var d in definitions.OfType<JsonObject>()) {
                if (Str(d, "offer") != "proactive") continue;
                proactive.Add((JsonNode)new JsonObject { ["id"] = Str(d, "id"), ["offer"] = "proactive", ["when_to_use"] = Str(d, "when_to_use") });
            }
        }

        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["definitions"] = proactive }.ToJsonString());
        if (bytes.Length > MaxBytes) {
            Delete(baseUrl);
            return;
        }

        string? temp = null;
        try {
            var dir = SessionStartMemoryStorePaths.ValidateRoot(root);
            temp = Path.Combine(dir, $"{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
                stream.Write(bytes);
            }
            File.Move(temp, PathFor(baseUrl), overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The listing superseded the entry even though it could not be stored.
            Delete(baseUrl);
        } finally {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Deletes the entry, throwing when it cannot: unlike <see cref="Delete"/>, a caller can rely on it being
    /// gone.</summary>
    public void Forget(string baseUrl) {
        try {
            File.Delete(PathFor(baseUrl));
        } catch (DirectoryNotFoundException) {
            // No cache was ever written.
        }
    }

    public void Delete(string baseUrl) {
        try { File.Delete(PathFor(baseUrl)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    string PathFor(string baseUrl) {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl.TrimEnd('/').ToLowerInvariant())))[..32];

        return Path.Combine(root, key + ".json");
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
