using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

record TranscriptBatch {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    [JsonPropertyName("line_numbers")]
    public int[]? LineNumbers { get; init; }

    [JsonPropertyName("repository")]
    public RepositoryPayload? Repository { get; init; }

    // Routes the server's INormalizerSelector to CodexNormalizer when "codex".
    // Null/absent → server treats the batch as Claude (default). Omitted on the
    // wire when null so older servers (pre-#576) keep deserialising the batch
    // unchanged — the server-side record had no vendor field before that PR.
    [JsonPropertyName("vendor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vendor { get; init; }

    // When true, the server returns non-2xx if any line in the batch fails to normalize
    // (HandleTranscript → 500 on batch.Strict && Failed>0), so a fail-closed importer aborts
    // instead of proceeding over a partially-ingested transcript. Omitted on the wire when
    // false so older servers keep deserialising unchanged.
    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Strict { get; init; }
}

record ErrorEntry(
        string         SessionId,
        string?        SessionSlug,
        string?        AgentId,
        int            EventNumber,
        string?        ToolName,
        string         Error,
        DateTimeOffset Timestamp
    );

record RecapEntry(
        string         Type,
        string?        SessionId,
        string?        AgentId,
        string?        AgentType,
        string         Content,
        string?        FilePath,
        DateTimeOffset Timestamp
    );

record RepositoryPayload {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PrTitle { get; init; }

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("pr_head_ref")]
    public string? PrHeadRef { get; init; }
}

record GitCacheEntry {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("cached_at")]
    public DateTimeOffset CachedAt { get; init; }
}

class WatchState {
    public int                LinesProcessed     { get; set; }
    public RepositoryPayload? Repository         { get; set; }
    public RepositoryPayload? LastSentRepository { get; set; }
    public DateTimeOffset     LastRepoDetection  { get; set; }
    public bool               InitialTitleSent   { get; set; }
    public bool               TitleGenerated     { get; set; }
    public int                TitleAttempts      { get; set; }
    public bool               TitleInFlight      { get; set; }
    public string?            FirstUserText      { get; set; }
    public bool               FullFileScanDone   { get; set; }
    public string?            FirstAssistantText { get; set; }
    public int                EventCount         { get; set; }

    // Buffering: hold transcript lines until threshold is reached to avoid polluting
    // the server with short-lived sessions (e.g. <local-command-caveat> prompts)
    public List<string> BufferedLines       { get; } = [];
    public List<int>    BufferedLineNumbers { get; } = [];
    public int          LinesReadAhead      { get; set; } // file position while buffering
    public bool         ThresholdReached    { get; set; }

    // Last wall-clock time new transcript content was observed on the rollout file.
    // Drives the Codex idle-timeout fallback (see WatchCommand.ShouldEndOnIdle).
    // Initialized when the watcher starts; updated in DrainNewLines on new lines.
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    // Tracks Codex tool-call call_ids that are currently in flight (started but
    // not yet finished). A function_call/custom_tool_call response_item adds the
    // call_id; its matching _output removes it. While this set is non-empty, the
    // idle-end check is suppressed: a long-running shell command or custom tool can
    // legitimately produce no new rollout lines for >60 min between its start and
    // output lines (the tool is still running). No hard ceiling: if the tool hangs
    // forever but the Codex process is still alive, the parent-exit watchdog will
    // eventually fire and end the session — adding a ceiling here would be YAGNI.
    public HashSet<string> PendingCodexToolCalls { get; } = new(StringComparer.Ordinal);

    // Highwater mark of the last Antigravity gen_metadata row already streamed as a
    // synthetic USAGE line, so the watcher only sends newly-appended cost rows on each
    // poll (server dedup by deterministic id is the backstop). -1 = none seen yet.
    public long LastAntigravityGenIdx { get; set; } = -1;

    // Most-recent Antigravity transcript step created_at, stamped onto synthetic USAGE lines
    // so their backfill event's recency reflects the turn, not the event-store write time.
    public string? LastAntigravityCreatedAt { get; set; }

    // Antigravity tool calls seen without a matching result step yet (PLANNER_RESPONSE
    // tool_calls increment; RUN_COMMAND/VIEW_FILE/LIST_DIRECTORY/CODE_ACTION decrement). A
    // long-running command produces no transcript line between its call and result, so this
    // suppresses the idle-timeout session-end while a tool is genuinely in flight (mirrors
    // the Codex PendingCodexToolCalls guard).
    public int PendingAntigravityToolCalls { get; set; }

    public const int TranscriptThreshold = 10;
}

record SessionTitlePayload {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }
}

record WhatsDonePayload {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long CacheWriteTokens { get; init; }
}

record RepoRecapEntry(
        string          SessionId,
        string?         Slug,
        string?         Title,
        DateTimeOffset  StartedAt,
        DateTimeOffset? EndedAt,
        string          Summary
    );

// ── Eval command types — see DEV-1433 ─────────────────────────────────────

// Response shape from GET /api/sessions/{id}/eval-context. Only the fields
// the CLI needs; the server emits more that we don't parse (agent tagging,
// per-tool truncation breakdown, etc.) and System.Text.Json ignores them.
public record EvalContextEntry {
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }
}

public record EvalContextCompactionSummary {
    [JsonPropertyName("threshold_bytes")]
    public required int ThresholdBytes { get; init; }

    [JsonPropertyName("entries")]
    public required int Entries { get; init; }

    [JsonPropertyName("tool_results_total")]
    public required int ToolResultsTotal { get; init; }

    [JsonPropertyName("tool_results_truncated")]
    public required int ToolResultsTruncated { get; init; }

    [JsonPropertyName("bytes_saved")]
    public required long BytesSaved { get; init; }
}

public record EvalContextResult {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("session_chain")]
    public required List<string> SessionChain { get; init; }

    [JsonPropertyName("trace")]
    public required List<EvalContextEntry> Trace { get; init; }

    [JsonPropertyName("compaction")]
    public required EvalContextCompactionSummary Compaction { get; init; }
}

/// <summary>
/// Wire-format DTO for a single eval question served by
/// <c>GET /api/eval/questions</c>. Mirrors the shape of
/// <c>Kurrent.Capacitor.EvalQuestionMetadata.Question</c> on the server —
/// the CLI cannot reference the Shared library (standalone submodule),
/// so the shape is duplicated here.
/// </summary>
public record EvalQuestionDto {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    // DEV-1486: server-owned flag that opts this question into tools-enabled
    // judging. Defaults to false so older servers that don't send the field
    // keep producing text-only judge runs.
    [JsonPropertyName("needs_tools")]
    public bool NeedsTools { get; init; }

    // AI-9 Phase 3 — the catalog prompt version this question's rendered prompt
    // ran against. Null on the back-compat /api/eval/questions alias (which does
    // not emit it) and on older servers; populated only by /api/eval/catalog.
    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }

    // AI-9 Phase 3 — RAW question text from the catalog, used by the tools path
    // (the embedded tools template substitutes this into {QUESTION_TEXT}). Null on
    // the alias / older servers. Distinct from Prompt, which on a reconciled
    // text-path question holds the server-RENDERED prompt.
    [JsonPropertyName("raw_text")]
    public string? RawText { get; init; }
}

/// <summary>
/// Wire-format DTO for <c>GET /api/eval/catalog</c> (AI-9 Phase 3). Carries the
/// server-rendered retrospective prompt + its version, and the active questions
/// with raw text + server-rendered prompt + per-question prompt version +
/// needs_tools. There is NO top-level question template — the daemon uses each
/// question's rendered <c>Prompt</c> directly. The daemon fetches this once per
/// run and reconciles its run question list from it. Mirrors the server's Phase-2
/// EvalCatalogResponse shape (the CLI cannot reference the server library — shape
/// is duplicated).
/// </summary>
public record EvalCatalogDto {
    [JsonPropertyName("retrospective_prompt")]
    public required string RetrospectivePrompt { get; init; }

    [JsonPropertyName("retrospective_prompt_version")]
    public required string RetrospectivePromptVersion { get; init; }

    [JsonPropertyName("questions")]
    public List<EvalCatalogQuestionDto> Questions { get; init; } = [];
}

/// <summary>A single active question from <c>GET /api/eval/catalog</c>.</summary>
public record EvalCatalogQuestionDto {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("question_text")]
    public required string QuestionText { get; init; }   // RAW (tools path)

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }         // server-rendered (text path)

    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; init; }

    // SHOULD-FIX (round 2): `required` so a Phase-2 response that OMITS needs_tools
    // fails deserialization loudly rather than silently defaulting false (which would
    // route a tools question to the text path). System.Text.Json enforces `required`
    // members — a missing `needs_tools` throws JsonException. See the missing-field test.
    [JsonPropertyName("needs_tools")]
    public required bool NeedsTools { get; init; }
}

// Per-question verdict returned by each judge invocation. Matches the server
// event shape in SessionMetadataEvents.cs. `Evidence` is optional — judges
// may omit it when there's nothing specific to quote.
public record EvalQuestionVerdict {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("question_id")]
    public required string QuestionId { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("finding")]
    public required string Finding { get; init; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }

    // DEV-1486: tool-call count for tools-enabled judges. Null for text-only
    // questions. Populated from the claude CLI's num_turns field minus 1
    // (the final StructuredOutput turn). Shipped back to the server so the
    // dashboard can surface actual budget spent per question.
    [JsonPropertyName("tools_used")]
    public int? ToolsUsed { get; init; }

    // AI-9 Phase 3 — catalog prompt version stamped at aggregation time before
    // POSTing the V3 payload. Null until Aggregate fills it from the catalog.
    [JsonPropertyName("prompt_version")]
    public string? PromptVersion { get; init; }
}

public record EvalCategoryResult {
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("score")]
    public required int Score { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("questions")]
    public List<EvalQuestionVerdict> Questions { get; init; } = [];
}

public record EvalRetrospective {
    [JsonPropertyName("overall")]
    public required string OverallSummary { get; init; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; init; } = [];

    [JsonPropertyName("issues")]
    public List<string> Issues { get; init; } = [];

    [JsonPropertyName("suggestions")]
    public List<string> Suggestions { get; init; } = [];
}

// Cross-eval memory — DEV-1434 / DEV-1438. Judges may optionally emit a
// retain_fact when they spot a cross-cutting pattern; the CLI POSTs it to
// the session-scoped endpoint and the server derives repo scope from the
// session (facts live on JudgeFacts-repo-{repoHash}-{category} streams).
// Facts accumulated on the same repo by any team member are fetched at
// eval startup and injected into each judge's prompt as "known patterns".
record JudgeFactPayload {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }
}

public record JudgeFact {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    // Nullable for backward compat with older servers that don't return this field.
    [JsonPropertyName("fact_hash")]
    public string? FactHash { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    // Nullable for backward compat with older servers that don't return this field.
    [JsonPropertyName("retainer_github_id")]
    public long? RetainerGitHubId { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("retained_at")]
    public required DateTimeOffset RetainedAt { get; init; }
}

// Snapshot of a judge fact at eval time. Sent in the facts_used field of
// SessionEvalCompletedPayload so the server can persist which facts were
// in scope when the eval ran, even if the live judge_facts pool is later
// modified (muted, deleted, replaced).
public record EvalFactSnapshotPayload {
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fact_hash")]
    public required string FactHash { get; init; }

    [JsonPropertyName("fact")]
    public required string Fact { get; init; }

    [JsonPropertyName("retainer_github_id")]
    public required long RetainerGitHubId { get; init; }

    [JsonPropertyName("source_session_id")]
    public required string SourceSessionId { get; init; }

    [JsonPropertyName("source_eval_run_id")]
    public required string SourceEvalRunId { get; init; }

    [JsonPropertyName("retained_at")]
    public required DateTimeOffset RetainedAt { get; init; }
}

// Posted to POST /api/sessions/{id}/evals. The server fills evaluated_at.
public record SessionEvalCompletedPayload {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("judge_model")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("categories")]
    public List<EvalCategoryResult> Categories { get; init; } = [];

    [JsonPropertyName("overall_score")]
    public required int OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("retrospective")]
    public EvalRetrospective? Retrospective { get; init; }

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
}

// V2 retrospective types — structured suggestions with audience tag.
// Mirror of server-side RetrospectiveSuggestion / EvalRetrospectiveV2 /
// SessionEvalCompletedPayloadV2 in Capacitor.Server.Core.
// Wire shape must stay 1:1 with the server so the V2 POST route deserializes
// correctly (snake_case field names enforced by [JsonPropertyName] below).

public record RetrospectiveSuggestion {
    [JsonPropertyName("text")]     public required string Text     { get; init; }
    [JsonPropertyName("audience")] public required string Audience { get; init; } // "agent" | "human"
}

public record EvalRetrospectiveV2 {
    // Backing fields coerce an explicit JSON `null` to an empty list so a
    // judge response like `"strengths": null` deserializes to an empty list
    // rather than a null field, keeping downstream code null-safe.

    [JsonPropertyName("overall")]
    public required string OverallSummary { get; init; }

    [JsonPropertyName("strengths")]
    public List<string> Strengths { get; init => field = value ?? []; } = [];

    [JsonPropertyName("issues")]
    public List<string> Issues { get; init => field = value ?? []; } = [];

    [JsonPropertyName("suggestions")]
    public List<RetrospectiveSuggestion> Suggestions { get; init => field = value ?? []; } = [];
}

// Posted to POST /api/sessions/{id}/evals/v2.
// Differs from SessionEvalCompletedPayload only in Retrospective type.
public record SessionEvalCompletedPayloadV2 {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("judge_model")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("categories")]
    public List<EvalCategoryResult> Categories { get; init; } = [];

    [JsonPropertyName("overall_score")]
    public required int OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("retrospective")]
    public EvalRetrospectiveV2? Retrospective { get; init; }

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
}

// Posted to POST /api/sessions/{id}/evals/v3 (AI-9 Phase 3). Differs from V2 by
// adding retrospective_prompt_version; the per-question version rides on each
// EvalQuestionVerdict.PromptVersion. Wire shape must stay 1:1 with the server's
// SessionEvalCompletedPayloadV3 in Capacitor.Server.
public record SessionEvalCompletedPayloadV3 {
    [JsonPropertyName("eval_run_id")]
    public required string EvalRunId { get; init; }

    [JsonPropertyName("judge_model")]
    public required string JudgeModel { get; init; }

    [JsonPropertyName("categories")]
    public List<EvalCategoryResult> Categories { get; init; } = [];

    [JsonPropertyName("overall_score")]
    public required int OverallScore { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("retrospective")]
    public EvalRetrospectiveV2? Retrospective { get; init; }

    [JsonPropertyName("retrospective_prompt_version")]
    public string? RetrospectivePromptVersion { get; init; }

    [JsonPropertyName("facts_used")]
    public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];
}

enum HistorySessionStatus { New, Partial, AlreadyLoaded }

class SessionMetadata {
    public string?         Cwd            { get; set; }
    public string?         Model          { get; set; }
    public string?         Slug           { get; set; }
    public string?         SessionId      { get; set; }
    public DateTimeOffset? FirstTimestamp { get; set; }
    public DateTimeOffset? LastTimestamp  { get; set; }
}

static partial class GitUrlParser {
    public static (string? Owner, string? RepoName) ParseRemoteUrl(string? url) {
        if (url is null) {
            return (null, null);
        }

        var sshMatch = SshRegex().Match(url);

        if (sshMatch.Success) {
            return (sshMatch.Groups["owner"].Value, sshMatch.Groups["repo"].Value);
        }

        var sshProtoMatch = SshProtoRegex().Match(url);

        if (sshProtoMatch.Success) {
            return (sshProtoMatch.Groups["owner"].Value, sshProtoMatch.Groups["repo"].Value);
        }

        var httpsMatch = HttpsRegex().Match(url);

        return httpsMatch.Success
            ? (httpsMatch.Groups["owner"].Value, httpsMatch.Groups["repo"].Value)
            : (null, null);
    }

    // owner is greedy (`.+`) so a nested GitLab namespace (group/subgroup/...) is
    // captured whole, with repo as the final path segment. AI-1121 / §6b.
    [GeneratedRegex(@"https?://[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    // Anchored: a greedy multi-segment owner would otherwise let this match the
    // "git@host:port" inside an ssh:// URL and steal it from SshProtoRegex.
    [GeneratedRegex(@"^git@[\w.-]+:(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshRegex();

    [GeneratedRegex(@"ssh://(?:[^@/]+@)?[^/]+/(?<owner>.+)/(?<repo>[^/]+?)(?:\.git)?$")]
    internal static partial Regex SshProtoRegex();
}

public record RepoEntry {
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("last_used")]
    public required DateTimeOffset LastUsed { get; init; }
}

public sealed record CurationApplyItem {
    [JsonPropertyName("category")]      public string?               Category     { get; init; }
    [JsonPropertyName("cluster_id")]    public string?               ClusterId    { get; init; }
    [JsonPropertyName("promoted_text")] public string?               PromotedText { get; init; }
    [JsonPropertyName("target_kinds")]  public IReadOnlyList<string>? TargetKinds { get; init; }
    [JsonPropertyName("status")]        public string?               Status       { get; init; }
}

public sealed record CurationApplyResponse {
    [JsonPropertyName("repo_hash")] public string?                      RepoHash { get; init; }
    [JsonPropertyName("items")]     public IReadOnlyList<CurationApplyItem>? Items { get; init; }
}

[JsonSerializable(typeof(List<RecapEntry>))]
[JsonSerializable(typeof(List<RepoRecapEntry>))]
[JsonSerializable(typeof(EvalContextResult))]
[JsonSerializable(typeof(EvalQuestionDto))]
[JsonSerializable(typeof(EvalQuestionDto[]))]
[JsonSerializable(typeof(EvalQuestionVerdict))]
[JsonSerializable(typeof(IReadOnlyList<EvalQuestionVerdict>))]
[JsonSerializable(typeof(EvalRetrospective))]
[JsonSerializable(typeof(SessionEvalCompletedPayload))]
[JsonSerializable(typeof(RetrospectiveSuggestion))]
[JsonSerializable(typeof(EvalRetrospectiveV2))]
[JsonSerializable(typeof(SessionEvalCompletedPayloadV2))]
[JsonSerializable(typeof(JudgeFactPayload))]
[JsonSerializable(typeof(List<JudgeFact>))]
[JsonSerializable(typeof(EvalFactSnapshotPayload))]
[JsonSerializable(typeof(List<EvalFactSnapshotPayload>))]
[JsonSerializable(typeof(EvalCatalogDto))]
[JsonSerializable(typeof(EvalCatalogQuestionDto))]
[JsonSerializable(typeof(SessionEvalCompletedPayloadV3))]
[JsonSerializable(typeof(List<ErrorEntry>))]
[JsonSerializable(typeof(RepositoryPayload))]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSerializable(typeof(TranscriptBatch))]
[JsonSerializable(typeof(SessionTitlePayload))]
[JsonSerializable(typeof(WhatsDonePayload))]
[JsonSerializable(typeof(Auth.StoredTokens))]
[JsonSerializable(typeof(Auth.AuthDiscoveryResponse))]
[JsonSerializable(typeof(Auth.TokenExchangeRequest))]
[JsonSerializable(typeof(Auth.TokenExchangeResponse))]
[JsonSerializable(typeof(Auth.AuthErrorResponse))]
[JsonSerializable(typeof(Auth.RefreshTokenRequest))]
[JsonSerializable(typeof(Auth.GitHubDeviceCodeResponse))]
[JsonSerializable(typeof(Auth.GitHubTokenResponse))]
[JsonSerializable(typeof(Auth.GitHubCodeExchangeRequest))]
[JsonSerializable(typeof(Auth.WorkOSAuthResponse))]
[JsonSerializable(typeof(Auth.WorkOSUserInfo))]
[JsonSerializable(typeof(Auth.ProxyConfigResponse))]
[JsonSerializable(typeof(Auth.DiscoveredTenant[]))]
[JsonSerializable(typeof(LaunchAgentCommand))]
[JsonSerializable(typeof(ReviewLaunchInfo))]
[JsonSerializable(typeof(LaunchKind))]
[JsonSerializable(typeof(FindRepoForRemoteRequest))]
[JsonSerializable(typeof(RefreshAgentWorktreeCommand))]
[JsonSerializable(typeof(RefreshAgentWorktreeResult))]
[JsonSerializable(typeof(SendInputCommand))]
[JsonSerializable(typeof(ResizeTerminalCommand))]
[JsonSerializable(typeof(PrepareEvalCommand))]
[JsonSerializable(typeof(RunQuestionCommand))]
[JsonSerializable(typeof(FinalizeEvalCommand))]
[JsonSerializable(typeof(CancelEvalCommand))]
[JsonSerializable(typeof(PrepareResult))]
[JsonSerializable(typeof(QuestionResult))]
[JsonSerializable(typeof(FinalizeResult))]
[JsonSerializable(typeof(EvalStarted))]
[JsonSerializable(typeof(EvalQuestionStarted))]
[JsonSerializable(typeof(EvalQuestionCompleted))]
[JsonSerializable(typeof(EvalQuestionFailed))]
[JsonSerializable(typeof(EvalFinished))]
[JsonSerializable(typeof(EvalFailed))]
[JsonSerializable(typeof(EvalRetrospectiveStarted))]
[JsonSerializable(typeof(EvalRetrospectiveCompleted))]
[JsonSerializable(typeof(EvalRetrospectiveFailed))]
[JsonSerializable(typeof(DaemonConnect))]
[JsonSerializable(typeof(AgentRegistered))]
[JsonSerializable(typeof(AgentStatusChanged))]
[JsonSerializable(typeof(AgentUnregistered))]
[JsonSerializable(typeof(LaunchFailed))]
[JsonSerializable(typeof(TerminalOutput))]
[JsonSerializable(typeof(AgentRunStarted))]
[JsonSerializable(typeof(AgentRunStopped))]
[JsonSerializable(typeof(AgentRunHeartbeat))]
[JsonSerializable(typeof(PermissionDecision))]
[JsonSerializable(typeof(HostedPermissionRequest))]
[JsonSerializable(typeof(PermissionResolution))]
[JsonSerializable(typeof(EndAgentSessionResult))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(RepoEntry[]))]
[JsonSerializable(typeof(CurationApplyResponse))]
[JsonSerializable(typeof(CurationApplyItem))]
[JsonSerializable(typeof(Auth.ProvisionRequest))]
[JsonSerializable(typeof(Auth.ProvisionResponse))]
[JsonSerializable(typeof(Auth.AvailabilityResponse))]
[JsonSerializable(typeof(Auth.StatusResponse))]
[JsonSerializable(typeof(Acp.AcpRequest))]
[JsonSerializable(typeof(Acp.AcpResponse))]
[JsonSerializable(typeof(Acp.AcpNotification))]
[JsonSerializable(typeof(Acp.AcpError))]
[JsonSerializable(typeof(Acp.InitializeParams))]
[JsonSerializable(typeof(Acp.ClientCapabilities))]
[JsonSerializable(typeof(Acp.FsCapabilities))]
[JsonSerializable(typeof(Acp.SessionNewParams))]
[JsonSerializable(typeof(Acp.SessionPromptParams))]
[JsonSerializable(typeof(Acp.PromptContentBlock))]
[JsonSerializable(typeof(Acp.SessionCancelParams))]
[JsonSerializable(typeof(Acp.SessionRequestPermissionParams))]
[JsonSerializable(typeof(Acp.PermissionOptionDto))]
[JsonSerializable(typeof(Acp.PermissionOutcomeResult))]
[JsonSerializable(typeof(Acp.PermissionOutcomeDto))]
[JsonSerializable(typeof(Acp.ElicitationCreateParams))]
[JsonSerializable(typeof(Acp.ElicitationCreateResult))]
[JsonSerializable(typeof(AcpInteractionRequest))]
[JsonSerializable(typeof(AcpInteractionOption))]
[JsonSerializable(typeof(AcpInteractionDecision))]
[JsonSerializable(typeof(AcpInteractionResolution))]
// UseStringEnumConverter=true matches the server's SignalR JSON protocol, which
// serialises enums (e.g. LaunchKind) as camelCase strings. Without it the
// source-gen LaunchKind JsonTypeInfo defaults to numeric and silently drops the
// invocation — the daemon receives "kind": "review" / "default" and the
// LaunchAgent handler never fires (DEV-1665).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true
)]
partial class CapacitorJsonContext : JsonSerializerContext;

/// <summary>
/// Decision returned by the server's <c>RequestPermission</c> SignalR hub method.
/// Mirrors <c>PermissionResponseEntry</c> on the server side. ApplyPermissions /
/// UpdatedInput are typed as <see cref="JsonElement"/> so the daemon can relay
/// them verbatim into Claude's hook decision payload without the server having
/// to know about the hook wire shape.
/// </summary>
public readonly record struct PermissionDecision(
        string       Behavior,
        JsonElement? ApplyPermissions,
        JsonElement? UpdatedInput
    );

/// <summary>
/// Single-argument payload for the <c>RequestPermission2</c> hub invocation (AI-864). SignalR
/// binds hub-method arguments by count, so a record keeps the arity fixed at 1 and lets the wire
/// contract gain fields without breaking mixed-version servers. Mirrors the server-side record of
/// the same name in Capacitor.Server; property names must stay in sync (snake_case on the wire).
/// </summary>
public readonly record struct HostedPermissionRequest(
        string       SessionId,
        string?      ToolName,
        JsonElement? ToolInput,
        JsonElement? Suggestions
    );

/// <summary>
/// Payload of the <c>PermissionResolved</c> server→client push (AI-864): the user's decision for a
/// hosted-agent permission request, correlated by <see cref="RequestId"/>. A single record (not
/// positional args) so the push contract can gain fields without breaking mixed-version daemons —
/// SignalR binds by argument count. Mirrors the server-side record of the same name.
/// </summary>
public readonly record struct PermissionResolution(
        string             RequestId,
        PermissionDecision Decision
    );

/// <summary>
/// Single-argument payload for the <c>AcpRequestInteraction</c> hub invocation (AI-686). Mirrors
/// the server-side record of the same name in <c>Capacitor.Server.Core</c> (<c>src/Capacitor.Server.Core/AcpInteraction.cs</c>);
/// property names must stay in sync (snake_case on the wire via this context's naming policy).
/// <b>Spec-review Finding 1:</b> <see cref="RequestedSchema"/> is a new OPTIONAL trailing field,
/// mirroring the server-side <c>AcpInteractionRequest.RequestedSchema</c> exactly (same name,
/// position, and nullability) — kept in lockstep across the wire boundary the same way every other
/// field on this type already is (see Task A2's Interfaces note for the "server `record` / daemon
/// `readonly record struct`, same JSON shape" convention this type follows).
/// </summary>
public readonly record struct AcpInteractionRequest(
        string                 AgentId,
        string                 AcpSessionId,
        string                 Kind,
        string?                ToolName,
        JsonElement?           ToolInput,
        string?                ToolCallId,
        string?                Prompt,
        AcpInteractionOption[]? Options,
        bool                   IsMultiSelect,
        JsonElement?           RequestedSchema = null
    );

/// <summary>
/// One selectable option for an ACP permission or elicitation interaction (AI-686). Spec-review
/// Finding 6: <see cref="OptionId"/> is the stable resolution key (mirrors
/// <c>Acp.PermissionOptionDto.OptionId</c>) — <see cref="Label"/> is display-only.
/// </summary>
public readonly record struct AcpInteractionOption(string OptionId, string Label, string? Description, string? Kind = null);

/// <summary>
/// Decision for an ACP interaction (AI-686), pushed from the server. Mirrors the server-side
/// record of the same name. Spec-review Finding 6: <see cref="SelectedOptionId"/> is what
/// <c>AcpInteractionBridge.MapPermissionDecision</c> (Task B3) matches against — never
/// <see cref="SelectedOptionLabel"/>, which is retained for display/attribution only.
/// </summary>
public readonly record struct AcpInteractionDecision(
        string       Outcome,
        string?      SelectedOptionId,
        string?      SelectedOptionLabel,
        int?         SelectedIndex,
        string?      FreeText,
        JsonElement? UpdatedToolInput
    );

/// <summary>
/// Payload of the <c>AcpInteractionResolved</c> server→client push (AI-686), correlated by
/// <see cref="RequestId"/>. Mirrors the server-side record of the same name.
/// </summary>
public readonly record struct AcpInteractionResolution(
        string             RequestId,
        AcpInteractionDecision Decision
    );

/// <summary>Commands sent from the server to daemon clients via SignalR.</summary>
public readonly record struct LaunchAgentCommand(
        string            AgentId,
        string?           Prompt,
        string            Model,
        string?           Effort,
        string            RepoPath,
        string[]?         Tools,
        string[]?         AttachmentIds,
        string            Vendor,
        LaunchKind        Kind            = LaunchKind.Default,
        ReviewLaunchInfo? Review          = null,
        string?           BaseRef         = null,
        // AI-1163: for a mirror-requester review flow, the requester's repo root. When set, the
        // daemon syncs its working tree (uncommitted + untracked) into the freshly-created reviewer
        // worktree BEFORE spawning, so round 1 sees in-progress code — not just committed HEAD. The
        // daemon validates the source is a checkout of the same repo (origin match) before copying;
        // a mismatch (e.g. a different machine, where the path doesn't resolve) skips the sync.
        // Appended last as an optional field so the SignalR positional binding stays wire-compatible
        // with older daemons/servers.
        string?           SyncFromRepoRoot = null,
        // AI-1126 D-c: for a review-flow launch, the flow definition's MCP allowlist — server-owned
        // names the daemon resolves against the kcap-owned KcapMcpRegistry and materializes into the
        // launcher's MCP config (flow-starting servers are stripped regardless of listing). Appended
        // last, same wire-compat rule as SyncFromRepoRoot above.
        string[]?         McpAllowlist = null
    );

/// <summary>
/// Discriminator for daemon launch commands. <see cref="Default"/> preserves
/// the existing prompt-driven launch; <see cref="Review"/> uses
/// <see cref="ReviewLaunchInfo"/> + <c>BaseRef</c> to drive a hosted PR review;
/// <see cref="ReviewFlow"/> (AI-1089) marks a durable agent-review-flow reviewer, which the
/// daemon runs unattended (never approval + no MCP). The value crosses the CLI↔server wire, so
/// it MUST stay Default=0, Review=1, ReviewFlow=2.
/// </summary>
public enum LaunchKind {
    Default    = 0,
    Review     = 1,
    ReviewFlow = 2
}

public readonly record struct ReviewLaunchInfo(
        string Owner,
        string Repo,
        int    PrNumber
    );

/// <summary>
/// Server → daemon probe asking "which of these candidate paths are a local
/// checkout of <c>owner/repo</c>?". The daemon merges the candidates with its
/// own knowledge, walks each up to a git root, validates origin, and returns
/// the confirmed roots.
/// </summary>
public readonly record struct FindRepoForRemoteRequest(
        string   Owner,
        string   Repo,
        string[] CandidatePaths
    );

/// <summary>
/// Server → daemon command to sync the source repo's current working-tree state (tracked +
/// untracked non-ignored files) into the reviewer agent's daemon-created worktree, so the
/// reviewer sees Claude's latest uncommitted changes before a code-review follow-up round.
/// Wire keys (snake_case): <c>agent_id</c>, <c>source_repo_root</c>, <c>exclude_paths</c>.
/// </summary>
public readonly record struct RefreshAgentWorktreeCommand(
        string   AgentId,
        string   SourceRepoRoot,
        string[] ExcludePaths
    );

/// <summary>
/// Daemon reply to <see cref="RefreshAgentWorktreeCommand"/>. <see cref="Success"/> is
/// <c>false</c> when a guard prevented the sync or the sync itself threw; <see cref="Error"/>
/// carries the reason. Wire keys: <c>success</c>, <c>error</c>.
/// </summary>
public readonly record struct RefreshAgentWorktreeResult(
        bool    Success,
        string? Error
    );

public readonly record struct SendInputCommand(
        string    AgentId,
        string    Text,
        string[]? AttachmentIds
    );

public readonly record struct ResizeTerminalCommand(
        string AgentId,
        int    Cols,
        int    Rows
    );

/// <summary>
/// Commands sent from daemon clients to the server via SignalR.
///
/// <para><c>InstanceId</c> is a fresh GUID generated at daemon process startup
/// and held only in memory (also written to the daemon's per-name flock
/// file content for diagnostics). The server uses it to distinguish a
/// legitimate reconnect of the same daemon (new SignalR connectionId, same
/// instance) from a different daemon process claiming the same
/// <c>(owner, name)</c> slot. Pre-AI-630 daemons sent no <c>InstanceId</c>;
/// the server still accepts them under a legacy-displacement fallback.</para>
///
/// <para><c>Version</c> is the daemon binary's
/// <c>AssemblyInformationalVersion</c>. Logged on connect and surfaced on
/// the server's <c>DaemonInfo</c> so the dashboard can show what version
/// each connected daemon is running.</para>
/// </summary>
public readonly record struct DaemonConnect(
        string    Name,
        string    Platform,
        string[]  RepoPaths,
        int       MaxAgents,
        string[]  LiveAgentIds,
        string?   InstanceId       = null,
        string?   Version          = null,
        string[]? SupportedVendors = null
    );

public readonly record struct AgentRegistered(
        string  AgentId,
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath
    );

public readonly record struct AgentStatusChanged(
        string  AgentId,
        string  Status,
        string? SessionId
    );

public readonly record struct AgentUnregistered(string AgentId);

public readonly record struct LaunchFailed(
        string AgentId,
        string Reason
    );

public readonly record struct TerminalOutput(
        string AgentId,
        string Base64Data
    );

// ── Per-question eval dispatch (DEV-1463 PR 2) ────────────────────────────
// Plain PascalCase records — no [JsonPropertyName] attrs — so they round-trip
// via SignalR's default JSON protocol with the matching server-side records.
// Inner DTOs (EvalQuestionDto, EvalQuestionVerdict) carry their own snake_case
// [JsonPropertyName] attrs which agree on both ends (see server's
// EvalQuestionMetadata.Question and SessionMetadataEvents.EvalQuestionVerdict).

/// <summary>Server → daemon: prepare an eval run. Daemon fetches + caches context, returns counts.</summary>
public readonly record struct PrepareEvalCommand(
        string                         EvalRunId,
        string                         SessionId,
        string                         Model,
        bool                           Chain,
        int?                           ThresholdBytes,
        IReadOnlyList<EvalQuestionDto> Questions
    );

/// <summary>Server → daemon: run a single judge question against the cached context.</summary>
public readonly record struct RunQuestionCommand(
        string          EvalRunId,
        EvalQuestionDto Question,
        int             Index,
        int             Total
    );

/// <summary>Server → daemon: aggregate verdicts, run retrospective, persist final result.</summary>
public readonly record struct FinalizeEvalCommand(
        string                             EvalRunId,
        IReadOnlyList<EvalQuestionVerdict> Verdicts,
        string                             Model
    );

/// <summary>Server → daemon: discard any cached context for this run (e.g. dashboard aborted).</summary>
public readonly record struct CancelEvalCommand(string EvalRunId);

/// <summary>Daemon → server: prepare-phase result.</summary>
public readonly record struct PrepareResult(
        bool    Success,
        string? Error,
        string? CanonicalSessionId,
        int     TraceEntries,
        int     TraceChars,
        int     ToolResultsTotal,
        int     ToolResultsTruncated,
        long    BytesSaved
    );

/// <summary>Daemon → server: per-question judge result.</summary>
public readonly record struct QuestionResult(
        bool                 Success,
        EvalQuestionVerdict? Verdict,
        string?              Error,
        long                 InputTokens,
        long                 OutputTokens
    );

/// <summary>Daemon → server: finalize-phase result including the aggregate to persist.</summary>
public readonly record struct FinalizeResult(
        bool                         Success,
        string?                      Error,
        SessionEvalCompletedPayload? Aggregate
    );

/// <summary>Daemon → server: eval has fetched context and is about to run the first judge.</summary>
public readonly record struct EvalStarted(
        string EvalRunId,
        string SessionId,
        string JudgeModel,
        int    TotalQuestions
    );

/// <summary>Daemon → server: a judge question started running. Emitted before each claude invocation so the dashboard can show which question is currently in flight even when earlier ones failed.</summary>
public readonly record struct EvalQuestionStarted(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId
    );

/// <summary>Daemon → server: a judge question completed with a verdict.</summary>
public readonly record struct EvalQuestionCompleted(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId,
        int    Score,
        string Verdict
    );

/// <summary>Daemon → server: a judge question failed (claude returned no/unparseable result, timed out, or emitted an out-of-range score). The overall eval continues to the next question.</summary>
public readonly record struct EvalQuestionFailed(
        string EvalRunId,
        string SessionId,
        int    Index,
        int    Total,
        string Category,
        string QuestionId,
        string Reason
    );

/// <summary>Daemon → server: eval run finished end-to-end and aggregate has been persisted.</summary>
public readonly record struct EvalFinished(
        string EvalRunId,
        string SessionId,
        int    OverallScore,
        string Summary
    );

/// <summary>Daemon → server: eval run failed before producing an aggregate.</summary>
public readonly record struct EvalFailed(string EvalRunId, string SessionId, string Reason);

/// <summary>Daemon → server: retrospective pass is about to start (all category judges have completed).</summary>
public readonly record struct EvalRetrospectiveStarted(string SessionId, string EvalRunId);

/// <summary>Daemon → server: retrospective pass produced a summary and has been folded into the aggregate.</summary>
public readonly record struct EvalRetrospectiveCompleted(string SessionId, string EvalRunId);

/// <summary>Daemon → server: retrospective pass failed; the aggregate is still persisted without a retrospective.</summary>
public readonly record struct EvalRetrospectiveFailed(string SessionId, string EvalRunId, string Reason);

/// <summary>Agent run events posted to the server HTTP API.</summary>
public record AgentRunStarted(
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        string? WorktreePath,
        string  Vendor
    );

public record AgentRunStopped(string? Reason, int? ExitCode);

public record AgentRunHeartbeat(string? SessionId);

/// <summary>
/// Returned by the server's <c>EndAgentSession</c> SignalR hub method. Mirrors the
/// server-side record of the same name. SessionId is surfaced because the daemon
/// only knows agentId — it can't spawn <c>kcap generate-whats-done</c> without
/// the sessionId, which the server resolves via FindAgentSessionIdAsync.
/// </summary>
public record EndAgentSessionResult {
    [JsonPropertyName("generate_whats_done")]
    public bool GenerateWhatsDone { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}
