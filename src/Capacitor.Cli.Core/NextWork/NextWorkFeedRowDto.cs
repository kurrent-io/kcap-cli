using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.NextWork;

public sealed record NextWorkFeedRowDto {
    List<NextWorkFeedEvidenceDto> _evidence = [];

    [JsonPropertyName("rank")]          public int                           Rank         { get; init; }
    [JsonPropertyName("tier")]          public int                           Tier         { get; init; }
    [JsonPropertyName("arm")]           public string                        Arm          { get; init; } = "";
    [JsonPropertyName("group")]         public string?                       Group        { get; init; }
    [JsonPropertyName("target_key")]    public string                        TargetKey    { get; init; } = "";
    [JsonPropertyName("target_kind")]   public string                        TargetKind   { get; init; } = "";
    [JsonPropertyName("target_id")]     public string                        TargetId     { get; init; } = "";
    [JsonPropertyName("target_label")]  public string                        TargetLabel  { get; init; } = "";
    [JsonPropertyName("target_href")]   public string                        TargetHref   { get; init; } = "";
    [JsonPropertyName("repo_hash")]     public string?                       RepoHash     { get; init; }
    [JsonPropertyName("because")]       public string                        Because      { get; init; } = "";
    [JsonPropertyName("evidence")]      public List<NextWorkFeedEvidenceDto> Evidence     { get => _evidence; init => _evidence = value ?? []; }
    [JsonPropertyName("page_one")]      public bool                          PageOne      { get; init; }
    [JsonPropertyName("work_item_id")]  public string?                       WorkItemId   { get; init; }
    [JsonPropertyName("session_id")]    public string?                       SessionId    { get; init; }
    [JsonPropertyName("worktree_path")] public string?                       WorktreePath { get; init; }
    [JsonPropertyName("launch_prompt")] public string?                       LaunchPrompt { get; init; }

    [JsonIgnore]
    public string ResolvedGroup => !string.IsNullOrWhiteSpace(Group)
        ? Group
        : string.Equals(TargetKind, "evals", StringComparison.OrdinalIgnoreCase)
          || string.Equals(Arm, "Housekeeping", StringComparison.OrdinalIgnoreCase)
          || Tier == 4
            ? "housekeeping"
            : Arm is "ReviewRequested" or "ApprovedUnmerged" or "BlocksOthers" || Tier == 1
                ? "waiting"
                : Arm is "FinishYours" or "ResumeInterrupted" or "ContinueWorking" || Tier == 2
                    ? "finish"
                    : "pick_up";

    [JsonIgnore]
    public string? ResolvedWorkItemId => !string.IsNullOrWhiteSpace(WorkItemId)
        ? WorkItemId
        : string.Equals(TargetKind, "work_item", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(TargetId)
            ? TargetId
            : TargetKey.StartsWith("wi:", StringComparison.Ordinal) && TargetKey.Length > 3
                ? TargetKey[3..]
                : null;

    [JsonIgnore]
    public string? ResolvedSessionId => !string.IsNullOrWhiteSpace(SessionId)
        ? SessionId
        : string.Equals(TargetKind, "session", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(TargetId)
            ? TargetId
            : TargetKey.StartsWith("session:", StringComparison.Ordinal) && TargetKey.Length > "session:".Length
                ? TargetKey["session:".Length..]
                : null;
}
