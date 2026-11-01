using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>. A client gates
/// on an entry, so each must be live: either <see cref="LocalControlServer"/> routes its frames to a
/// handler, or it names fields this daemon fills on a frame it already sends (<c>experimental/1</c> on
/// the status snapshot).
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "input/2", "settings/1", "fence/1", "shutdown/1", "resume/1", AgentAttachClient.RuntimeCapability, AgentSessionIpc.Capability, "accounts/1", "experimental/1", RenameWire.Capability, HandoffWire.Capability, ResumeInPlaceWire.Capability];
}
