using System.Globalization;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B (D4 §6.4(3)): reaps hosted-agent children that OUTLIVED the daemon that spawned
/// them — the crash/restart case where the in-memory registry is empty but a review-flow reviewer (or
/// any hosted child) is still running and holding a slot. Runs once at boot under the daemon lock (next
/// to <c>WorktreeManager.CleanupOrphanedAsync</c>) and again on a heartbeat tick. Two passes, in order:
/// <list type="number">
/// <item><b>Record pass</b> — every durable PID record this daemon left behind: if a live process still
/// matches its EXACT <c>(pid, start-identity)</c> (and, on Unix, still carries the expected
/// <c>KCAP_AGENT_ID</c> env), kill it (killpg→SIGKILL) and delete the record on confirmed death. A
/// proven identity mismatch (PID reused) deletes the stale record. An unreadable env (macOS 26 redacts
/// it) SPARES the process and retains the record — ambiguity never kills.</item>
/// <item><b>Env-marker scan</b> (Unix) — a recordless survivor is found by reading its OWN env: kill
/// only a process carrying <c>KCAP_AGENT_ID</c> AND <c>KCAP_DAEMON_ID == this daemon</c> AND
/// <c>KCAP_DAEMON_EPOCH != current</c> (i.e. a prior incarnation of THIS daemon). A different daemon's
/// child, a current-incarnation child, or any process whose env can't be read is spared. Process
/// enumeration failure is a logged no-op (retried next tick). On macOS the env redaction makes this
/// pass find nothing — the record pass is the effective mechanism there; production is Linux.</item>
/// </list>
/// </summary>
internal sealed class OrphanReaper(
        AgentPidRecordStore store, string daemonId, string currentEpoch, ILogger logger) {
    /// <summary>Run both passes once. Best-effort throughout — a failure on one process never aborts the
    /// sweep of the others.</summary>
    public async Task ReapOnceAsync(CancellationToken ct = default) {
        var handledPids = new HashSet<int>();

        // ── (1) record pass ──────────────────────────────────────────────────────────────────────
        foreach (var record in store.ReadAll()) {
            if (ct.IsCancellationRequested) return;

            // An IdentityUnavailable record carries NO comparable token — the record pass can
            // NEVER resolve it (ProcessReaper.Classify always lands Ambiguous for an empty
            // expectedIdentity). Do NOT mark the pid "handled" here, so the env-marker scan below
            // still gets a chance to reap it via the live process's OWN env triple (Linux only;
            // macOS has no marker scan, so such a record stays identity_unresolvable/manual — see
            // §4.3 and this file's class doc).
            if (record.IdentityKind == PidIdentityKind.Present) {
                handledPids.Add(record.Pid);
            }

            // Reap ONLY prior-incarnation records. A record stamped with the CURRENT epoch belongs to a
            // live agent of THIS incarnation (its normal teardown deletes it) — reaping it would kill
            // our own running agent. This makes the heartbeat RE-RUN safe, not just the boot pass: at
            // boot every leftover record is prior (this incarnation's epoch is fresh), while mid-life
            // the store also holds current-epoch records that must be left alone.
            if (string.Equals(record.DaemonEpoch, currentEpoch, StringComparison.Ordinal)) continue;

            try {
                var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, logger, ct);
                if (confirmedGone) {
                    store.Delete(record.AgentId); // killed+confirmed, already-gone, or proven identity mismatch
                    logger.LogInformation(
                        "OrphanReaper: reaped leftover agent {AgentId} (pid {Pid}) from a prior daemon run",
                        record.AgentId, record.Pid);
                } else if (record.IdentityKind == PidIdentityKind.IdentityUnavailable) {
                    logger.LogWarning(
                        "OrphanReaper: identity_unavailable record for {AgentId} (pid {Pid}, age {Age}) unresolved by the record pass — the env-marker scan may still reap it on Linux; macOS requires a manual kill",
                        record.AgentId, record.Pid, DateTimeOffset.UtcNow - record.SpawnedAt);
                } else if (OperatingSystem.IsMacOS()) {
                    // Present but Ambiguous on macOS almost always means a cross-scheme mismatch
                    // (a pre-M1-A tk: record compared against the now-mac:-producing live
                    // process) — the spec's "legacy_unresolvable" residual.
                    logger.LogWarning(
                        "OrphanReaper: legacy_unresolvable record for {AgentId} (pid {Pid}, age {Age}) — spared every pass (cross-scheme token); manually verify and kill the pid",
                        record.AgentId, record.Pid, DateTimeOffset.UtcNow - record.SpawnedAt);
                }
                // otherwise spared (unreadable env) → retain the record for the next tick
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogWarning(ex, "OrphanReaper: record-pass reap failed for {AgentId} (pid {Pid})", record.AgentId, record.Pid);
            }
        }

        // ── (2) env-marker scan (recordless survivors) ───────────────────────────────────────────
        await ScanEnvMarkersAsync(handledPids, ct);
    }

    async Task ScanEnvMarkersAsync(HashSet<int> handledPids, CancellationToken ct) {
        IReadOnlyList<int> pids;
        try {
            pids = EnumeratePids();
        } catch (Exception ex) {
            logger.LogWarning(ex, "OrphanReaper: process enumeration failed — skipping env-marker scan this tick");
            return;
        }

        foreach (var pid in pids) {
            if (ct.IsCancellationRequested) return;
            if (handledPids.Contains(pid)) continue;       // already covered by the record pass
            if (!ProcessIdentity.IsAlive(pid)) continue;

            // Capture the start token BEFORE reading the env markers, so the token is bound to the SAME
            // incarnation whose markers we're about to trust. If the pid is recycled between the marker
            // reads and the kill, the post-read re-validation (below) catches it — a recapture-after-read
            // would instead adopt the replacement's identity. Uncapturable → spare.
            var token = ProcessIdentity.Capture(pid);
            if (token is null) continue;

            // The FULL marker triple must be readable and prove "this daemon, prior incarnation" before
            // we kill. Any missing/unreadable member SPARES (ambiguity never kills) — this covers a
            // process/PID race, a partial read, and a current-incarnation recordless child whose epoch
            // read momentarily fails (record writes may fail, so such a child can exist).
            var agentId = ProcessIdentity.ReadAgentEnv(pid, "KCAP_AGENT_ID");
            if (agentId is null) continue;                 // not a hosted agent / env unreadable → spare

            var did = ProcessIdentity.ReadAgentEnv(pid, "KCAP_DAEMON_ID");
            if (did is null || !string.Equals(did, daemonId, StringComparison.Ordinal)) continue;   // unreadable / another daemon → spare

            var epoch = ProcessIdentity.ReadAgentEnv(pid, "KCAP_DAEMON_EPOCH");
            if (epoch is null || string.Equals(epoch, currentEpoch, StringComparison.Ordinal)) continue; // unreadable / current incarnation → spare

            // Re-validate the SAME token after all three reads: if the pid was recycled mid-scan, the
            // markers we just read belong to a different incarnation — the token no longer matches, so spare.
            if (ProcessIdentity.MatchesTri(pid, token) != true) continue;

            // Recordless survivor of a PRIOR incarnation of THIS daemon → reap by the captured identity.
            try {
                var gone = await ProcessReaper.ReapByMarkerAsync(pid, token, agentId, logger, ct);
                if (gone) {
                    // Positive, PID-independent resolution: delete any record for THIS agent id (a
                    // no-op if none exists — the ordinary recordless-survivor case). This is what
                    // makes the "identity_unavailable record + live-env-triple confirms it" case
                    // self-heal WITHOUT ever keying off the numeric pid alone (a reused pid between
                    // this kill and any later record pass can't act on the wrong occupant, because the
                    // record is already gone by then).
                    store.Delete(agentId);
                    logger.LogInformation(
                        "OrphanReaper: reaped recordless survivor {AgentId} (pid {Pid}) of a prior daemon incarnation", agentId, pid);
                } else {
                    logger.LogWarning(
                        "OrphanReaper: env-marker kill of {AgentId} (pid {Pid}) not confirmed — retrying next tick", agentId, pid);
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogWarning(ex, "OrphanReaper: env-marker reap failed for pid {Pid}", pid);
            }
        }
    }

    /// <summary>Enumerate candidate pids for the env-marker scan. Linux lists <c>/proc/[0-9]+</c>;
    /// per-pid env readability (same-uid) naturally scopes the scan. macOS returns an empty list — its
    /// env redaction makes the scan a no-op, so there's no point walking every process (the record pass
    /// covers macOS). Windows has no scan path (layer-1 containment).</summary>
    static IReadOnlyList<int> EnumeratePids() {
        if (!OperatingSystem.IsLinux()) return [];

        var pids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            var name = Path.GetFileName(dir);
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                pids.Add(pid);
        }

        return pids;
    }
}
