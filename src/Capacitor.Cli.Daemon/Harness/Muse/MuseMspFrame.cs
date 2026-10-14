using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// One parsed MSP JSON-RPC line. The element members point into <see cref="Owner"/>, so they are
/// valid only until this frame is disposed. A server-initiated request (both an id and a method) is
/// neither a response nor a notification.
/// </summary>
internal sealed record MuseMspFrame(
    long?        Id,
    string?      Method,
    JsonElement? Params,
    JsonElement? Result,
    JsonElement? Error,
    JsonDocument Owner
) : IDisposable {
    public bool IsResponse     => Id is not null && Method is null;
    public bool IsNotification => Id is null && Method is not null;

    /// <summary>Null for a line that is not JSON or not a JSON object; never throws.</summary>
    public static MuseMspFrame? TryParse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(line.TrimEnd('\r'));
        } catch (JsonException) {
            return null;
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) {
            doc.Dispose();
            return null;
        }

        long? id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var n)
            ? n
            : null;

        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        return new MuseMspFrame(id, method, Member(root, "params"), Member(root, "result"), Member(root, "error"), doc);
    }

    public void Dispose() => Owner.Dispose();

    static JsonElement? Member(JsonElement root, string name) => root.TryGetProperty(name, out var v) ? v : null;
}
