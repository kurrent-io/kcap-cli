using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>Per-run handoff written iff the foreground import pass ran, so the eval-watch skill
/// (and anything else reading <c>~/.config/kcap/import-handoff-{run_id}.json</c>) can pick up where
/// setup left off without re-deriving it from scratch.</summary>
internal sealed record ImportHandoffFile(
        string RunId, DateTimeOffset WrittenAt, bool HandoffOffered, HandoffSuppressedReason? HandoffSuppressed,
        ForegroundCertainty Certainty, string ServerUrl, string Profile, HandoffCohort Cohort,
        IReadOnlyList<string> SessionIds, IReadOnlyList<string> ForegroundSucceededIds, int UnattributedOnDisk,
        BackgroundImportStatus Background, string? BackgroundLog) {
    public const int SchemaVersion = 1;
    public const int CohortCap     = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static ImportHandoffFile Compose(
            string runId, DateTimeOffset now, bool offered, HandoffSuppressedReason? reason,
            ForegroundImportOutcome outcome, BackgroundImportLaunch background, string serverUrl, string profile, int unattributedOnDisk) {
        var candidates = outcome.RunCandidateIds;
        var cohort = candidates is null ? HandoffCohort.Unknown
                   : candidates.Count > CohortCap ? HandoffCohort.PartialExact
                   : HandoffCohort.Exact;

        return new ImportHandoffFile(
            runId, now, offered, reason, outcome.Certainty, serverUrl.TrimEnd('/'), profile, cohort,
            candidates is null ? [] : [.. candidates.Take(CohortCap)],
            outcome.SucceededIds, unattributedOnDisk, background.Status, background.LogPath);
    }

    public static string PathFor(ConfigRoot config, string runId) => config.Path($"import-handoff-{runId}.json");

    public string ToJson() {
        var ids = new JsonArray();
        foreach (var id in SessionIds) ids.Add((JsonNode?)id);
        var succeeded = new JsonArray();
        foreach (var id in ForegroundSucceededIds) succeeded.Add((JsonNode?)id);

        var json = new JsonObject {
            ["schema_version"]           = SchemaVersion,
            ["run_id"]                   = RunId,
            ["written_at"]               = WrittenAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["handoff_offered"]          = HandoffOffered,
            ["handoff_suppressed"]       = HandoffSuppressed is { } r ? (JsonNode?)r.Wire() : null,
            ["foreground_certainty"]     = Certainty == ForegroundCertainty.Complete ? "complete" : "incomplete",
            ["server_url"]               = ServerUrl,
            ["profile"]                  = Profile,
            ["scope"]                    = "all",
            ["cohort"]                   = Cohort switch { HandoffCohort.Exact => "exact", HandoffCohort.PartialExact => "partial_exact", _ => "unknown" },
            ["session_ids"]              = ids,
            ["foreground_succeeded_ids"] = succeeded,
            ["unattributed_on_disk"]     = UnattributedOnDisk,
            ["background"]               = Background switch {
                BackgroundImportStatus.NotNeeded  => "not_needed",
                BackgroundImportStatus.Running    => "running",
                BackgroundImportStatus.ExitedZero => "exited_zero",
                _                                 => "failed" },
            ["background_log"]           = BackgroundLog is { } log ? (JsonNode?)log : null,
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Unique temp, created new and owner-only; published with a move that refuses an
    /// existing final name; the temp never survives, whichever step failed.</summary>
    public void Write(ConfigRoot config, TimeProvider time) {
        var final = PathFor(config, RunId);
        var temp  = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = OwnerOnlyFile.CreateNew(temp))
            using (var writer = new StreamWriter(stream)) {
                writer.Write(ToJson());
            }
            File.Move(temp, final);
        } finally {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* best effort */ } }
        }

        Prune(config, time.GetUtcNow());
    }

    static void Prune(ConfigRoot config, DateTimeOffset now) {
        foreach (var path in Directory.EnumerateFiles(config.Directory, "import-handoff-*.json")) {
            try {
                if (now - File.GetLastWriteTimeUtc(path) > Retention) File.Delete(path);
            } catch { /* another process may own it; best effort */ }
        }
    }
}
