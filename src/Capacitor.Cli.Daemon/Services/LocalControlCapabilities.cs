using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>. An entry may
/// exist here only if <see cref="LocalControlServer"/> routes its frames to a live handler: a client
/// gates on the entry and then sends frames an older daemon would fail to decode.
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "input/2", "settings/1", "fence/1", "shutdown/1", "resume/1", AgentAttachClient.RuntimeCapability, AgentSessionIpc.Capability];
}
