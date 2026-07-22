using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>How an unattended ACP reviewer receives its validated stdio MCP servers.</summary>
internal enum AcpReviewFlowMcpTransport {
    /// <summary>Infer the transport from <see cref="AcpVendorDescriptor.SupportsMcpServers"/>.</summary>
    Default,
    /// <summary>The vendor cannot carry the reviewer's required result channel.</summary>
    Unsupported,
    /// <summary>Send the servers in ACP <c>session/new.mcpServers</c>.</summary>
    SessionNew,
    /// <summary>Preload the servers through Copilot CLI's <c>--additional-mcp-config</c>.</summary>
    CopilotAdditionalConfig
}

/// <summary>
/// Per-vendor wiring for AcpHostedAgentRuntimeFactory: which binary to spawn, what argv an
/// interactive vs. an unattended (review-flow) launch gets, and how (or whether) this vendor's ACP
/// surface supports model selection / an mcpServers list. Cursor and Copilot are registered today;
/// onboarding another ACP-speaking vendor means adding one descriptor + a factory registration
/// line, not touching AcpHostedAgentRuntimeFactory itself.
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
    public string                      Vendor                 { get; }
    public Func<DaemonConfig, string>  ResolveBinaryPath     { get; }
    public Func<DaemonConfig, string?> ResolveDefaultModel   { get; }
    public ImmutableArray<string>      Argv                   { get; }
    public ImmutableArray<string>      UnattendedTrustArgv    { get; }
    public bool                        SupportsUnattended     { get; }
    public IAcpModelSelector           ModelSelector          { get; }
    public bool                        SupportsMcpServers     { get; }
    public AcpReviewFlowMcpTransport   ReviewFlowMcpTransport { get; }
    public bool                        SupportsBorrowedReviewFlow { get; }

    public AcpVendorDescriptor(
            string                      Vendor,
            Func<DaemonConfig, string>  ResolveBinaryPath,
            Func<DaemonConfig, string?> ResolveDefaultModel,
            ImmutableArray<string>      Argv,
            ImmutableArray<string>      UnattendedTrustArgv,
            bool                        SupportsUnattended,
            IAcpModelSelector           ModelSelector,
            bool                        SupportsMcpServers,
            AcpReviewFlowMcpTransport   ReviewFlowMcpTransport = AcpReviewFlowMcpTransport.Default,
            bool                        SupportsBorrowedReviewFlow = false
        ) {
        var normalizedUnattendedTrustArgv = UnattendedTrustArgv.IsDefault ? ImmutableArray<string>.Empty : UnattendedTrustArgv;

        if (!SupportsUnattended && !normalizedUnattendedTrustArgv.IsEmpty)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgv)} must be empty when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedTrustArgv));

        if (SupportsBorrowedReviewFlow && !SupportsUnattended)
            throw new ArgumentException(
                $"{nameof(SupportsBorrowedReviewFlow)} requires {nameof(SupportsUnattended)} (vendor: {Vendor}).",
                nameof(SupportsBorrowedReviewFlow));

        this.Vendor              = Vendor;
        this.ResolveBinaryPath   = ResolveBinaryPath;
        this.ResolveDefaultModel = ResolveDefaultModel;
        this.Argv                = Argv.IsDefault ? ImmutableArray<string>.Empty : Argv;
        this.UnattendedTrustArgv = normalizedUnattendedTrustArgv;
        this.SupportsUnattended  = SupportsUnattended;
        this.ModelSelector       = ModelSelector;
        this.SupportsMcpServers  = SupportsMcpServers;
        this.SupportsBorrowedReviewFlow = SupportsBorrowedReviewFlow;
        this.ReviewFlowMcpTransport = ReviewFlowMcpTransport switch {
            AcpReviewFlowMcpTransport.Default when SupportsMcpServers => AcpReviewFlowMcpTransport.SessionNew,
            AcpReviewFlowMcpTransport.Default                         => AcpReviewFlowMcpTransport.Unsupported,
            _                                                         => ReviewFlowMcpTransport
        };

        if (this.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.SessionNew && !SupportsMcpServers)
            throw new ArgumentException(
                $"{nameof(AcpReviewFlowMcpTransport.SessionNew)} requires {nameof(SupportsMcpServers)} (vendor: {Vendor}).",
                nameof(ReviewFlowMcpTransport));
    }
}

internal static class AcpVendorDescriptors {
    /// <summary>Cursor CLI's ACP hosted-agent surface: <c>cursor-agent acp</c>, no trust-at-spawn
    /// flags, model selection through ACP config options, and stdio MCP delivery through
    /// <c>session/new.mcpServers</c>. Unattended review flows remain owned-worktree-only and rely on
    /// <see cref="AcpInteractionBridge"/>'s local review-flow permission policy, so no permission or
    /// elicitation is routed to a human.</summary>
    public static readonly AcpVendorDescriptor Cursor = new(
        Vendor:              "cursor",
        ResolveBinaryPath:   cfg => cfg.CursorPath,
        ResolveDefaultModel: cfg => cfg.CursorModel,
        Argv:                ["acp"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true
    );

    /// <summary>GitHub Copilot CLI as an ACP hosted agent (<c>copilot --acp --stdio</c>).
    /// ACP itself advertises MCP over http/sse only, so interactive <c>session/new</c> stdio servers
    /// stay disabled. Review flows preload their validated stdio servers through Copilot's
    /// <c>--additional-mcp-config</c> process argument and clamp the visible tool surface.</summary>
    public static readonly AcpVendorDescriptor Copilot = new(
        Vendor:              "copilot",
        ResolveBinaryPath:   cfg => cfg.CopilotPath,
        ResolveDefaultModel: _ => null,
        Argv:                ["--acp", "--stdio"],
        UnattendedTrustArgv: ["--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  false,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.CopilotAdditionalConfig,
        SupportsBorrowedReviewFlow: true
    );
}
