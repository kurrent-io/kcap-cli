using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>get_connection</c>: the server URL and profile this <c>kcap-sessions</c> process resolved at
/// start. A fresh <c>kcap whoami</c> runs in another process and cannot speak for this one.
/// </summary>
static class ConnectionTool {
    internal const string Name = "get_connection";

    internal static McpTool Definition => new(
        Name,
        "The Capacitor server URL and kcap profile this MCP server uses for every other tool. Makes no network call.",
        new("object", new(), []),
        McpToolAnnotations.Read
    );

    /// <summary>A null <c>server_url</c> is a process with no server configured, which no import's
    /// handoff file can match.</summary>
    internal static JsonObject Result(string? serverUrl, string profile) =>
        new() { ["server_url"] = serverUrl?.TrimEnd('/'), ["profile"] = profile };
}
