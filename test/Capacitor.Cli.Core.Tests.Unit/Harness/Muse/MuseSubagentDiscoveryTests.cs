using System.Collections.Frozen;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseSubagentDiscoveryTests {
    const string ParentId   = "1d8166bf-0f96-48cf-b6ff-ee03c8b9e69c";
    const string ReminderId = "43b7956d-b5c5-422c-b9ca-80c98e8dff28";
    const string WorkerId   = "01a13565-a626-7f70-bda0-1c19ba4deadf";

    // Verbatim from real Muse logs, except that the worker records drop their large admission blobs.
    const string ReminderLine =
        """{"schema_version":1,"id":"c68ed5b7-4d60-49bc-a14c-bd814cde07ba","stream":{"kind":"session","id":"1d8166bf-0f96-48cf-b6ff-ee03c8b9e69c"},"sequence":248,"recorded_at":1791916423093156,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"runtime.session","payload_schema_version":1,"payload":{"kind":"run","run_id":"6f9716d5-b989-487b-a298-6980997ce55f","event":{"kind":"memory_reminder_child_session_linked","parent_session_id":"1d8166bf-0f96-48cf-b6ff-ee03c8b9e69c","parent_run_id":"6f9716d5-b989-487b-a298-6980997ce55f","task_id":"01a1368a-b7a0-78b0-bc2b-3a2bd0481276","task_stream":{"kind":"task","id":"01a1368a-b7a0-78b0-bc2b-3a2bd0481276"},"reminder_agent_id":"goal-reminder","generation_id":1,"child_session_id":"43b7956d-b5c5-422c-b9ca-80c98e8dff28","child_session_log_path":"subagent/43b7956d-b5c5-422c-b9ca-80c98e8dff28/session.jsonl"},"source_run_record_id":"dda5bc25-be97-468a-af23-b04881e89550","source_run_record_sequence":133}}""";

    const string SpawnLine =
        """{"schema_version":1,"id":"533c1cc9-29e6-586c-a278-1752d20fcc59","stream":{"kind":"session","id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e"},"sequence":280,"recorded_at":1791897216448194,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"subagent.control.spawn_accepted","payload_schema_version":4,"payload":{"kind":"subagent_control","record":{"kind":"spawn_accepted","command_id":"test-coverage-integrations-01a13563-1","parent_session_id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e","parent_run_id":"01a13565-88d0-72c0-ab37-cea217cfd40e","subagent_id":"01a13565-a5b7-7071-874a-8c22f461fc24","agent_path":"main/Test coverage auditor — Integrations group/1","depth":1,"role":"Test coverage auditor — Integrations group","task_id":"01a13565-a5a9-7330-a03b-3febeab2f755","task_stream":"task/01a13565-a5a9-7330-a03b-3febeab2f755","objective":"audit"}}}""";

    const string BoundLine =
        """{"schema_version":1,"id":"ed976e63-54a5-4471-a351-03225dc9a492","stream":{"kind":"session","id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e"},"sequence":288,"recorded_at":1791897216551211,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"subagent.control.child_session_bound","payload_schema_version":1,"payload":{"kind":"subagent_control","record":{"kind":"child_session_bound","schema_version":1,"subagent_id":"01a13565-a5b7-7071-874a-8c22f461fc24","child_session_id":"01a13565-a626-7f70-bda0-1c19ba4deadf","storage_layout":"nested_session_v1"}}}""";

    const string ReadyLine =
        """{"schema_version":1,"id":"9f4c7516-2542-48bd-80ac-d2ae8378ba16","stream":{"kind":"session","id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e"},"sequence":409,"record_type":"event","payload_type":"subagent.control.result_ready","payload_schema_version":2,"payload":{"kind":"subagent_control","record":{"kind":"result_ready","subagent_id":"01a13565-a5b7-7071-874a-8c22f461fc24","summary":"done","evidence_refs":[]}}}""";

    const string FrameLine =
        """{"retained_frame":"session_permission_transaction","frame_schema_version":1,"outer_log_ordinal":1,"transaction_id":"ebef68e5-70e2-5d1c-9e0b-4760eb780bc6","children":[],"content_sha256":"00"}""";

    const string MarkerLine =
        """{"retained_marker":"omitted_live_only","schema_version":1,"stream":{"kind":"session","id":"1d8166bf-0f96-48cf-b6ff-ee03c8b9e69c"},"position":{"id":"1f060ac0-0c9b-4914-80b7-12ed11d778e3","sequence":79}}""";

    [TempDir] public required TempDir Tmp { get; init; }

    string WriteParent(params string[] lines) {
        var dir = Path.Combine(Tmp.Path, ParentId);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "session.jsonl");
        File.WriteAllLines(log, lines);
        return log;
    }

    static void WriteChild(string parentLog, string childId) {
        var path = MusePaths.ChildSessionLogPath(parentLog, childId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}\n");
    }

    [Test]
    public async Task Enumerate_lists_only_child_logs_that_exist() {
        var parent = WriteParent(FrameLine);
        WriteChild(parent, ReminderId);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(parent)!, "subagent", WorkerId));
        WriteChild(parent, "not-a-guid");

        var logs = MuseSubagentDiscovery.EnumerateChildLogs(parent);

        await Assert.That(logs).IsEquivalentTo(new[] { MusePaths.ChildSessionLogPath(parent, ReminderId) });
    }

    [Test]
    public async Task Enumerate_is_empty_without_a_subagent_directory() {
        var parent = WriteParent(FrameLine);
        await Assert.That(MuseSubagentDiscovery.EnumerateChildLogs(parent)).IsEmpty();
        await Assert.That(MuseSubagentDiscovery.EnumerateChildLogs(Path.Combine(Tmp.Path, "missing", "session.jsonl"))).IsEmpty();
    }

    [Test]
    public async Task A_reminder_child_is_labelled_with_its_reminder_agent_id() {
        var parent = WriteParent(FrameLine, ReminderLine, MarkerLine);

        var labels = MuseSubagentDiscovery.ResolveChildLabels(parent);

        var label = labels[ReminderId];
        await Assert.That(label.ChildSessionId).IsEqualTo(ReminderId);
        await Assert.That(label.AgentId).IsEqualTo("43b7956db5c5422cb9ca80c98e8dff28");
        await Assert.That(label.AgentType).IsEqualTo("goal-reminder");
    }

    [Test]
    public async Task A_worker_child_is_labelled_with_its_role() {
        var parent = WriteParent(SpawnLine, BoundLine);

        var label = MuseSubagentDiscovery.ResolveChildLabels(parent)[WorkerId];

        await Assert.That(label.AgentId).IsEqualTo("01a13565a6267f70bda01c19ba4deadf");
        await Assert.That(label.AgentType).IsEqualTo("Test coverage auditor — Integrations group");
    }

    [Test]
    public async Task A_worker_pair_resolves_whichever_record_comes_first() {
        var parent = WriteParent(BoundLine, SpawnLine);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)[WorkerId].AgentType)
            .IsEqualTo("Test coverage auditor — Integrations group");
    }

    [Test]
    public async Task A_worker_without_a_role_falls_back_to_agent_path_then_worker() {
        var noRole = SpawnLine.Replace("\"role\":\"Test coverage auditor — Integrations group\",", "");
        var parent = WriteParent(noRole, BoundLine);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)[WorkerId].AgentType)
            .IsEqualTo("main/Test coverage auditor — Integrations group/1");

        var bare = SpawnLine.Replace("\"role\":\"Test coverage auditor — Integrations group\",", "")
            .Replace("\"agent_path\":\"main/Test coverage auditor — Integrations group/1\",", "");
        parent = WriteParent(bare, BoundLine);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)[WorkerId].AgentType).IsEqualTo("worker");
    }

    [Test]
    public async Task A_bound_child_with_no_spawn_record_is_a_worker() {
        var parent = WriteParent(BoundLine);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)[WorkerId].AgentType).IsEqualTo("worker");
    }

    [Test]
    public async Task Malformed_and_partial_lines_are_skipped() {
        var parent = WriteParent("{not json", ReminderLine, "", BoundLine.Substring(0, 90));
        var labels = MuseSubagentDiscovery.ResolveChildLabels(parent);
        await Assert.That(labels.Keys).IsEquivalentTo(new[] { ReminderId });
    }

    [Test]
    public async Task A_child_session_id_that_is_not_a_guid_is_ignored() {
        var hostile = ReminderLine.Replace(ReminderId, "../../etc");
        var parent  = WriteParent(hostile, SpawnLine, BoundLine.Replace(WorkerId, "../x"));
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)).IsEmpty();
    }

    [Test]
    public async Task No_announcements_yield_the_shared_empty_dictionary() {
        var parent = WriteParent(FrameLine, MarkerLine);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent)).IsSameReferenceAs(FrozenDictionary<string, MuseChildLabel>.Empty);
        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(Path.Combine(Tmp.Path, "gone.jsonl")))
            .IsSameReferenceAs(FrozenDictionary<string, MuseChildLabel>.Empty);
    }

    [Test]
    public async Task A_log_held_open_for_append_can_still_be_read() {
        var parent = WriteParent();
        await using var writer = new FileStream(parent, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var sw     = new StreamWriter(writer) { AutoFlush = true };
        await sw.WriteLineAsync(ReminderLine);

        await Assert.That(MuseSubagentDiscovery.ResolveChildLabels(parent).ContainsKey(ReminderId)).IsTrue();
    }

    [Test]
    public async Task Label_for_an_unknown_child_is_a_dashless_subagent() {
        var label = MuseSubagentDiscovery.LabelFor(ReminderId, FrozenDictionary<string, MuseChildLabel>.Empty);

        await Assert.That(label.AgentType).IsEqualTo("subagent");
        await Assert.That(label.AgentId).IsEqualTo(Guid.Parse(ReminderId).ToString("N"));
        await Assert.That(label.ChildSessionId).IsEqualTo(ReminderId);
    }

    [Test]
    public async Task Label_for_a_known_child_is_found_by_either_guid_spelling() {
        var parent = WriteParent(ReminderLine);
        var labels = MuseSubagentDiscovery.ResolveChildLabels(parent);

        await Assert.That(MuseSubagentDiscovery.LabelFor(ReminderId, labels).AgentType).IsEqualTo("goal-reminder");
        await Assert.That(MuseSubagentDiscovery.LabelFor(Guid.Parse(ReminderId).ToString("N"), labels).AgentType).IsEqualTo("goal-reminder");
    }

    [Test]
    public async Task Label_for_a_non_guid_never_carries_path_characters() {
        var label = MuseSubagentDiscovery.LabelFor("../../etc/passwd", FrozenDictionary<string, MuseChildLabel>.Empty);

        await Assert.That(label.AgentType).IsEqualTo("subagent");
        await Assert.That(label.AgentId).IsEqualTo("etcpasswd");
    }

    [Test]
    public async Task Start_payload_has_the_claude_shaped_subagent_start_keys() {
        var p = MuseSubagentDiscovery.BuildStartPayload(ParentId, "aid", "goal-reminder", "/x/session.jsonl");

        await Assert.That(p.Select(kv => kv.Key).ToList()).IsEquivalentTo(new[] { "hook_event_name", "session_id", "agent_id", "agent_type", "transcript_path", "cwd" });
        await Assert.That(p["hook_event_name"]!.GetValue<string>()).IsEqualTo("subagent_start");
        await Assert.That(p["session_id"]!.GetValue<string>()).IsEqualTo(ParentId);
        await Assert.That(p["agent_id"]!.GetValue<string>()).IsEqualTo("aid");
        await Assert.That(p["agent_type"]!.GetValue<string>()).IsEqualTo("goal-reminder");
        await Assert.That(p["transcript_path"]!.GetValue<string>()).IsEqualTo("/x/session.jsonl");
        await Assert.That(p["cwd"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task Stop_payload_has_the_claude_shaped_subagent_stop_keys() {
        var p = MuseSubagentDiscovery.BuildStopPayload(ParentId, "aid", "goal-reminder", "/x/session.jsonl");

        await Assert.That(p.Select(kv => kv.Key).ToList()).IsEquivalentTo(new[] {
            "hook_event_name", "session_id", "agent_id", "agent_type", "transcript_path", "cwd",
            "stop_hook_active", "agent_transcript_path", "last_assistant_message",
        });
        await Assert.That(p["hook_event_name"]!.GetValue<string>()).IsEqualTo("subagent_stop");
        await Assert.That(p["stop_hook_active"]!.GetValue<bool>()).IsFalse();
        await Assert.That(p["agent_transcript_path"]!.GetValue<string>()).IsEqualTo("/x/session.jsonl");
        await Assert.That(p["last_assistant_message"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    [Arguments("skill-reminder", true)]
    [Arguments("goal-reminder", true)]
    [Arguments("", true)]
    [Arguments("43b7956d-b5c5-422c-b9ca-80c98e8dff28", false)]
    [Arguments("01a13565-a5b7-7071-874a-8c22f461fc24", false)]
    public async Task A_subagent_id_that_is_not_a_guid_names_a_reminder(string id, bool reminder) {
        await Assert.That(MuseSubagentDiscovery.IsReminderAgentId(id)).IsEqualTo(reminder);
    }

    [Test]
    public async Task A_worker_whose_result_is_ready_is_counted_once_per_result() {
        var once  = MuseSubagentDiscovery.ResolveResultReadyCounts(WriteParent(SpawnLine, BoundLine, ReadyLine));
        var twice = MuseSubagentDiscovery.ResolveResultReadyCounts(WriteParent(SpawnLine, BoundLine, ReadyLine, ReadyLine));

        await Assert.That(once).IsEquivalentTo(new Dictionary<string, int> { [WorkerId] = 1 });
        await Assert.That(twice[WorkerId]).IsEqualTo(2);
    }

    [Test]
    public async Task A_worker_without_a_ready_result_or_a_binding_has_no_count() {
        await Assert.That(MuseSubagentDiscovery.ResolveResultReadyCounts(WriteParent(SpawnLine, BoundLine))).IsEmpty();
        await Assert.That(MuseSubagentDiscovery.ResolveResultReadyCounts(WriteParent(ReadyLine))).IsEmpty();
        await Assert.That(MuseSubagentDiscovery.ResolveResultReadyCounts(Path.Combine(Tmp.Path, "missing", "session.jsonl"))).IsEmpty();
    }
}
