using System.Diagnostics.CodeAnalysis;
using Capacitor.Cli.Core.LocalIpc;
using System.Runtime.Versioning;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Time.Testing;

[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:Capacitor.Cli.Core.Tests.Unit.Resume", Justification = "The namespace mirrors the Resume directory it tests.")]

namespace Capacitor.Cli.Core.Tests.Unit.Resume;

public class ResumeLedgerStoreTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    static ResumeLedgerEntry Entry(string agentId, int schema = ResumeLedgerEntry.CurrentSchemaVersion) => new(
        schema, agentId, "daemon-1", T0, T0, "claude", "pty", "session-1", "vendor-1",
        new ResumeLaunchDescriptor(
            "do it", "opus", "high", "/repo", "/repo/wt", WorkLocation.OwnedWorktree, "sub/dir", "sandbox", "never",
            "preset", "/mcp.json", "title", true, "server", "user-1", "User One", "owner", "2026-10-17T08:00:00Z",
            "flow-1", "reviewer", WorktreeBranch: "capacitor/wt", WorktreeIsStandalone: false, WorktreeSourceRepo: "/repo",
            PermissionMode: "plan", InactivityBoundSeconds: 600,
            AccountDirectory: "/home/u/.claude-work", AccountId: "a1b2", WorktreeOwnsBranch: false, AccountLabel: "Work"),
        ResumeState.Running, 2, "err", "pred-1", T0.AddMinutes(1), DaemonInstanceId: "instance-1", AwaitingInput: true);

    [Test]
    public async Task Write_then_TryRead_round_trips_every_field() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        var       entry = Entry("agent-1");

        store.Write(entry);

        await Assert.That(store.TryRead("agent-1")).IsEqualTo(entry);
    }

    [Test]
    public async Task Inherited_agent_ids_round_trip_as_a_list() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");

        store.Write(Entry("agent-2") with { InheritedAgentIds = ["agent-0", "agent-1"] });

        await Assert.That(store.TryRead("agent-2")!.InheritedAgentIds).IsEquivalentTo(["agent-0", "agent-1"]);
    }

    [Test]
    public async Task An_entry_written_before_the_instance_worktree_permission_and_waiting_fields_reads_them_as_null() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));
        var path = store.EntryPath("agent-1");
        var json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path),
            ",\"(daemon_instance_id|worktree_branch|worktree_is_standalone|worktree_source_repo|permission_mode|inactivity_bound_seconds|awaiting_input|account_directory|account_id|worktree_owns_branch|account_label)\":(\"[^\"]*\"|true|false|[0-9]+)", "");
        await Assert.That(json).DoesNotContain("worktree_branch");
        await Assert.That(json).DoesNotContain("permission_mode");
        await Assert.That(json).DoesNotContain("inactivity_bound_seconds");
        await Assert.That(json).DoesNotContain("account_id");
        await Assert.That(json).DoesNotContain("awaiting_input");
        File.WriteAllText(path, json);

        var read = store.TryRead("agent-1")!;

        await Assert.That(read.DaemonInstanceId).IsNull();
        await Assert.That(read.Launch.WorktreeBranch).IsNull();
        await Assert.That(read.Launch.WorktreeIsStandalone).IsNull();
        await Assert.That(read.Launch.WorktreeSourceRepo).IsNull();
        await Assert.That(read.Launch.PermissionMode).IsNull();
        await Assert.That(read.Launch.InactivityBoundSeconds).IsNull();
        await Assert.That(read.AwaitingInput).IsNull();
        await Assert.That(read.Launch.AccountDirectory).IsNull();
        await Assert.That(read.Launch.AccountId).IsNull();
        await Assert.That(read.Launch.WorktreeOwnsBranch).IsNull();
        await Assert.That(read.Launch.AccountLabel).IsNull();
    }

    [Test]
    public async Task Write_persists_enums_as_strings() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));

        var json = File.ReadAllText(store.EntryPath("agent-1"));

        await Assert.That(json).Contains("\"state\":\"running\"");
        await Assert.That(json).Contains("\"work\":\"OwnedWorktree\"");
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_keeps_the_directory_and_entry_owner_only() {
        Skip.When(OperatingSystem.IsWindows(), "Unix file modes only");

        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));

        await Assert.That(File.GetUnixFileMode(tmp.Store.ResumeDirectory("d1")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(File.GetUnixFileMode(store.EntryPath("agent-1")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Delete_removes_entry_and_ring_and_reports_absence() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));
        File.WriteAllText(store.RingPath("agent-1"), "ring");

        await Assert.That(store.Delete("agent-1")).IsTrue();
        await Assert.That(store.Delete("agent-1")).IsFalse();
        await Assert.That(File.Exists(store.EntryPath("agent-1"))).IsFalse();
        await Assert.That(File.Exists(store.RingPath("agent-1"))).IsFalse();
    }

    /// <summary>Every holder of an entry's lock must lock the same file; a delete that unlinked it would let
    /// a later opener lock a fresh file while an earlier holder still holds the old one.</summary>
    [Test]
    public async Task Delete_keeps_the_entry_lock_file() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));

        store.Delete("agent-1");

        await Assert.That(File.Exists(ResumeEntryLock.PathFor(store.EntryPath("agent-1")))).IsTrue();
    }

    /// <summary>A delete waits out a locked update in progress, so the update cannot write back the
    /// entry the delete removed.</summary>
    [Test]
    public async Task Delete_waits_for_a_locked_update_and_leaves_no_entry() {
        using var tmp     = new TempDaemonStore();
        var       store   = ResumeLedgerStore.For(tmp.Store, "d1");
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.Write(Entry("agent-1"));

        var update = Task.Run(() => store.TryUpdateUnderLock("agent-1", entry => {
            reading.Set();
            release.Wait();
            return entry with { LastError = "stamped" };
        }, TimeSpan.FromSeconds(5)));
        reading.Wait();
        var delete = Task.Run(() => store.Delete("agent-1"));
        await Task.Delay(100);
        release.Set();
        await Task.WhenAll(update, delete);

        await Assert.That(store.TryRead("agent-1")).IsNull();
    }

    [Test]
    public async Task TryRead_quarantines_an_unparseable_file() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        var       stem  = ResumeLedgerStore.FileStem("agent-1");
        tmp.CreateFile($"d1/resume/{stem}.json", "{not json");

        await Assert.That(store.TryRead("agent-1")).IsNull();
        await Assert.That(File.Exists(store.EntryPath("agent-1") + ".corrupt")).IsTrue();
    }

    [Test]
    public async Task ReadAll_ignores_unknown_members() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));
        var path = store.EntryPath("agent-1");
        File.WriteAllText(path, File.ReadAllText(path).Replace("{", "{\"future\":1,", StringComparison.Ordinal));

        var all = store.ReadAll();

        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0]).IsEqualTo(Entry("agent-1"));
    }

    [Test]
    public async Task Newer_schema_reads_as_interrupted_with_reason() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1", schema: 2));

        var read = store.TryRead("agent-1");

        await Assert.That(read!.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(read.LastError).IsEqualTo("ledger_newer_than_daemon");
    }

    [Test]
    public async Task MarkInterrupted_rewrites_matching_entries_only() {
        using var tmp   = new TempDaemonStore();
        var       clock = new FakeTimeProvider(T0.AddHours(1));
        var       store = ResumeLedgerStore.For(tmp.Store, "d1", clock);
        store.Write(Entry("agent-1"));
        store.Write(Entry("agent-2"));

        var changed = store.MarkInterrupted("boom", e => e.AgentId == "agent-1");

        var one = store.TryRead("agent-1")!;
        var two = store.TryRead("agent-2")!;
        await Assert.That(changed).IsEqualTo(1);
        await Assert.That(one.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(one.LastError).IsEqualTo("boom");
        await Assert.That(one.UpdatedAt).IsEqualTo(T0.AddHours(1));
        await Assert.That(two).IsEqualTo(Entry("agent-2"));
    }

    [Test]
    public async Task MarkInterrupted_keeps_the_reason_of_an_entry_already_interrupted() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1", new FakeTimeProvider(T0.AddHours(1)));
        var       entry = Entry("agent-1") with { State = ResumeState.Interrupted, LastError = "too_old" };
        store.Write(entry);

        var changed = store.MarkInterrupted("stopped_by_operator");

        await Assert.That(changed).IsEqualTo(0);
        await Assert.That(store.TryRead("agent-1")).IsEqualTo(entry);
    }

    [Test]
    public async Task MarkInterrupted_leaves_a_newer_schema_file_untouched() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1", new FakeTimeProvider(T0.AddHours(1)));
        store.Write(Entry("agent-1", schema: ResumeLedgerEntry.CurrentSchemaVersion + 1));
        var before = await File.ReadAllBytesAsync(store.EntryPath("agent-1"));

        var changed = store.MarkInterrupted("stopped_by_operator");

        await Assert.That(changed).IsEqualTo(0);
        await Assert.That(await File.ReadAllBytesAsync(store.EntryPath("agent-1"))).IsEquivalentTo(before);
    }

    [Test]
    public async Task FindByAgentId_scans_every_daemon_directory() {
        using var tmp = new TempDaemonStore();
        tmp.CreateFile($"first/resume/{ResumeLedgerStore.FileStem("agent-1")}.json", "{not json");
        ResumeLedgerStore.For(tmp.Store, "second").Write(Entry("agent-1"));

        var found = ResumeLedgerStore.FindByAgentId(tmp.Store, "agent-1");

        await Assert.That(found).IsEqualTo(Entry("agent-1"));
        await Assert.That(ResumeLedgerStore.FindByAgentId(tmp.Store, "missing")).IsNull();
    }

    [Test]
    public async Task Participant_entries_are_invisible_to_the_interactive_store() {
        using var tmp = new TempDaemonStore();

        ResumeLedgerStore.ParticipantsFor(tmp.Store, "d1").Write(Entry("agent-1"));

        await Assert.That(ResumeLedgerStore.For(tmp.Store, "d1").ReadAll()).IsEmpty();
        await Assert.That(ResumeLedgerStore.ParticipantsFor(tmp.Store, "d1").ReadAll().Select(e => e.AgentId)).IsEquivalentTo(["agent-1"]);
    }

    [Test]
    public async Task FindByAgentId_sees_a_participant_entry() {
        using var tmp = new TempDaemonStore();

        ResumeLedgerStore.ParticipantsFor(tmp.Store, "d1").Write(Entry("agent-1"));

        await Assert.That(ResumeLedgerStore.FindByAgentId(tmp.Store, "agent-1")?.AgentId).IsEqualTo("agent-1");
    }

    [Test]
    public async Task Participant_and_fence_fields_round_trip() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.ParticipantsFor(tmp.Store, "d1");
        var certification = new ReviewerCertificationRequirement("claude", ">=2.1.0", "claude-v3", "rev-1", "", "");
        var participant = new ParticipantResume(
            LaunchKind.ReviewFlow, ["kcap-flow-result"], ["Read"], certification, 42, 90_000, "round 2: look again", T0.AddMinutes(3));

        store.Write(Entry("agent-1") with {
            Participant = participant, RunEpoch = "epoch-2", LaunchEpoch = "epoch-1",
            EndCommittedAt = T0.AddMinutes(4), EndCommittedEpoch = "epoch-2"
        });

        var read = store.TryRead("agent-1")!;

        await Assert.That(read.RunEpoch).IsEqualTo("epoch-2");
        await Assert.That(read.LaunchEpoch).IsEqualTo("epoch-1");
        await Assert.That(read.EndCommittedAt).IsEqualTo(T0.AddMinutes(4));
        await Assert.That(read.EndCommittedEpoch).IsEqualTo("epoch-2");
        await Assert.That(read.Participant!.Kind).IsEqualTo(LaunchKind.ReviewFlow);
        await Assert.That(read.Participant.McpAllowlist!).IsEquivalentTo(["kcap-flow-result"]);
        await Assert.That(read.Participant.Tools!).IsEquivalentTo(["Read"]);
        await Assert.That(read.Participant.Certification).IsEqualTo(certification);
        await Assert.That(read.Participant.ActivitySeq).IsEqualTo(42UL);
        await Assert.That(read.Participant.AgeMs).IsEqualTo(90_000UL);
        await Assert.That(read.Participant.LastRoundInput).IsEqualTo("round 2: look again");
        await Assert.That(read.Participant.LastRoundAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task An_entry_written_without_the_participant_and_fence_fields_reads_them_as_null() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");

        store.Write(Entry("agent-1"));
        var json = File.ReadAllText(store.EntryPath("agent-1"));
        await Assert.That(json).DoesNotContain("participant\":{");

        var read = store.TryRead("agent-1")!;

        await Assert.That(read.Participant).IsNull();
        await Assert.That(read.RunEpoch).IsNull();
        await Assert.That(read.EndCommittedAt).IsNull();
    }

    [Test]
    public async Task TryUpdateUnderLock_rewrites_the_entry() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));

        var result = store.TryUpdateUnderLock("agent-1", e => e with { RunEpoch = "epoch-9" }, TimeSpan.FromSeconds(1));

        await Assert.That(result).IsEqualTo(LockedUpdate.Written);
        await Assert.That(store.TryRead("agent-1")!.RunEpoch).IsEqualTo("epoch-9");
    }

    [Test]
    public async Task TryUpdateUnderLock_times_out_while_another_holder_has_the_lock() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));
        using var held = ResumeEntryLock.TryAcquire(store.EntryPath("agent-1"), TimeSpan.FromSeconds(1));

        var result = store.TryUpdateUnderLock("agent-1", e => e with { RunEpoch = "epoch-9" }, TimeSpan.FromMilliseconds(50));

        await Assert.That(held).IsNotNull();
        await Assert.That(result).IsEqualTo(LockedUpdate.LockTimeout);
        await Assert.That(store.TryRead("agent-1")!.RunEpoch).IsNull();
    }

    [Test]
    public async Task TryUpdateUnderLock_reports_a_missing_entry_and_a_declined_change() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));

        await Assert.That(store.TryUpdateUnderLock("missing", e => e, TimeSpan.FromSeconds(1))).IsEqualTo(LockedUpdate.NoEntry);
        await Assert.That(store.TryUpdateUnderLock("agent-1", _ => null, TimeSpan.FromSeconds(1))).IsEqualTo(LockedUpdate.Unchanged);
    }
}
