using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>get_eval_report</c> and <c>list_eval_runs</c>: a session's latest completed eval as Markdown an agent can act on,
/// and a repository's recent evaluated sessions to pick one from. Both read routes gated by session visibility alone
/// (<c>/api/sessions/{id}/eval-progress</c>, <c>/api/repositories/{hash}/eval-trends</c>), so they work on every plan.
/// </summary>
static class EvalReportTool {
    internal const string ReportName = "get_eval_report";
    internal const string RunsName   = "list_eval_runs";

    internal const int DefaultRuns = 10;
    internal const int MaxRuns     = 25;

    // A report is read into a context window; past this it is cut, with the cut stated.
    internal const int MaxReportChars = 24_000;

    internal static McpTool ReportDefinition => new(
        ReportName,
        "Get a session's latest completed eval as a Markdown report: the overall score, each category, each question's finding, evidence and recommendation, and the retrospective's strengths, issues and suggestions. " +
        "Use it to act on what the judge found before or while working. With no session_id it reports on the most recently evaluated session of the repository (repo, or the current repository). " +
        "A session not ingested yet, not visible to you, not evaluated, or still being evaluated is answered with its state instead of a report.",
        new(
            "object",
            new() {
                ["session_id"] = new("string", "The session to report on. Omit for the repository's most recently evaluated session."),
                ["repo"]       = new("string", "\"<owner>/<name>\" or a 16-hex repo hash, used only when session_id is omitted. Defaults to the current repository.")
            },
            []
        ),
        McpToolAnnotations.Read
    );

    internal static McpTool RunsDefinition => new(
        RunsName,
        "List a repository's recently evaluated sessions, newest first, with each session's category scores (out of 5) and their mean, so you can pick one for get_eval_report. " +
        "Covers the 50 most recent evaluated sessions you can see from the last year.",
        new(
            "object",
            new() {
                ["repo"]  = new("string", "\"<owner>/<name>\" or a 16-hex repo hash. Defaults to the current repository."),
                ["limit"] = new("integer", $"How many sessions to list, 1 to {MaxRuns}. Default {DefaultRuns}.")
            },
            []
        ),
        McpToolAnnotations.Read
    );

    // The server's widest window: a repository evaluated rarely still has a latest run to report on.
    static readonly TimeSpan TrendsWindow = TimeSpan.FromDays(365);

    internal static string BuildTrendsUrl(string baseUrl, string repoHash, TimeProvider time) =>
        $"{baseUrl}/api/repositories/{Uri.EscapeDataString(repoHash)}/eval-trends?limit=50&since=" +
        Uri.EscapeDataString((time.GetUtcNow() - TrendsWindow).ToString("O", CultureInfo.InvariantCulture));

    internal static string BuildProgressUrl(string baseUrl, string sessionId) =>
        $"{baseUrl}/api/sessions/{Uri.EscapeDataString(sessionId)}/eval-progress";

    internal static int ParseLimit(JsonObject? args) {
        if (args?["limit"] is not JsonValue value) return DefaultRuns;
        if (!value.TryGetValue(out int limit) || limit is < 1 or > MaxRuns)
            throw new ArgumentException($"limit must be an integer from 1 to {MaxRuns}.");
        return limit;
    }

    /// <summary>The evaluated sessions in an eval-trends response, newest first: one entry per session, its scores
    /// gathered from every category's trend line.</summary>
    internal static List<Run> Runs(string trendsBody) {
        using var doc  = JsonDocument.Parse(trendsBody);
        var       runs = new Dictionary<string, Run>(StringComparer.Ordinal);

        foreach (var trend in Items(doc.RootElement, "category_trends")) {
            var category = trend.Str("category");
            foreach (var point in Items(trend, "points")) {
                // A trend score is a category average, so it is fractional as often as not.
                if (point.Str("session_id") is not { } id || category is null || point.Prop("score") is not { ValueKind: JsonValueKind.Number } score) continue;
                if (!runs.TryGetValue(id, out var run)) runs[id] = run = new Run(id, point.Str("session_slug"), point.Str("evaluated_at"), []);
                run.Scores[category] = score.GetDouble();
            }
        }

        return [.. runs.Values.OrderByDescending(r => r.EvaluatedAt is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, out var t) ? t : DateTimeOffset.MinValue)];
    }

    internal sealed record Run(string SessionId, string? Slug, string? EvaluatedAt, Dictionary<string, double> Scores) {
        public double Mean => Scores.Count == 0 ? 0 : Scores.Values.Average();
    }

    internal static JsonObject ProjectRuns(IReadOnlyList<Run> runs, int limit) {
        var sessions = new JsonArray();
        foreach (var run in runs.Take(limit)) {
            var scores = new JsonObject();
            foreach (var (category, score) in run.Scores.OrderBy(s => s.Key, StringComparer.Ordinal)) scores[category] = Math.Round(score, 2);
            sessions.Add((JsonNode)new JsonObject {
                ["session_id"]   = run.SessionId,
                ["session_slug"] = run.Slug,
                ["evaluated_at"] = run.EvaluatedAt,
                ["mean_score"]   = Math.Round(run.Mean, 2),
                ["categories"]   = scores
            });
        }
        return new JsonObject { ["sessions"] = sessions };
    }

    /// <summary>The report for one session's eval-progress answer, or the state that stands in for it. A state is an
    /// answer; only a read that failed is an error.</summary>
    internal static (string Text, bool IsError) Render(string sessionId, HttpStatusCode status, string? body) {
        switch (status) {
            case HttpStatusCode.NotFound:  return ($"Session {sessionId} is not ingested yet, or is not visible to you.", false);
            case HttpStatusCode.NoContent: return ($"Session {sessionId} has not been evaluated.", false);
            case not HttpStatusCode.OK:    return ($"The eval of session {sessionId} could not be read (HTTP {(int)status}).", true);
        }

        JsonElement root;
        try {
            using var doc = JsonDocument.Parse(body ?? "");
            root = doc.RootElement.Clone();
        } catch (JsonException) {
            return ($"The eval of session {sessionId} could not be read: the response was not valid JSON.", true);
        }

        // A run that judged nothing and recorded only coded failures is a failed run; a completed one with no
        // categories still has its score, summary and retrospective to report.
        var result      = root.Obj("completed_result");
        var failureOnly = result is { } r && !Items(r, "categories").Any() && Items(r, "failed_questions").Any();

        if (result is { } completed && !failureOnly) return (Cap(Markdown(sessionId, completed, root)), false);

        // A completed run with no assessed question has no score, so the server sends no result; its question list
        // still says what the judge answered.
        if (!failureOnly && root.Bool("is_terminal") == true && root.Obj("failed_result") is null && root.Str("failure_reason") is null) {
            var md = new StringBuilder($"# Eval report: session {sessionId}\n\nThe latest eval completed without a score: the judge assessed no question.\n\n");
            Unscored(md, root);
            return (md.ToString().TrimEnd() + "\n", false);
        }

        if (failureOnly || root.Bool("is_terminal") == true || root.Obj("failed_result") is not null)
            return ($"The latest eval of session {sessionId} failed: {root.Str("failure_reason") ?? "every question failed to judge"}.", false);
        return ($"Session {sessionId} is being evaluated; no completed report yet.", false);
    }

    static string Markdown(string sessionId, JsonElement result, JsonElement progress) {
        var md = new StringBuilder();
        md.Append("# Eval report: session ").Append(sessionId).Append('\n').Append('\n');
        md.Append("Overall ").Append(result.Num("overall_score")?.ToString(CultureInfo.InvariantCulture) ?? "?").Append("/5");
        if (result.Str("judge_model") is { } model) md.Append(" · judge ").Append(model);
        if (result.Str("evaluated_at") is { } at) md.Append(" · ").Append(at);
        md.Append("\n\n");
        if (result.Str("summary") is { Length: > 0 } summary) md.Append(summary.Trim()).Append("\n\n");

        foreach (var category in Items(result, "categories")) {
            md.Append("## ").Append(category.Str("name")).Append(" (").Append(category.Num("score")?.ToString(CultureInfo.InvariantCulture) ?? "?").Append("/5)\n\n");
            foreach (var q in Items(category, "questions")) {
                md.Append("- **").Append(q.Str("question_id")).Append("**: ")
                  .Append(q.Num("score") is { } score ? $"{score}/5" : "not scored");
                if (q.Str("verdict") is { } verdict) md.Append(", ").Append(verdict);
                md.Append('\n');
                Line(md, "Finding", q.Str("finding"));
                Line(md, "Evidence", q.Str("evidence"));
                Line(md, "Recommendation", q.Str("recommendation"));
            }
            md.Append('\n');
        }

        Unscored(md, progress);

        var failed = Items(result, "failed_questions").ToList();
        if (failed.Count > 0) {
            md.Append("## Questions that failed to judge\n\n");
            foreach (var f in failed) md.Append("- ").Append(f.Str("category")).Append('/').Append(f.Str("question_id")).Append(": ").Append(f.Str("code")).Append('\n');
            md.Append('\n');
        }

        if (result.Obj("retrospective") is { } retro) {
            md.Append("## Retrospective\n\n");
            if (retro.Str("overall") is { Length: > 0 } overall) md.Append(overall.Trim()).Append("\n\n");
            List(md, "Strengths", Items(retro, "strengths", objects: false).Select(s => s.GetString()));
            List(md, "Issues", Items(retro, "issues", objects: false).Select(s => s.GetString()));
            List(md, "Suggestions", Items(retro, "suggestions").Select(s =>
                s.Str("audience") is { } audience ? $"({audience}) {s.Str("text")}" : s.Str("text")));
        }

        return md.ToString().TrimEnd() + "\n";
    }

    // The completed result keeps only assessed questions; the run's question list names the ones the judge answered
    // without a score, and why.
    static void Unscored(StringBuilder md, JsonElement progress) {
        var unscored = Items(progress, "questions").Where(q => q.Str("outcome") is { } o && o != "assessed").ToList();
        if (unscored.Count == 0) return;
        md.Append("## Questions answered without a score\n\n");
        foreach (var q in unscored) md.Append("- ").Append(q.Str("category")).Append('/').Append(q.Str("question_id")).Append(": ").Append(q.Str("outcome")).Append('\n');
        md.Append('\n');
    }

    static void Line(StringBuilder md, string label, string? text) {
        if (string.IsNullOrWhiteSpace(text)) return;
        md.Append("  - ").Append(label).Append(": ").Append(text.Trim().ReplaceLineEndings(" ")).Append('\n');
    }

    static void List(StringBuilder md, string heading, IEnumerable<string?> items) {
        var lines = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (lines.Count == 0) return;
        md.Append("**").Append(heading).Append("**\n\n");
        foreach (var line in lines) md.Append("- ").Append(line!.Trim().ReplaceLineEndings(" ")).Append('\n');
        md.Append('\n');
    }

    static string Cap(string report) =>
        report.Length <= MaxReportChars
            ? report
            : report[..MaxReportChars] + $"\n\n[Report cut at {MaxReportChars} characters of {report.Length}.]\n";

    static IEnumerable<JsonElement> Items(JsonElement parent, string name, bool objects = true) =>
        parent.Arr(name) is { } array ? array.EnumerateArray().Where(e => objects ? e.IsObject : e.IsString) : [];
}
