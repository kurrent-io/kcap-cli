using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// <param name="Agents">"keep" leaves hosted agents running for the next daemon; "end" stops them.</param>
/// <param name="Exit">"relaunch" exits for a supervisor to restart the daemon; "final" exits for good.</param>
/// <param name="WhenIdle">True refuses with a <c>busy:</c> error while an eval or a launch in admission is
/// in flight, rather than stopping; absent from a client that predates it.</param>
public sealed record ShutdownRequestDto(string Agents, string Exit, bool WhenIdle = false);

public static class ShutdownIpc {
    public static string Serialize(ShutdownRequestDto dto) =>
        JsonSerializer.Serialize(dto, ShutdownIpcJsonContext.Default.ShutdownRequestDto);

    /// Null for malformed JSON or an agents/exit value outside the allowed sets.
    public static ShutdownRequestDto? Parse(string text) {
        ShutdownRequestDto? dto;
        try {
            dto = JsonSerializer.Deserialize(text, ShutdownIpcJsonContext.Default.ShutdownRequestDto);
        } catch (JsonException) {
            return null;
        }

        return dto is { Agents: "keep" or "end", Exit: "relaunch" or "final" } ? dto : null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShutdownRequestDto))]
public partial class ShutdownIpcJsonContext : JsonSerializerContext;
