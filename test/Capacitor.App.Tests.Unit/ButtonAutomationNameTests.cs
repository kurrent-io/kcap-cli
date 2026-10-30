using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Avalonia names a button with no automation name and no TextBlock content after its Content's
/// ToString(), so a composite button left unnamed announces a .NET type name. Each test renders a
/// surface in the states that show its buttons and sweeps every visible one: a hidden button is
/// never templated, so even a plain-text one reads as a type until it first shows.
public class ButtonAutomationNameTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string SessionA = "0123456789abcdef0123456789abcdef";

    /// Every button under the roots, the toggle and radio kinds included, by the name its peer
    /// announces; one that announces a type name fails the test, named with its classes and data.
    static async Task<List<string>> AnnouncedAsync(params Visual[] roots) {
        var names = new List<string>();
        var offenders = new List<string>();
        foreach (var button in roots.SelectMany(r => r.GetVisualDescendants().OfType<Button>()).Where(b => b.IsEffectivelyVisible)) {
            var name = ControlAutomationPeer.CreatePeerForElement(button).GetName();
            if (name.StartsWith("Avalonia.", StringComparison.Ordinal) || name.StartsWith("Capacitor.", StringComparison.Ordinal))
                offenders.Add($"{button.GetType().Name} '{button.Name}' [{string.Join(' ', button.Classes)}] over "
                            + $"{button.DataContext?.GetType().Name ?? "nothing"} announces {name}");
            else names.Add(name);
        }
        await Assert.That(string.Join(Environment.NewLine, offenders)).IsEqualTo("");
        return names;
    }

    HomeViewModel Home(FakeDaemonClientService service) =>
        new(service, new AppStateStore(Tmp.PathTo("app-state.json")), new NeverLaunch(),
            () => Task.FromResult(new[] { "/repos/alpha" }), TimeProvider.System);

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_main_window_names_its_rail_launcher_and_workspace_buttons() => RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null,
            Title: "Fix the flaky test", SessionId: "s1"), new RepoIdentity("path:/repo", "repo")));
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        using var home = Home(service);
        var actions = NewActions();
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            workspaceFactory: id => new WorkspaceViewModel(
                id, service, actions, new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(),
                new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(),
                new ScriptedLocalControlOps(), new NoAttachmentUploader()),
            rail: rail, home: home);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Settle(window);
        try {
            var launcher = await AnnouncedAsync(window);
            await Assert.That(launcher).Contains("New session");
            await Assert.That(launcher).Contains("Fix the flaky test");

            vm.OpenSession("a1");
            Settle(window);
            await Assert.That(window.GetVisualDescendants().OfType<WorkspaceView>().Any()).IsTrue();
            await AnnouncedAsync(window);
            await ((WorkspaceViewModel)vm.CurrentWorkspace!).TeardownAsync();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// A ready read with a part, two contributors, separate issues, a plan, a subagent and the
    /// session. The pull requests come from the card when one is wired, else as legacy link cards;
    /// the issues are a titled pair, else one untitled issue whose title row has no text to read.
    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [NotInParallel("AvaloniaSession")]
    public Task The_work_context_pane_names_every_section_and_row(bool pullRequestCard, bool titledIssues) => RunOnUiAsync(async () => {
        var presence = new BehaviorSubject<AgentStatusDto?>(null);
        var source = new FakeWorkContextSource();
        var time = new FakeTimeProvider();
        var opener = new RecordingOpener();
        var runs = new SessionRuns(time);
        var activity = new PlanActivity();
        var plans = new FakePlanSource { Default = new(SessionPlansReadKind.Ready, [new SessionPlanDto {
            PlanId = "p1", IsCurrent = true,
            Tasks = [new PlanLedgerTaskDto { TaskId = "t1", Title = "First step", Ordinal = 0, Status = "in_progress" }],
        }]) };
        var artefacts = new FakePlanArtifactSource { Default = Ready(Doc("docs/x-design.md")) };
        var vm = new WorkContextViewModel(presence, source, time, opener, runs, plans: plans, planActivity: activity,
            artefacts: new ArtefactsTabViewModel(artefacts, activity, time));
        var prs = pullRequestCard ? new PullRequestContextViewModel(presence, new FakePullRequestSource(time), time, opener, () => { }) : null;
        if (prs is not null) {
            vm.PullRequests = prs;
            prs.SetForeground(true);
        }

        var assignment = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "WK-2198", Source = "mcp", Confidence = 1, IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId = "w1",
            Title = "Name the buttons",
            Key = new WorkItemKeyDto { ShortKey = "WK-2198", Provider = "linear", Kind = "issue", Value = "WK-2198" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Parts = [new WorkItemPartDto { WorkItemId = "p1", Title = "Sweep", Ordinal = 0 }],
            Links = titledIssues
                ? [Issue("WK-1", "One", seed: true), Issue("WK-2", "Two", seed: false)]
                : [Issue("WK-1", null, seed: true)],
            Contributors = [
                new WorkItemContributorDto { UserId = "u1", DisplayName = "Ada" },
                new WorkItemContributorDto { UserId = "u2", DisplayName = "Bob" },
            ],
            SessionCount = 2,
        };
        var summary = new SessionSummaryDto { SessionId = SessionA, PrNumber = 7, PrTitle = "Fix", PrUrl = "https://github.com/o/r/pull/7" };
        source.Enqueue(new WorkContextRead(WorkContextReadKind.Ready, [assignment], assignment, item, null, summary, false, false, false, null));
        runs.Apply(new ChatProjectionResult([], [], [new RunSignal.Started("c1", "Explore", "Map the UI", time.GetUtcNow())]));

        var window = new Window { Content = new WorkContextView { DataContext = vm }, Width = 320, Height = 1600 };
        var artefactsWindow = new Window { Content = new ArtefactsView { DataContext = vm.Artefacts }, Width = 800, Height = 600 };
        window.Show();
        artefactsWindow.Show();
        try {
            Dispatcher.UIThread.RunJobs();
            presence.OnNext(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: SessionA));
            await (vm.PendingReadForTesting ?? Task.CompletedTask);
            await (vm.Artefacts!.PendingReadForTesting ?? Task.CompletedTask);
            await (vm.Plan.PendingReadForTesting ?? Task.CompletedTask);
            if (prs is not null) await WaitUntilAsync(() => vm.ShowsPullRequestCard, what: "pull request card");
            Settle(window);
            Settle(artefactsWindow);

            var folded = await AnnouncedAsync(window, artefactsWindow);

            await vm.TogglePeopleCommand.Execute();
            await vm.ToggleRunsCommand.Execute();
            await vm.ToggleSessionCommand.Execute();
            await vm.Plan.ToggleCommand.Execute();
            await vm.TogglePartsCommand.Execute();
            Settle(window);
            var opened = await AnnouncedAsync(window);

            var names = folded.Concat(opened).ToHashSet();
            string[] expected = ["Parts, 1 open", "Parts, 0 of 1", "Who's on it", "Copy Ada", "Copy Bob", "Issues", "Plan", "Agents and commands",
                                 "Session details, More", "Session details, Less",
                                 "x-design.md", pullRequestCard ? "Pull requests" : "#7: Fix",
                                 .. titledIssues ? ["WK-1: One", "WK-2: Two"] : new[] { "WK-1" }];
            await Assert.That(expected.Where(n => !names.Contains(n))).IsEmpty();
        } finally {
            window.Close();
            artefactsWindow.Close();
            Dispatcher.UIThread.RunJobs();
            if (prs is not null) await prs.TeardownAsync();
            await vm.TeardownAsync();
        }
    });

    static WorkItemLinkDto Issue(string key, string? title, bool seed) => new() {
        Kind = "issue", Provider = "linear", Value = key, ShortKey = key,
        Url = $"https://linear.app/x/issue/{key}", Title = title, LinkClass = "link", IsSeed = seed,
    };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_launcher_chip_menus_name_their_rows() => RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var vm = Home(service);
        var view = new LauncherPaneView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Settle(window);
        try {
            await Assert.That(await AnnouncedAsync(window)).Contains("Claude Code · Default");

            async Task<List<string>> OpenAsync(string chip) {
                var before = view.OpenMenuForTesting;
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == chip).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => view.OpenMenuForTesting is { } menu && menu != before, what: chip);
                Settle(window);
                var menu = view.OpenMenuForTesting!;
                var names = await AnnouncedAsync((Control)menu.Content!);
                menu.Hide();
                Dispatcher.UIThread.RunJobs();
                return names;
            }

            await Assert.That(await OpenAsync("RepositoryChip")).Contains("alpha, selected");
            await Assert.That(await OpenAsync("MachineChip")).Contains($"{service.DaemonName}, selected");
            var agent = await OpenAsync("AgentChip");
            await Assert.That(agent).Contains("Claude Code");
            await Assert.That(agent).Contains("Default — Claude Code chooses, Claude Code, selected");
            await Assert.That(await OpenAsync("EffortChip")).Contains($"{HostedHarnessCatalog.EffortDefaultLabel}, selected");
            await Assert.That(await OpenAsync("PermissionChip")).Contains($"{HostedHarnessCatalog.PermissionModes[0].Label}, selected");
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    sealed class NeverLaunch : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unexpected launch"));
    }

    const string ThinkingLine = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"weighing it"}]}}""";
    const string BashCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string BashResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string ReadResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}""";
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
    const string TwoQuestions = """{"questions":[{"question":"Pick","header":"Colour","options":[{"label":"A","description":"first"},{"label":"B"}]},{"question":"Again","options":[{"label":"C"}]}]}""";

    /// A thinking row, a folded tool group, a published-page card and a two-question card, then
    /// the card's review step.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_chat_tab_names_its_summaries_cards_and_question_rows() => RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        var time = new FakeTimeProvider();
        var permissions = new FakePermissionService();
        var terminal = new TerminalTabViewModel("a1", daemon, new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(), time);
        var chat = new ChatTabViewModel(
            "a1", daemon, new TerminalChatInput(terminal, "a1", daemon, new ScriptedLocalControlOps(), new ReplaySubject<AgentPresence>(1)),
            new NoAttachmentUploader(), TranscriptChat.For("claude"), new RecordingOpener(), time, permissions, new SessionRuns(time));
        var view = new ChatTabView { DataContext = chat };
        var window = new Window { Content = view, Width = 800, Height = 1600 };
        window.Show();
        try {
            var transcript = Tmp.CreateFile("chat.jsonl", [ThinkingLine, BashCallLine, BashResultLine, ReadCallLine, ReadResultLine, PublishCallLine, PublishResultLine]);
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = transcript });
            await (terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            permissions.Add(PermissionEntries.Question("q1", toolInputJson: TwoQuestions));
            Settle(window);

            var asking = await AnnouncedAsync(window);
            await Assert.That(asking).Contains("Thinking");
            await Assert.That(asking).Contains("Open");
            await Assert.That(asking).Contains("Colour");
            await Assert.That(asking).Contains("A");

            var card = view.GetVisualDescendants().Select(v => v.DataContext).OfType<QuestionCardViewModel>().First();
            await card.Steps[^1].GoCommand.Execute();
            Settle(window);
            await Assert.That(await AnnouncedAsync(window)).Contains("Pick");
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            await chat.TeardownAsync();
            await terminal.TeardownAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_markdown_details_header_is_named_by_its_summary() => RunOnUiAsync(async () => {
        var (window, view, _) = MarkdownViewHarness.Show(
            "<details>\n<summary><strong>Agent Prompt</strong></summary>\n\nbody\n\n</details>", MarkdownFlavor.GitHub);
        try {
            await Assert.That(await AnnouncedAsync(view)).IsEquivalentTo(["Agent Prompt"]);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_history_step_names_its_window_choices() => RunOnUiAsync(async () => {
        var since = new DateOnly(2026, 9, 1);
        var cli = new FakeKcapCli {
            DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(new ImportDiscoveryReport(
                [new ImportDiscoveryRepo("acme", "web", 40, DateTimeOffset.UnixEpoch, [new(since, 5), new(null, 40)])],
                0, [new(since, 5), new(null, 40)])),
        };
        var vm = new HistoryStepViewModel(cli, () => [HarnessId.Claude], action => action(), "test-mac", TimeProvider.System);
        await vm.OnEnterAsync(CancellationToken.None);
        await vm.Discovery;
        vm.ToggleCustomizeCommand.Execute().Subscribe();
        var window = new Window { Content = new HistoryPaneView { DataContext = vm }, Width = 800, Height = 900 };
        window.Show();
        Settle(window);
        try {
            await Assert.That(await AnnouncedAsync(window)).Contains(vm.Windows[0].Label);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}
