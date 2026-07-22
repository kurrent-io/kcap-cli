using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.3): the durable boot-chain attestation that
/// drives <c>RecordlessSurvivorsImpossible</c>. A boolean alone cannot witness boots by UNAWARE
/// binaries, so this folds across the lock file's persistent per-boot <c>InstanceId</c> nonce
/// (the only marker every shipped version rewrites at boot and never deletes):
///
///   cumulative_covered(this boot) = tail.cumulative_covered AND chain_check AND this_epoch_contained
///
/// where chain_check is "the immediately-preceding boot was our own recorded aware epoch"
/// (prior lock InstanceId == journal tail's recorded id). Sticky-false by construction: the
/// detecting boot persists false in its OWN tail, so every later boot inherits it. Fail-closed:
/// any missing/corrupt/unwritable state evaluates to false. The only false->true path is the
/// documented operator reset (delete the state dir AND the per-name lock -> next boot is genesis).
///
/// The spec's "state_dir_initialized marker + journal in the SAME atomic operation" is satisfied by
/// folding the <c>Initialized</c> flag INTO the journal document — a single JSON
/// {initialized, instance_id, cumulative_covered} written by ONE temp+rename. There is deliberately
/// NO separate marker file (two non-atomic writes would let a genesis-boot crash leave
/// journal-present/marker-absent → next boot reads genesis=false → permanently poisoned). Genesis
/// eligibility is therefore "the journal file is absent": the single rename is the only durable state
/// transition, so a crash is provably either before it (no journal ⇒ re-seed) or after it (a valid
/// initialized journal).
/// </summary>
internal sealed partial class CoverageJournal(string stateDir, ILogger logger) {
    readonly string _journalPath = Path.Combine(stateDir, "coverage.json");

    readonly record struct Journal(bool Initialized, string InstanceId, bool CumulativeCovered);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Journal))]
    partial class Ctx : JsonSerializerContext;

    /// <summary>Fold this boot and persist the new journal atomically BEFORE Connect/spawn. Returns
    /// cumulative_covered. Never throws — an I/O failure returns false (fail-closed).</summary>
    public bool RecordBoot(string myInstanceId, string? priorLockInstanceId, bool thisEpochContained) {
        try {
            var journalExists = File.Exists(_journalPath);
            var prior         = journalExists ? ReadJournal() : null; // null ⇒ present-but-corrupt

            bool covered;
            if (!journalExists) {
                // Genesis is the ONLY seed-true base case: the journal file is absent (we are about to
                // atomically initialize it) AND the captured prior lock shows no pre-existing InstanceId
                // (a genuine first-ever boot for this name). A previously-used dir whose journal is gone
                // but whose prior lock DOES carry an InstanceId is un-journaled history that re-pointing/
                // deleting the dir cannot launder ⇒ false.
                var genesis = priorLockInstanceId is null;
                covered = genesis && thisEpochContained;
            } else if (prior is not { Initialized: true } t) {
                covered = false; // journal present but corrupt / not fully initialized -> fail-closed
            } else {
                var chainOk = priorLockInstanceId is { } pid && string.Equals(pid, t.InstanceId, StringComparison.Ordinal);
                covered = t.CumulativeCovered && chainOk && thisEpochContained;
            }

            WriteJournalAtomic(new Journal(true, myInstanceId, covered)); // persists the break in THIS entry (sticky)
            return covered;
        } catch (Exception ex) {
            logger.LogWarning(ex, "CoverageJournal: fold/persist failed — advertising RecordlessSurvivorsImpossible=false (fail-closed)");
            return false;
        }
    }

    Journal? ReadJournal() {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(_journalPath), Ctx.Default.Journal);
        } catch { return null; } // corrupt -> journal present but unparseable => false (never genesis)
    }

    void WriteJournalAtomic(Journal journal) {
        Directory.CreateDirectory(stateDir);
        var tmp = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Ctx.Default.Journal));
        File.Move(tmp, _journalPath, overwrite: true);   // THE single atomic same-directory rename (marker folded in)
    }
}
