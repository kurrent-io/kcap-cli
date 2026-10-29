namespace Capacitor.Cli.Harness.Claude;

using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// Reads a judged session's transcript backlog from the watcher, off every hook's path, so the
/// first consultation of a long resumed session does not spend its budget on the scan.
/// </summary>
internal static class ClaudeJudgeWarmup {
    /// <summary>The session-start hook freezes the snapshot after it spawns the watcher, so the
    /// watcher looks for it a few times before deciding the session is not judged.</summary>
    internal static readonly TimeSpan[] SnapshotWaits = [
        TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];

    /// <summary>Never throws. A session without a judge-enabled snapshot is left untouched: no scan
    /// and no state file.</summary>
    /// <returns>The number of transcript reads taken; zero when nothing was warmed.</returns>
    public static async Task<int> RunAsync(
            ConfigRoot config, string sessionId, string transcriptPath, string? cwd, TimeProvider time,
            CancellationToken ct, IReadOnlyList<TimeSpan>? snapshotWaits = null) {
        try {
            var key = sessionId.Replace("-", "");
            var store = new PolicySnapshotStore(config);

            foreach (var wait in snapshotWaits ?? SnapshotWaits) {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
                if (store.TryLoad(key) is not { } snapshot) continue;
                if (!snapshot.JudgeEnabled) return 0;

                return ClaudeJudgeDeclarationReader.Warm(transcriptPath, cwd, ClaudeJudgeDeclarationReader.StatePath(config, key), ct);
            }
        } catch {
            // The warm-up only ever saves work: the first consultation reads whatever it left.
        }
        return 0;
    }
}
