using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Config;

/// <summary>
/// <c>experimental.json</c>: <c>{ "&lt;profile&gt;": { "&lt;feature-id&gt;": true|false } }</c>. Unknown profiles
/// and ids are kept on write, and an unparseable file is never overwritten.
/// </summary>
public sealed class ExperimentalSettingsStore(ConfigRoot root) {
    public const string FileName = "experimental.json";

    public string Path { get; } = root.Path(FileName);

    public ExperimentalSettingsRead Load(string profile) {
        var (document, problem) = ReadDocument();
        if (problem is not null) return new(false, ExperimentalSettingsRead.Empty.Features, problem);

        var features = new Dictionary<string, bool>();
        if (document[profile] is JsonObject section)
            foreach (var (id, value) in section)
                if (value is JsonValue v && v.TryGetValue<bool>(out var on))
                    features[id] = on;

        return new(true, features, null);
    }

    /// <returns>False when the file is unreadable and was left untouched.</returns>
    public Task<bool> SetAsync(string profile, string featureId, bool enabled, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        using var _ = root.AcquireLock(FileName);

        var (document, problem) = ReadDocument();
        if (problem is not null) return Task.FromResult(false);

        if (document[profile] is not JsonObject section) {
            section = new JsonObject();
            document[profile] = section;
        }
        section[featureId] = enabled;

        Directory.CreateDirectory(root.Directory);
        var temp = System.IO.Path.Combine(root.Directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try {
            File.WriteAllText(temp, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, Path, overwrite: true);
        } finally {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return Task.FromResult(true);
    }

    (JsonObject Document, string? Problem) ReadDocument() {
        string text;
        try {
            if (!File.Exists(Path)) return (new JsonObject(), null);
            text = File.ReadAllText(Path);
        } catch (IOException e) {
            return (new JsonObject(), $"{FileName} could not be read: {e.Message}");
        } catch (UnauthorizedAccessException e) {
            return (new JsonObject(), $"{FileName} could not be read: {e.Message}");
        }

        if (string.IsNullOrWhiteSpace(text)) return (new JsonObject(), $"{FileName} is empty");

        try {
            // A JsonObject with a duplicate key parses, then throws ArgumentException on first access.
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject obj
                ? (obj, null)
                : (new JsonObject(), $"{FileName} is not a JSON object");
        } catch (JsonException e) {
            return (new JsonObject(), $"{FileName} is not valid JSON: {e.Message}");
        }
    }
}
