using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Service.Sessions;

/// <summary>
/// The environment <c>kcap daemon service install</c> captured from the user's shell, which the daemon
/// gets over the one Windows builds at logon — what a terminal-exported <c>KCAP_*</c> variable or a
/// version manager's <c>PATH</c> needs to reach a daemon nobody started from that terminal
/// </summary>
public static partial class CapturedEnvironment {
    public const int MaxBytes = 256 * 1024;

    [JsonSerializable(typeof(Dictionary<string, string>))]
    partial class Json : JsonSerializerContext;

    /// <summary>Empty for anything that is not a JSON object of strings; a name that could not be an
    /// environment variable is dropped</summary>
    public static IReadOnlyDictionary<string, string> Parse(string json) {
        try {
            var values = JsonSerializer.Deserialize(json, Json.Default.DictionaryStringString);
            if (values is null) return new Dictionary<string, string>();
            return values
                .Where(kv => kv.Key.Length > 0 && kv.Key.IndexOfAny(['=', '\0']) < 0 && !kv.Value.Contains('\0'))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        } catch (JsonException) {
            return new Dictionary<string, string>();
        }
    }
}
