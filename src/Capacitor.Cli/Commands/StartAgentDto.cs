using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>Body of POST /api/agents/start. The four optional members are left off the wire when null.</summary>
record StartAgentDto(
    [property: JsonPropertyName("session_id")]      string  SessionId,
    [property: JsonPropertyName("cwd")]             string  Cwd,
    [property: JsonPropertyName("repo_path")]       string  RepoPath,
    [property: JsonPropertyName("prompt")]          string  Prompt,
    [property: JsonPropertyName("title")]           string  Title,
    [property: JsonPropertyName("work_item")]       string  WorkItem,
    [property: JsonPropertyName("vendor")]          string  Vendor,
    [property: JsonPropertyName("machine_id")]      string? MachineId     = null,
    [property: JsonPropertyName("caller_agent_id")] string? CallerAgentId = null,
    [property: JsonPropertyName("model")]           string? Model         = null,
    [property: JsonPropertyName("daemon")]          string? Daemon        = null
);
