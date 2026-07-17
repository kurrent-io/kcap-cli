using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// AI-1313 Phase B (D4 §6.4(2)): <see cref="AgentPidRecordStore"/> — atomic write/read/delete round-trip
/// (exact identity preserved) and corrupt-record quarantine.
/// </summary>
public class AgentPidRecordStoreTests {
    static string NewStateDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-pidrec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static AgentPidRecord Rec(string agentId, int pid = 123) =>
        new(agentId, pid, "lx:boot:999", "ReviewFlow", "codex", "flow-1", "reviewer", "daemon-id", "epoch-1", DateTimeOffset.UtcNow);

    [Test]
    public async Task Write_ReadAll_Delete_roundtrip_preserves_exact_identity() {
        var dir   = NewStateDir();
        var store = new AgentPidRecordStore(dir, NullLogger.Instance);

        store.Write(Rec("a1", pid: 4242));

        var all = store.ReadAll();
        await Assert.That(all).Count().IsEqualTo(1);
        await Assert.That(all[0].Pid).IsEqualTo(4242);
        await Assert.That(all[0].StartIdentity).IsEqualTo("lx:boot:999");
        await Assert.That(all[0].FlowRunId).IsEqualTo("flow-1");

        await Assert.That(store.Delete("a1")).IsTrue();
        await Assert.That(store.ReadAll()).IsEmpty();
        await Assert.That(store.Delete("a1")).IsFalse(); // idempotent
    }

    [Test]
    public async Task ReadAll_quarantines_a_corrupt_record_and_excludes_it() {
        var dir   = NewStateDir();
        var store = new AgentPidRecordStore(dir, NullLogger.Instance);
        store.Write(Rec("good"));

        var agentsDir = Path.Combine(dir, "agents");
        File.WriteAllText(Path.Combine(agentsDir, "bad.json"), "{ not valid json");

        var all = store.ReadAll();

        await Assert.That(all.Select(r => r.AgentId)).IsEquivalentTo(new[] { "good" });
        await Assert.That(File.Exists(Path.Combine(agentsDir, "bad.json.corrupt"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(agentsDir, "bad.json"))).IsFalse();
    }

    [Test]
    public async Task Write_is_atomic_overwrite_no_temp_files_left() {
        var dir   = NewStateDir();
        var store = new AgentPidRecordStore(dir, NullLogger.Instance);

        store.Write(Rec("a1", pid: 1));
        store.Write(Rec("a1", pid: 2)); // overwrite

        var all = store.ReadAll();
        await Assert.That(all).Count().IsEqualTo(1);
        await Assert.That(all[0].Pid).IsEqualTo(2);
        await Assert.That(Directory.EnumerateFiles(Path.Combine(dir, "agents"), "*.tmp-*")).IsEmpty();
    }
}
