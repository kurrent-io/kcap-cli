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
        if (MachineId is not null) json["machine_id"] = MachineId;
        if (DaemonName is not null) json["daemon_name"] = DaemonName;
        if (AccountId is not null) json["account_id"] = AccountId;
        if (ConfigDir is not null) json["config_dir"] = ConfigDir;

        return json;
    }
}
