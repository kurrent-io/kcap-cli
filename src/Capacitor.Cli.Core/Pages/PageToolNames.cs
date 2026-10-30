namespace Capacitor.Cli.Core.Pages;

/// The `kcap-pages` MCP tools, by the names the server registers them under.
public static class PageToolNames {
    public const string Publish = "publish_page";

    /// A transcript carries the vendor's spelling of a tool name: a server prefix or none, the
    /// tool's own name always last.
    public static bool IsPublish(string? toolName) =>
        toolName is not null
     && toolName.EndsWith(Publish, StringComparison.Ordinal)
     && (toolName.Length == Publish.Length || !char.IsLetterOrDigit(toolName[^(Publish.Length + 1)]));
}
