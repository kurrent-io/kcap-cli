using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>Pure renderer for the status payload — kept separate from I/O so it's directly testable.</summary>
internal static class StatusJsonRender {
    public static string Render(StatusJson status) =>
        JsonSerializer.Serialize(status, StatusJsonContext.Default.StatusJson);

    /// <summary>A machine is set up when it knows a server and can authenticate to it — with a
    /// credential, or because the server asks for none. An unreachable server does not make it
    /// unconfigured — that is a network fact, not a setup one.</summary>
    public static bool IsConfigured(string? serverUrl, StatusAuthState auth) =>
        serverUrl is not null
     && auth is StatusAuthState.NotRequired or StatusAuthState.Machine or StatusAuthState.Valid;
}
