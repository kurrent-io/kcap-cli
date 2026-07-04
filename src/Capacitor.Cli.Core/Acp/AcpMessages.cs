// src/Capacitor.Cli.Core/Acp/AcpMessages.cs
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
