using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// TranscriptQuestion is a Claude AskUserQuestion the transcript pipeline recorded under its own
/// id: it counts for attention, but the permission-response route knows nothing of that id.
public enum PendingInterruptKind { ClaudePermission, AcpPermission, AcpQuestion, TranscriptQuestion }

public sealed record PendingInterrupt(
        string RequestId, PendingInterruptKind Kind, string ToolName, JsonElement? ToolInput, string Prompt,
        IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections,
        DateTimeOffset RequestedAt) {
    public bool IsAnswerableOverHttp => Kind != PendingInterruptKind.TranscriptQuestion;
}
