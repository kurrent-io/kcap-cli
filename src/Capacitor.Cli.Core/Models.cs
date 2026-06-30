using System.Text.Json;
using System.Text.Json.Serialization;
using Eventuous;

namespace Capacitor;

// --- Enums ---

[JsonConverter(typeof(JsonStringEnumConverter<SessionStartSource>))]
public enum SessionStartSource {
    Startup,
    Resume,
    Clear,
    Compact
}

[JsonConverter(typeof(SessionEndReasonConverter))]
public enum SessionEndReason {
    Clear,
    Logout,

    [JsonStringEnumMemberName("prompt_input_exit")]
    PromptInputExit,

    [JsonStringEnumMemberName("end_turn")]
    EndTurn,

    [JsonStringEnumMemberName("bypass_permissions_disabled")]
    BypassPermissionsDisabled,

    /// <summary>
    /// Daemon-driven stop: the user clicked Stop on a hosted agent and claude
    /// did not fire its own session-end hook within the graceful window.
    /// </summary>
    [JsonStringEnumMemberName("agent_stopped")]
    AgentStopped,

    /// <summary>
    /// Daemon observed claude exiting without firing session-end (crash, force-kill,
    /// natural exit on SIGTERM where claude didn't run its hooks).
    /// </summary>
    [JsonStringEnumMemberName("agent_exited")]
    AgentExited,

    /// <summary>
    /// Local watcher observed its parent coding-agent process disappear without firing
    /// session-end (terminal closed, crash, force-kill). Watcher self-terminates and
    /// POSTs session-end with this reason so the session doesn't stay "active" forever.
    /// </summary>
    [JsonStringEnumMemberName("parent_exited")]
    ParentExited,

    /// <summary>
    /// Local watcher observed the Codex rollout file go idle (no writes within the
    /// configured timeout window) and synthesised a session-end with this reason.
    /// </summary>
    [JsonStringEnumMemberName("idle_timeout")]
    IdleTimeout,

    Other
}

/// <summary>Falls back to <see cref="SessionEndReason.Other"/> for unknown reason strings.</summary>
public class SessionEndReasonConverter : JsonConverter<SessionEndReason> {
    static readonly JsonStringEnumConverter<SessionEndReason> Inner           = new();
    static readonly JsonSerializerOptions                     FallbackOptions = new();

    static SessionEndReasonConverter() {
        FallbackOptions.Converters.Add(Inner);
    }

    public override SessionEndReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        // Clone the reader so a failed parse doesn't consume the token
        var readerCopy = reader;

        try {
            var value = JsonSerializer.Deserialize<SessionEndReason>(ref readerCopy, FallbackOptions);
            reader = readerCopy; // advance the real reader on success

            return value;
        } catch (JsonException) {
            reader.Skip(); // skip the unknown token

            return SessionEndReason.Other;
        }
    }

    public override void Write(Utf8JsonWriter writer, SessionEndReason value, JsonSerializerOptions options) {
        JsonSerializer.Serialize(writer, value, FallbackOptions);
    }
}

public enum SessionStatus {
    Active,
    Ended
}

public enum DashboardTab {
    Agents,
    Sessions,
    Analytics,
    Facts,
    Curation,
    Evals
}

// --- Transcript entry ---

public record TranscriptEntry {
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("parentUuid")]
    public string? ParentUuid { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

// --- Shared model types ---

public record SessionSummary {
    public required string          SessionId         { get; init; }
    public          string?         Slug              { get; init; }
    public          string?         Title             { get; init; }
    public          string?         Model             { get; init; }
    public          string?         Vendor            { get; init; }
    public          string?         Cwd               { get; init; }
    public          SessionStatus   Status            { get; init; }
    public          DateTimeOffset  StartedAt         { get; init; }
    public          DateTimeOffset? EndedAt           { get; init; }
    public          TimeSpan?       Duration          { get; init; }
    public          int             EventCount        { get; init; }
    public          string?         PreviousSessionId { get; init; }
    public          string?         NextSessionId     { get; init; }
    public          string?         RepoBranch        { get; init; }
    public          string?         RepoOwner         { get; init; }
    public          string?         RepoName          { get; init; }
    public          int?            PrNumber          { get; init; }
    public          string?         PrUrl             { get; init; }
    public          string?         PrTitle           { get; init; }
    public          string?         GitUserName       { get; init; }
    public          string?         GitUserEmail      { get; init; }
    public          string?         HomeDir           { get; init; }
    public          DateTimeOffset? LastEventAt       { get; init; }
    public          SessionStats?   Stats             { get; init; }
    public          string?         OwnerUserId       { get; init; }
    public          string?         Visibility        { get; init; }
    public          string?         DefaultVisibility { get; init; }

    /// <summary>AI-1081: overall eval score (0–5) for this session, or null if not evaluated.
    /// Populated only by the ended-sessions read query; serialized as <c>eval_score</c>.</summary>
    public          int?            EvalScore         { get; init; }

    public List<SessionRepositoryInfo>  Repositories  { get; init; } = [];
    public List<SessionPullRequestInfo> PullRequests  { get; init; } = [];
}

public record SessionRepositoryInfo {
    public required string         RepoHash    { get; init; }
    public required string         Owner       { get; init; }
    public required string         RepoName    { get; init; }
    public          string?        Branch      { get; init; }
    public          bool           IsPrimary   { get; init; }
    public          DateTimeOffset FirstSeenAt { get; init; }
}

public record SessionPullRequestInfo {
    public required string  RepoHash { get; init; }
    public required string  Owner    { get; init; }
    public required string  RepoName { get; init; }
    public required int     Number   { get; init; }
    public          string? Url      { get; init; }
    public          string? Title    { get; init; }
    public          string? HeadRef  { get; init; }
}

public record AgentSummary {
    public required string          AgentId     { get; init; }
    public          string?         AgentType   { get; init; }
    public          string?         SessionId   { get; init; }
    public          SessionStatus   Status      { get; init; }
    public          DateTimeOffset  StartedAt   { get; init; }
    public          DateTimeOffset? EndedAt     { get; init; }
    public          int             EventCount  { get; init; }
    public          DateTimeOffset? LastEventAt { get; init; }
    public          SessionStats?   Stats       { get; init; }
}

public record RepositorySummary {
    public required string         RepoHash         { get; init; }
    public required string         Owner            { get; init; }
    public required string         RepoName         { get; init; }
    public          List<string>   Slugs            { get; init; } = [];
    public          int            MetaSessionCount { get; init; }
    public          DateTimeOffset LatestActivity   { get; init; }
}

public record PendingInput(
        string         SessionId,
        string         AgentId,
        string         AgentType,
        string         EventUuid,
        JsonElement    ToolInput,
        DateTimeOffset DetectedAt,
        // The elicitation tool call's CallId. Inline-result vendors (Gemini) carry
        // no parentUuid on the result, so resolution correlates by this instead (AI-1050).
        string?        CallId = null
    );

public record PendingNotification(string SessionId, string NotificationType, string Message, DateTimeOffset DetectedAt);

public record StreamEvent {
    public required string         EventType   { get; init; }
    public required DateTimeOffset Timestamp   { get; init; }
    public          object?        Payload     { get; init; } // Typed event record from Eventuous TypeMap
    public          Metadata?      Metadata    { get; init; }
    public          long           EventNumber { get; init; } = -1;
}

public class SessionData {
    public required string            SessionId         { get; init; }
    public          string?           Slug              { get; set; }
    public          string?           Title             { get; set; }
    public          string?           Model             { get; set; }
    public          string?           Vendor            { get; set; }
    public          string?           Cwd               { get; set; }
    public          SessionStatus     Status            { get; set; } = SessionStatus.Active;
    public          DateTimeOffset    StartedAt         { get; set; }
    public          DateTimeOffset?   EndedAt           { get; set; }
    public          string?           PreviousSessionId { get; set; }
    public          string?           NextSessionId     { get; set; }
    public          string?           PlanContent       { get; set; }
    public          string?           WhatsDoneContent  { get; set; }
    public          string?           RepoBranch        { get; set; }
    public          string?           RepoOwner         { get; set; }
    public          string?           RepoName          { get; set; }
    public          int?              PrNumber          { get; set; }
    public          string?           PrTitle           { get; set; }
    public          string?           PrUrl             { get; set; }
    public          string?           GitUserName       { get; set; }
    public          string?           GitUserEmail      { get; set; }
    public          string?           HomeDir           { get; set; }
    public          long              LastEventNumber   { get; set; } = -1;
    public          List<StreamEvent> Events            { get; set; } = [];
}

// --- Repository info ---

public record RepositoryInfo {
    public string?          UserName    { get; init; }
    public string?          UserEmail   { get; init; }
    public string?          RemoteUrl   { get; init; }
    public string?          Owner       { get; init; }
    public string?          RepoName    { get; init; }
    public string?          Branch      { get; init; }
    public PullRequestInfo? PullRequest { get; init; }
}

public record PullRequestInfo {
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("headRefName")]
    public string? HeadRefName { get; init; }
}

public record AgentInstanceInfo(
        string         AgentId,
        string?        SessionId,
        string         Status,
        string?        Prompt,
        string?        Model,
        string?        Effort,
        string?        RepoPath,
        bool           ClientConnected,
        DateTimeOffset RegisteredAt,
        string?        RepoOwner       = null,
        string?        RepoName        = null,
        int?           PrNumber        = null,
        string?        PrUrl           = null,
        string?        PrTitle         = null,
        string?        FailureReason   = null,
        string?        OwnerUserId     = null,
        string?        VisibilityMode  = null,
        IReadOnlyList<AccessGrant>? Grants = null,
        string?        Vendor          = null,
        DateTimeOffset? EndedAt        = null
    ) {
    public string? RepoHash => RepoOwner is not null && RepoName is not null
        ? RepoHashHelper.ComputeRepoHash(RepoOwner, RepoName)
        : null;
}

public class AgentRunStats {
    public long InputTokens  { get; init; }
    public long OutputTokens { get; init; }
    public int  FilesCreated { get; init; }
    public int  FilesUpdated { get; init; }
    public int  LinesAdded   { get; init; }
    public int  LinesRemoved { get; init; }
    public int  ToolCalls    { get; init; }
    public int  ToolErrors   { get; init; }
}

public record DaemonInfo(
        string         Name,
        string         Platform,
        string[]       RepoPaths,
        int            MaxAgents,
        int            ActiveAgents,
        bool           Connected,
        DateTimeOffset ConnectedAt,
        string         OwnerUserId      = "",
        string?        Version          = null,
        string[]?      SupportedVendors = null
    );

/// <summary>
/// One row in the "Review this PR" daemon picker. A single daemon may
/// contribute multiple matches if the user has the repo cloned in more than
/// one location.
/// </summary>
public readonly record struct DaemonRepoMatch(
    string DaemonName,
    string RepoPath
);

/// <summary>
/// One row of "this daemon was contacted but couldn't tell us about its
/// checkouts". Distinct from "no match found" — this surfaces outdated
/// daemons (no <c>FindRepoForRemote</c> handler), probe timeouts, and
/// unexpected SignalR errors so the UI can prompt the user to restart or
/// upgrade rather than silently showing the empty-state message.
/// </summary>
public readonly record struct DaemonProbeFailure(
    string DaemonName,
    string Reason
);

/// <summary>
/// Combined output of "Review this PR" daemon discovery: matched checkouts
/// plus per-daemon probe failures. Either list may be empty independently —
/// no matches and no failures means the user has no clones of the repo on
/// any connected daemon; no matches but failures present means at least one
/// daemon is misbehaving (typically outdated).
/// </summary>
public record DaemonRepoDiscovery(
    IReadOnlyList<DaemonRepoMatch>    Matches,
    IReadOnlyList<DaemonProbeFailure> Failures
);

/// <summary>
/// User-facing reason strings for <see cref="DaemonProbeFailure"/>. Centralised
/// so the server-direct (Blazor) and SignalR-hub paths produce identical text.
/// </summary>
public static class DaemonProbeFailureReasons {
    public const string Outdated =
        "Daemon couldn't respond — likely outdated. Update kapacitor and restart `kcap agent start`.";

    public static string Timeout(TimeSpan timeout) =>
        $"Daemon didn't respond within {timeout.TotalSeconds:0}s.";
}

/// <summary>
/// Discriminator for daemon launch commands. <see cref="Default"/> preserves
/// the existing prompt-driven launch; <see cref="Review"/> uses
/// <see cref="ReviewLaunchInfo"/> + <c>BaseRef</c> to drive a hosted PR review;
/// <see cref="ReviewFlow"/> (AI-1089) marks the durable agent-review-flow
/// reviewer so the daemon can scope autonomous behaviour (e.g. the Codex
/// launcher's <c>never</c> approval + empty <c>mcp_servers</c>) to it without
/// affecting interactive launches.
///
/// <para>Wire values are explicit and MUST stay in lockstep with the CLI's
/// <c>LaunchKind</c> enum (<c>Default=0, Review=1, ReviewFlow=2</c>). The value
/// crosses the SignalR boundary as a camelCase enum-name string, but the
/// numeric values are pinned so a rename on either side is caught rather than
/// silently rebinding to the wrong kind.</para>
/// </summary>
public enum LaunchKind {
    Default    = 0,
    Review     = 1,
    ReviewFlow = 2
}

/// <summary>PR identifier carried by review-kind launches.</summary>
public readonly record struct ReviewLaunchInfo(
    string Owner,
    string Repo,
    int    PrNumber
);

public record AgentRunSummary {
    public required string          AgentId       { get; init; }
    public          string?         SessionId     { get; init; }
    public          string?         Prompt        { get; init; }
    public          string?         Model         { get; init; }
    public          string?         RepoPath      { get; init; }
    public          string?         WorktreePath  { get; init; }
    public          string?         Status        { get; init; } // "running", "completed", "failed"
    public          DateTimeOffset  StartedAt     { get; init; }
    public          DateTimeOffset? EndedAt       { get; init; }
    public          DateTimeOffset  LastHeartbeat { get; init; }
    public          AgentRunStats?  Stats         { get; init; }
}

// --- Judge-fact moderation DTOs (DEV-1442) ---

public record SoftDeleteJudgeFactPayload {
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public record JudgeFactListItem {
    [JsonPropertyName("category")]             public required string         Category { get; init; }
    [JsonPropertyName("fact_hash")]            public required string         FactHash { get; init; }
    [JsonPropertyName("fact")]                 public required string         Fact { get; init; }
    [JsonPropertyName("occurrence_count")]     public required long           OccurrenceCount { get; init; }
    [JsonPropertyName("retainer_user_id")]     public required string         RetainerUserId { get; init; }
    [JsonPropertyName("retainer_username")]    public          string?        RetainerUsername { get; init; }
    [JsonPropertyName("retainer_display_name")]public          string?        RetainerDisplayName { get; init; }
    [JsonPropertyName("source_session_id")]    public required string         SourceSessionId { get; init; }
    [JsonPropertyName("source_session_slug")]  public          string?        SourceSessionSlug { get; init; }
    [JsonPropertyName("source_eval_run_id")]   public required string         SourceEvalRunId { get; init; }
    [JsonPropertyName("retained_at")]          public required DateTimeOffset RetainedAt { get; init; }
    [JsonPropertyName("muted_for_caller")]     public required bool           MutedForCaller { get; init; }
    [JsonPropertyName("can_moderate")]         public required bool           CanModerate { get; init; }
}

// AI-1014 — Evals tab cross-repo list row (sessions LEFT JOIN eval_summaries + retained-fact count).
public record EvalListRow {
    [JsonPropertyName("session_id")]          public required string          SessionId        { get; init; }
    [JsonPropertyName("slug")]                public          string?         Slug             { get; init; }
    [JsonPropertyName("title")]               public          string?         Title            { get; init; }
    [JsonPropertyName("repo_hash")]           public          string?         RepoHash         { get; init; }
    [JsonPropertyName("repo_owner")]          public          string?         RepoOwner        { get; init; }
    [JsonPropertyName("repo_name")]           public          string?         RepoName         { get; init; }
    [JsonPropertyName("vendor")]              public          string?         Vendor           { get; init; }
    [JsonPropertyName("ended_at")]            public          DateTimeOffset? EndedAt          { get; init; }
    [JsonPropertyName("event_count")]         public required int             EventCount       { get; init; }
    [JsonPropertyName("has_eval")]            public required bool            HasEval          { get; init; }
    [JsonPropertyName("latest_eval_run_id")]  public          string?         LatestEvalRunId  { get; init; }
    [JsonPropertyName("overall_score")]       public          int?            OverallScore     { get; init; }
    [JsonPropertyName("evaluated_at")]        public          DateTimeOffset? EvaluatedAt      { get; init; }
    [JsonPropertyName("retained_fact_count")] public required int             RetainedFactCount { get; init; }
}

// AI-1014 — a single durable fact the latest eval run contributed (detail pane).
public record EvalRetainedFactRow {
    [JsonPropertyName("category")]   public required string  Category  { get; init; }
    [JsonPropertyName("fact_text")]  public required string  FactText  { get; init; }
    [JsonPropertyName("cluster_id")] public          string? ClusterId { get; init; }
}

// AI-55 — per-eval-run facts-used response (snapshot enriched with current mute/delete state)
public record EvalFactsUsedResponse {
    [JsonPropertyName("repo_hash")]
    public string? RepoHash { get; init; }

    [JsonPropertyName("can_moderate")]
    public required bool CanModerate { get; init; }

    [JsonPropertyName("categories")]
    public List<EvalFactsUsedCategory> Categories { get; init; } = [];
}

public record EvalFactsUsedCategory {
    [JsonPropertyName("category")] public required string               Category { get; init; }
    [JsonPropertyName("facts")]    public          List<EvalFactsUsedRow> Facts  { get; init; } = [];
}

public record EvalFactsUsedRow {
    [JsonPropertyName("fact_hash")]                public required string          FactHash            { get; init; }
    [JsonPropertyName("fact")]                     public required string          Fact                { get; init; }
    [JsonPropertyName("retainer_user_id")]         public required string          RetainerUserId      { get; init; }
    [JsonPropertyName("retainer_username")]        public          string?         RetainerUsername    { get; init; }
    [JsonPropertyName("retainer_display_name")]    public          string?         RetainerDisplayName { get; init; }
    [JsonPropertyName("source_session_id")]        public required string          SourceSessionId     { get; init; }
    [JsonPropertyName("source_session_slug")]      public          string?         SourceSessionSlug   { get; init; }
    [JsonPropertyName("retained_at")]              public required DateTimeOffset  RetainedAt          { get; init; }
    [JsonPropertyName("is_muted_by_caller")]       public required bool            IsMutedByCaller     { get; init; }
    [JsonPropertyName("is_deleted")]               public required bool            IsDeleted           { get; init; }
    [JsonPropertyName("deleted_by_user_id")]       public          string?         DeletedByUserId     { get; init; }
    [JsonPropertyName("deleted_by_username")]      public          string?         DeletedByUsername   { get; init; }
    [JsonPropertyName("deleted_by_display_name")]  public          string?         DeletedByDisplayName { get; init; }
    [JsonPropertyName("deleted_at")]               public          DateTimeOffset? DeletedAt           { get; init; }
}

public record JudgeFactDeletionItem {
    [JsonPropertyName("category")]              public required string         Category { get; init; }
    [JsonPropertyName("fact_hash")]             public required string         FactHash { get; init; }
    [JsonPropertyName("fact")]                  public required string         Fact { get; init; }
    [JsonPropertyName("deleted_by_user_id")]    public required string         DeletedByUserId { get; init; }
    [JsonPropertyName("deleted_by_username")]   public          string?        DeletedByUsername { get; init; }
    [JsonPropertyName("deleted_by_display_name")]public         string?        DeletedByDisplayName { get; init; }
    [JsonPropertyName("retainer_user_id")]      public required string         RetainerUserId { get; init; }
    [JsonPropertyName("reason")]                public          string?        Reason { get; init; }
    [JsonPropertyName("deleted_at")]            public required DateTimeOffset DeletedAt { get; init; }
    [JsonPropertyName("can_moderate")]          public required bool           CanModerate { get; init; }
}

public record JudgeFactSuppressionItem {
    [JsonPropertyName("scope_id")]              public required string         ScopeId { get; init; }
    [JsonPropertyName("category")]              public required string         Category { get; init; }
    [JsonPropertyName("candidate_fact_hash")]   public required string         CandidateFactHash { get; init; }
    [JsonPropertyName("matched_fact_hash")]     public required string         MatchedFactHash { get; init; }
    [JsonPropertyName("similarity")]            public required double         Similarity { get; init; }
    [JsonPropertyName("retainer_user_id")]      public required string         RetainerUserId { get; init; }
    [JsonPropertyName("retainer_username")]     public          string?        RetainerUsername { get; init; }
    [JsonPropertyName("retainer_display_name")] public          string?        RetainerDisplayName { get; init; }
    [JsonPropertyName("source_session_id")]     public required string         SourceSessionId { get; init; }
    [JsonPropertyName("source_session_slug")]   public          string?        SourceSessionSlug { get; init; }
    [JsonPropertyName("source_eval_run_id")]    public required string         SourceEvalRunId { get; init; }
    [JsonPropertyName("strategy")]              public required string         Strategy { get; init; }
    [JsonPropertyName("suppressed_at")]         public required DateTimeOffset SuppressedAt { get; init; }
}

// DEV-1471 — cross-session eval trend view. Returned by
// GET /api/repositories/{hash}/eval-trends.
public record EvalTrendResponse {
    [JsonPropertyName("repo_hash")]          public required string                       RepoHash          { get; init; }
    [JsonPropertyName("sessions_evaluated")] public required int                          SessionsEvaluated { get; init; }
    [JsonPropertyName("window_start")]       public required DateTimeOffset?              WindowStart       { get; init; }
    [JsonPropertyName("window_end")]         public required DateTimeOffset?              WindowEnd         { get; init; }
    [JsonPropertyName("category_trends")]    public required List<EvalCategoryTrend>      CategoryTrends    { get; init; }
    [JsonPropertyName("repeat_findings")]    public required List<EvalRepeatFinding>      RepeatFindings    { get; init; }
    [JsonPropertyName("aggregated_facts")]   public required List<EvalAggregatedFact>     AggregatedFacts   { get; init; }
}

public record EvalCategoryTrend {
    [JsonPropertyName("category")]      public required string                Category     { get; init; }
    [JsonPropertyName("average_score")] public required double                AverageScore { get; init; }
    [JsonPropertyName("points")]        public required List<EvalScorePoint>  Points       { get; init; }
}

public record EvalScorePoint {
    [JsonPropertyName("session_id")]   public required string         SessionId   { get; init; }
    [JsonPropertyName("session_slug")] public          string?        SessionSlug { get; init; }
    [JsonPropertyName("evaluated_at")] public required DateTimeOffset EvaluatedAt { get; init; }
    [JsonPropertyName("score")]        public required double         Score       { get; init; }
}

public record EvalRepeatFinding {
    [JsonPropertyName("category")]            public required string       Category          { get; init; }
    [JsonPropertyName("question_id")]         public required string       QuestionId        { get; init; }
    [JsonPropertyName("question_text")]       public required string       QuestionText      { get; init; }
    [JsonPropertyName("occurrence_count")]    public required int          OccurrenceCount   { get; init; }
    [JsonPropertyName("window_size")]         public required int          WindowSize        { get; init; }
    [JsonPropertyName("example_session_ids")] public required List<string> ExampleSessionIds { get; init; }
}

public record EvalAggregatedFact {
    [JsonPropertyName("category")]          public required string         Category        { get; init; }
    [JsonPropertyName("fact")]              public required string         Fact            { get; init; }
    [JsonPropertyName("occurrence_count")]  public required long           OccurrenceCount { get; init; }
    [JsonPropertyName("retained_at")]       public required DateTimeOffset RetainedAt      { get; init; }
    [JsonPropertyName("source_session_id")] public required string         SourceSessionId { get; init; }
}

// --- Curation queue DTOs (DEV-1677) ---

public sealed record CurationItem(
    [property: JsonPropertyName("category")]                 string         Category,
    [property: JsonPropertyName("cluster_id")]               string         ClusterId,
    [property: JsonPropertyName("representative_fact_hash")] string         RepresentativeFactHash,
    [property: JsonPropertyName("best_representative")]      string         BestRepresentative,
    [property: JsonPropertyName("weight")]                   long           Weight,
    // AI-10 — same as Weight, but with read-time exponential decay applied
    // (half-life = Evals:GuidelineInjection:DecayHalfLifeDays). Surfaced so the
    // curation UI can show curators a freshness cue alongside raw weight.
    [property: JsonPropertyName("effective_weight")]         double         EffectiveWeight,
    [property: JsonPropertyName("member_count")]             int            MemberCount,
    [property: JsonPropertyName("first_seen")]               DateTimeOffset? FirstSeen,
    [property: JsonPropertyName("last_seen")]                DateTimeOffset? LastSeen,
    [property: JsonPropertyName("status")]                   string         Status,
    [property: JsonPropertyName("decided_at")]               DateTimeOffset? DecidedAt,
    [property: JsonPropertyName("decided_by_user_id")]       string?        DecidedByUserId,
    [property: JsonPropertyName("promoted_text")]            string?        PromotedText,
    [property: JsonPropertyName("target_kinds")]             IReadOnlyList<string> TargetKinds,
    [property: JsonPropertyName("reason")]                   string?        Reason,
    // AI-3 — raw weight at the moment of promotion. NULL unless this row's
    // status is 'promoted'. The curation UI uses (Weight - PrePromotionWeight)
    // to drive the regressions lane: positive delta = guideline didn't prevent
    // recurrence post-promotion.
    [property: JsonPropertyName("pre_promotion_weight")]     long?          PrePromotionWeight
);

public sealed record CurationQueueResponse(
    [property: JsonPropertyName("repo_hash")] string             RepoHash,
    [property: JsonPropertyName("items")]     List<CurationItem> Items
);

public sealed record CurationMember(
    [property: JsonPropertyName("member_fact_hash")]  string         MemberFactHash,
    [property: JsonPropertyName("fact_text")]         string         FactText,
    [property: JsonPropertyName("occurrence_count")]  long           OccurrenceCount,
    [property: JsonPropertyName("source_session_id")] string         SourceSessionId,
    [property: JsonPropertyName("first_seen")]        DateTimeOffset FirstSeen,
    [property: JsonPropertyName("last_seen")]         DateTimeOffset LastSeen
);

public sealed record CurationAuditEntryDto(
    [property: JsonPropertyName("log_position")]     long           LogPosition,
    [property: JsonPropertyName("action")]           string         Action,
    [property: JsonPropertyName("text")]             string?        Text,
    [property: JsonPropertyName("target_kinds")]     IReadOnlyList<string> TargetKinds,
    [property: JsonPropertyName("reason")]           string?        Reason,
    // actor_github_id stays for wire back-compat — derived from the canonical id,
    // 0 for actors without a GitHub identity (WorkOS users, Phase 2). actor_user_id
    // is the canonical id and the field forward consumers should prefer.
    [property: JsonPropertyName("actor_github_id")]  long           ActorGitHubId,
    [property: JsonPropertyName("actor_user_id")]    string?        ActorUserId,
    [property: JsonPropertyName("occurred_at")]      DateTimeOffset OccurredAt
);

public sealed record CurationClusterDetailDto(
    [property: JsonPropertyName("item")]    CurationItem                Item,
    [property: JsonPropertyName("members")] List<CurationMember>        Members,
    [property: JsonPropertyName("history")] List<CurationAuditEntryDto> History
);

internal record PromoteClusterPayload {
    [JsonPropertyName("text")]         public required string   Text        { get; init; }
    [JsonPropertyName("target_kinds")] public required string[] TargetKinds { get; init; }
}

internal record DismissClusterPayload {
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed record RepoGuidelineSettingsResponse(
    [property: JsonPropertyName("repo_hash")]              string          RepoHash,
    [property: JsonPropertyName("auto_inject_uncurated")]  bool            AutoInjectUncurated,
    [property: JsonPropertyName("updated_at")]             DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("updated_by_user_id")]     string?         UpdatedByUserId
);

// AI-1016 — effective per-repo settings (override + resolved global default for the UI placeholder).
public sealed record RepoSettingsResponse(
    [property: JsonPropertyName("repo_hash")]              string RepoHash,
    [property: JsonPropertyName("guideline_min_weight")]   int?   GuidelineMinWeight,
    [property: JsonPropertyName("auto_eval_enabled")]      bool   AutoEvalEnabled,
    [property: JsonPropertyName("default_min_weight")]     int    DefaultMinWeight,
    [property: JsonPropertyName("server_runner_configured")] bool ServerRunnerConfigured
);
