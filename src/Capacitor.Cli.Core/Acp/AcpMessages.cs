// src/Capacitor.Cli.Core/Acp/AcpMessages.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Acp;

/// <summary>
/// Typed <c>params</c> payloads for the ACP methods <see cref="Daemon.Acp.AcpConnection"/> callers
/// (<c>AcpHostedAgentRuntime</c>, AI-684 Task 9) send. These exist only so request construction can
/// go through source-gen (<see cref="JsonSerializer.SerializeToElement{T}(T, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T})"/>
/// against <see cref="CapacitorJsonContext"/>) instead of the reflection-based overloads, which are
/// unsafe under NativeAOT. Field names/shapes are pinned to the probe-confirmed wire shapes in
/// <c>docs/acp-probe-findings.md</c> — every property carries an explicit
/// <see cref="JsonPropertyNameAttribute"/> because the wire protocol uses camelCase while this
/// context's default naming policy (set on <see cref="CapacitorJsonContext"/>) is snake_case.
/// </summary>

/// <summary>
/// <c>initialize</c> params. Deliberately advertises MINIMAL client capabilities (no <c>fs</c>, no
/// <c>terminal</c>) — AI-687 decides those from the AI-688 findings; AI-684 does not implement
/// either capability.
/// </summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")]  int                     ProtocolVersion,
    [property: JsonPropertyName("clientCapabilities")] ClientCapabilities    ClientCapabilities
);

public sealed record ClientCapabilities(
    [property: JsonPropertyName("fs")]       FsCapabilities Fs,
    [property: JsonPropertyName("terminal")] bool           Terminal
);

public sealed record FsCapabilities(
    [property: JsonPropertyName("readTextFile")]  bool ReadTextFile,
    [property: JsonPropertyName("writeTextFile")] bool WriteTextFile
);

/// <summary><c>session/new</c> params. <c>Cwd</c> must be an absolute path (the worktree root).</summary>
public sealed record SessionNewParams(
    [property: JsonPropertyName("cwd")]        string        Cwd,
    [property: JsonPropertyName("mcpServers")] object[]      McpServers
);

/// <summary><c>session/prompt</c> params — a content-block array, per the probe (not a bare string).</summary>
public sealed record SessionPromptParams(
    [property: JsonPropertyName("sessionId")] string             SessionId,
    [property: JsonPropertyName("prompt")]    PromptContentBlock[] Prompt
);

public sealed record PromptContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text
);

/// <summary><c>session/cancel</c> params — sent as a notification (no response expected).</summary>
public sealed record SessionCancelParams(
    [property: JsonPropertyName("sessionId")] string SessionId
);

/// <summary>
/// <c>session/request_permission</c> params sent BY THE AGENT (server-initiated request, handled
/// via <see cref="Daemon.Acp.AcpConnection.OnServerRequest"/>) — AI-686. Spec-derived, NOT
/// probe-confirmed: the AI-684 probe never observed a real <c>session/request_permission</c> frame
/// (the probe account's turn ended before any tool call — see
/// <c>docs/acp-probe-findings.md</c> §"Permission / elicitation requests"). Mirrors the shape
/// <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.BuildRequestPermissionFrame"/> already
/// builds for tests. <see cref="ToolCall"/> stays an opaque <see cref="JsonElement"/> — its exact
/// schema is unconfirmed and it is never re-serialized, only forwarded to the server as
/// <see cref="AcpInteractionRequest.ToolInput"/> best-effort (see <c>AcpInteractionBridge</c>).
/// </summary>
public sealed record SessionRequestPermissionParams(
    [property: JsonPropertyName("sessionId")] string              SessionId,
    [property: JsonPropertyName("toolCall")]  JsonElement          ToolCall,
    [property: JsonPropertyName("options")]   PermissionOptionDto[] Options
);

/// <summary>One offered option in a <see cref="SessionRequestPermissionParams"/> — spec-derived, NOT probe-confirmed.</summary>
public sealed record PermissionOptionDto(
    [property: JsonPropertyName("optionId")] string OptionId,
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("kind")]     string Kind
);

/// <summary>
/// Client's JSON-RPC <c>result</c> for a <c>session/request_permission</c> request — spec-derived,
/// NOT probe-confirmed. Mirrors <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.PermissionOutcomeSelected"/>/
/// <see cref="Capacitor.Cli.Tests.Unit.Acp.FakeAcpAgent.PermissionOutcomeCancelled"/>.
/// </summary>
public sealed record PermissionOutcomeResult(
    [property: JsonPropertyName("outcome")] PermissionOutcomeDto Outcome
);

/// <summary>
/// <c>Outcome</c> is <c>"selected"</c> (with <see cref="OptionId"/> set to the chosen
/// <see cref="PermissionOptionDto.OptionId"/>) or <c>"cancelled"</c> (denial/timeout/agent-exit) —
/// spec-derived, NOT probe-confirmed.
/// </summary>
public sealed record PermissionOutcomeDto(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("optionId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                             string? OptionId = null
);

/// <summary>
/// Capability-gated, NOT part of the core ACP spec — modeled defensively on the same
/// request/response shape as <see cref="SessionRequestPermissionParams"/> since no confirmed
/// elicitation schema exists for Cursor (AI-682 R3's open question: "Does Cursor use the ACP
/// elicitation RFD shape or a vendor extension?" is unanswered). The daemon never advertises
/// support for this method in <c>initialize</c> (see <c>AcpHostedAgentRuntime.StartAsync</c>'s
/// existing minimal-capabilities <c>ClientCapabilities</c>, unchanged by this plan) — if a real
/// agent sends it anyway, <c>AcpInteractionBridge</c> still answers deterministically (see Task
/// B2) rather than crashing or hanging, but this is explicitly a defensive best-effort path, not a
/// negotiated capability.
///
/// <b>Spec-review Finding 1:</b> <see cref="RequestedSchema"/> is the JSON-Schema half of the
/// roadmap spec's "options OR JSON Schema" elicitation shape — spec-derived field name
/// (<c>requestedSchema</c> on the wire), following the MCP elicitation convention this
/// ACP-capability-gated method is modeled after (there is no ACP-native precedent for it at all).
/// <see cref="Options"/> and <see cref="RequestedSchema"/> are independent optional fields, not a
/// discriminated union — a real agent could in principle send either, both, or neither;
/// <see cref="Capacitor.Cli.Daemon.Acp.AcpInteractionBridge"/> (Task B3) forwards
/// <see cref="RequestedSchema"/> to the server verbatim (capped server-side, Task A1/A3) and never
/// attempts to render or validate it itself.
/// </summary>
public sealed record ElicitationCreateParams(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("message")]   string Message,
    [property: JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                               PermissionOptionDto[]? Options = null,
    [property: JsonPropertyName("requestedSchema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                               JsonElement? RequestedSchema = null
);

/// <summary>Client's JSON-RPC <c>result</c> for a (capability-gated) <c>elicitation/create</c> request.</summary>
public sealed record ElicitationCreateResult(
    [property: JsonPropertyName("outcome")] PermissionOutcomeDto Outcome
);
