using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its injected preludes as user messages; the chat shows none of them, nor the
/// handoff messages agents send each other. A tool call gains the vendor-neutral kind its raw name
/// alone does not carry.
public sealed class CodexChatRules : IChatDisplayRules {
    public static readonly CodexChatRules Instance = new(ownTranscript: false);

    /// For a subagent's own transcript, where the message its parent sent is the task it was given.
    public static readonly CodexChatRules Subagent = new(ownTranscript: true);

    readonly bool _ownTranscript;

    static readonly string[] InjectedPreludes = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>",
    ];

    CodexChatRules(bool ownTranscript) => _ownTranscript = ownTranscript;

    public IReadOnlyList<string> LiteralInputs(CanonicalEvent evt, AcpEventEnvelope raw) =>
        raw is { Kind: AcpEventKind.UserMessage, Text: { Length: > 0 } text } ? [text] : [];

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) => envelope.Kind switch {
        AcpEventKind.UserMessage when IsInjectedPrelude(envelope.Text ?? "") => null,
        AcpEventKind.AssistantText when IsAgentMessage(evt)                  =>
            _ownTranscript ? envelope with { Kind = AcpEventKind.SystemNote } : null,
        AcpEventKind.ToolCall => envelope with { ToolKind = CodexToolKinds.Of(envelope.ToolName, envelope.ToolInputJson) },
        _                     => envelope,
    };

    static bool IsAgentMessage(CanonicalEvent evt) =>
        SchemaExtensions.Slug(evt.Payload, CodexExtension.Slug)?.Fields.ContainsKey(CodexExtension.AgentMessage) == true;

    static bool IsInjectedPrelude(string text) {
        var trimmed = text.TrimStart();
        foreach (var prelude in InjectedPreludes)
            if (trimmed.StartsWith(prelude, StringComparison.Ordinal)) return true;
        return false;
    }
}
