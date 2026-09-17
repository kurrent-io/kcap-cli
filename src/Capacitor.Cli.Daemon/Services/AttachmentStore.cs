using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Per-agent attachment directories under the daemon's state dir, for runtimes whose placement is
/// DaemonStore. Named by the same hash as the PID record and journal: the agent id crosses the wire
/// unconstrained, so it is never a path segment.
internal sealed class AttachmentStore(string stateDir) {
    public string Root => Path.Combine(stateDir, "attachments");

    public string DirectoryFor(string agentId) => Path.Combine(Root, AgentFileNames.For(agentId));

    public void Remove(string agentId) {
        var dir = DirectoryFor(agentId);
        if (Directory.Exists(dir)) WorktreeManager.DeleteTreeNoFollow(dir);
    }

    public AttachmentStoreLease Lease(string agentId) => new(this, agentId);

    /// Startup only: a directory whose agent is not live, and any staging directory left by a crash.
    /// The enumeration itself is guarded too — a lazy <see cref="Directory.EnumerateDirectories(string)"/>
    /// throws from MoveNext, not from the loop body, so a fault reading Root (permissions, or Root
    /// removed concurrently) must not escape and abort daemon startup.
    public void SweepOrphans(Func<string, bool> isLive, ILogger logger) {
        try {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root)) SweepOne(dir, isLive, logger);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Attachment store sweep: enumeration failed");
        }
    }

    static void SweepOne(string dir, Func<string, bool> isLive, ILogger logger) {
        try {
            var stem = Path.GetFileName(dir);
            if (!isLive(stem)) { WorktreeManager.DeleteTreeNoFollow(dir); return; }
            foreach (var pending in Directory.EnumerateDirectories(dir, ".pending-*"))
                WorktreeManager.DeleteTreeNoFollow(pending);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Attachment store sweep: skipped {Dir}", dir);
        }
    }
}
