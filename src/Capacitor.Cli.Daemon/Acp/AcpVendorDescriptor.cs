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
///
/// <b>Task-review defensive hardening:</b> this record uses an EXPLICIT constructor — not a
/// positional-record primary constructor, which can't carry a normalizing body — for the same
/// reason as <see cref="Core.Acp.AcpMcpServerSpec"/>: <c>default(ImmutableArray{string})</c> (the
/// unallocated sentinel, distinct from <see cref="ImmutableArray{T}.Empty"/>) throws a
/// <see cref="NullReferenceException"/> on enumeration, not on construction — so a descriptor built
/// by a future author who forgets to pass <c>[]</c>/an initializer for <see cref="Argv"/> or
/// <see cref="UnattendedTrustArgv"/> would construct successfully and only blow up later, on first
/// use, far from the mistake. The constructor normalizes both to
/// <see cref="ImmutableArray{T}.Empty"/> up front so that class of bug can't happen at all. Every
/// call site today already passes <c>[]</c>/<c>["acp"]</c> explicitly, so this is purely
/// defensive — no observable behavior change.
/// </summary>
internal sealed record AcpVendorDescriptor {
    public string                      Vendor               { get; }
    public Func<DaemonConfig, string>  ResolveBinaryPath     { get; }
    public Func<DaemonConfig, string?> ResolveDefaultModel   { get; }
    public ImmutableArray<string>      Argv                  { get; }
    public ImmutableArray<string>      UnattendedTrustArgv   { get; }
    public bool                        SupportsUnattended    { get; }
    public IAcpModelSelector           ModelSelector         { get; }
    public bool                        SupportsMcpServers    { get; }

    public AcpVendorDescriptor(
            string                      Vendor,
            Func<DaemonConfig, string>  ResolveBinaryPath,
            Func<DaemonConfig, string?> ResolveDefaultModel,
            ImmutableArray<string>      Argv,
            ImmutableArray<string>      UnattendedTrustArgv,
            bool                        SupportsUnattended,
            IAcpModelSelector           ModelSelector,
            bool                        SupportsMcpServers
        ) {
        var normalizedUnattendedTrustArgv = UnattendedTrustArgv.IsDefault ? ImmutableArray<string>.Empty : UnattendedTrustArgv;

        if (!SupportsUnattended && !normalizedUnattendedTrustArgv.IsEmpty)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgv)} must be empty when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedTrustArgv));

        this.Vendor              = Vendor;
        this.ResolveBinaryPath   = ResolveBinaryPath;
        this.ResolveDefaultModel = ResolveDefaultModel;
        this.Argv                = Argv.IsDefault ? ImmutableArray<string>.Empty : Argv;
        this.UnattendedTrustArgv = normalizedUnattendedTrustArgv;
        this.SupportsUnattended  = SupportsUnattended;
        this.ModelSelector       = ModelSelector;
        this.SupportsMcpServers  = SupportsMcpServers;
    }
}

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

    /// <summary>GitHub Copilot CLI as an ACP hosted agent — spawns <c>{CopilotPath} --acp --stdio</c>
    /// (stdio, one child per hosted agent, matching the daemon's process-ownership model). Model
    /// selection is left to <see cref="NoOpModelSelector"/> (Copilot's ACP model surface is
    /// unverified). <c>SupportsUnattended</c> stays <c>false</c> — the reviewer flip + trust argv are
    /// a follow-up.
    ///
    /// <b>SupportsMcpServers is FALSE</b> per the live capability probe (copilot 1.0.69): its
    /// <c>initialize</c> response advertises <c>agentCapabilities.mcpCapabilities = {http, sse}</c>
    /// with NO stdio transport, but <see cref="Capacitor.Cli.Core.Acp.AcpMcpServerSpec"/> is
    /// stdio-only. Advertising a capability the vendor lacks would let a reviewer launch inject a
    /// stdio <c>kcap-flow-result</c> server Copilot can't consume, so the flag reflects reality —
    /// the Copilot reviewer path is blocked until Copilot gains stdio MCP support or the ACP layer
    /// gains http/sse MCP transport + a matching flow-result endpoint.</summary>
    public static readonly AcpVendorDescriptor Copilot = new(
        Vendor:              "copilot",
        ResolveBinaryPath:   cfg => cfg.CopilotPath,
        ResolveDefaultModel: _ => null,
        Argv:                ["--acp", "--stdio"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  false
    );
}
