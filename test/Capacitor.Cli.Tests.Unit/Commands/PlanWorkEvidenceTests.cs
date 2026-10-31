using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PlanWorkEvidenceTests {
    const string Session = "9dc2775376454e4691ecc2d69973c152";
    const string Dashed  = "9dc27753-7645-4e46-91ec-c2d69973c152";
    const string Sibling = "0000000000000000000000000000beef";

    static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    static RecapEntry Row(string type, string? path, int minute, string session = Session, string? agent = null) =>
        new(type, session, agent, null, "", path, T0.AddMinutes(minute));

    static ErrorEntry Failure(string tool, string inputJson, int minute, string session = Session, string? agent = null) =>
        new(session, null, agent, 0, tool, "failed", T0.AddMinutes(minute)) { ToolInput = JsonDocument.Parse(inputJson).RootElement.Clone() };

    [Test]
    public async Task Rows_reported_under_the_canonical_id_belong_to_a_dashed_session_id() {
        var rows = PlanWorkEvidence.Collect([Row("write", "a.md", 1), Row("edit", "b.cs", 2, session: Sibling)], [], Dashed);

        await Assert.That(rows.Select(r => (r.Kind, r.Path, r.Failed))).IsEquivalentTo([("Write", (string?)"a.md", false)]);
    }

    [Test]
    public async Task A_codex_patch_delete_is_listed() {
        var rows = PlanWorkEvidence.Collect([Row("delete", "old.cs", 1)], [], Session);

        await Assert.That(rows.Single().Kind).IsEqualTo("Delete");
    }

    /// <summary>A meta-session slug is carried by no row, so every session the server returned is the slug's.</summary>
    [Test]
    public async Task A_slug_keeps_every_session_the_server_returned() {
        var rows = PlanWorkEvidence.Collect([Row("write", "a.md", 1), Row("edit", "b.cs", 2, session: Sibling)], [], "my-slug");

        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_failed_write_marks_its_own_call_and_not_the_earlier_one_that_applied() {
        var rows = PlanWorkEvidence.Collect(
            [Row("write", "/repo/a.md", 1), Row("write", "/repo/a.md", 3)],
            [Failure("Write", """{"file_path":"/repo/a.md","content":"x"}""", 4)],
            Session);

        await Assert.That(rows.Select(r => r.Failed)).IsEquivalentTo([false, true], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_failure_in_another_agent_does_not_mark_the_root_sessions_write() {
        var rows = PlanWorkEvidence.Collect(
            [Row("write", "a.md", 1)],
            [Failure("Write", """{"file_path":"a.md","content":"x"}""", 2, agent: "sub-1")],
            Session);

        await Assert.That(rows.Count(r => r.Failed)).IsEqualTo(1);
        await Assert.That(rows.Single(r => !r.Failed).AgentId).IsNull();
    }

    /// <summary>The recap projects no row for these calls, so the failure is the only trace and must be listed.</summary>
    [Test]
    [Arguments("MultiEdit", """{"file_path":"/repo/m.cs","edits":[]}""", "/repo/m.cs")]
    [Arguments("NotebookEdit", """{"notebook_path":"/repo/n.ipynb"}""", "/repo/n.ipynb")]
    [Arguments("replace", """{"file_path":"/repo/g.ts"}""", "/repo/g.ts")]
    [Arguments("search_replace", """{"file_path":"/repo/v.py"}""", "/repo/v.py")]
    [Arguments("write_file", """{"path":"/repo/w.txt"}""", "/repo/w.txt")]
    [Arguments("edit", """{"filePath":"/repo/o.ts","oldString":"a","newString":"b"}""", "/repo/o.ts")]
    public async Task A_failed_change_the_recap_never_projected_is_listed_as_failed(string tool, string input, string path) {
        var rows = PlanWorkEvidence.Collect([Row("edit", path, 1)], [Failure(tool, input, 2)], Session);

        await Assert.That(rows.Select(r => (r.Path, r.Failed))).IsEquivalentTo([((string?)path, false), ((string?)path, true)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_failed_pi_edit_marks_every_row_its_replacements_projected() {
        var rows = PlanWorkEvidence.Collect(
            [Row("edit", "src/p.ts", 1), Row("edit", "src/p.ts", 1)],
            [Failure("edit", """{"path":"src/p.ts","edits":[{"oldText":"a","newText":"b"},{"oldText":"c","newText":"d"}]}""", 2)],
            Session);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.All(r => r.Failed)).IsTrue();
    }

    [Test]
    public async Task A_failed_codex_patch_marks_each_file_its_headers_name_across_path_spellings() {
        const string patch = "*** Begin Patch\n*** Add File: docs/new.md\n+x\n*** Update File: src/a.cs\n@@\n-a\n+b\n*** End Patch";
        var rows = PlanWorkEvidence.Collect(
            [Row("write", "/repo/docs/new.md", 2), Row("edit", "/repo/src/a.cs", 2), Row("edit", "/repo/src/untouched.cs", 1)],
            [Failure("apply_patch", new JsonObject { ["input"] = patch }.ToJsonString(), 2)],
            Session);

        await Assert.That(rows.Where(r => r.Failed).Select(r => r.Path ?? "")).IsEquivalentTo(["/repo/docs/new.md", "/repo/src/a.cs"]);
        await Assert.That(rows.Single(r => !r.Failed).Path).IsEqualTo("/repo/src/untouched.cs");
    }

    [Test]
    public async Task A_failed_tool_that_changes_no_file_marks_nothing() {
        var rows = PlanWorkEvidence.Collect([Row("write", "a.md", 1)], [Failure("Read", """{"file_path":"a.md"}""", 2)], Session);

        await Assert.That(rows.Single().Failed).IsFalse();
    }
}
