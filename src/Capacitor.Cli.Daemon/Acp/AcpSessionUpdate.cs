// src/Capacitor.Cli.Daemon/Acp/AcpSessionUpdate.cs
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Discriminator for <see cref="AcpSessionUpdate.Kind"/>, mirroring the <c>sessionUpdate</c> string
/// values documented in <c>docs/acp-probe-findings.md</c>. Only <see cref="AgentMessageChunk"/> and
/// <see cref="AvailableCommands"/> are probe-confirmed; <see cref="AgentThoughtChunk"/>,
/// <see cref="ToolCall"/>, <see cref="ToolCallUpdate"/>, and <see cref="Plan"/> are spec-derived but
/// not yet observed on the wire (see the probe doc's "Recommended follow-up"). <see cref="Unknown"/>
/// covers any future/unrecognized discriminator so the reducer never throws on an unfamiliar
/// variant.
/// </summary>
internal enum AcpUpdateKind {
    AgentMessageChunk,
    AgentThoughtChunk,
    ToolCall,
    ToolCallUpdate,
    Plan,
    AvailableCommands,
    Unknown,
}

/// <summary>
/// Reduced, AOT-friendly internal DTO for one <c>session/update</c> notification's inner
/// <c>update</c> object. Flat and non-polymorphic by design (AI-684 scope: reduce the wire shape to
/// something AI-685's mapper can turn into canonical events, without modeling every field of every
/// variant). <see cref="Raw"/> always carries the full <c>update</c> object (not the outer
/// notification envelope) so the mapper can reach into fields this DTO doesn't surface yet —
/// notably the untyped <c>plan</c> entries and any fields on spec-derived variants that drift once
/// AI-684's probe follow-up re-verifies them against a non-plan-gated account.
/// </summary>
internal sealed record AcpSessionUpdate(
    AcpUpdateKind Kind,
    string?       Text       = null,
    string?       ToolCallId = null,
    string?       ToolTitle  = null,
    string?       ToolKind   = null,
    string?       ToolStatus = null,
    JsonElement?  Raw        = null
);
