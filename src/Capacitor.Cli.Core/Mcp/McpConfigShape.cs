namespace Capacitor.Cli.Core.Mcp;

public enum EnableStyle { None, EnabledTrue }

/// <summary>How one harness renders an MCP server entry. The canonical
/// <see cref="KcapMcpServer"/> is field-name-agnostic; this maps it to a host's on-disk shape.</summary>
public sealed record McpConfigShape(
    string      BlockKey,           // "mcpServers" (most) or "mcp" (OpenCode)
    bool        CommandAsArgvArray, // OpenCode: command = ["kcap","mcp","review"]; else command="kcap" + args=[...]
    string?     TypeValue,          // "stdio" (Copilot), "local" (OpenCode), or null
    string      EnvKey,             // "env" (most) or "environment" (OpenCode) — reserved for future env support
    EnableStyle Enable              // OpenCode wants "enabled": true
) {
    // Cursor / Gemini / Kiro / Antigravity all share the plain shape.
    public static readonly McpConfigShape Standard = new("mcpServers", CommandAsArgvArray: false, TypeValue: null,    EnvKey: "env",         Enable: EnableStyle.None);
    public static readonly McpConfigShape Copilot  = new("mcpServers", CommandAsArgvArray: false, TypeValue: "stdio", EnvKey: "env",         Enable: EnableStyle.None);
    public static readonly McpConfigShape OpenCode = new("mcp",        CommandAsArgvArray: true,  TypeValue: "local", EnvKey: "environment", Enable: EnableStyle.EnabledTrue);
}
