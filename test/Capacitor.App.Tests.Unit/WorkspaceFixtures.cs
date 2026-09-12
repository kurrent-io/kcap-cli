using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Shared workspace-suite fixture pieces, imported via `using static` so call sites read the
/// same as the private copies they replaced.
static class WorkspaceFixtures {
    /// The canonical AgentStatusDto construction: one home for the positional tail, so the DTO
    /// gaining a field means one edit here, not one drifting copy per suite. Suites with their
    /// own defaults (fixed repo path, hasTerminal) keep a thin local wrapper delegating here.
    public static AgentStatusDto Agent(
            string id, string vendor, bool? hasTerminal, string? repoPath = null,
            string kind = "agent", string? model = null,
            string? worktreePath = null, string? workLocation = null, string? borrowedFrom = null,
            string? sessionId = null, string? branch = null) => new(
        id, kind, vendor, repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: model,
        RequesterDisplay: null, HasTerminal: hasTerminal,
        WorktreePath: worktreePath, WorkLocation: workLocation, BorrowedFrom: borrowedFrom,
        SessionId: sessionId, Branch: branch);

    /// An AgentActionService over the scripted/recording deps, for suites that never assert on
    /// those deps individually.
    public static AgentActionService NewActions() =>
        new(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(),
            new ReplaySubject<DaemonStatusDto>(1), CancellationToken.None, NeverConfirm.Confirm);

    /// Real-time poll for a condition an async continuation settles OUTSIDE the test's own await
    /// chain (e.g. a Task.ContinueWith observer attached to an abandoned task), letting an
    /// already-fired continuation flush. A condition may also advance a FakeTimeProvider itself,
    /// for the one case where the code under test arms its timer just after publishing the state
    /// the test waits on: a single advance can fall in that gap and fire nothing.
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what = "condition", TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!await condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
