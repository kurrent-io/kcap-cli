using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The eval report an agent pulls: a completed run rendered as Markdown with every finding, recommendation and
/// retrospective suggestion, a state in place of a report when there is none, and a repository's evaluated sessions
/// newest first.</summary>
public class EvalReportToolTests {
    const string Completed = """
        {"eval_run_id":"r1","is_terminal":true,"questions":[],
         "completed_result":{"eval_run_id":"r1","judge_model":"sonnet","evaluated_at":"2026-10-01T10:00:00+00:00","overall_score":3,
           "summary":"Shipped, but skipped the tests.",
           "categories":[{"name":"testing","score":2,"verdict":"warn","questions":[
             {"category":"testing","question_id":"wrote_tests","score":2,"verdict":"warn","finding":"No test covers the fix.",
              "evidence":"Only src/ changed.","recommendation":"Add a test that fails without the fix."}]}],
           "retrospective":{"overall":"Fast but loose.","strengths":["Small diff"],"issues":["No tests"],
             "suggestions":[{"text":"Run the targeted tests before pushing.","audience":"agent"}]},
           "facts_used":[]}}
        """;

    [Test]
    public async Task A_completed_run_renders_every_finding_recommendation_and_suggestion() {
        var (report, isError) = EvalReportTool.Render("s-1", HttpStatusCode.OK, Completed);

        await Assert.That(isError).IsFalse();
        await Assert.That(report).StartsWith("# Eval report: session s-1");
        await Assert.That(report).Contains("Overall 3/5 · judge sonnet");
        await Assert.That(report).Contains("## testing (2/5)");
        await Assert.That(report).Contains("- **wrote_tests**: 2/5, warn");
        await Assert.That(report).Contains("  - Finding: No test covers the fix.");
        await Assert.That(report).Contains("  - Evidence: Only src/ changed.");
        await Assert.That(report).Contains("  - Recommendation: Add a test that fails without the fix.");
        await Assert.That(report).Contains("- (agent) Run the targeted tests before pushing.");
        await Assert.That(report).Contains("- No tests");
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound, null, "is not ingested yet, or is not visible to you", false)]
    [Arguments(HttpStatusCode.NoContent, null, "has not been evaluated", false)]
    [Arguments(HttpStatusCode.InternalServerError, null, "could not be read (HTTP 500)", true)]
    [Arguments(HttpStatusCode.Forbidden, null, "could not be read (HTTP 403)", true)]
    [Arguments(HttpStatusCode.OK, "not json", "was not valid JSON", true)]
    [Arguments(HttpStatusCode.OK, """{"is_terminal":false,"completed_result":null,"questions":[]}""", "is being evaluated", false)]
    [Arguments(HttpStatusCode.OK, """{"is_terminal":true,"completed_result":null,"failure_reason":"daemon disconnected mid-run"}""", "failed: daemon disconnected mid-run", false)]
    [Arguments(HttpStatusCode.OK, """{"is_terminal":true,"completed_result":{"categories":[],"failed_questions":[{"code":"spend_budget"}]}}""", "failed: every question failed to judge", false)]
    public async Task No_completed_run_answers_its_state_instead(HttpStatusCode status, string? body, string expected, bool error) {
        var (text, isError) = EvalReportTool.Render("s-1", status, body);

        await Assert.That(text).Contains(expected);
        await Assert.That(isError).IsEqualTo(error);
    }

    /// <summary>The completed result keeps only assessed questions, so the unscored ones come from the run's question
    /// list, and the ones that failed to judge from its failed questions.</summary>
    [Test]
    public async Task Unscored_and_failed_questions_are_reported() {
        const string body = """
            {"is_terminal":true,
             "questions":[
               {"category":"process","question_id":"tracked","status":"completed","outcome":"insufficient_evidence"},
               {"category":"process","question_id":"scoped","status":"completed","outcome":"not_applicable"},
               {"category":"process","question_id":"planned","status":"completed","outcome":"assessed","score":3}],
             "completed_result":{"overall_score":3,"summary":"Mixed.","categories":[{"name":"process","score":3,"questions":[
               {"question_id":"planned","score":3,"verdict":"warn","finding":"Partly."}]}],
             "failed_questions":[{"category":"safety","question_id":"secrets","code":"spend_budget"}]}}
            """;

        var (text, _) = EvalReportTool.Render("s-1", HttpStatusCode.OK, body);

        await Assert.That(text).Contains("- process/tracked: insufficient_evidence");
        await Assert.That(text).Contains("- process/scoped: not_applicable");
        await Assert.That(text).DoesNotContain("process/planned:");
        await Assert.That(text).Contains("- safety/secrets: spend_budget");
    }

    [Test]
    public async Task A_completed_run_with_no_assessed_question_reports_its_outcomes_not_a_failure() {
        const string body = """
            {"is_terminal":true,"completed_result":null,"failed_result":null,"failure_reason":null,
             "questions":[{"category":"process","question_id":"tracked","status":"completed","outcome":"insufficient_evidence"}]}
            """;

        var (text, isError) = EvalReportTool.Render("s-1", HttpStatusCode.OK, body);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("completed without a score");
        await Assert.That(text).Contains("- process/tracked: insufficient_evidence");
        await Assert.That(text).DoesNotContain("failed");
    }

    [Test]
    public async Task A_completed_run_with_no_categories_still_reports_its_score_and_retrospective() {
        const string body = """
            {"is_terminal":true,"completed_result":{"overall_score":4,"summary":"Fine.","categories":[],"failed_questions":[],
             "retrospective":{"overall":"Clean run.","strengths":[],"issues":[],"suggestions":[]}}}
            """;

        var (text, isError) = EvalReportTool.Render("s-1", HttpStatusCode.OK, body);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("Overall 4/5");
        await Assert.That(text).Contains("Clean run.");
    }

    [Test]
    public async Task An_oversized_report_is_cut_and_says_so() {
        var huge = Completed.Replace("No test covers the fix.", new string('x', EvalReportTool.MaxReportChars));

        var (report, _) = EvalReportTool.Render("s-1", HttpStatusCode.OK, huge);

        await Assert.That(report.Length).IsLessThan(EvalReportTool.MaxReportChars + 100);
        await Assert.That(report).Contains($"[Report cut at {EvalReportTool.MaxReportChars} characters of");
    }

    const string Trends = """
        {"repo_hash":"h","sessions_evaluated":2,"category_trends":[
          {"category":"testing","average_score":3,"points":[
            {"session_id":"old","session_slug":"old-slug","evaluated_at":"2026-09-01T10:00:00+00:00","score":2.5},
            {"session_id":"new","session_slug":null,"evaluated_at":"2026-10-01T10:00:00+00:00","score":4}]},
          {"category":"safety","average_score":5,"points":[
            {"session_id":"new","session_slug":null,"evaluated_at":"2026-10-01T10:00:00+00:00","score":5}]}],
         "repeat_findings":[],"aggregated_facts":[]}
        """;

    [Test]
    public async Task Runs_gather_each_sessions_scores_newest_first() {
        var runs = EvalReportTool.Runs(Trends);

        await Assert.That(runs.Select(r => r.SessionId)).IsEquivalentTo(["new", "old"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(runs[0].Mean).IsEqualTo(4.5);
        await Assert.That(runs[1].Scores["testing"]).IsEqualTo(2.5);   // a category average, fractional

        var listed = EvalReportTool.ProjectRuns(runs, limit: 1)["sessions"]!.AsArray();
        await Assert.That(listed.Count).IsEqualTo(1);
        await Assert.That(listed[0]!["categories"]!["safety"]!.GetValue<double>()).IsEqualTo(5);
    }

    [Test]
    public async Task Limit_defaults_and_rejects_values_outside_its_range() {
        await Assert.That(EvalReportTool.ParseLimit(null)).IsEqualTo(EvalReportTool.DefaultRuns);
        await Assert.That(EvalReportTool.ParseLimit(new JsonObject { ["limit"] = 3 })).IsEqualTo(3);
        await Assert.That(() => EvalReportTool.ParseLimit(new JsonObject { ["limit"] = 0 })).Throws<ArgumentException>();
        await Assert.That(() => EvalReportTool.ParseLimit(new JsonObject { ["limit"] = EvalReportTool.MaxRuns + 1 })).Throws<ArgumentException>();
        await Assert.That(() => EvalReportTool.ParseLimit(new JsonObject { ["limit"] = "3" })).Throws<ArgumentException>();
    }

    [Test]
    public async Task The_trends_read_reaches_back_a_year() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));

        await Assert.That(EvalReportTool.BuildTrendsUrl("https://x", "abc", time))
            .IsEqualTo("https://x/api/repositories/abc/eval-trends?limit=50&since=2025-10-22T00%3A00%3A00.0000000%2B00%3A00");
    }
}
