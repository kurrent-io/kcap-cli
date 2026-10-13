using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeTaskListEventTests {
    const string Sid = "5b5e2030-4a20-4690-b853-5e874f88712c";

    static string Payload(string tool, JsonObject? input = null, string? agentId = null) {
        var hook = new JsonObject {
            ["hook_event_name"] = "PostToolUse", ["session_id"] = Sid, ["tool_name"] = tool,
            ["tool_input"]      = input ?? new JsonObject(),
        };
        if (agentId is not null) hook["agent_id"] = agentId;
        return hook.ToJsonString();
    }

    static JsonObject Todos(params string[] statuses) =>
        new() { ["todos"] = new JsonArray(statuses.Select(s => (JsonNode) new JsonObject { ["content"] = "x", ["status"] = s }).ToArray()) };

    [Test]
    public async Task TaskCreate_starts_the_list() {
        var evt = ClaudeTaskListEvent.Parse(Payload("TaskCreate", new JsonObject { ["subject"] = "one" }));

        await Assert.That(evt).IsEqualTo(new ClaudeTaskListEvent(Sid.Replace("-", ""), ClaudeTaskListEvent.Moment.Created));
    }

    [Test]
    public async Task Only_a_TaskUpdate_to_completed_counts() {
        var completed = ClaudeTaskListEvent.Parse(Payload("TaskUpdate", new JsonObject { ["taskId"] = "1", ["status"] = "completed" }));
        var started   = ClaudeTaskListEvent.Parse(Payload("TaskUpdate", new JsonObject { ["taskId"] = "1", ["status"] = "in_progress" }));

        await Assert.That(completed?.Kind).IsEqualTo(ClaudeTaskListEvent.Moment.Completed);
        await Assert.That(started).IsNull();
    }

    [Test]
    public async Task TodoWrite_completes_when_any_item_is_completed() {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("TodoWrite", Todos("pending", "in_progress")))?.Kind)
            .IsEqualTo(ClaudeTaskListEvent.Moment.Created);
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("TodoWrite", Todos("completed", "pending")))?.Kind)
            .IsEqualTo(ClaudeTaskListEvent.Moment.Completed);
    }

    [Test]
    [Arguments("mcp__plugin_kcap_kcap-plans__set_plan_tasks")]
    [Arguments("mcp__kcap-plans__update_plan_task")]
    public async Task A_kcap_plans_task_write_is_a_plan(string tool) {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload(tool))?.Kind).IsEqualTo(ClaudeTaskListEvent.Moment.PlanWritten);
    }

    [Test]
    [Arguments("mcp__kcap-plans__get_plan")]
    [Arguments("mcp__kcap-plans__declare_plan_document")]
    public async Task Reading_the_plan_or_declaring_a_document_writes_no_tasks(string tool) {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload(tool))).IsNull();
    }

    [Test]
    public async Task A_subagent_call_is_ignored() {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("TaskCreate", agentId: "a1"))).IsNull();
    }

    [Test]
    public async Task A_Bash_append_to_the_ledger_is_a_ledger_moment() {
        var started = ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject {
            ["command"] = "S=/tmp/x; printf '# SDD ledger — plan: %s/plan.md\\n' \"$S\" > \"$S/progress.md\""
        }));
        var finished = ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject {
            ["command"] = "git commit -q -m \"Add CI\" && echo \"Task 4: complete (commits 480bc9b..)\" >> \"$S/progress.md\""
        }));

        await Assert.That(started).IsEqualTo(new ClaudeTaskListEvent(Sid.Replace("-", ""), ClaudeTaskListEvent.Moment.Created, FromLedger: true));
        await Assert.That(finished?.Kind).IsEqualTo(ClaudeTaskListEvent.Moment.Completed);
    }

    [Test]
    public async Task A_Write_or_Edit_counts_only_on_the_ledger_file() {
        var ledger = ClaudeTaskListEvent.Parse(Payload("Edit", new JsonObject {
            ["file_path"] = "/repo/.superpowers/x/progress.md", ["old_string"] = "a", ["new_string"] = "Task 1: complete"
        }));
        var other = ClaudeTaskListEvent.Parse(Payload("Write", new JsonObject { ["file_path"] = "/repo/README.md", ["content"] = "complete" }));

        await Assert.That(ledger).IsEqualTo(new ClaudeTaskListEvent(Sid.Replace("-", ""), ClaudeTaskListEvent.Moment.Completed, FromLedger: true));
        await Assert.That(other).IsNull();
    }

    [Test]
    [Arguments("cat \"$S/progress.md\"")]
    [Arguments("tail -5 progress.md && git log --oneline -3")]
    public async Task Reading_the_ledger_is_not_writing_it(string command) {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject { ["command"] = command }))).IsNull();
    }

    [Test]
    [Arguments("git commit -m \"complete refactor\" && echo \"Task 2: in progress\" >> \"$S/progress.md\"")]
    [Arguments("printf 'Task 1: pending\\n' >> progress.md; echo completed")]
    public async Task Only_a_finished_task_entry_completes(string command) {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject { ["command"] = command }))?.Kind)
            .IsEqualTo(ClaudeTaskListEvent.Moment.Created);
    }

    [Test]
    public async Task A_tee_into_the_ledger_is_a_write() {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject { ["command"] = "echo 'Task 3: complete' | tee -a \"$S/progress.md\"" }))?.Kind)
            .IsEqualTo(ClaudeTaskListEvent.Moment.Completed);
    }

    [Test]
    public async Task A_rejected_plan_write_is_not_a_plan() {
        var hook = JsonNode.Parse(Payload("mcp__kcap-plans__set_plan_tasks"))!.AsObject();
        hook["tool_response"] = new JsonObject { ["isError"] = true };

        await Assert.That(ClaudeTaskListEvent.Parse(hook.ToJsonString())).IsNull();
    }

    [Test]
    public async Task A_Bash_command_that_never_names_the_ledger_is_ignored() {
        await Assert.That(ClaudeTaskListEvent.Parse(Payload("Bash", new JsonObject { ["command"] = "git commit -m complete" }))).IsNull();
    }

    [Test]
    public async Task The_ledger_nudge_points_at_the_plan_the_ledger_names() {
        var nudge = new ClaudeTaskListEvent(Sid.Replace("-", ""), ClaudeTaskListEvent.Moment.Created, FromLedger: true).Nudge();

        await Assert.That(nudge).Contains("ledger's first line");
        await Assert.That(nudge).Contains("`declare_plan_document`");
    }

    [Test]
    public async Task The_nudge_names_the_tools_and_the_session() {
        var nudge = new ClaudeTaskListEvent(Sid.Replace("-", ""), ClaudeTaskListEvent.Moment.Created).Nudge();

        await Assert.That(nudge).Contains("`set_plan_tasks`");
        await Assert.That(nudge).Contains("`update_plan_task`");
        await Assert.That(nudge).Contains(Sid.Replace("-", ""));
    }

    [Test]
    public async Task A_session_id_that_is_not_a_guid_suppresses_the_nudge() {
        await Assert.That(new ClaudeTaskListEvent("ab`cd", ClaudeTaskListEvent.Moment.Created).Nudge()).IsNull();
    }
}
