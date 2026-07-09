namespace Capacitor.Cli.Core;

/// <summary>AI-1126 D-c: the kcap-owned MCP server registry — the ONLY source a flow
/// definition's mcp: allowlist resolves against (never user config). StartsFlows marks
/// servers that can start flows; they are stripped from every allowlist regardless of
/// listing (the recursion guard — the server strips too, this is the authoritative layer).
/// Ids are canonical lower-case; Resolve is case-insensitive so casing can't dodge the strip.</summary>
public sealed record KcapMcpServerDescriptor(string Id, string[] Args, bool StartsFlows);

public static class KcapMcpRegistry {
    static readonly Dictionary<string, KcapMcpServerDescriptor> Entries = new(StringComparer.OrdinalIgnoreCase) {
        ["kcap-review"]    = new("kcap-review",    ["mcp", "review"],    false),
        ["kcap-sessions"]  = new("kcap-sessions",  ["mcp", "sessions"],  false),
        ["kcap-memory"]    = new("kcap-memory",    ["mcp", "memory"],    false),
        ["kcap-flows"]     = new("kcap-flows",     ["mcp", "flows"],     true),
        ["kcap-workitems"] = new("kcap-workitems", ["mcp", "workitems"], false),
    };

    /// <summary>Resolves an allowlist entry to its descriptor. Case-insensitive, trims
    /// surrounding whitespace. A null or blank name — e.g. a wire-deserialized allowlist
    /// element — returns null rather than throwing, so callers can route it through the
    /// same unknown-name skip path as any other unresolvable name.</summary>
    public static KcapMcpServerDescriptor? Resolve(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return Entries.TryGetValue(name.Trim(), out var d) ? d : null;
    }
}
