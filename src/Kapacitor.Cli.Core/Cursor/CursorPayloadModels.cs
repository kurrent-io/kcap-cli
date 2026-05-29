using System.Text.Json.Serialization;

namespace Kapacitor.Cli.Core.Cursor;

public sealed record CursorImportPayload {
    [JsonPropertyName("vendor")]              public required string                        Vendor              { get; init; }
    [JsonPropertyName("composerId")]          public required string                        ComposerId          { get; init; }
    [JsonPropertyName("schemaSourceVersion")] public required CursorSchemaVersion           SchemaSourceVersion { get; init; }
    [JsonPropertyName("header")]              public required CursorHeader                  Header              { get; init; }
    [JsonPropertyName("composerData")]        public required CursorComposerData            ComposerData        { get; init; }
    [JsonPropertyName("bubbles")]             public required IReadOnlyList<CursorBubble>   Bubbles             { get; init; }
    [JsonPropertyName("contentBlobs")]        public required IReadOnlyDictionary<string,string> ContentBlobs   { get; init; }
    [JsonPropertyName("cli_owner")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CliOwner { get; init; }
    [JsonPropertyName("cli_repo")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CliRepo  { get; init; }
}

public sealed record CursorSchemaVersion {
    [JsonPropertyName("composerData")] public required int ComposerData { get; init; }
    [JsonPropertyName("bubble")]       public required int Bubble       { get; init; }
}

public sealed record CursorHeader {
    [JsonPropertyName("name")]              public string? Name              { get; init; }
    [JsonPropertyName("unifiedMode")]       public required string UnifiedMode { get; init; }
    [JsonPropertyName("createdAtMs")]       public required long   CreatedAtMs { get; init; }
    [JsonPropertyName("lastUpdatedAtMs")]   public required long   LastUpdatedAtMs { get; init; }
    [JsonPropertyName("trackedGitRepos")]   public IReadOnlyList<CursorTrackedRepo>? TrackedGitRepos { get; init; }
    [JsonPropertyName("totalLinesAdded")]   public int  TotalLinesAdded   { get; init; }
    [JsonPropertyName("totalLinesRemoved")] public int  TotalLinesRemoved { get; init; }
    [JsonPropertyName("filesChangedCount")] public int  FilesChangedCount { get; init; }
    [JsonPropertyName("subtitle")]          public string? Subtitle        { get; init; }
}

public sealed record CursorTrackedRepo {
    [JsonPropertyName("repoPath")] public required string RepoPath { get; init; }
    [JsonPropertyName("branches")] public IReadOnlyList<CursorTrackedBranch>? Branches { get; init; }
}

public sealed record CursorTrackedBranch {
    [JsonPropertyName("branchName")] public required string BranchName { get; init; }
}

public sealed record CursorComposerData {
    [JsonPropertyName("modelConfig")]                 public required CursorModelConfig          ModelConfig                 { get; init; }
    [JsonPropertyName("fullConversationHeadersOnly")] public required IReadOnlyList<CursorTurnHeader> FullConversationHeadersOnly { get; init; }
    [JsonPropertyName("generatingBubbleIds")]         public required IReadOnlyList<string>       GeneratingBubbleIds         { get; init; }
    [JsonPropertyName("status")]                      public string? Status { get; init; }
}

public sealed record CursorModelConfig {
    [JsonPropertyName("modelName")]      public string? ModelName      { get; init; }
    [JsonPropertyName("selectedModels")] public IReadOnlyList<string>? SelectedModels { get; init; }
}

public sealed record CursorTurnHeader {
    [JsonPropertyName("bubbleId")] public required string BubbleId { get; init; }
    [JsonPropertyName("type")]     public required int    Type     { get; init; }
}

public sealed record CursorBubble {
    [JsonPropertyName("bubbleId")]       public required string BubbleId       { get; init; }
    [JsonPropertyName("type")]           public required int    Type           { get; init; }
    [JsonPropertyName("capabilityType")] public int?            CapabilityType { get; init; }
    [JsonPropertyName("createdAtIso")]   public required string CreatedAtIso   { get; init; }
    [JsonPropertyName("text")]           public string?         Text           { get; init; }
    [JsonPropertyName("tokenCount")]     public CursorTokenCount? TokenCount   { get; init; }
    [JsonPropertyName("toolFormerData")] public CursorToolFormerData? ToolFormerData { get; init; }
    [JsonPropertyName("thinking")]       public CursorThinking? Thinking       { get; init; }
}

public sealed record CursorTokenCount {
    [JsonPropertyName("inputTokens")]  public long InputTokens  { get; init; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; init; }
}

public sealed record CursorToolFormerData {
    [JsonPropertyName("toolCallId")] public required string ToolCallId { get; init; }
    [JsonPropertyName("name")]       public required string Name       { get; init; }
    // Params/Result are JSON-encoded strings on the Cursor wire (not nested objects) — parse twice on use.
    [JsonPropertyName("params")]     public string? Params { get; init; }
    [JsonPropertyName("rawArgs")]    public string? RawArgs { get; init; }
    [JsonPropertyName("result")]     public string? Result { get; init; }
    [JsonPropertyName("status")]     public string? Status { get; init; }
}

public sealed record CursorThinking {
    [JsonPropertyName("text")]       public string? Text       { get; init; }
    [JsonPropertyName("signature")]  public string? Signature  { get; init; }
    [JsonPropertyName("durationMs")] public int     DurationMs { get; init; }
}
