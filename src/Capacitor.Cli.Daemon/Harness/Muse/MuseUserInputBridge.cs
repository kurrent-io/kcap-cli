using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Maps a Muse <c>userInput/requested</c> payload onto the daemon's elicitation route and the answer back.</summary>
internal static class MuseUserInputBridge {
    /// <summary>Only the first question is asked and answered; <c>userInput/answer</c> wants an answer for every
    /// question, so a prompt with more than one is a known gap.</summary>
    public static AcpInteractionRequest ToInteraction(string agentId, string sessionId, JsonElement userInputParams) {
        var question = FirstQuestion(userInputParams);
        var header   = MuseApprovalBridge.StringOf(question, "header");
        var text     = MuseApprovalBridge.StringOf(question, "question") ?? "";
        var multiple = question.ValueKind == JsonValueKind.Object
                    && question.TryGetProperty("selection", out var selection)
                    && MuseApprovalBridge.StringOf(selection, "mode") == "multiple";

        var options = new List<AcpInteractionOption>();

        if (question.ValueKind == JsonValueKind.Object
         && question.TryGetProperty("options", out var arr)
         && arr.ValueKind == JsonValueKind.Array) {
            foreach (var option in arr.EnumerateArray()) {
                if (MuseApprovalBridge.StringOf(option, "label") is { } label)
                    options.Add(new AcpInteractionOption(label, label, MuseApprovalBridge.StringOf(option, "description")));
            }
        }

        return new AcpInteractionRequest(
            AgentId:       agentId,
            AcpSessionId:  sessionId,
            Kind:          "elicitation",
            ToolName:      MuseApprovalBridge.StringOf(userInputParams, "toolName"),
            ToolInput:     null,
            ToolCallId:    MuseApprovalBridge.StringOf(userInputParams, "toolCallId"),
            Prompt:        string.IsNullOrEmpty(header) ? text : header + ": " + text,
            Options:       options.Count == 0 ? null : [.. options],
            IsMultiSelect: multiple);
    }

    /// <summary>Null means the question was dismissed; the caller sends <c>userInput/cancel</c>.</summary>
    public static IReadOnlyList<(string QuestionId, string SelectedLabel)>? ToAnswers(
            AcpInteractionDecision decision, JsonElement userInputParams) {
        var answered = decision.Outcome == "answered" || decision.SelectedOptionId is not null || decision.SelectedOptionLabel is not null;
        if (!answered) return null;

        var label      = decision.SelectedOptionLabel ?? decision.SelectedOptionId ?? decision.FreeText;
        var questionId = MuseApprovalBridge.StringOf(FirstQuestion(userInputParams), "id");

        if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(questionId)) return null;

        return [(questionId, label)];
    }

    static JsonElement FirstQuestion(JsonElement userInputParams) =>
        userInputParams.ValueKind == JsonValueKind.Object
     && userInputParams.TryGetProperty("questions", out var questions)
     && questions.ValueKind == JsonValueKind.Array
     && questions.GetArrayLength() > 0
            ? questions[0]
            : default;
}
