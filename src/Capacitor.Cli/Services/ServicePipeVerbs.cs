namespace Capacitor.Cli.Services;

/// <summary>The verbs <c>kcap-service</c>'s pipe answers. The protocol is append-only: a new ability is a new verb,
/// never a new field on an old one</summary>
static class ServicePipeVerbs {
    public const string Capabilities = "capabilities";

    public const string ServerStatus   = "server-status";
    public const string ServerStart    = "server-start";
    public const string ServerStop     = "server-stop";
    public const string ServerEnroll   = "server-enroll";
    public const string ServerUnenroll = "server-unenroll";

    public static readonly IReadOnlyList<string> Server = [ServerStatus, ServerStart, ServerStop, ServerEnroll, ServerUnenroll];

    /// <summary>An older service answers some of these as unknown verbs, so the local server goes to it only when it
    /// advertises every one</summary>
    public static bool ServesLocalServer(IServicePipe pipe, TimeSpan timeout) =>
        pipe.Send(Capabilities, timeout)?.Capabilities?.Verbs is { } verbs && Server.All(v => verbs.Contains(v));
}
