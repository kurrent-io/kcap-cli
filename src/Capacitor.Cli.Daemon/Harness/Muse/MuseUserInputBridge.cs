using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Maps a Muse <c>userInput/requested</c> payload onto the daemon's elicitation route and the answer back.</summary>
internal static class MuseUserInputBridge {
    /// <summary>Only the first question is asked and answered; <c>userInput/answer</c> wants an answer for every
    /// question, so a prompt with more than one is refused and the runtime then cancels it.</summary>
    public static AcpInteractionRequest ToInteraction(string agentId, string sessionId, JsonElement userInputParams) {
        var question = FirstQuestion(userInputParams);
        var header   = question.Str("header");
        var text     = question.Str("question") ?? "";
        var multiple = question.Obj("selection")?.Str("mode") == "multiple";

        var options = new List<AcpInteractionOption>();

        if (question.Arr("options") is { } arr) {
            foreach (var option in arr.EnumerateArray()) {
                if (option.Str("label") is { } label)
                    options.Add(new AcpInteractionOption(label, label, option.Str("description")));
            }
        }

        return new AcpInteractionRequest(
            AgentId:       agentId,
            AcpSessionId:  sessionId,
            Kind:          "elicitation",
            ToolName:      userInputParams.Str("toolName"),
            ToolInput:     null,
            ToolCallId:    userInputParams.Str("toolCallId"),
            Prompt:        string.IsNullOrEmpty(header) ? text : header + ": " + text,
            Options:       options.Count == 0 ? null : [.. options],
            IsMultiSelect: multiple);
    }

    /// <summary>Null means nothing usable was chosen; the caller sends <c>userInput/cancel</c>.</summary>
    public static IReadOnlyList<MuseUserInputAnswer>? ToAnswers(AcpInteractionDecision decision, JsonElement userInputParams) {
        if (decision.Outcome is "cancel" or "timeout" or "deny") return null;

        var question   = FirstQuestion(userInputParams);
        var questionId = question.Str("id");
        if (string.IsNullOrEmpty(questionId)) return null;

        var multiple = question.Obj("selection")?.Str("mode") == "multiple";

        var single = decision.SelectedOptionLabel ?? decision.SelectedOptionId;

        if (multiple) {
            string[] labels = decision.SelectedOptionLabels is { Length: > 0 } many ? many
                            : !string.IsNullOrEmpty(single)                        ? [single]
                            : [];

            if (labels.Length > 0) return [new MuseUserInputAnswer(questionId, SelectedLabels: labels)];
        } else if (!string.IsNullOrEmpty(single)) {
            return [new MuseUserInputAnswer(questionId, SelectedLabel: single)];
        }

        return string.IsNullOrWhiteSpace(decision.FreeText) ? null : [new MuseUserInputAnswer(questionId, FreeText: decision.FreeText)];
    }

    static JsonElement FirstQuestion(JsonElement userInputParams) =>
        userInputParams.Arr("questions") is { } questions && questions.GetArrayLength() > 0 ? questions[0] : default;
}
