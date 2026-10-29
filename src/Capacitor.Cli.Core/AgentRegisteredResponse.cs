namespace Capacitor.Cli.Core;

/// <summary>The server's answer to <c>AgentRegisteredAcked</c>. Not registered means the server holds no live
/// entry under the agent id. <see cref="RefusedReason"/> is an open set; a value this daemon does not know
/// is still a refusal.</summary>
public sealed record AgentRegisteredResponse(bool Registered, string? RefusedReason = null) {
    public static readonly AgentRegisteredResponse Accepted = new(true);
}
