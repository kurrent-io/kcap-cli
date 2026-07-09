namespace Capacitor.Cli.Core.Mcp;

public enum EnableStyle { None, EnabledTrue }

/// <summary>How a harness expresses "auto-approve this server's tools" (skip the per-tool prompt) in
/// the config file we write, applied only to <see cref="KcapMcpServer.ReadOnly"/> servers. <c>None</c>
/// = the harness has no per-server trust knob we can write:
///  • Cursor — <c>mcp.json</c> has no trust field; auto-approve is a separate <c>permissions.json</c>
///    classifier or the <c>--approve-mcps</c> CLI flag.
///  • Copilot — <c>mcp-config.json</c>'s <c>tools</c> field is an AVAILABILITY allowlist (default
///    <c>*</c>), not an approval knob; approval is governed by runtime <c>--allow-tool</c> /
///    <c>--allow-all-tools</c> flags.
/// Codex is not represented here — its per-server approval lives in TOML (<c>CodexConfigToml</c>),
/// not this JSON writer.</summary>
public enum TrustStyle {
    None,      // no per-server trust field we can write (Cursor / Copilot / OpenCode)
    TrustBool  // Gemini: "trust": true → bypasses tool-call confirmations for the server
}

/// <summary>How one harness renders an MCP server entry. The canonical
/// <see cref="KcapMcpServer"/> is field-name-agnostic; this maps it to a host's on-disk shape.</summary>
public sealed record McpConfigShape(
    string      BlockKey,           // "mcpServers" (most) or "mcp" (OpenCode)
    bool        CommandAsArgvArray, // OpenCode: command = ["kcap","mcp","review"]; else command="kcap" + args=[...]
    string?     TypeValue,          // "stdio" (Copilot), "local" (OpenCode), or null
    string      EnvKey,             // "env" (most) or "environment" (OpenCode) — reserved for future env support
    EnableStyle Enable,             // OpenCode wants "enabled": true
    TrustStyle  Trust = TrustStyle.None // per-server auto-approve for ReadOnly servers, if the harness supports it
) {
    // Cursor / Kiro / Antigravity share the plain, trust-less shape (Cursor has no per-server trust
    // field — auto-approve there is a separate permissions.json classifier / the --approve-mcps flag).
    public static readonly McpConfigShape Standard = new("mcpServers", CommandAsArgvArray: false, TypeValue: null,    EnvKey: "env",         Enable: EnableStyle.None);
    // Gemini is the plain shape PLUS a per-server "trust" boolean for read-only servers.
    public static readonly McpConfigShape Gemini   = new("mcpServers", CommandAsArgvArray: false, TypeValue: null,    EnvKey: "env",         Enable: EnableStyle.None, Trust: TrustStyle.TrustBool);
    // Copilot's `tools` field is availability, not approval — no per-server trust knob to write here.
    public static readonly McpConfigShape Copilot  = new("mcpServers", CommandAsArgvArray: false, TypeValue: "stdio", EnvKey: "env",         Enable: EnableStyle.None);
    public static readonly McpConfigShape OpenCode = new("mcp",        CommandAsArgvArray: true,  TypeValue: "local", EnvKey: "environment", Enable: EnableStyle.EnabledTrue);
}
