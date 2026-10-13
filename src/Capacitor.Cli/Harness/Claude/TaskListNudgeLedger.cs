using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Per session: whether it has written a kcap plan, and which task-list reminders it already
/// got. Best effort throughout: a lost marker costs one repeated reminder, never the hook.</summary>
sealed class TaskListNudgeLedger(ConfigRoot config) {
    const string PlanWritten = "plan-written";

    public bool HasPlan(string sessionId) => Has(sessionId, PlanWritten);

    public void RecordPlan(string sessionId) => Mark(sessionId, PlanWritten);

    public bool WasNudged(string sessionId, ClaudeTaskListEvent.Moment moment) => Has(sessionId, moment.ToString());

    public void RecordNudge(string sessionId, ClaudeTaskListEvent.Moment moment) => Mark(sessionId, moment.ToString());

    public void Evict(string sessionId) {
        try { Directory.Delete(SessionDir(sessionId), recursive: true); } catch { }
    }

    bool Has(string sessionId, string name) {
        try { return File.Exists(Path.Combine(SessionDir(sessionId), name)); } catch { return false; }
    }

    void Mark(string sessionId, string name) {
        try {
            var dir = SessionDir(sessionId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), "");
        } catch { }
    }

    string SessionDir(string sessionId) => config.Path("task-list", PolicySnapshotStore.Sanitize(sessionId));
}
