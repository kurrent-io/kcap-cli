using System.Threading.Channels;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Exposes an ACP runtime's ordered, aggregated transcript plus the session metadata the
/// orchestrator needs to bind (<c>AcpSessionStarted</c>) and forward (<c>AcpSessionEvents</c>) —
/// without downcasting <see cref="IHostedAgentRuntime"/> or re-deriving state
/// <c>AcpHostedAgentRuntime.StartAsync</c> already resolved during the handshake.
/// <c>AcpHostedAgentRuntime</c> implements this directly; <see cref="PtyHostedAgentRuntime"/>
/// (Claude/Codex) does not — the factory wiring on <c>HostedRuntimeStart</c> is responsible for
/// exposing a null reference for non-ACP runtimes, not this interface itself.
/// </summary>
internal interface IAcpTranscriptSource {
    /// <summary>The ACP <c>sessionId</c> resolved by <c>session/new</c> during the handshake.</summary>
    string AcpSessionId { get; }

    /// <summary>The absolute working directory the ACP session was started with.</summary>
    string Cwd { get; }

    /// <summary>
    /// The confirmed running model of the ACP session: either the id resolved AND applied by model
    /// selection (the vendor's model-selection RPC — <c>session/set_config_option</c> or
    /// <c>session/set_model</c> — was sent and answered without error), OR, when NO model was
    /// requested, the current model the <c>session/new</c> handshake reported. It is
    /// <see langword="null"/> in two cases: a REQUESTED model that did not take (no match in
    /// <c>availableModels</c> / the agent rejected the option), and a no-request launch whose
    /// <c>session/new</c> published no current-model marker. In both the vendor's own default runs and
    /// its specific id is unknown — so a non-null value is always a model the session is actually
    /// running, never a requested-but-unconfirmed or guessed one.
    /// </summary>
    string? ResolvedModel { get; }

    /// <summary>
    /// The ordered, aggregated, per-serialized-turn <see cref="AcpEventEnvelope"/> stream (chunk
    /// aggregation + single-flight prompt-turn worker). Every envelope carries a placeholder
    /// <see cref="AcpEventEnvelope.Seq"/> of <c>0</c> — the forwarder assigns the real
    /// monotonic seq on dequeue. Channel FIFO order is the contract the forwarder relies on; nothing
    /// downstream of this reader may reorder it.
    /// </summary>
    ChannelReader<AcpEventEnvelope> Envelopes { get; }
}
