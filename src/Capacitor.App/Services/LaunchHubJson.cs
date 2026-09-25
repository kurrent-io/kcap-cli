using System.Text.Json;

namespace Capacitor.App.Services;

/// What ServerConnectionService layers onto SignalR's default hub payload options, shared so
/// wire-contract tests serialize with the same naming policy: the server applies snake_case to
/// every hub payload.
public static class LaunchHubJson {
    public static void Configure(JsonSerializerOptions options) =>
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
}
