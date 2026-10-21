using System.Diagnostics.CodeAnalysis;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>A resolved launch-time permission preset: its wire token and which permission requests it
/// auto-approves — those of the listed ACP <c>ToolKind</c> tokens, or every request when
/// <see cref="ApprovesEveryRequest"/>. Consumed by both the pre-flight policy (validation) and the
/// <see cref="AcpInteractionBridge"/> (enforcement), from ONE map, so the two cannot drift.</summary>
internal sealed record AcpLaunchPermissionPreset(
        string               Token,
        IReadOnlySet<string> AutoApprovedKinds,
        bool                 ApprovesEveryRequest = false) {
    /// <summary>True when a request of this ACP tool kind (null for a kind-less frame) is pre-approved.</summary>
    internal bool Covers(string? kind) => ApprovesEveryRequest || (kind is not null && AutoApprovedKinds.Contains(kind));
}

/// <summary>
/// The single launch-time permission-preset vocabulary for ACP-hosted agents. A covered request is
/// auto-approved with a least-privilege <c>allow_once</c>; everything else keeps prompting.
/// <c>explore</c> and <c>edit</c> are monotone by ACP tool kind and neither pre-approves
/// <c>execute</c> (shell) or <c>fetch</c> (network); <c>auto</c> covers every permission request,
/// kind-less and MCP tool calls included, but never answers a question.
/// </summary>
internal static class AcpPermissionPresets {
    internal const string Explore = "explore";
    internal const string Edit    = "edit";
    internal const string Auto    = "auto";

    /// <summary>Every token this daemon enforces, advertised on <c>DaemonConnect</c> so a server never
    /// sends one an older daemon would refuse.</summary>
    internal static readonly string[] Advertised = [Explore, Edit, Auto];

    static readonly AcpLaunchPermissionPreset ExplorePreset = new(
        Explore, new HashSet<string>(StringComparer.Ordinal) { AcpToolKind.Read, AcpToolKind.Search });

    static readonly AcpLaunchPermissionPreset EditPreset = new(
        Edit,
        new HashSet<string>(StringComparer.Ordinal) {
            AcpToolKind.Read, AcpToolKind.Search, AcpToolKind.Edit, AcpToolKind.Move, AcpToolKind.Delete
        });

    static readonly AcpLaunchPermissionPreset AutoPreset = new(
        Auto, new HashSet<string>(StringComparer.Ordinal), ApprovesEveryRequest: true);

    /// <summary>Resolves a wire token to its preset; false (and null out) for null/blank/unknown.</summary>
    internal static bool TryResolve(string? token, [NotNullWhen(true)] out AcpLaunchPermissionPreset? preset) {
        preset = token switch {
            Explore => ExplorePreset,
            Edit    => EditPreset,
            Auto    => AutoPreset,
            _       => null
        };

        return preset is not null;
    }

    /// <summary>True when <paramref name="token"/> is a recognised preset token (never for null/blank).</summary>
    internal static bool IsKnown(string? token) => token is Explore or Edit or Auto;

    /// <summary>The daemon's authoritative ACP-permission-routed vendor tokens, sourced from the ACP
    /// descriptors' own <see cref="AcpVendorDescriptor.Vendor"/> so the policy (which rejects a preset
    /// for a non-member) and the advertisement (which offers presets for supported members) cannot
    /// drift from the launch runtime that enforces them.</summary>
    internal static readonly IReadOnlySet<string> RoutedVendors = new HashSet<string>(StringComparer.Ordinal) {
        AcpVendorDescriptors.Cursor.Vendor,
        AcpVendorDescriptors.Copilot.Vendor,
        AcpVendorDescriptors.Kiro.Vendor,
        AcpVendorDescriptors.OpenCode.Vendor,
        AcpVendorDescriptors.Gemini.Vendor,
        AcpVendorDescriptors.Grok.Vendor,
    };
}
