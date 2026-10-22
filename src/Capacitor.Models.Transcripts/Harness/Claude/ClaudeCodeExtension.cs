using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The claude_code extension slug: the coding-agent fields the schema keeps out of the canonical
/// payloads. Only the fields this projection writes are named here, under the names the server writes.
public static class ClaudeCodeExtension {
    public const string Slug             = "claude_code";
    public const string IsMeta           = "is_meta";
    public const string IsSidechain      = "is_sidechain";
    public const string OriginKind       = "origin_kind";
    public const string IsError          = "is_error";
    public const string IsCompactSummary = "is_compact_summary";
    /// A tool-result line's root `toolUseResult` object, verbatim; the key the server writes.
    public const string ToolUseResult    = "tool_use_result";
    /// A tool result's structured `content` when it was not a plain string, verbatim.
    public const string OutputRaw        = "output_raw";
    public const string IsHandback       = "is_handback";
    public const string PromptSource     = "prompt_source";
    public const string PromptIndex      = "prompt_index";
    /// A user message delivered by a `queued_command` attachment, and the attachment's `commandMode`.
    public const string IsQueued         = "is_queued";
    public const string CommandMode      = "command_mode";
    public const string LocalCommand     = "local_command";

    /// The block for one event, or null when nothing is set so the slug stays absent.
    public static Struct? Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false, Struct? toolUseResult = null,
            bool isCompactSummary = false, Value? outputRaw = null, Origin? origin = null) {
        var s = new Struct();
        if (isSidechain) s.Fields[IsSidechain] = Value.ForBool(true);
        if (isMeta) s.Fields[IsMeta]           = Value.ForBool(true);
        if (originKind is not null) s.Fields[OriginKind] = Value.ForString(originKind);
        if (isError) s.Fields[IsError]         = Value.ForBool(true);
        if (toolUseResult is not null) s.Fields[ToolUseResult] = Value.ForStruct(toolUseResult);
        if (outputRaw is not null) s.Fields[OutputRaw] = outputRaw;
        if (isCompactSummary) s.Fields[IsCompactSummary] = Value.ForBool(true);
        if (origin is { } o) {
            if (o.IsHandback) s.Fields[IsHandback]                     = Value.ForBool(true);
            if (o.PromptSource is { } source) s.Fields[PromptSource]   = Value.ForString(source);
            if (o.PromptIndex is { } index) s.Fields[PromptIndex]      = Value.ForNumber(index);
            if (o.IsQueued) s.Fields[IsQueued]                         = Value.ForBool(true);
            if (o.CommandMode is { } mode) s.Fields[CommandMode]       = Value.ForString(mode);
        }
        return s.Fields.Count == 0 ? null : s;
    }

    /// Who wrote a user line, beyond its kind and meta flag: Claude Code's own fields, as the server reads them.
    public readonly record struct Origin(bool IsHandback, string? PromptSource, int? PromptIndex, bool IsQueued = false, string? CommandMode = null);
}
