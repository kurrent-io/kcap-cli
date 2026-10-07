namespace Capacitor.Models.Transcripts.Harness.MistralVibe;

/// What a Vibe tool call did, in the vendor-neutral vocabulary. Vibe groups its built-in tools under
/// a namespace prefix (<c>file_system.bash</c>, <c>ui.ask_user_question</c>); the projection strips
/// that before classifying, so this maps the bare tool name. An MCP tool arrives under its
/// server-side name and lands on <see cref="AcpToolKind.Other"/> like any unrecognised tool.
public static class MistralVibeToolKinds {
    public static string Of(string? toolName) => toolName switch {
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
