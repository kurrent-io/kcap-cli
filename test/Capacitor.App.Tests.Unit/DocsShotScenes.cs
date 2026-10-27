using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// The fictional Northwind tenant every docs figure is rendered over, so no real host, path or user
/// reaches a published image.
static class DocsShotScenes {
    public const string OutEnvVar = "KCAP_SHOTS_OUT";

    public static readonly string[] Names = [
        "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
        "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-wizard-history", "desktop-local-recovery"];

    const string ServerUrl = "https://northwind.kcap.ai";
    const string PaymentsRepo = "/Users/maya/dev/northwind/payments-api";
    const string StorefrontRepo = "/Users/maya/dev/northwind/storefront";
    const string RetryTitle = "Retry transient gateway failures";

    static readonly DateTimeOffset Now = new(2026, 10, 26, 14, 0, 0, TimeSpan.Zero);

    static readonly string[] Transcript = [
        """{"type":"user","message":{"role":"user","content":"The payments gateway drops requests on a 503. Add a retry with backoff."}}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"I will wrap the gateway client in a retry policy: three attempts with exponential backoff, transient failures only. Then I will run the tests."}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test test/Payments.Gateway.Tests"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Passed! 48 tests, 0 failed"}]}}""",
    ];

    public static void Northwind(FakeDaemonClientService service) {
        service.SnapshotsSubject.OnNext(Snap(daemon: "maya-mbp", version: "1.4.0", serverUrl: ServerUrl, connection: "connected", active: 2, max: 5));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var now = Now.UtcDateTime;
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", PaymentsRepo, "Running",
            null, null, "maya", now.AddMinutes(-12), null, "Maya Chen", HasTerminal: true,
            Title: "Add retry to the payments gateway", WorktreePath: PaymentsRepo, SessionId: "s1",
            Branch: "feat/gateway-retry"));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a2", "agent", "codex", StorefrontRepo, "Running",
            null, null, null, now.AddMinutes(-35), null, null,
            Title: "Fix the checkout total rounding", SessionId: "s2", AwaitingInput: true));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a3", "agent", "claude", StorefrontRepo, "Completed",
            null, null, null, now.AddHours(-2), null, null,
            Title: "Tidy the README", SessionId: "s3"));
    }

    public static BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>> PullRequests() =>
        new(new Dictionary<string, RailPullRequestSummary> {
            ["s1"] = new([new(Subject("payments-api", 42), RetryTitle, "Open")]),
            ["s2"] = new([new(Subject("storefront", 118), "Round totals at the line level", "Merged")]),
        });

    /// The payments session's work context: one in-flight work item Maya is on.
    static WorkContextRead PaymentsWorkContext() {
        var row = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "PAY-142", Source = "mcp", Confidence = 1, IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId = "w1",
            Title = RetryTitle,
            Key = new WorkItemKeyDto { ShortKey = "PAY-142", Provider = "linear", Kind = "issue", Value = "PAY-142" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Links = [new WorkItemLinkDto {
                Kind = "issue", Provider = "linear", Value = "PAY-142", ShortKey = "PAY-142",
                Url = "https://linear.app/northwind/issue/PAY-142", Title = RetryTitle, LinkClass = "link", IsSeed = true,
            }],
            Contributors = [new WorkItemContributorDto { UserId = "maya", DisplayName = "Maya Chen" }],
            SessionCount = 1,
        };
        return new WorkContextRead(WorkContextReadKind.Ready, [row], row, item, null, new SessionSummaryDto { SessionId = "s1" }, false, false, false, null);
    }

    static PullRequestSubjectDto Subject(string repo, int number) => new() {
        Provider = "github", Host = "github.com", RepoHash = $"northwind-{repo}", Owner = "northwind", RepoName = repo, Number = number,
    };

    /// The main window over the Northwind tenant, not yet shown. With `openSession` the payments
    /// session is open and its transcript read. With `localServerDown` the server is the local
    /// edition's and its lane is retrying, so the launcher offers to start it.
    public static async Task<DocsShotScene> MainWindowAsync(
            IReadOnlySet<string>? pendingIds = null, IReadOnlyList<PendingPermissionRequest>? permissionRequests = null,
            Action<FeedbackCategory>? openFeedback = null, Action? requestSetup = null,
            bool openSession = true, bool localServerDown = false) {
        var scene = new DocsShotScene();
        try {
            await BuildMainWindowAsync(scene, pendingIds, permissionRequests, openFeedback, requestSetup, openSession, localServerDown);
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    static async Task BuildMainWindowAsync(DocsShotScene scene, IReadOnlySet<string>? pendingIds,
            IReadOnlyList<PendingPermissionRequest>? permissionRequests, Action<FeedbackCategory>? openFeedback,
            Action? requestSetup, bool openSession, bool localServerDown) {
        var transcript = openSession ? scene.Own(new TempDir()) : null;
        var homeState = scene.Own(TempDir.WithPathTo("app-state.json", out var homeStatePath));
        var service = new FakeDaemonClientService();
        Northwind(service);
        var permissions = new FakePermissionService();
        foreach (var request in permissionRequests ?? []) permissions.Add(request);
        var actions = new AgentActionService(new ScriptedLocalControlOps(), new AppNotifier(), new RecordingOpener(),
            service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        var pullRequests = scene.Own(PullRequests());
        var attach = new FakeTerminalAttachClientFactory();
        var workContext = new FakeWorkContextSource { Default = PaymentsWorkContext() };
        var clock = new FakeTimeProvider(Now);
        var linkedPullRequests = new FakePullRequestSource(clock) {
            Links = [new PullRequestLinkDto {
                Provider = "github", Host = "github.com", RepoHash = "northwind-payments-api", Owner = "northwind", RepoName = "payments-api",
                Number = 42, Url = "https://github.com/northwind/payments-api/pull/42", Title = RetryTitle, HeadRef = "feat/gateway-retry",
            }],
            TitleFor = _ => RetryTitle,
        };
        var home = scene.Own(new HomeViewModel(service, new AppStateStore(homeStatePath), new NoLaunch(),
            () => Task.FromResult<string[]>([PaymentsRepo, StorefrontRepo]), clock));
        BehaviorSubject<ServerLaneStatus>? lane = null;
        if (localServerDown) {
            service.SnapshotsSubject.OnNext(Snap(daemon: "maya-mbp", version: "1.4.0", serverUrl: ServerUrl, connection: "disconnected", active: 2, max: 5));
            lane = scene.Own(new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying, "Connection refused")));
            var localServer = scene.Own(new LocalServerRecoveryViewModel(lane, isLocalServer: true,
                _ => Task.FromResult(new ProcessResult(0, "", "", false)), () => Task.CompletedTask, CancellationToken.None));
            home.AttachLocalServerRecovery(localServer);
        }
        var web = scene.Own(new WebWorkspaceViewModel(ServerUrl, new RecordingOpener(), _ => { }, CancellationToken.None));
        MainWindowViewModel? vm = null;
        var directory = new AgentDirectory(
            service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
            path => path, null, ServerUrl, clock);
        var rail = scene.Own(new SessionRailViewModel(
            directory, id => vm!.OpenSession(id), _ => { }, clock,
            agentsWithPending: pendingIds is null ? null : Observable.Return(pendingIds),
            pullRequests: pullRequests, openPullRequest: (id, origin, subject) => vm!.OpenSessionPullRequest(id, origin, subject)));
        var workspaceTeardowns = new List<Task>();
        vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            trackWorkspaceTeardown: teardown => workspaceTeardowns.Add(teardown()),
            workspaceFactory: id => new WorkspaceViewModel(
                id, service, actions, attach.Factory, () => new FakeTerminalSurface(),
                clock, new RecordingOpener(), permissions, workContext,
                new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: linkedPullRequests),
            rail: rail, home: home, tenantName: localServerDown ? "local" : "northwind", laneStatus: lane,
            openFeedback: openFeedback, requestSetup: requestSetup) { Web = web };
        scene.Present(new Views.MainWindow { DataContext = vm });
        Task? pendingRead = null;
        scene.Defer(async () => {
            vm.CloseWorkspace();
            Dispatcher.UIThread.RunJobs();
            await Task.WhenAll(workspaceTeardowns);
            await (pendingRead ?? Task.CompletedTask);
        });

        if (transcript is not null) {
            var transcriptPath = transcript.CreateFile("transcript.jsonl", string.Join('\n', Transcript));
            vm.OpenSession("a1");
            Dispatcher.UIThread.RunJobs();
            service.Agents.AddOrUpdate(service.Agents.Lookup("a1").Value with { TranscriptPath = transcriptPath });
            Dispatcher.UIThread.RunJobs();
            var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await (workspace.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (workspace.Chat!.PendingReadForTesting ?? Task.CompletedTask);
            pendingRead = workspace.Chat.PendingReadForTesting;
            Dispatcher.UIThread.RunJobs();
            await attach.Created[^1].TriggerAttached([]);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// The settings window over the Northwind profile, not yet shown, tall enough that the whole
    /// Daemon tab shows without scrolling.
    public static async Task<DocsShotScene> SettingsAsync(TempConfigRoot config) {
        var scene = new DocsShotScene();
        try {
            ConfigMutator.Mutate(config.Root, c => c with { Profiles = new() {
                ["northwind"] = new Profile { ServerUrl = ServerUrl, Daemon = new DaemonSettings { Name = "maya-mbp", MaxAgents = 5 } }
            } });
            var service = new FakeDaemonClientService();
            Northwind(service);
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
            var vm = scene.Own(new SettingsViewModel(new SettingsProfileStore(config.Root, "northwind", ServerUrl),
                service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
                (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true)));
            scene.Present(new SettingsWindow { DataContext = vm, Height = SettingsHeight });
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    const double SettingsHeight = 760;

    /// The setup wizard from the real wizard graph, opened on `startAt` before anything has been chosen.
    public static Task<DocsShotScene> WizardAsync(TempConfigRoot config, WizardStepId startAt) =>
        WizardAsync(config, startAt, null);

    /// The wizard on its History step once discovery has read Maya's machine. The graph's own History
    /// step names this host, so the scene puts one naming hers in its place.
    public static Task<DocsShotScene> WizardHistoryAsync(TempConfigRoot config) {
        var cli = new FakeKcapCli { DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(NorthwindHistory()) };
        var history = new HistoryStepViewModel(cli, () => [HarnessId.Claude, HarnessId.Codex], action => action(),
            "maya-mbp", new FakeTimeProvider(Now));
        return WizardAsync(config, WizardStepId.Import, history);
    }

    static ImportDiscoveryReport NorthwindHistory() {
        var d30 = new DateOnly(2026, 9, 26);
        var d90 = new DateOnly(2026, 7, 28);
        ImportDiscoveryRepo Repo(string name, int all, int last90, int last30, int daysAgo) =>
            new("northwind", name, all, Now.AddDays(-daysAgo), [new(d30, last30), new(d90, last90), new(null, all)]);

        return new([
            Repo("payments-api", 18, 14, 6, 0),
            Repo("storefront", 12, 9, 4, 1),
            Repo("inventory-service", 7, 5, 2, 3),
            Repo("design-system", 5, 3, 1, 9),
        ], 0, [new(d30, 13), new(d90, 31), new(null, 42)]);
    }

    static async Task<DocsShotScene> WizardAsync(TempConfigRoot config, WizardStepId startAt, HistoryStepViewModel? history) {
        var scene = new DocsShotScene();
        try {
            var harness = scene.Own(new WizardFixtures.GraphHarness(config.Root));
            var graph = WizardComposition.BuildGraph(harness.Options());
            IReadOnlyList<IWizardStep> steps = history is null
                ? graph.Steps
                : [.. graph.Steps.Select(s => s.Id == history.Id ? history : s)];
            var vm = new OnboardingViewModel(steps, startAt: startAt);
            await vm.PendingEnterForTesting;
            if (history is not null) await history.Discovery;
            scene.Present(new Views.MainWindow { Onboarding = vm, Width = 760, Height = 760 });
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    sealed class NoLaunch : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, null));
    }
}
