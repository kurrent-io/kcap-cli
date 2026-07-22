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
        AgentPidRecordStore store, string daemonId, string currentEpoch, ILogger logger,
        Action<string, string, string?, string?>? onRecordResolved = null,
        MarkerCandidateStore? markerStore = null,
        Action<string, string>? onMarkerResolved = null) {
    /// <summary>Phase B2-b (sequenced-settlement design): the recordless-survivor env-marker-scan
    /// status, advertised on the daemon's startup-completeness signals. Seeded per-platform:
    /// <c>Pending</c> on Linux (a scan runs and must complete one clean pass), <c>NotApplicable</c> on
    /// Windows (no scan — layer-1 containment) and macOS (env redaction makes the scan find nothing).
    /// The Linux scan flips it to <c>Complete</c> after one clean enumeration pass or <c>Failed</c> on an
    /// enumeration error (retried next heartbeat); off Linux it stays <c>NotApplicable</c> forever.</summary>
    public StartupDiscovery CurrentDiscovery { get; private set; } =
        new(OperatingSystem.IsLinux() ? MarkerScanState.Pending : MarkerScanState.NotApplicable);

    /// <summary>Phase B2-b (sequenced-settlement design): the known-id prior-incarnation candidates
    /// that are BLOCKED — each keeps <c>StartupReapComplete</c> false until resolved. Three sources,
    /// each with its reason:
    /// <list type="bullet">
    /// <item><c>identity_unresolvable</c> — a prior-epoch <see cref="PidIdentityKind.IdentityUnavailable"/>
    /// record the record pass can never resolve (no comparable token); the Linux env-marker scan may
    /// still reap it, macOS requires a manual kill.</item>
    /// <item><c>legacy_unresolvable</c> — a macOS prior-epoch <see cref="PidIdentityKind.Present"/> record
    /// whose live pid is still alive but whose token is ambiguous (a cross-scheme mismatch spared every
    /// pass).</item>
    /// <item><c>pending_marker</c> — any persisted marker-candidate source (a recordless prior-epoch
    /// survivor whose live triple no longer matches, so it stays pending). Listed WITHOUT a liveness
    /// check — a source on disk is a blocking candidate until the resolution matrix clears it. Flow
    /// identity is unknown for a recordless survivor (the env is untrusted), so it is omitted.</item>
    /// </list>
    /// Flow fields for the record-tracked reasons come from the TRUSTED durable record. Uses the
    /// ctor-injected <c>markerStore</c>/<c>store</c>/<c>currentEpoch</c>.</summary>
    public IReadOnlyList<UnresolvedStartupCandidate> BlockedCandidates() {
        var list = new List<UnresolvedStartupCandidate>();
        foreach (var r in store.ReadAll()) {
            if (string.Equals(r.DaemonEpoch, currentEpoch, StringComparison.Ordinal)) continue; // current-incarnation
            if (r.IdentityKind == PidIdentityKind.IdentityUnavailable)
                list.Add(new(r.AgentId, StartupCandidateUnresolvedReason.IdentityUnresolvable, r.FlowRunId, r.FlowRole));
            else if (OperatingSystem.IsMacOS() && ProcessIdentity.IsAlive(r.Pid) && ProcessIdentity.MatchesTri(r.Pid, r.StartIdentity) is null)
                list.Add(new(r.AgentId, StartupCandidateUnresolvedReason.LegacyUnresolvable, r.FlowRunId, r.FlowRole));
        }
        foreach (var m in markerStore?.ReadAll() ?? [])
            list.Add(new(m.AgentId, StartupCandidateUnresolvedReason.PendingMarker));
        return list;
    }

    /// <summary>Run all passes once. Best-effort throughout — a failure on one process never aborts the
    /// sweep of the others.</summary>
    public async Task ReapOnceAsync(CancellationToken ct = default) {
        var handledPids = new HashSet<int>();

        // ── (0) marker-candidate reconciliation (crash recovery) ───────────────────────────────────
        // Phase B2-b (sequenced-settlement design §4.2.4): re-derive any persisted marker-candidate
        // source BEFORE the record pass. A source outlives one pass, so this covers the crash-before-kill
        // window: re-read the LIVE triple so a pid reused since the source was written is spared, never
        // mis-killed. It runs FIRST so a source co-existing with an identity_unavailable RECORD is
        // resolved by EmitAndClear (which routes the TRUSTED record flow via onRecordResolved) before the
        // record pass could independently resolve+delete that record — no double emit.
        if (markerStore is not null) {
            foreach (var c in markerStore.ReadAll()) {
                if (ct.IsCancellationRequested) return;
                try {
                    await ResolveMarkerCandidateAsync(c, ct);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // Per-source fault (e.g. a sink throwing before the append): the source is left on
                    // disk (append-before-delete), so the next boot re-derives it. Never abort the sweep.
                    logger.LogWarning(ex, "OrphanReaper: marker-candidate reconciliation faulted for {AgentId} (pid {Pid}) — retained for next boot", c.AgentId, c.Pid);
                }
            }
        }

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
                    // Phase B2-b (sequenced-settlement design §4.2.4): ledger-append BEFORE
                    // source-deletion. A crash between the two leaves a committed entry + leftover
                    // source; the next boot re-derives it and Upsert (idempotent on the source-stable
                    // (AgentId, OldEpoch) key) collapses onto the committed entry, then deletes the
                    // leftover. Flow fields come from the TRUSTED record. Emitted ONLY for this
                    // positive confirmed-gone branch — never for spared/ambiguous records.
                    onRecordResolved?.Invoke(record.AgentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
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
            // Phase B2-b (sequenced-settlement design): an enumeration failure leaves discovery FAILED on
            // Linux (retried next heartbeat); keep the last successful scan time so a prior clean pass is
            // still remembered. Off Linux there is no scan, so CurrentDiscovery stays NotApplicable.
            if (OperatingSystem.IsLinux())
                CurrentDiscovery = new StartupDiscovery(MarkerScanState.Failed, CurrentDiscovery.LastSuccessfulScanAt);
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

            // Recordless survivor of a PRIOR incarnation of THIS daemon. Persist a durable
            // marker-candidate source BEFORE the kill (crash-consistency: a crash before the kill
            // re-runs the resolution next boot; never a source-less window, never a retroactive mint),
            // then resolve it through the same matrix as the reconciliation pass.
            try {
                var candidate = new MarkerCandidate(agentId, daemonId, epoch, pid);
                markerStore?.Write(candidate);
                await ResolveMarkerCandidateAsync(candidate, ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogWarning(ex, "OrphanReaper: env-marker reap failed for pid {Pid}", pid);
            }
        }

        // Phase B2-b (sequenced-settlement design): one clean env-marker-scan pass completed —
        // enumeration succeeded and every candidate was walked (never cancelled mid-pass; a cancel
        // returns early above, leaving discovery unchanged). On Linux this is the completeness proof, so
        // flip to Complete and stamp the scan time. A pass that merely SPARED a pending_marker candidate
        // is still clean here — that spared source blocks completion via BlockedCandidates, not via the
        // pass state. Off Linux there is no scan (EnumeratePids returns empty), so CurrentDiscovery stays
        // NotApplicable.
        if (OperatingSystem.IsLinux())
            CurrentDiscovery = new StartupDiscovery(MarkerScanState.Complete, DateTimeOffset.UtcNow);
    }

    /// <summary>Resolve a marker-candidate source through the asymmetric matrix. Because a recordless
    /// survivor has NO spawn-bound start-identity, a triple mismatch is spare-only — never positive death
    /// evidence — since a mismatch can't distinguish PID-reuse from the original mutating its own env.
    /// <list type="bullet">
    /// <item><b>(a)</b> pid dead per the shipped zombie-aware <see cref="ProcessIdentity.IsAlive"/> (ESRCH
    /// or Linux 'Z') ⇒ conclusively resolved → emit + delete the source.</item>
    /// <item><b>(b)</b> alive (non-zombie) + the LIVE triple still matches ⇒ kill; on CONFIRMED death,
    /// resolve as (a).</item>
    /// <item><b>(c)</b> alive (non-zombie) + triple mismatch/unreadable ⇒ SPARE, the source stays PENDING,
    /// NO emit — a reused pid no longer carrying our triple is left untouched (PID-reuse safe).</item>
    /// </list></summary>
    async Task ResolveMarkerCandidateAsync(MarkerCandidate c, CancellationToken ct) {
        // (a) dead per the shipped zombie-aware IsAlive (ESRCH or Linux 'Z') -> conclusively resolved.
        if (!ProcessIdentity.IsAlive(c.Pid)) { EmitAndClear(c); return; }

        // Re-read the LIVE triple: (c) mismatch/unreadable -> SPARE, stay pending, NO emit (a triple
        // mismatch cannot distinguish PID-reuse from the process mutating its own env).
        var agentId = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_AGENT_ID");
        var did     = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_DAEMON_ID");
        var epoch   = ProcessIdentity.ReadAgentEnv(c.Pid, "KCAP_DAEMON_EPOCH");
        var token   = ProcessIdentity.Capture(c.Pid);
        var tripleMatches = agentId == c.AgentId && did == c.DaemonId && epoch == c.OldEpoch && token is not null;
        if (!tripleMatches) return; // (c) pending

        // (b) alive + triple still matches -> kill; on CONFIRMED death, resolve.
        if (await ProcessReaper.ReapByMarkerAsync(c.Pid, token!, c.AgentId, logger, ct)) EmitAndClear(c);
        else logger.LogWarning("OrphanReaper: marker kill of {AgentId} (pid {Pid}) not confirmed — retry next tick", c.AgentId, c.Pid);
    }

    /// <summary>Ledger-append BEFORE source-deletion (crash reconciled next boot; idempotent). Trust
    /// boundary: a fully RECORDLESS survivor's env is untrusted, so it maps to NO role
    /// (<paramref name="onMarkerResolved"/>, null flow). But a Linux identity_unavailable RECORD resolved
    /// via the marker scan carries TRUSTED flow identity — written from the daemon's own AgentInstance
    /// into the durable record at spawn — so pull FlowRunId/FlowRole (and the trusted DaemonEpoch) FROM
    /// THAT RECORD via the record-resolved sink, so its role can be individually healed. Never trust flow
    /// from the mutable env.</summary>
    void EmitAndClear(MarkerCandidate c) {
        var record = store.ReadAll().FirstOrDefault(r => string.Equals(r.AgentId, c.AgentId, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(record.AgentId))
            onRecordResolved?.Invoke(record.AgentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
        else
            onMarkerResolved?.Invoke(c.AgentId, c.OldEpoch);
        store.Delete(c.AgentId);   // clears the identity_unavailable record (no-op for a pure recordless survivor)
        markerStore?.Delete(c.AgentId);
        logger.LogInformation(
            "OrphanReaper: resolved marker-candidate {AgentId} (pid {Pid}) of a prior daemon incarnation", c.AgentId, c.Pid);
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
