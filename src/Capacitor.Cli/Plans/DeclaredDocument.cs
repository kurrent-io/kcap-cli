using System.Text.Json.Nodes;

namespace Capacitor.Cli.Plans;

/// <summary>One <c>declare_plan_document</c> call, resolved: absolute paths and the directories the
/// declaration was resolved against, so it can be replayed from a process with neither. Every
/// argument is replayed, because the server overwrites what a later declaration omits.
/// <paramref name="WorkspaceRoot"/> is the checkout the agent named, if it named one.</summary>
sealed record DeclaredDocument(string Path, string Kind, string? ArguesFrom, string? WorkItemId, string Cwd, string? RepoRoot, string? WorkspaceRoot = null) {
    /// <summary>The <c>declare_plan_document</c> arguments that declare this document again.</summary>
    public JsonObject ToArguments(string sessionId) {
        var args = new JsonObject { ["session_id"] = sessionId, ["kind"] = Kind, ["path"] = Path };
        if (ArguesFrom is not null) args["argues_from"]  = ArguesFrom;
        if (WorkItemId is not null) args["work_item_id"] = WorkItemId;
        if (WorkspaceRoot is not null) args["workspace_root"] = WorkspaceRoot;
        return args;
    }

    public JsonObject ToJson() => new() {
        ["path"]           = Path,
        ["kind"]           = Kind,
        ["argues_from"]    = ArguesFrom,
        ["work_item_id"]   = WorkItemId,
        ["cwd"]            = Cwd,
        ["repo_root"]      = RepoRoot,
        ["workspace_root"] = WorkspaceRoot
    };

    public static DeclaredDocument? FromJson(JsonObject? json) {
        if (json is null) return null;
        if (Text(json["path"]) is not { Length: > 0 } path
         || Text(json["kind"]) is not { Length: > 0 } kind
         || Text(json["cwd"]) is not { Length: > 0 } cwd)
            return null;
        return new(path, kind, Text(json["argues_from"]), Text(json["work_item_id"]), cwd, Text(json["repo_root"]), Text(json["workspace_root"]));
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
