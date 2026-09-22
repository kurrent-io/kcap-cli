namespace Capacitor.Cli.Commands;

/// <summary>The MCP annotations a kcap tool advertises. A hint left null is omitted from the wire and
/// read by the spec's defaults: not read-only, destructive, not idempotent, open-world. A harness
/// that decides approval from annotations prompts for such a tool on every call, so every kcap tool
/// passes one of the presets below.</summary>
record McpToolAnnotations(bool? ReadOnlyHint = null, bool? DestructiveHint = null, bool? IdempotentHint = null, bool? OpenWorldHint = null) {
    /// <summary>Reads Capacitor or a configured service and changes nothing.</summary>
    public static readonly McpToolAnnotations Read = new(ReadOnlyHint: true, OpenWorldHint: false);

    /// <summary>Adds or restates a fact in the user's Capacitor workspace; repeating the call changes nothing more.</summary>
    public static readonly McpToolAnnotations Upsert = new(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false);

    /// <summary>Creates a new record on each call; nothing that exists is touched.</summary>
    public static readonly McpToolAnnotations Create = new(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: false);

    /// <summary>Removes, replaces or overwrites something that exists; repeating the call changes nothing more.</summary>
    public static readonly McpToolAnnotations Destructive = new(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: true, OpenWorldHint: false);

    /// <summary>Starts or messages a separate hosted agent that then acts on its own.</summary>
    public static readonly McpToolAnnotations Launch = new(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: true);
}
