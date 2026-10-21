using System.Text.Json;
using System.Text.Json.Nodes;
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
            if (q.Str("question") is not { Length: > 0 } text) return null;

            var options = q.Arr("options") is { } offered
                ? offered.EnumerateArray()
                    .Where(o => o.Str("label") is { Length: > 0 })
                    .Select(o => new AcpVendorQuestionOption(o.Str("label")!, o.Str("description")))
                    .ToArray()
                : [];

            questions.Add(new AcpVendorQuestion(text, options, q.Bool("multiSelect") == true));
        }

        return new AcpVendorQuestionRequest(sessionId, questions);
    }

    static JsonElement Reply(IReadOnlyList<AcpVendorQuestionAnswer>? answered) {
        var reply = new JsonObject();

        if (answered is null) {
            reply["outcome"] = "cancelled";
        } else {
            var answers = new JsonObject();

            foreach (var a in answered)
                answers[a.Question.Text] = a.Question.MultiSelect
                    ? new JsonArray([.. a.Answers.Select(l => (JsonNode?)JsonValue.Create(l))])
                    : JsonValue.Create(a.Answers[0]);

            reply["outcome"] = "accepted";
            reply["answers"] = answers;
        }

        return JsonDocument.Parse(reply.ToJsonString()).RootElement.Clone();
    }
}
