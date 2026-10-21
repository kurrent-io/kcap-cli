using System.Buffers;
using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Grok's <c>_x.ai/ask_user_question</c>: <c>{sessionId, toolCallId, mode, questions: [{question, options:
/// [{label, description}], multiSelect}]}</c>, answered with <c>{outcome, answers}</c>. Grok rejects a reply
/// whose <c>outcome</c> is not one of its own variant names, and reads each answer as a label or a list
/// of labels keyed by the question's text.
/// </summary>
internal static class GrokQuestions {
    internal static readonly AcpQuestionProtocol Protocol = new("_x.ai/ask_user_question", Read, Reply);

    static AcpVendorQuestionRequest? Read(JsonElement request) {
        if (request.Str("sessionId") is not { Length: > 0 } sessionId || request.Arr("questions") is not { } list)
            return null;

        var questions = new List<AcpVendorQuestion>();

        foreach (var q in list.EnumerateArray()) {
            if (q.ValueKind != JsonValueKind.Object || q.Str("question") is not { } text || string.IsNullOrWhiteSpace(text)) return null;
            if (!IsOptional(q, "multiSelect", JsonValueKind.True, JsonValueKind.False)) return null;
            if (OptionsOf(q) is not { } options) return null;

            questions.Add(new AcpVendorQuestion(text, options, q.Bool("multiSelect") == true));
        }

        return new AcpVendorQuestionRequest(sessionId, questions);
    }

    /// <summary>A question's choices, none when it offers none; null when they cannot all be read, since
    /// asking it as a typed question would hide the choices Grok meant to offer.</summary>
    static AcpVendorQuestionOption[]? OptionsOf(JsonElement question) {
        if (!question.TryGetProperty("options", out var offered) || offered.ValueKind == JsonValueKind.Null) return [];
        if (offered.ValueKind != JsonValueKind.Array) return null;

        var options = new List<AcpVendorQuestionOption>();

        foreach (var o in offered.EnumerateArray()) {
            if (o.ValueKind != JsonValueKind.Object || o.Str("label") is not { } label || string.IsNullOrWhiteSpace(label)) return null;
            if (!IsOptional(o, "description", JsonValueKind.String)) return null;

            options.Add(new AcpVendorQuestionOption(label, o.Str("description")));
        }

        return [.. options];
    }

    /// <summary>True when <paramref name="name"/> is absent, null, or of one of the expected kinds.</summary>
    static bool IsOptional(JsonElement element, string name, params JsonValueKind[] kinds) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null || kinds.Contains(value.ValueKind);

    // Written token by token: JsonNode values lower to generic calls that NativeAOT cannot compile.
    static JsonElement Reply(IReadOnlyList<AcpVendorQuestionAnswer>? answered) {
        var buffer = new ArrayBufferWriter<byte>();

        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("outcome", answered is null ? "cancelled" : "accepted");

            if (answered is not null) {
                json.WriteStartObject("answers");

                foreach (var a in answered) {
                    if (a.Question.MultiSelect) {
                        json.WriteStartArray(a.Question.Text);
                        foreach (var label in a.Answers) json.WriteStringValue(label);
                        json.WriteEndArray();
                    } else {
                        json.WriteString(a.Question.Text, a.Answers[0]);
                    }
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);

        return doc.RootElement.Clone();
    }
}
