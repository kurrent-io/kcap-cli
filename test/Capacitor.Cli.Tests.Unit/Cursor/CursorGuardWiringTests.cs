using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// AI-1382 review fix #2 — regression tests for the Cursor runtime rewrite-guard WIRING inside
/// <see cref="WatchCommand.DrainNewLines"/> (as distinct from <see cref="CursorRewriteGuardTests"/>,
/// which pins the guard's own pure hash-comparison logic in isolation). Both scenarios here trip
/// the guard and return BEFORE <c>DrainNewLines</c> ever touches its <see cref="HubConnection"/>
/// argument, so an unconnected/never-started connection is sufficient — no live SignalR server
/// needed (mirrors the constraint <see cref="Integration.CursorTailingWatcherTests"/>'s class doc
/// already states for this project).
/// </summary>
public class CursorGuardWiringTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() =>
        new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    [Test]
    public async Task DrainNewLines_trips_on_a_shrink_even_though_the_previous_code_only_checked_growth() {
        // Before the fix, the guard block was gated entirely behind
        // `cursorGuardNewLength > cursorGuardOldOffset` — a file that SHRINKS below the last
        // checkpoint skipped the whole zone check (including the prior-zone re-hash) and sailed
        // through undetected.
        var sid  = NewSessionId();
        var dir  = Directory.CreateTempSubdirectory("kcap-cursor-guard-shrink").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "abc\n"); // 4 bytes — far shorter than the checkpoint below

            var guard = new CursorRewriteGuard(sid);
            var state = new WatchState { CursorByteOffset = 100 }; // a checkpoint from a much longer prior file

            var tripped = false;
            await using var hub = UnconnectedHub();

            var result = await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

            await Assert.That(tripped).IsTrue();
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsTrue();
            await Assert.That(result).IsEmpty();
            // The batch was discarded, not delivered — the byte checkpoint must be untouched.
            await Assert.That(state.CursorByteOffset).IsEqualTo(100L);
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task DrainNewLines_wires_the_periodic_full_prefix_rehash_and_trips_on_a_stale_seed_mismatch() {
        // CursorRewriteGuard.VerifyFullPrefix existed (D0) but nothing ever called it. Drive
        // DrainNewLines's poll counter to exactly the Nth poll (CursorFullPrefixVerifyEveryNPolls)
        // with a pre-seeded (deliberately wrong) prefix sample, so the wired-in periodic check —
        // not the two-zone checks, which pass trivially on a first/no-checkpoint poll — is what
        // must catch the mismatch.
        var sid  = NewSessionId();
        var dir  = Directory.CreateTempSubdirectory("kcap-cursor-guard-fullprefix").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

            var guard = new CursorRewriteGuard(sid);
            // Seed with a WRONG hash for the first 6 bytes — the seed call never validates
            // against `laterBytes` (nothing to compare against yet), so this is accepted as-is.
            var wrongSeed = new CursorAppendOnlyProbe.Sample(6, CursorAppendOnlyProbe.Sha256Hex("WRONGX"u8));
            await Assert.That(guard.VerifyFullPrefix(wrongSeed, "WRONGX"u8)).IsTrue(); // seeds, doesn't validate

            var state = new WatchState {
                CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 1, // this poll lands on N
            };

            var tripped = false;
            await using var hub = UnconnectedHub();

            var result = await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

            await Assert.That(tripped).IsTrue();
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsTrue();
            await Assert.That(result).IsEmpty();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    public async Task DrainNewLines_does_not_run_the_periodic_full_prefix_check_before_the_nth_poll() {
        // Regression guard for the cadence itself: one poll short of N must NOT trigger the
        // (expensive) full re-read/re-hash, even with a seeded mismatch waiting — otherwise the
        // cadence constant is meaningless and every poll pays the full-file cost.
        var sid  = NewSessionId();
        var dir  = Directory.CreateTempSubdirectory("kcap-cursor-guard-fullprefix-precadence").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

            var guard = new CursorRewriteGuard(sid);
            var wrongSeed = new CursorAppendOnlyProbe.Sample(6, CursorAppendOnlyProbe.Sha256Hex("WRONGX"u8));
            await Assert.That(guard.VerifyFullPrefix(wrongSeed, "WRONGX"u8)).IsTrue();

            var state = new WatchState {
                CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 2, // one short of N
            };

            var tripped = false;
            await using var hub = UnconnectedHub();

            // Below threshold (only 3 lines, all buffered) — the call returns without ever
            // reaching the hub regardless, so this stays offline-safe.
            await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

            await Assert.That(tripped).IsFalse();
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // AI-1382 review fix #3 — VerifyFullPrefix's only production call site (the periodic cadence
    // check) used to seed lazily on its OWN first invocation, which — before this fix — never
    // happened until poll N (CursorFullPrefixVerifyEveryNPolls). The two OTHER full-prefix tests
    // above manually pre-seed the guard via a direct guard.VerifyFullPrefix(...) call to isolate
    // the cadence itself, which is exactly why they do NOT cover this production seeding gap: a
    // same-length rewrite of an already-checkpointed MIDDLE region landing anywhere in polls
    // 1..N-1 had no real baseline to be caught against, and poll N would just seed the
    // ALREADY-rewritten file as if it were the original, valid one. This test drives a REAL
    // poll 1 (seeding from the file's actual, un-rewritten content via DrainNewLines itself,
    // never manually pre-seeding the guard), rewrites the file afterward, then fast-forwards to
    // poll N and confirms the rewrite is still caught.
    [Test]
    public async Task DrainNewLines_seeds_the_full_prefix_sample_on_the_real_first_poll_and_catches_a_mid_region_rewrite_at_the_cadence_poll() {
        var sid = NewSessionId();
        var dir = Directory.CreateTempSubdirectory("kcap-cursor-guard-startup-seed").FullName;
        try {
            var transcriptPath = Path.Combine(dir, "t.jsonl");
            // Below WatchState.TranscriptThreshold (10) so every poll returns before ever
            // touching the unconnected hub.
            await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

            var guard = new CursorRewriteGuard(sid);
            var state = new WatchState();

            var trippedOnFirstPoll = false;
            await using var hub = UnconnectedHub();

            // Poll 1 — the REAL production seed (no manual guard.VerifyFullPrefix call anywhere).
            await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => trippedOnFirstPoll = true);

            await Assert.That(trippedOnFirstPoll).IsFalse();
            await Assert.That(state.CursorGuardPollCount).IsEqualTo(1);
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsFalse();

            // Rewrite an already-"seen" MIDDLE region — same total length, first line changed —
            // entirely outside the two-zone tail (nothing has ever been acked/checkpointed here;
            // the session stays below threshold the whole test, so the periodic full-prefix
            // re-hash is the ONLY check that can ever see this region).
            await File.WriteAllTextAsync(transcriptPath, "lineX\nline2\nline3\n");

            // Fast-forward to one poll before the cadence — mirrors the existing cadence tests'
            // technique of setting CursorGuardPollCount directly rather than looping 58 real polls.
            state.CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 1;

            var tripped = false;
            var result = await WatchCommand.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

            await Assert.That(tripped).IsTrue();
            await Assert.That(CursorMarkers.IsQuarantined(sid)).IsTrue();
            await Assert.That(result).IsEmpty();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
