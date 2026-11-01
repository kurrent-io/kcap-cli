using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Recording;

/// <summary>Where a session was recorded: this machine's <see cref="Core.MachineId"/>, the daemon that hosted
/// it, the registered account its store belongs to, and the store itself. A null
/// <see cref="ConfigDir"/> means the store is unknown.</summary>
public sealed record RecordingIdentity(string? MachineId, string? DaemonName, string? AccountId, string? ConfigDir) {
    public const string JsonName = "recording";

    /// <summary>The SessionStart body's <c>recording</c> object; an unknown field is left out.</summary>
    public JsonObject ToJson() {
        var json = new JsonObject();
        if (MachineId is not null) json["machine_id"] = Str(MachineId);
        if (DaemonName is not null) json["daemon_name"] = Str(DaemonName);
        if (AccountId is not null) json["account_id"] = Str(AccountId);
        if (ConfigDir is not null) json["config_dir"] = Str(ConfigDir);

        return json;
    }

    // Under NativeAOT a string assigned straight into a JsonObject throws for want of type metadata;
    // a parsed string node avoids the lookup.
    static JsonNode Str(string value) => JsonNode.Parse($"\"{JsonEncodedText.Encode(value)}\"")!;
}
