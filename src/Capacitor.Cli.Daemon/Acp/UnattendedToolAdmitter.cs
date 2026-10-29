using System.Text.Json;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>How a vendor's unattended reviewer decides which permission frames to approve under
/// <see cref="AcpUnattendedInteractionPolicy.AllowlistedAutoApprove"/>: the identities a launch admits,
/// built from the same specs <c>session/new</c> receives, and whether one frame names exactly one of
/// them. Per vendor because each names a tool call differently.</summary>
internal sealed record UnattendedToolAdmitter(
    Func<IReadOnlyList<AcpMcpServerSpec>, LaunchIdentity, IReadOnlySet<string>> AdmittedFor,
    Func<JsonElement, IReadOnlySet<string>, bool>                              IsAdmitted
) {
    internal static readonly UnattendedToolAdmitter Kiro = new(UnattendedToolAdmission.AdmittedFor, UnattendedToolAdmission.IsAdmitted);
}
