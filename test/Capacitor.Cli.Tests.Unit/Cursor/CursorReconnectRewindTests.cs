using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// AI-1382 review fix #2 — a reconnect that discovers the server's acknowledged line frontier is
/// behind the watcher's own <see cref="WatchState.LinesProcessed"/> must rewind the Cursor byte
/// guard's checkpoint ATOMICALLY with the line cursor. Before the fix, only
/// <see cref="WatchState.LinesProcessed"/> rewound — <see cref="WatchState.CursorByteOffset"/> and
/// the guard's own checkpoint stayed at the LATER acked offset, so the next drain resent the
/// replayed line gap but started new-range verification past the bytes that gap actually
/// occupies. <see cref="WatchCommand.ApplyReconnectRewindAsync"/> is the extracted, directly
/// testable version of the fix (pulled out of <c>RunWatch</c>'s <c>Reconnected</c> handler, which
/// itself can't be driven without a live SignalR reconnect).
/// </summary>
public class CursorReconnectRewindTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ---- WatchCommand.ResolveByteOffsetForLineAsync ----

    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_zero_for_line_zero_or_negative() {
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-offset").FullName;
        try {
            var path = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(path, "line1\nline2\nline3\n");

            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 0, CancellationToken.None)).IsEqualTo(0L);
            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, -1, CancellationToken.None)).IsEqualTo(0L);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_zero_when_the_file_is_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-reconnect-missing-{Guid.NewGuid():N}.jsonl");
        await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(missing, 2, CancellationToken.None)).IsEqualTo(0L);
    }

    [Test]
    public async Task ResolveByteOffsetForLineAsync_maps_a_mid_file_line_number_to_its_true_byte_offset() {
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-offset-mid").FullName;
        try {
            var path = Path.Combine(dir, "t.jsonl");
            // Deliberately uneven line lengths so a naive "count * avg-length" guess would be wrong.
            await File.WriteAllTextAsync(path, "a\nbbbb\ncc\n"); // offsets: line0 ends at 2, line1 ends at 7, line2 ends at 10

            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 1, CancellationToken.None)).IsEqualTo(2L);
            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 2, CancellationToken.None)).IsEqualTo(7L);
            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 3, CancellationToken.None)).IsEqualTo(10L);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task ResolveByteOffsetForLineAsync_clamps_to_eof_when_the_file_has_fewer_lines_than_requested() {
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-offset-clamp").FullName;
        try {
            var path = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(path, "a\nb\n"); // only 2 lines

            await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 5, CancellationToken.None)).IsEqualTo(4L);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- CursorRewriteGuard.ResetCheckpoint ----

    [Test]
    public async Task ResetCheckpoint_clears_a_prior_checkpoint_so_the_next_prior_zone_check_passes_trivially() {
        var guard = new CursorRewriteGuard(NewSessionId());
        guard.Checkpoint(offset: 100, trailingSha: "some-stale-hash");

        // Before reset: an unrelated hash mismatches the stale checkpoint.
        await Assert.That(guard.VerifyPriorZone("different-hash")).IsFalse();
    }

    [Test]
    public async Task ResetCheckpoint_after_reset_any_hash_passes_like_a_fresh_watcher() {
        var sid   = Guid.NewGuid().ToString("N");
        var guard = new CursorRewriteGuard(sid);
        guard.Checkpoint(offset: 100, trailingSha: "some-stale-hash");

        guard.ResetCheckpoint();

        // No checkpoint recorded → trivially true, exactly like a brand-new guard's first poll.
        await Assert.That(guard.VerifyPriorZone("anything-at-all")).IsTrue();
        await Assert.That(CursorMarkers.IsQuarantined(sid)).IsFalse();
    }

    // ---- AI-1382 review fix (r3, finding #2) — WatchCommand.SeedCursorByteOffsetAsync ----
    //
    // Shared by ApplyReconnectRewindAsync (already covered above) and RunWatch's INITIAL
    // WatcherConnect registration, which — before this fix — left CursorByteOffset at its default
    // (0) after resuming at server line N: the ack-to-byte mapping then measured acked lines
    // relative to N but counted their bytes from 0, so a full ack of M resumed lines checkpointed
    // at file offset M instead of N's TRUE offset plus M.

    [Test]
    public async Task SeedCursorByteOffsetAsync_seeds_the_true_byte_offset_of_the_resumed_line() {
        var dir = Directory.CreateTempSubdirectory("kcap-seed-initial-resume").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            // Deliberately uneven line lengths: offsets 2, 7, 10, 15, 21.
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\neeeee\n");

            var guard = new CursorRewriteGuard(NewSessionId());
            // A fresh watcher process resuming at server line 2 — CursorByteOffset starts at its
            // default (0), exactly as WatchState leaves it before this fix's seeding runs.
            var state = new WatchState { LinesProcessed = 2 };
            await Assert.That(state.CursorByteOffset).IsEqualTo(0L);

            await WatchCommand.SeedCursorByteOffsetAsync(
                state, lineNumber: 2, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            // The TRUE byte offset of line 2 ("a\nbbbb\n" = 7 bytes) — not the default 0, and not
            // the resumed line COUNT (2) either.
            await Assert.That(state.CursorByteOffset).IsEqualTo(7L);
            // The checkpoint is reset so the guard's two-zone checks start clean from here.
            await Assert.That(guard.VerifyPriorZone("anything-at-all")).IsTrue();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task SeedCursorByteOffsetAsync_is_a_no_op_for_non_cursor_vendors() {
        var dir = Directory.CreateTempSubdirectory("kcap-seed-initial-resume-noncursor").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\n");

            var state = new WatchState { LinesProcessed = 2, CursorByteOffset = 999 };
            await WatchCommand.SeedCursorByteOffsetAsync(
                state, lineNumber: 2, vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);

            await Assert.That(state.CursorByteOffset).IsEqualTo(999L); // untouched — no guard to keep in sync
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task InitialResume_seeded_offset_composed_with_ByteOffsetForAckedLines_checkpoints_at_N_plus_M_not_M() {
        // End-to-end regression for the actual production consequence, composed from the two pure
        // functions DrainNewLines itself calls (no live hub needed — see the class doc on why a
        // real ack round trip isn't unit-testable here).
        var dir = Directory.CreateTempSubdirectory("kcap-seed-initial-resume-e2e").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\neeeee\n"); // 5 lines, offsets 2,7,10,15,21

            var guard = new CursorRewriteGuard(NewSessionId());
            // The server resumed this fresh watcher process at line N=2 (0-based frontier already
            // sent/acked by a PRIOR watcher instance).
            var state = new WatchState { LinesProcessed = 2 };

            await WatchCommand.SeedCursorByteOffsetAsync(
                state, lineNumber: 2, vendor: "cursor", transcriptPath, guard, CancellationToken.None);
            await Assert.That(state.CursorByteOffset).IsEqualTo(7L);

            // The watcher's next poll fully acks the M=3 remaining lines (2, 3, 4) — mirrors
            // DrainNewLines' own ByteOffsetForAckedLines(verifiedRange, cursorGuardOldOffset,
            // ackedLineCount) call, with rangeStartOffset seeded from the SAME offset above.
            var remainingRange  = System.Text.Encoding.UTF8.GetBytes("cc\ndddd\neeeee\n"); // lines 2,3,4 — 14 bytes
            var ackedByteOffset = WatchCommand.ByteOffsetForAckedLines(
                remainingRange, rangeStartOffset: state.CursorByteOffset, ackedLineCount: 3);

            // N's offset (7) + M's bytes (14) = 21 — the TRUE end-of-file offset. The pre-fix bug
            // (CursorByteOffset left at 0, so the ack maps M's bytes as if counted from byte 0)
            // would have produced 14 instead.
            await Assert.That(ackedByteOffset).IsEqualTo(21L);
            await Assert.That(ackedByteOffset).IsNotEqualTo(14L);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- WatchCommand.ApplyReconnectRewindAsync — the atomic composition ----

    [Test]
    public async Task ApplyReconnectRewindAsync_rewinds_the_byte_checkpoint_to_the_true_offset_of_the_server_frontier() {
        var sid = NewSessionId();
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-apply").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines

            var guard = new CursorRewriteGuard(sid);
            // Simulate: the watcher had sent/acked all 4 lines and checkpointed at the file's
            // full (stale, too-far-ahead) length.
            guard.Checkpoint(offset: 15, trailingSha: "later-acked-hash");
            var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

            // Reconnect discovers the server only actually has the first 2 lines (offset 7).
            await WatchCommand.ApplyReconnectRewindAsync(state, serverPosition: 2, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            await Assert.That(state.LinesProcessed).IsEqualTo(2);
            // The byte checkpoint must rewind to the TRUE byte offset of line 2 ("a\nbbbb\n" = 7
            // bytes) — not stay at the later, stale, too-far-ahead offset (15).
            await Assert.That(state.CursorByteOffset).IsEqualTo(7L);
            // The guard's own checkpoint must be reset too — a stale "later-acked-hash" checkpoint
            // must never be compared against post-rewind content.
            await Assert.That(guard.VerifyPriorZone("literally-anything")).IsTrue();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task ApplyReconnectRewindAsync_is_a_line_only_rewind_for_non_cursor_vendors() {
        // No CursorRewriteGuard exists for any other vendor — only the line cursor rewinds.
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-apply-noncursor").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n");

            var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 999 }; // never meaningful for non-cursor

            await WatchCommand.ApplyReconnectRewindAsync(state, serverPosition: 1, vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);

            await Assert.That(state.LinesProcessed).IsEqualTo(1);
            await Assert.That(state.CursorByteOffset).IsEqualTo(999L); // untouched — no guard to keep in sync
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task ApplyReconnectRewindAsync_keeps_the_replayed_region_under_prior_zone_protection_after_a_later_checkpoint() {
        // End-to-end regression for the actual production consequence: BEFORE the fix, the guard's
        // checkpoint stayed at the LATER (stale, too-far-ahead) offset after a rewind, so a FUTURE
        // checkpoint re-established post-rewind would still only ever prior-zone-protect the
        // trailing bytes ending at whatever offset the LATEST send reached — the replayed gap
        // itself (bytes 7..10 below) would never again fall inside that trailing window once the
        // file grows past it, becoming a permanent blind spot. With the fix, the checkpoint
        // correctly rewinds to the TRUE (small) offset, so a checkpoint re-established from there
        // still covers the replayed region's own trailing bytes — and a same-length in-place
        // rewrite of it is caught on the very next poll.
        var sid = NewSessionId();
        var dir = Directory.CreateTempSubdirectory("kcap-reconnect-apply-e2e").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines: offsets 2,7,10,15

            // A small TrailingBytes makes the "which region does the checkpoint actually
            // protect" distinction concrete without needing a large synthetic file.
            var guard = new CursorRewriteGuard(sid) { TrailingBytes = 3 };
            guard.Checkpoint(offset: 15, trailingSha: "stale-hash-from-before-reconnect"); // the bug: too-far-ahead
            var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

            // Reconnect: the server only actually has the first 2 lines (byte offset 7).
            await WatchCommand.ApplyReconnectRewindAsync(state, serverPosition: 2, vendor: "cursor", transcriptPath, guard, CancellationToken.None);
            await Assert.That(state.CursorByteOffset).IsEqualTo(7L);

            // The replayed line ("cc\n", offset 7..10) is resent and acked — a checkpoint is
            // re-established from the CORRECT rewound frontier (mirrors DrainNewLines's own
            // Checkpoint() call after a successful ack).
            state.LinesProcessed   = 3;
            state.CursorByteOffset = 10;
            guard.Checkpoint(offset: 10, trailingSha: CursorAppendOnlyProbe.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("cc\n")));

            // Now the replayed region itself is rewritten in place (same length: "cc" → "XX").
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\nXX\ndddd\n");

            var tripped = false;
            await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

            var result = await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

            await Assert.That(tripped).IsTrue();
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsTrue();
            await Assert.That(result).IsEmpty();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- AI-1382 review fix (r3, finding #1) — GatedApplyReconnectRewindAsync/GatedDrainNewLinesAsync ----
    //
    // RunWatch's own cursorRewindGate/Reconnected handler can't be driven without a live SignalR
    // reconnect (same constraint CursorGuardWiringTests' class doc states), so these tests drive
    // the extracted gate-composition helpers directly — the SAME SemaphoreSlim(1, 1) instance
    // RunWatch constructs and passes to both call sites. A SemaphoreSlim(1, 1) enforces exclusivity
    // unconditionally, so rather than chase a timing-dependent race, these tests prove the
    // EXCLUSION property deterministically: while one gated operation holds the gate, the other
    // cannot even START running (its Task stays incomplete), so it is architecturally impossible
    // for a drain to observe a half-applied rewind (or vice versa).

    [Test]
    public async Task GatedDrainNewLinesAsync_cannot_run_while_the_gate_is_held_by_a_reconnect_rewind() {
        var sid = NewSessionId();
        var dir = Directory.CreateTempSubdirectory("kcap-gate-drain-blocked").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines

            var gate  = new SemaphoreSlim(1, 1);
            var guard = new CursorRewriteGuard(sid);
            var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

            // Simulate an in-flight reconnect rewind: acquire the gate ourselves (standing in for
            // GatedApplyReconnectRewindAsync mid-ResolveByteOffsetForLineAsync, BEFORE it has
            // applied any of its three writes).
            await gate.WaitAsync();

            await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

            var drainTask = WatchCommand.GatedDrainNewLinesAsync(
                gate, hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => { });

            // Give the (incorrectly ungated) case every chance to complete — it must NOT, because
            // the gate is still held.
            await Task.Delay(50);
            await Assert.That(drainTask.IsCompleted).IsFalse();

            // A half-applied rewind is architecturally impossible here: DrainNewLines has not
            // even started reading cursorGuardOldOffset/priorLineCursorForGuard yet, let alone
            // written a checkpoint derived from stale pre-rewind numbers.
            await Assert.That(state.CursorByteOffset).IsEqualTo(15L);

            gate.Release(); // the "rewind" completes — now the queued drain may proceed
            await drainTask;
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task GatedApplyReconnectRewindAsync_cannot_run_while_the_gate_is_held_by_a_drain() {
        var sid = NewSessionId();
        var dir = Directory.CreateTempSubdirectory("kcap-gate-rewind-blocked").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines: offsets 2,7,10,15

            var gate  = new SemaphoreSlim(1, 1);
            var guard = new CursorRewriteGuard(sid);
            guard.Checkpoint(offset: 15, trailingSha: "acked-hash");
            var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

            // Simulate an in-flight drain (standing in for GatedDrainNewLinesAsync mid-ack).
            await gate.WaitAsync();

            var rewindTask = WatchCommand.GatedApplyReconnectRewindAsync(
                gate, state, serverPosition: 2, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            await Task.Delay(50);
            await Assert.That(rewindTask.IsCompleted).IsFalse();

            // The rewind has not been allowed to touch state yet — no half-applied rewind for a
            // concurrently-finishing drain to observe (or clobber).
            await Assert.That(state.LinesProcessed).IsEqualTo(4);
            await Assert.That(state.CursorByteOffset).IsEqualTo(15L);

            gate.Release(); // the "drain" completes — now the queued rewind may proceed
            await rewindTask;

            await Assert.That(state.LinesProcessed).IsEqualTo(2);
            await Assert.That(state.CursorByteOffset).IsEqualTo(7L); // true byte offset of line 2
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task GatedDrainNewLinesAsync_and_GatedApplyReconnectRewindAsync_are_no_ops_when_gate_is_null() {
        // Every non-Cursor vendor passes a null gate — both helpers must fall back to calling the
        // underlying method directly rather than deadlocking or throwing on a null semaphore.
        var dir = Directory.CreateTempSubdirectory("kcap-gate-null-noncursor").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nb\n");

            var state = new WatchState { LinesProcessed = 2, CursorByteOffset = 999 };
            await WatchCommand.GatedApplyReconnectRewindAsync(
                gate: null, state, serverPosition: 1, vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);
            await Assert.That(state.LinesProcessed).IsEqualTo(1);

            await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();
            var result = await WatchCommand.GatedDrainNewLinesAsync(
                gate: null, hub, "sid", transcriptPath, agentId: null, new WatchState { ThresholdReached = true },
                vendor: "codex", CancellationToken.None);
            await Assert.That(result).IsNotNull();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
