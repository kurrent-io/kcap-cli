using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its injected preludes as user messages; the chat shows none of them, nor the
/// handoff messages agents send each other. A tool call gains the vendor-neutral kind its raw name
/// alone does not carry.
public sealed class CodexChatRules : IChatDisplayRules {
    public static readonly CodexChatRules Instance = new();

    CodexChatRules() { }

    public IReadOnlyList<string> LiteralInputs(CanonicalEvent evt, AcpEventEnvelope raw) =>
        raw is { Kind: AcpEventKind.UserMessage, Text: { Length: > 0 } text } ? [text] : [];

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) => envelope.Kind switch {
        AcpEventKind.UserMessage when CodexPrelude.IsPrelude(envelope.Text ?? "", null) => null,
        AcpEventKind.AssistantText when IsAgentMessage(evt)                  => null,
        AcpEventKind.ToolCall => envelope with { ToolKind = CodexToolKinds.Of(envelope.ToolName, envelope.ToolInputJson) },
        _                     => envelope,
    };

    static bool IsAgentMessage(CanonicalEvent evt) =>
        SchemaExtensions.Slug(evt.Payload, CodexExtension.Slug)?.Fields.ContainsKey(CodexExtension.AgentMessage) == true;
}
