using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// The fictional Northwind tenant every docs figure is rendered over, so no real host, path or user
/// reaches a published image.
static partial class DocsShotScenes {
    public const string OutEnvVar = "KCAP_SHOTS_OUT";

    public static readonly string[] Names = [
        "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
        "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-wizard-history", "desktop-local-recovery",
        "desktop-launcher", "desktop-pull-request", "desktop-work-context", "desktop-rail-grouped",
        "desktop-settings-notifications", "desktop-settings-accounts", "desktop-settings-profiles", "desktop-settings-search",
        "desktop-launch-approvals", "desktop-consent-prompt", "desktop-wizard-agents", "desktop-wizard-visibility",
        "desktop-settings-privacy", "desktop-wizard-usage-data", "desktop-settings-usage-data", "desktop-settings-harnesses",
            "desktop-settings-about"];

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

    public static void Northwind(FakeDaemonClientService service, bool moreSessions = false) {
        service.DaemonName = "maya-mbp";
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
        if (!moreSessions) return;
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a4", "agent", "claude", PaymentsRepo, "Completed",
            null, null, "maya", now.AddDays(-1), null, "Maya Chen",
            Title: "Add idempotency keys to refunds", SessionId: "s4"));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a5", "agent", "claude", StorefrontRepo, "Running",
            null, null, "maya", now.AddMinutes(-5), null, "Maya Chen",
            Title: "Lazy-load the product images", SessionId: "s5"));
    }

    public static BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>> PullRequests() =>
        new(new Dictionary<string, RailPullRequestSummary> {
            ["s1"] = new([new(PullRequestWire.Subject(NorthwindPullRequestSource.Link), NorthwindPullRequestSource.Title, "Open")]),
            ["s2"] = new([new(new PullRequestSubjectDto {
                Provider = "github", Host = "github.com", RepoHash = "northwind-storefront", Owner = "northwind", RepoName = "storefront", Number = 118,
            }, "Round totals at the line level", "Merged")]),
        });

    /// The payments session's work context: one in-flight work item Maya is on.
    static WorkContextRead PaymentsWorkContext() {
        var row = Assignment("w1", "PAY-142", primary: true);
        return new WorkContextRead(WorkContextReadKind.Ready, [row], row, PaymentsItem(), null, new SessionSummaryDto { SessionId = "s1" }, false, false, false, null);
    }

    /// PAY-142 as the work-context figure shows it: split into two parts, shared with Dana, part of a
    /// larger effort and waiting on another item.
    public static WorkContextRead PaymentsWorkContextInFull() {
        var row = Assignment("w1", "PAY-142", primary: true);
        var item = PaymentsItem() with {
            Overview = "Charges fail outright when the payments gateway answers 503 during a deploy. Retry the transient failures with backoff, without ever charging twice.",
            Parts = [
                new WorkItemPartDto { WorkItemId = "w2", Title = "Classify transient gateway errors", Ordinal = 1, IsSettled = true, SettledAt = Now.AddHours(-3) },
                new WorkItemPartDto { WorkItemId = "w3", Title = "Retry policy with backoff", Ordinal = 2 },
            ],
            Contributors = [
                new WorkItemContributorDto { UserId = "maya", DisplayName = "Maya Chen", LastActivityAt = Now.AddMinutes(-2) },
                new WorkItemContributorDto { UserId = "dana", DisplayName = "Dana Okafor", LastActivityAt = Now.AddMinutes(-12) },
            ],
            SessionCount = 3,
        };
        var topology = new WorkItemTopologyDto {
            Item = new WorkItemRefDto { WorkItemId = "w1", Title = RetryTitle },
            PartOf = new WorkItemRefDto { WorkItemId = "w0", Title = "Payments reliability" },
            BlockedBy = [new WorkItemRefDto { WorkItemId = "w9", Title = "Rotate the gateway sandbox credentials" }],
        };
        return new WorkContextRead(WorkContextReadKind.Ready, [row, Assignment("w3", "Retry policy with backoff", primary: false)], row, item,
            topology, new SessionSummaryDto { SessionId = "s1" }, false, false, false, null);
    }

    static SessionWorkItemAssignmentDto Assignment(string id, string label, bool primary) =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = primary };

    static WorkItemDto PaymentsItem() => new() {
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

    /// The plan the payments session declared, part way through.
    public static SessionPlansRead PaymentsPlan() {
        static PlanLedgerTaskDto Step(int ordinal, string title, string status) =>
            new() { TaskId = $"p{ordinal}", Ordinal = ordinal, Title = title, Status = status, Source = "agent" };
        return new(SessionPlansReadKind.Ready, [new SessionPlanDto {
            PlanId = "plan-1", IsCurrent = true,
            Tasks = [
                Step(1, "Classify the transient gateway status codes", "completed"),
                Step(2, "Wrap GatewayClient in a retry policy", "completed"),
                Step(3, "Carry the idempotency key across attempts", "in_progress"),
                Step(4, "Replay the 503 burst in the integration suite", "pending"),
            ],
        }]);
    }

    /// The main window over the Northwind tenant, not yet shown. With `openSession` the payments
    /// session is open and its transcript read. With `localServerDown` the server is the local
    /// edition's and its lane is retrying, so the launcher offers to start it. With `remoteMachine`
    /// Maya owns a second daemon, so the launcher offers a machine choice. `arrange` runs last, over
    /// the finished window model.
    public static async Task<DocsShotScene> MainWindowAsync(
            IReadOnlySet<string>? pendingIds = null, IReadOnlyList<PendingPermissionRequest>? permissionRequests = null,
            Action<FeedbackCategory>? openFeedback = null, Action? requestSetup = null,
            bool openSession = true, bool localServerDown = false, WorkContextRead? workContext = null,
            SessionPlansRead? plan = null, bool moreSessions = false, bool remoteMachine = false,
            Func<MainWindowViewModel, Task>? arrange = null) {
        var scene = new DocsShotScene();
        try {
            var vm = await BuildMainWindowAsync(scene, pendingIds, permissionRequests, openFeedback, requestSetup, openSession,
                localServerDown, workContext ?? PaymentsWorkContext(), plan, moreSessions, remoteMachine);
            if (arrange is not null) await arrange(vm);
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    /// The launcher with Maya's next task typed and every chip chosen, a log file staged beside it.
    public static Task<DocsShotScene> LauncherAsync() => MainWindowAsync(openSession: false, remoteMachine: true, arrange: async vm => {
        var home = vm.Home!;
        await home.SelectRepositoryAsync(PaymentsRepo);
        home.Goal = "Add retry to the payments gateway";
        home.SelectedModel = "claude-opus-5";
        home.SelectedEffort = "high";
        home.SelectedPermissionMode = ClaudePermissionModes.AcceptEdits;
        home.Tray.AddAll([new StagedAttachment("gateway-503.log", "text/plain", new byte[18 * 1024])]);
        await WorkspaceFixtures.WaitUntilAsync(() => home.MachinePickerVisible, what: "the machine chip");
    });

    /// The payments session's pull request open on its Checks tab. The reader reads only while its
    /// window shows, so the scene settles once the checks have been read there.
    public static async Task<DocsShotScene> PullRequestAsync() {
        PullRequestContextViewModel? reader = null;
        var scene = await MainWindowAsync(arrange: vm => {
            vm.OpenSessionPullRequest("a1", AgentOrigin.Local, PullRequestWire.Subject(NorthwindPullRequestSource.Link));
            reader = ((WorkspaceViewModel)vm.CurrentWorkspace!).PullRequests!;
            return Task.CompletedTask;
        });
        scene.Settled = () => {
            if (reader!.CanReveal && !reader.IsChecks) reader.SelectedTabIndex = 1;
            return reader.CheckRows.Count == 4 && !reader.IsReading && reader.ShowsFix;
        };
        return scene;
    }

    /// The payments session with its work context read in full, people and plan opened, in a window
    /// tall enough that the whole panel shows.
    public static async Task<DocsShotScene> WorkContextAsync() {
        var scene = await MainWindowAsync(workContext: PaymentsWorkContextInFull(), plan: PaymentsPlan(), arrange: async vm => {
            var context = ((WorkspaceViewModel)vm.CurrentWorkspace!).WorkContext;
            await WorkspaceFixtures.WaitUntilAsync(() => context.IsReady && context.HasParts && context.Plan.HasTasks, what: "the work context");
            await context.TogglePeopleCommand.Execute();
            await context.Plan.ToggleCommand.Execute();
        });
        scene.Window.Height = 1120;
        return scene;
    }

    static async Task<MainWindowViewModel> BuildMainWindowAsync(DocsShotScene scene, IReadOnlySet<string>? pendingIds,
            IReadOnlyList<PendingPermissionRequest>? permissionRequests, Action<FeedbackCategory>? openFeedback,
            Action? requestSetup, bool openSession, bool localServerDown, WorkContextRead workContextRead,
            SessionPlansRead? plan, bool moreSessions, bool remoteMachine) {
        var transcript = openSession ? scene.Own(new TempDir()) : null;
        var homeState = scene.Own(TempDir.WithPathTo("app-state.json", out var homeStatePath));
        var service = new FakeDaemonClientService();
        Northwind(service, moreSessions);
        var permissions = new FakePermissionService();
        foreach (var request in permissionRequests ?? []) permissions.Add(request);
        var actions = new AgentActionService(new ScriptedLocalControlOps(), new AppNotifier(), new RecordingOpener(),
            service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        var pullRequests = scene.Own(PullRequests());
        var attach = new FakeTerminalAttachClientFactory();
        var workContext = new FakeWorkContextSource { Default = workContextRead };
        var plans = plan is null ? null : new FakePlanSource { Default = plan };
        var clock = new FakeTimeProvider(Now);
        var linkedPullRequests = new NorthwindPullRequestSource(clock);
        IReadOnlyList<DaemonInfo> machines = remoteMachine
            ? [new DaemonInfo { Name = "maya-studio", OwnerUserId = "maya", Connected = true, Platform = "linux", RepoPaths = [PaymentsRepo] }]
            : [];
        var home = scene.Own(new HomeViewModel(service, new AppStateStore(homeStatePath), new NoLaunch(),
            () => Task.FromResult<string[]>([PaymentsRepo, StorefrontRepo]), clock,
            daemons: Observable.Return(machines), viewerId: _ => Task.FromResult<string?>("maya")));
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
                new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: linkedPullRequests, plans: plans),
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
        return vm;
    }

    /// The setup wizard from the real wizard graph, opened on `startAt` before anything has been chosen.
    public static Task<DocsShotScene> WizardAsync(TempConfigRoot config, WizardStepId startAt) =>
        WizardAsync(config, startAt, null);

    /// The wizard on its History step once discovery has read Maya's machine. The graph's own History
    /// step names this host, so the scene puts one naming hers in its place.
    public static Task<DocsShotScene> WizardHistoryAsync(TempConfigRoot config) {
        var cli = new FakeKcapCli { DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(NorthwindHistory()) };
        var history = new HistoryStepViewModel(cli, () => [HarnessId.Claude, HarnessId.Codex], action => action(),
            "maya-mbp", new FakeTimeProvider(Now));
        return WizardAsync(config, WizardStepId.Import, history, () => history.Discovery);
    }

    /// The wizard on its harnesses step for Maya's machine, with Claude Code, Codex and Cursor found,
    /// all three recording, Cursor without the tools and new sessions readable only by her. The
    /// graph's own step names this host, so the scene puts one naming hers in its place. On the local
    /// edition the step asks no audience, so the table sits under the step counter.
    public static Task<DocsShotScene> WizardHarnessesAsync(TempConfigRoot config, bool localEdition = false) {
        IReadOnlyDictionary<HarnessId, DetectedAgent> found = new Dictionary<HarnessId, DetectedAgent> {
            [HarnessId.Claude] = new(true, true),
            [HarnessId.Codex] = new(true, true),
            [HarnessId.Cursor] = new(true, true),
        };
        var harnesses = new HarnessesStepViewModel(new FakeKcapCli(), _ => Task.FromResult(found),
            declined: () => FrozenSet<HarnessId>.Empty, stampOffered: _ => { }, config.Root, pathFix: null,
            providerKeys: _ => Task.FromResult<IReadOnlySet<string>?>(null), "maya-mbp", onLocalPath: () => localEdition);
        return WizardAsync(config, WizardStepId.Harnesses, harnesses, () => {
            harnesses.Rows.Single(r => r.Label == "Cursor").Tools = false;
            harnesses.Visibility = "private";
            return Task.CompletedTask;
        });
    }

    /// The wizard on its usage-data step with Share chosen, as a device that has not yet seen the notice
    /// meets it.
    public static Task<DocsShotScene> WizardUsageDataAsync(TempConfigRoot config) {
        var telemetry = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), config.Root).Telemetry;
        var usage = new UsageDataStepViewModel(new UsageDataNotice(telemetry, config.Root));
        // The notice reads the process environment, which can leave the step out or the choice hidden.
        if (!usage.Applicable || !usage.ChoiceVisible || !usage.Share)
            throw new InvalidOperationException("desktop-wizard-usage-data needs usage data on, the notice due and the choice offered");
        return WizardAsync(config, WizardStepId.Privacy, usage);
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

    /// `replacement` takes the place of the graph's step with the same id; `arrange` runs once the
    /// wizard has entered `startAt`.
    static async Task<DocsShotScene> WizardAsync(TempConfigRoot config, WizardStepId startAt, IWizardStep? replacement,
            Func<Task>? arrange = null) {
        var scene = new DocsShotScene();
        try {
            var harness = scene.Own(new WizardFixtures.GraphHarness(config.Root));
            var graph = WizardComposition.BuildGraph(harness.Options());
            IReadOnlyList<IWizardStep> steps = replacement is null
                ? graph.Steps
                : [.. graph.Steps.Select(s => s.Id == replacement.Id ? replacement : s)];
            var vm = new OnboardingViewModel(steps, startAt: startAt);
            await vm.PendingEnterForTesting;
            if (arrange is not null) await arrange();
            scene.Present(new Views.MainWindow { Onboarding = vm, Width = 760, Height = 760 });
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    sealed class NoLaunch : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, null));
    }
}
