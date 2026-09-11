using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the daemon settings frames. snake_case on the wire; every member always
/// emitted; each setting is nullable so a put names only what it changes and a later setting is
/// additive.
public sealed record DaemonSettingsPutDto(int? MaxAgents);

/// MaxAgents echoes the value in effect after the put, on success and on refusal alike.
public sealed record DaemonSettingsAckDto(bool Ok, string? Reason, int? MaxAgents);

public static class DaemonSettingsReasons {
    public const string Malformed       = "malformed";
    public const string InvalidMaxAgents = "invalid_max_agents";
    /// Client-side only: the request or the reply never crossed the socket.
    public const string Transport       = "transport";
}

public static class SettingsWire {
    /// The HelloReply capability that advertises the DaemonSettingsPut handler.
    public const string Capability = "settings/1";

    /// STJ source-gen leaves a missing member null and `{}` decodes fine, so "nothing to apply" is
    /// decided here rather than by the parser.
    public static bool HasAnySetting(DaemonSettingsPutDto? dto) =>
        dto is not null && dto.MaxAgents is not null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonSettingsPutDto))]
[JsonSerializable(typeof(DaemonSettingsAckDto))]
public partial class SettingsIpcJsonContext : JsonSerializerContext;
