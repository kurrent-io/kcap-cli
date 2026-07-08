// src/Capacitor.Cli.Daemon/Services/IAcpTranscriptSource.cs
using System.Threading.Channels;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-688 Option B task 2 (design spec §2.4, the bind-handoff shape): exposes an ACP runtime's
/// ordered, aggregated transcript plus the session metadata the orchestrator (task 4) needs to bind
/// (<c>AcpSessionStarted</c>) and forward (<c>AcpSessionEvents</c>) — without downcasting
/// <see cref="IHostedAgentRuntime"/> or re-deriving state <c>AcpHostedAgentRuntime.StartAsync</c>
/// already resolved during the handshake. <c>AcpHostedAgentRuntime</c> implements this directly;
/// <see cref="PtyHostedAgentRuntime"/> (Claude/Codex) does not — task 4's factory wiring is
/// responsible for exposing a null reference for non-ACP runtimes on <c>HostedRuntimeStart</c>, not
/// this interface itself.
///
/// Deliberately NOT wired onto <c>HostedRuntimeStart</c>/<c>IHostedAgentRuntimeFactory</c> yet — that
/// consumption wiring (plus <c>ServerConnection.AcpSessionStartedAsync</c>/<c>SendAcpEventsAsync</c>
/// and the orchestrator's <c>ForwardAcpTranscriptAsync</c>) is task 3/4's job. Task 2 only defines and
/// exposes this shape so task 4 can pick it up without touching the runtime's internals again.
/// </summary>
internal interface IAcpTranscriptSource {
    /// <summary>The ACP <c>sessionId</c> resolved by <c>session/new</c> during the handshake.</summary>
    string AcpSessionId { get; }

    /// <summary>The absolute working directory the ACP session was started with.</summary>
    string Cwd { get; }

    /// <summary>
    /// The model id actually resolved AND applied by gap 1's model selection (i.e.
    /// <c>session/set_config_option</c> was sent and answered without error) — or
    /// <see langword="null"/> when no model was requested, resolution found no match in
    /// <c>session/new</c>'s <c>availableModels</c>, or the agent rejected the option. In every "null"
    /// case Cursor's own default model applies, which is why this is nullable rather than falling
    /// back to the requested-but-unconfirmed id.
    /// </summary>
    string? ResolvedModel { get; }

    /// <summary>
    /// The ordered, aggregated, per-serialized-turn <see cref="AcpEventEnvelope"/> stream (task 2's
    /// chunk aggregation + single-flight prompt-turn worker). Every envelope carries a placeholder
    /// <see cref="AcpEventEnvelope.Seq"/> of <c>0</c> — the forwarder (task 3) assigns the real
    /// monotonic seq on dequeue. Channel FIFO order is the contract the forwarder relies on; nothing
    /// downstream of this reader may reorder it.
    /// </summary>
    ChannelReader<AcpEventEnvelope> Envelopes { get; }
}
