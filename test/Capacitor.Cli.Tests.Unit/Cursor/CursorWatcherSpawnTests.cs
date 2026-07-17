using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// AI-1382 Task 9 — the precedence-ordered per-session Cursor watcher spawn.
/// <see cref="CursorHookCommand.ShouldSpawnWatcher"/> is pure (no I/O); the
/// <see cref="CursorHookCommand.MaybeSpawnWatcherAsync"/> tests use
/// <see cref="WatcherManager.SpawnOverrideForTesting"/> so no real OS process is ever spawned.
/// [NotInParallel] because the override is a shared static — a racing test elsewhere that also
/// sets it (there are none today, but WatcherManagerSpawnArgsTests reads BuildSpawnArgs only)
/// must never interleave with this class's use of the seam.
/// </summary>
[NotInParallel(nameof(CursorWatcherSpawnTests))]
public class CursorWatcherSpawnTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task SessionEnd_never_spawns() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: false)).IsFalse();

    [Test]
    public async Task SessionEnd_never_spawns_even_for_a_correlated_child() =>
        // Precedence ①: terminal beats everything, including a would-be child spawn.
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task Correlated_child_never_spawns_toplevel() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task NonTerminal_toplevel_spawns() {
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("afterAgentResponse", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("beforeSubmitPrompt", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("postToolUse", isSubagentChild: false)).IsTrue();
    }

    [Test]
    public async Task Spawn_is_suppressed_when_session_is_quarantined() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        CursorMarkers.Quarantine(sid, "test");
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/qsid.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_sessionEnd() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionEnd", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_a_correlated_child() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: true);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_when_transcript_path_is_empty() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task NonQuarantined_toplevel_spawn_invokes_the_watcher_manager_keyed_on_the_session_id() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await CursorHookCommand.MaybeSpawnWatcherAsync("http://s", sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEquivalentTo([sid]);
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }
}
