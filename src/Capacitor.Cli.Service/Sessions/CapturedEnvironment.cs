using System.Collections.Frozen;
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
    /// environment variable is dropped, and of names differing only in case the last one wins</summary>
    public static IReadOnlyDictionary<string, string> Parse(string json) {
        Dictionary<string, string>? values;
        try {
            values = JsonSerializer.Deserialize(json, Json.Default.DictionaryStringString);
        } catch (JsonException) {
            return FrozenDictionary<string, string>.Empty;
        }
        if (values is null) return FrozenDictionary<string, string>.Empty;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values) {
            if (key.Length > 0 && key.IndexOfAny(['=', '\0']) < 0 && value is not null && !value.Contains('\0')) env[key] = value;
        }
        return env;
    }
}
