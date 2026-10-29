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

    /// <summary>Adds a record, or consumes something on the way (a status read that acknowledges the
    /// messages it rendered); nothing that exists is overwritten, but a repeat is not free.</summary>
    public static readonly McpToolAnnotations Additive = new(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: false);

    /// <summary>Removes or overwrites something that exists; repeating the call changes nothing more.</summary>
    public static readonly McpToolAnnotations Destructive = new(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: true, OpenWorldHint: false);

    /// <summary>Replaces a whole list, minting identities for entries that carry none, so a repeat is not a no-op.</summary>
    public static readonly McpToolAnnotations Replace = new(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: false);

    /// <summary>Starts or messages a separate hosted agent that then acts on its own.</summary>
    public static readonly McpToolAnnotations Launch = new(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: true);
}
