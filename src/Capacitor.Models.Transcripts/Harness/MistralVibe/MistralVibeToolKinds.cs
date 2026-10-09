namespace Capacitor.Models.Transcripts.Harness.MistralVibe;

/// What a Vibe tool call did, in the vendor-neutral vocabulary. Vibe groups its built-in tools under
/// a namespace prefix (<c>file_system.bash</c>, <c>ui.ask_user_question</c>); the projection strips
/// that before classifying, so this maps the bare tool name. An MCP tool arrives under its
/// server-side name and lands on <see cref="AcpToolKind.Other"/> like any unrecognised tool.
public static class MistralVibeToolKinds {
    /// A unified-store effect states its own <paramref name="effectKind"/>; a legacy tool call, or a
    /// generic <c>tool</c> effect such as an MCP call, falls back to the bare name.
    public static string Of(string? effectKind, string? toolName) => effectKind switch {
        "shell"                         => AcpToolKind.Execute,
        "file_read"                     => AcpToolKind.Read,
        "file_edit" or "file_write"     => AcpToolKind.Edit,
        "file_search"                   => AcpToolKind.Search,
        "web_search" or "web_fetch"     => AcpToolKind.Fetch,
        _                               => ByName(toolName),
    };

    static string ByName(string? toolName) => toolName switch {
        "bash" or "shell" or "run"                     => AcpToolKind.Execute,
        "read" or "read_file" or "view" or "cat"       => AcpToolKind.Read,
        "edit" or "write" or "write_file"
            or "search_replace" or "apply_patch"       => AcpToolKind.Edit,
        "delete" or "rm" or "remove"                   => AcpToolKind.Delete,
        "move" or "mv" or "rename"                     => AcpToolKind.Move,
        "grep" or "search" or "glob" or "find"         => AcpToolKind.Search,
        "fetch" or "web_fetch" or "web_search"         => AcpToolKind.Fetch,
        _                                              => AcpToolKind.Other,
    };
}
