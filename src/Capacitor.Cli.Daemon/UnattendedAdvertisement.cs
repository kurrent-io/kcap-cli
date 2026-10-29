using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon;

/// <summary>The unattended capabilities a registration sends, and the Codex resolution they were
/// computed from. Held as one reference so a registration can never send one and acknowledge the other.</summary>
internal sealed record UnattendedAdvertisement(IReadOnlyList<UnattendedVendorCapability>? Capabilities, CodexTransportResolution Codex) {
    public static UnattendedAdvertisement None { get; } = new(null, CodexTransportResolution.Unresolved);
}
