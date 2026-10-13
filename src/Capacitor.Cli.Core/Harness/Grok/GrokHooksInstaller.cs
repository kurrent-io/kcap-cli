using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// kcap's user-level Grok hook file. User-level hooks need no project trust. A handler may carry only
/// Grok's own fields: it refuses a file with Claude-only ones.
/// </summary>
public static class GrokHooksInstaller {
    public const string HookCommand    = "kcap hook --grok";
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>Start records the session and starts the watcher; Stop restarts a watcher that died. The
    /// watcher ends the session once Grok exits, which a 1.5 s session-end hook could not wait for.</summary>
    public static readonly string[] Events = ["SessionStart", "Stop"];

    /// <summary>Seconds; Grok's default of 5 is too short for the session-start post.</summary>
    const int TimeoutSeconds = 30;

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static bool Install(string hooksPath) {
        try {
            var hooks = new JsonObject();

            foreach (var evt in Events) {
                hooks[evt] = new JsonArray(new JsonObject {
                    ["hooks"] = new JsonArray(new JsonObject {
                        ["type"]    = "command",
                        ["command"] = HookCommand,
                        ["timeout"] = TimeoutSeconds,
                    }),
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, new JsonObject { ["hooks"] = hooks }.ToJsonString(WriteOptions));
            File.WriteAllText(MarkerPath(hooksPath), CapacitorVersion.Current());

            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>True when the file existed. kcap owns it whole, so nothing in it is the user's.</summary>
    public static bool Remove(string hooksPath) {
        var existed = File.Exists(hooksPath);

        if (existed) File.Delete(hooksPath);
        if (File.Exists(MarkerPath(hooksPath))) File.Delete(MarkerPath(hooksPath));

        return existed;
    }

    public static bool IsInstalled(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;

        try {
            return JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject { } root
                && root["hooks"] is JsonObject hooks
                && hooks.Any(h => h.Value?.ToJsonString().Contains(HookCommand, StringComparison.Ordinal) == true);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static string MarkerPath(string hooksPath) => Path.Combine(Path.GetDirectoryName(hooksPath)!, MarkerFileName);
}
