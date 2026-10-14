using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class WorkContextPlanProgressTests {
    sealed class Harness : IAsyncDisposable {
        readonly Subject<AgentStatusDto?> _presence = new();
        readonly SessionRuns _runs;
        public FakeTimeProvider Time { get; } = new();
        public FakePlanSource Plans { get; } = new();
        public FakePullRequestSource PrSource { get; }
        public PullRequestContextViewModel Prs { get; }
        public WorkContextViewModel Vm { get; }

        public Harness() {
            _runs = new(Time);
            Vm = new(_presence, new FakeWorkContextSource(), Time, new RecordingOpener(), _runs, plans: Plans);
            PrSource = new(Time);
            PrSource.Lifecycles[2] = "merged";
            Prs = new(_presence, PrSource, Time, new RecordingOpener(), () => { });
            Vm.PullRequests = Prs;
            Prs.SetForeground(true);
        }

        public async Task ShowAsync(string session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") {
            _presence.OnNext(Agent("a1", "claude", false, sessionId: session));
            await (Vm.Plan.PendingReadForTesting ?? Task.CompletedTask);
            await SettlePrAsync();
            Prs.Selected = Prs.Choices.First(choice => choice.Subject.Number == 2);
            await SettlePrAsync();
        }

        public async Task SettlePrAsync() {
            await WaitUntilAsync(() => Prs.CanReveal && !Prs.IsReading, what: "PR overview");
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SetTasksAsync(params string[] statuses) {
            Plans.Enqueue(Read(statuses));
            Vm.Plan.Refresh();
            await (Vm.Plan.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async ValueTask DisposeAsync() {
            await Vm.TeardownAsync();
            await Prs.TeardownAsync();
            _presence.Dispose();
        }
    }

    static SessionPlansRead Read(params string[] statuses) => new(SessionPlansReadKind.Ready, [new() {
        PlanId = "plan", IsCurrent = true,
        Tasks = statuses.Select((status, i) => new PlanLedgerTaskDto {
            TaskId = $"t{i}", Ordinal = i, Title = $"Task {i}", Status = status,
        }).ToList(),
    }]);

    [Test]
    public Task Merged_session_PR_explains_outstanding_tasks_without_completing_them_and_follows_selection() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending", "in_progress", "completed", "skipped"));
        var changes = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await h.ShowAsync();

        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();
        await Assert.That(h.Vm.PlanMergeHeading).IsEqualTo("PR #2 merged");
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("2 tasks are still marked open");
        await Assert.That(h.Vm.PlanMergeNote).Contains("relationship to the plan is unconfirmed");
        await Assert.That(h.Vm.Plan.DoneCount).IsEqualTo(2);
        await Assert.That(h.Vm.Plan.Tasks[0].State).IsEqualTo(PlanTaskState.Pending);
        await h.Vm.Plan.ToggleCommand.Execute();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();

        h.Prs.Selected = h.Prs.Choices.First(choice => choice.Subject.Number == 1);
        await h.SettlePrAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
        await Assert.That(h.Vm.PlanMergeNote).IsEmpty();
        await Assert.That(changes).Contains(nameof(WorkContextViewModel.ShowsPlanMergeNote));

        h.Prs.Selected = h.Prs.Choices.First(choice => choice.Subject.Number == 2);
        await h.SettlePrAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();
    });

    [Test]
    public Task Plan_refresh_updates_the_note_and_settled_or_empty_plans_hide_it() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending", "in_progress"));
        await h.ShowAsync();
        await h.SetTasksAsync("completed", "pending");
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("1 task is still marked open");
        await h.SetTasksAsync("completed", "skipped");
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
        await h.SetTasksAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
    });

    [Test]
    public Task Hidden_PR_content_and_a_new_session_do_not_leave_a_merge_note_behind() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync();
        h.Prs.SetForeground(false);
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
        await Assert.That(h.Vm.PlanMergeHeading).IsEmpty();
        h.Prs.SetForeground(true);
        await h.SettlePrAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();
        h.PrSource.Lifecycles[2] = "open";
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
    });
}
