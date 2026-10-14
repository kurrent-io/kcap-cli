using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Maps a Muse <c>approval/requested</c> payload onto the daemon's interaction route and the answer back. Fail-closed.</summary>
internal static class MuseApprovalBridge {
    const string AbortChoice     = "abort";
    const string DeclinedFeedback = "kcap: declined";

    public static AcpInteractionRequest ToInteraction(string agentId, string sessionId, JsonElement approvalParams) {
        var given    = approvalParams.Prop("subject");
        var subject  = given ?? default;
        var kind     = subject.Str("kind");
        var toolName = kind switch {
            "tool"  => subject.Str("toolName") ?? approvalParams.Str("toolName") ?? "tool",
            "shell" => "bash",
            _       => kind ?? approvalParams.Str("toolName") ?? "unknown",
        };

        var prompt = kind == "shell" ? subject.Str("command") ?? toolName : toolName;
        if (approvalParams.Prop("subagentOrigin") is not null) prompt = "[subagent] " + prompt;

        var options = new List<AcpInteractionOption>();
        foreach (var choice in Choices(approvalParams)) {
            var id = choice.Str("choiceId");
            if (id is null) continue;

            options.Add(new AcpInteractionOption(id, choice.Str("label") ?? id, null, KindOf(choice.Str("decision"))));
        }

        return new AcpInteractionRequest(
            AgentId:       agentId,
            AcpSessionId:  sessionId,
            Kind:          "permission",
            ToolName:      toolName,
            ToolInput:     ToolInput(given, approvalParams.Str("rawArgs")),
            ToolCallId:    approvalParams.Str("toolCallId"),
            Prompt:        prompt,
            Options:       [.. options],
            IsMultiSelect: false);
    }

    /// <summary>Only an explicit selection of an offered choice survives; cancel, timeout, an unknown id or a
    /// contradictory deny-plus-allow pair all become the abort choice.</summary>
    public static MuseApprovalDecision ToDecision(AcpInteractionDecision decision, JsonElement approvalParams) {
        var choices = Choices(approvalParams);

        if (decision.SelectedOptionId is { } id
         && decision.Outcome is "allow" or "allow_once" or "allow_always" or "deny") {
            foreach (var choice in choices) {
                if (!string.Equals(choice.Str("choiceId"), id, StringComparison.Ordinal)) continue;

                var isAbort = choice.Str("decision") == AbortChoice;
                if (decision.Outcome == "deny" && !isAbort) break;

                var takesFeedback = choice.Bool("acceptsFeedback") == true;

                return new MuseApprovalDecision(id, takesFeedback ? decision.FreeText : null);
            }
        }

        return new MuseApprovalDecision(AbortChoiceId(choices), DeclinedFeedback);
    }

    /// <summary>The first choice with <c>decision == "abort"</c>, else the literal <c>abort</c> id, which a host
    /// rejects rather than reads as a grant.</summary>
    internal static string AbortChoiceId(List<JsonElement> choices) {
        foreach (var choice in choices) {
            if (choice.Str("choiceId") is { } id && choice.Str("decision") == AbortChoice) return id;
        }

        return AbortChoice;
    }

    internal static List<JsonElement> Choices(JsonElement approvalParams) {
        var list = new List<JsonElement>();

        if (approvalParams.Arr("availableChoices") is { } arr) {
            foreach (var item in arr.EnumerateArray())
                if (item.IsObject) list.Add(item);
        }

        return list;
    }

    static string? KindOf(string? decision) => decision switch {
        "approved"                 => "allow_once",
        "approvedForSession"       => "allow_always",
        "approvedPolicyAmendment"  => "allow_always",
        "abort"                    => "reject_once",
        _                          => null,
    };

    static JsonElement ToolInput(JsonElement? subject, string? rawArgs) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WritePropertyName("subject");

            if (subject is { } given) given.WriteTo(writer);
            else writer.WriteNullValue();

            writer.WritePropertyName("args");

            if (rawArgs is null) {
                writer.WriteNullValue();
            } else {
                try {
                    using var args = JsonDocument.Parse(rawArgs);
                    args.RootElement.WriteTo(writer);
                } catch (JsonException) {
                    writer.WriteStringValue(rawArgs);
                }
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());

        return doc.RootElement.Clone();
    }
}
