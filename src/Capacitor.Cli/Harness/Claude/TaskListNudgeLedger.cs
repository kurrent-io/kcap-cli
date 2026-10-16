using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Per session: whether it has written kcap plan tasks, the tasks as the plan tools last reported
/// them, and which reminders it already got. Best effort throughout: a storage failure costs a reminder,
/// never the hook.</summary>
sealed class TaskListNudgeLedger(ConfigRoot config) {
    const string PlanWritten = "plan-written";

    public bool HasPlan(string sessionId) => Has(sessionId, PlanWritten);

    public void RecordPlan(string sessionId) => Mark(sessionId, PlanWritten);

    /// <summary>Claims a reminder by creating its marker, so concurrent hooks for one parallel tool batch
    /// cannot all emit it. False when another hook holds the claim or the marker cannot be written:
    /// without a marker every later event would repeat the reminder.</summary>
    public bool TryClaimNudge(string sessionId, ClaudeTaskListEvent.Moment moment) => TryClaim(sessionId, moment.ToString());

    public void ReleaseNudge(string sessionId, ClaudeTaskListEvent.Moment moment) => Release(sessionId, moment.ToString());

    /// <summary>A pairing reminder is claimed per task and status, so each lagging move is pointed out once.</summary>
    public bool TryClaimPairing(string sessionId, PlanTaskState task, string status) => TryClaim(sessionId, PairingName(task, status));

    public void ReleasePairing(string sessionId, PlanTaskState task, string status) => Release(sessionId, PairingName(task, status));

    /// <summary>One file per task, so concurrent hooks reporting different tasks never overwrite each
    /// other's update; a whole list replaces every file.</summary>
    public void SaveTasks(string sessionId, IReadOnlyList<PlanTaskState> tasks, bool whole) {
        try {
            var dir = TasksDir(sessionId);
            if (whole && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            foreach (var task in tasks) {
                if (!PlanTaskState.IsSafeId(task.TaskId)) continue;
                var temp = Path.Combine(dir, $"{task.TaskId}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temp, task.ToJson().ToJsonString());
                File.Move(temp, Path.Combine(dir, task.TaskId), overwrite: true);
            }
        } catch { }
    }

    public IReadOnlyList<PlanTaskState> LoadTasks(string sessionId) {
        try {
            var dir = TasksDir(sessionId);
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateFiles(dir)
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
                .Select(f => { try { return PlanTaskState.From(JsonNode.Parse(File.ReadAllText(f))); } catch { return null; } })
                .OfType<PlanTaskState>()
                .ToList();
        } catch {
            return [];
        }
    }

    bool TryClaim(string sessionId, string name) {
        var marker = Path.Combine(SessionDir(sessionId), name);
        try {
            Directory.CreateDirectory(SessionDir(sessionId));
            using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write)) { }
            return true;
        } catch {
            return false;
        }
    }

    void Release(string sessionId, string name) {
        try { File.Delete(Path.Combine(SessionDir(sessionId), name)); } catch { }
    }

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

    static string PairingName(PlanTaskState task, string status) => $"pair-{task.TaskId}-{status}";

    string TasksDir(string sessionId) => Path.Combine(SessionDir(sessionId), "tasks");

    string SessionDir(string sessionId) => config.Path("task-list", PolicySnapshotStore.Sanitize(sessionId));
}
