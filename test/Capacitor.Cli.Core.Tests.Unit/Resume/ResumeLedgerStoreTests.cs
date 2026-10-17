using System.Diagnostics.CodeAnalysis;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;

[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:Capacitor.Cli.Core.Tests.Unit.Resume", Justification = "The namespace mirrors the Resume directory it tests.")]

namespace Capacitor.Cli.Core.Tests.Unit.Resume;

public class ResumeLedgerStoreTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    static ResumeLedgerEntry Entry(string agentId, int schema = ResumeLedgerEntry.CurrentSchemaVersion) => new(
        schema, agentId, "daemon-1", T0, T0, "claude", "pty", "session-1", "vendor-1",
        new ResumeLaunchDescriptor(
            "do it", "opus", "high", "/repo", "/repo/wt", WorkLocation.OwnedWorktree, "sub/dir", "sandbox", "never",
            "preset", "/mcp.json", "title", true, "server", "user-1", "User One", "owner", "2026-10-17T08:00:00Z",
            "flow-1", "reviewer"),
        ResumeState.Running, 2, "err", "pred-1", T0.AddMinutes(1));

    [Test]
    public async Task Write_then_TryRead_round_trips_every_field() {
        using var tmp   = new TempDaemonStore();
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        var       entry = Entry("agent-1");

        store.Write(entry);

        await Assert.That(store.TryRead("agent-1")).IsEqualTo(entry);
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
        var       store = ResumeLedgerStore.For(tmp.Store, "d1");
        store.Write(Entry("agent-1"));
        store.Write(Entry("agent-2"));

        var changed = store.MarkInterrupted("boom", e => e.AgentId == "agent-1");

        var one = store.TryRead("agent-1")!;
        var two = store.TryRead("agent-2")!;
        await Assert.That(changed).IsEqualTo(1);
        await Assert.That(one.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(one.LastError).IsEqualTo("boom");
        await Assert.That(one.UpdatedAt > T0).IsTrue();
        await Assert.That(two).IsEqualTo(Entry("agent-2"));
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
}
