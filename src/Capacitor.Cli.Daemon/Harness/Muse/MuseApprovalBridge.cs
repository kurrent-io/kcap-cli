using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Maps a Muse <c>approval/requested</c> payload onto the daemon's interaction route and the answer back. Fail-closed.</summary>
internal static class MuseApprovalBridge {
    const string AbortChoice     = "abort";
    const string DeclinedFeedback = "kcap: declined";

    public static AcpInteractionRequest ToInteraction(string agentId, string sessionId, JsonElement approvalParams) {
        var subject  = approvalParams.TryGetProperty("subject", out var s) ? s : default;
        var kind     = StringOf(subject, "kind");
        var toolName = kind switch {
            "tool"  => StringOf(subject, "toolName") ?? StringOf(approvalParams, "toolName") ?? "tool",
            "shell" => "bash",
            _       => kind ?? StringOf(approvalParams, "toolName") ?? "unknown",
        };

        var prompt = kind == "shell" ? StringOf(subject, "command") ?? toolName : toolName;
        if (approvalParams.TryGetProperty("subagentOrigin", out _)) prompt = "[subagent] " + prompt;

        var options = new List<AcpInteractionOption>();
        foreach (var choice in Choices(approvalParams)) {
            var id = StringOf(choice, "choiceId");
            if (id is null) continue;

            options.Add(new AcpInteractionOption(id, StringOf(choice, "label") ?? id, null, KindOf(StringOf(choice, "decision"))));
        }

        return new AcpInteractionRequest(
            AgentId:       agentId,
            AcpSessionId:  sessionId,
            Kind:          "permission",
            ToolName:      toolName,
            ToolInput:     ToolInput(subject, StringOf(approvalParams, "rawArgs")),
            ToolCallId:    StringOf(approvalParams, "toolCallId"),
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
                if (!string.Equals(StringOf(choice, "choiceId"), id, StringComparison.Ordinal)) continue;

                var isAbort = StringOf(choice, "decision") == AbortChoice;
                if (decision.Outcome == "deny" && !isAbort) break;

                var takesFeedback = choice.TryGetProperty("acceptsFeedback", out var f) && f.ValueKind == JsonValueKind.True;

                return new MuseApprovalDecision(id, takesFeedback ? decision.FreeText : null);
            }
        }

        return new MuseApprovalDecision(AbortChoiceId(choices, fallToLast: true), DeclinedFeedback);
    }

    /// <summary>The choice with <c>decision == "abort"</c>. Without one: the last choice when <paramref name="fallToLast"/>,
    /// else the literal <c>abort</c> id, which a host rejects rather than reads as a grant.</summary>
    internal static string AbortChoiceId(List<JsonElement> choices, bool fallToLast) {
        string? last = null;

        foreach (var choice in choices) {
            var id = StringOf(choice, "choiceId");
            if (id is null) continue;
            if (StringOf(choice, "decision") == AbortChoice) return id;

            last = id;
        }

        return fallToLast ? last ?? AbortChoice : AbortChoice;
    }

    internal static List<JsonElement> Choices(JsonElement approvalParams) {
        var list = new List<JsonElement>();

        if (approvalParams.ValueKind == JsonValueKind.Object
         && approvalParams.TryGetProperty("availableChoices", out var arr)
         && arr.ValueKind == JsonValueKind.Array) {
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) list.Add(item);
        }

        return list;
    }

    internal static string? StringOf(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    static string? KindOf(string? decision) => decision switch {
        "approved"                 => "allow_once",
        "approvedForSession"       => "allow_always",
        "approvedPolicyAmendment"  => "allow_always",
        "abort"                    => "reject_once",
        _                          => null,
    };

    static JsonElement ToolInput(JsonElement subject, string? rawArgs) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WritePropertyName("subject");

            if (subject.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
            else subject.WriteTo(writer);

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
