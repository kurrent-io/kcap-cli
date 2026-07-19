using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Per-vendor wiring for AcpHostedAgentRuntimeFactory: which binary to spawn, what argv an
/// interactive vs. an unattended (review-flow) launch gets, and how (or whether) this vendor's ACP
/// surface supports model selection / an mcpServers list. Exactly one descriptor is registered
/// today (Cursor); onboarding a second ACP-speaking vendor means adding one more descriptor + a
/// factory registration line, not touching AcpHostedAgentRuntimeFactory itself.
///
/// <b>Round 2 Finding 2 — the selector object is the only source of truth:</b> there is
/// deliberately no separate "does this vendor support model selection" boolean.
/// <see cref="ModelSelector"/> alone decides: a vendor that doesn't select models carries
/// <see cref="NoOpModelSelector.Instance"/>; Cursor carries
/// <see cref="ConfigOptionModelSelector.Instance"/>. An earlier revision shipped a
/// <c>SupportsModelSelection</c> flag plus a construction-time invariant rejecting a
/// <c>false</c>-flagged descriptor that still carried a real selector — but by the time that
/// invariant existed, <see cref="Services.AcpHostedAgentRuntimeFactory.StartAsync"/> (D4) already forwarded
/// <see cref="ModelSelector"/> unconditionally and never branched on the flag, so the flag gated no
/// behavior; worse, the invariant only checked ONE of its two possible contradictions (it rejected
/// <c>SupportsModelSelection: false</c> + a real selector, but still accepted the equally
/// contradictory <c>SupportsModelSelection: true</c> + <c>NoOpModelSelector.Instance</c>). Removing
/// the field removes the dead state and the asymmetric guard together: there is exactly one thing
/// to get right — which selector object the descriptor carries — not a boolean that also has to
/// agree with it. This also drops any expectation that <c>ModelSelector</c> be
/// <c>ReferenceEquals</c> to one of the two singletons below: the runtime only needs SOME
/// <see cref="IAcpModelSelector"/>, so a future vendor's own implementation, or a test double, is
/// exactly as valid.
///
/// <c>Argv</c>/<c>UnattendedTrustArgv</c> are <see cref="ImmutableArray{T}"/>, not <c>string[]</c>:
/// a <c>static readonly</c> descriptor singleton (see <see cref="AcpVendorDescriptors"/>), shared
/// across every launch for the daemon's whole lifetime, must not expose a mutable array a caller
/// could reach in and alter, silently corrupting every later (and any concurrent) launch.
/// </summary>
internal sealed record AcpVendorDescriptor(
    string                      Vendor,
    Func<DaemonConfig, string>  ResolveBinaryPath,
    Func<DaemonConfig, string?> ResolveDefaultModel,
    ImmutableArray<string>      Argv,
    ImmutableArray<string>      UnattendedTrustArgv,
    bool                        SupportsUnattended,
    IAcpModelSelector           ModelSelector,
    bool                        SupportsMcpServers
);

internal static class AcpVendorDescriptors {
    /// <summary>Reproduces AcpHostedAgentRuntimeFactory's original Cursor-only behavior
    /// byte-for-byte: argv ["acp"], no trust flags (SupportsUnattended stays false so
    /// UnattendedTrustArgv is unreachable), model selection via the shared
    /// ConfigOptionModelSelector, mcpServers accepted (always sent as an array on the wire; every
    /// caller today still populates it with an empty list).</summary>
    public static readonly AcpVendorDescriptor Cursor = new(
        Vendor:              "cursor",
        ResolveBinaryPath:   cfg => cfg.CursorPath,
        ResolveDefaultModel: cfg => cfg.CursorModel,
        Argv:                ["acp"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true
    );
}
