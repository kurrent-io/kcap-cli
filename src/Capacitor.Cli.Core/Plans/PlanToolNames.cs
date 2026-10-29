namespace Capacitor.Cli.Core.Plans;

/// The `kcap-plans` MCP tools, by the names the server registers them under.
public static class PlanToolNames {
    public const string DeclareDocument = "declare_plan_document";
    public const string SetTasks        = "set_plan_tasks";
    public const string UpdateTask      = "update_plan_task";
    public const string GetPlan         = "get_plan";

    /// A transcript carries the vendor's spelling of a tool name: Claude prefixes the server
    /// (`mcp__kcap-plans__update_plan_task`), Codex passes the bare name. The tool's own name is
    /// the only part every vendor keeps, and it always comes last.
    public static bool IsWrite(string? toolName) =>
        toolName is not null
     && (EndsWithTool(toolName, DeclareDocument) || EndsWithTool(toolName, SetTasks) || EndsWithTool(toolName, UpdateTask));

    static bool EndsWithTool(string toolName, string tool) =>
        toolName.EndsWith(tool, StringComparison.Ordinal)
     && (toolName.Length == tool.Length || !char.IsLetterOrDigit(toolName[^(tool.Length + 1)]));
}
