using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.ChatTabViewSmokeTests;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// The fictional Northwind tenant every docs figure is rendered over, so no real host, path or user
/// reaches a published image.
static class DocsShotScenes {
    public const string OutEnvVar = "KCAP_SHOTS_OUT";

    public static readonly string[] Names = [
        "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
        "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-local-recovery"];

    const string ServerUrl = "https://northwind.kcap.ai";
    const string PaymentsRepo = "/Users/maya/dev/northwind/payments-api";
    const string StorefrontRepo = "/Users/maya/dev/northwind/storefront";

    public static void Northwind(FakeDaemonClientService service) {
        service.SnapshotsSubject.OnNext(Snap(daemon: "maya-mbp", version: "1.4.0", serverUrl: ServerUrl, connection: "connected", active: 3, max: 5));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var now = DateTime.UtcNow;
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
            ["s1"] = new([new(Subject("payments-api", 42), "Retry transient gateway failures", "Open")]),
            ["s2"] = new([new(Subject("storefront", 118), "Round totals at the line level", "Merged")]),
        });

    /// The payments session's work context: one in-flight work item Maya is on.
    static WorkContextRead PaymentsWorkContext() {
        var row = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "PAY-142", Source = "mcp", Confidence = 1, IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId = "w1",
            Title = "Retry transient gateway failures",
            Key = new WorkItemKeyDto { ShortKey = "PAY-142", Provider = "linear", Kind = "issue", Value = "PAY-142" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Links = [new WorkItemLinkDto {
                Kind = "issue", Provider = "linear", Value = "PAY-142", ShortKey = "PAY-142",
                Url = "https://linear.app/northwind/issue/PAY-142", Title = "Retry transient gateway failures", LinkClass = "link", IsSeed = true,
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
    /// session is open and its transcript read; the transcript's directory lives until the window closes.
    public static async Task<(MainWindowViewModel Vm, Views.MainWindow Window, FakePermissionService Permissions)> MainWindowAsync(
            IReadOnlySet<string>? pendingIds = null, Action<FeedbackCategory>? openFeedback = null,
            Action? requestSetup = null, bool openSession = true) {
        var service = new FakeDaemonClientService();
        Northwind(service);
        var permissions = new FakePermissionService();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), new AppNotifier(), new RecordingOpener(),
            service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        var pullRequests = PullRequests();
        var attach = new FakeTerminalAttachClientFactory();
        var workContext = new FakeWorkContextSource { Default = PaymentsWorkContext() };
        MainWindowViewModel? vm = null;
        var directory = new AgentDirectory(
            service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
            path => path, null, ServerUrl, TimeProvider.System);
        var rail = new SessionRailViewModel(
            directory, id => vm!.OpenSession(id), _ => { }, TimeProvider.System,
            agentsWithPending: pendingIds is null ? null : Observable.Return(pendingIds),
            pullRequests: pullRequests, openPullRequest: (id, origin, subject) => vm!.OpenSessionPullRequest(id, origin, subject));
        vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            workspaceFactory: id => new WorkspaceViewModel(
                id, service, actions, attach.Factory, () => new FakeTerminalSurface(),
                new FakeTimeProvider(), new RecordingOpener(), permissions, workContext,
                new ScriptedLocalControlOps(), new NoAttachmentUploader()),
            rail: rail, tenantName: "northwind", openFeedback: openFeedback, requestSetup: requestSetup);
        var window = new Views.MainWindow { DataContext = vm };
        TempDir? transcript = null;
        window.Closed += (_, _) => {
            rail.Dispose();
            pullRequests.Dispose();
            transcript?.Dispose();
        };

        if (openSession) {
            transcript = TempDir.WithPathTo("transcript.jsonl", out var path);
            await File.WriteAllLinesAsync(path, [
                UserLine.Replace("hello", "The payments gateway drops requests on a 503. Add a retry with backoff."),
                AssistantLinkLine.Replace("See [docs](https://example.com/docs) now.",
                    "I will wrap the gateway client in a retry policy: three attempts with exponential backoff, transient failures only. Then I will run the tests."),
                ToolCallLine.Replace("ls -la", "dotnet test test/Payments.Gateway.Tests"),
                ToolResultLine.Replace("\"ok\"", "\"Passed! 48 tests, 0 failed\""),
            ]);
            vm.OpenSession("a1");
            Dispatcher.UIThread.RunJobs();
            service.Agents.AddOrUpdate(service.Agents.Lookup("a1").Value with { TranscriptPath = path });
            Dispatcher.UIThread.RunJobs();
            var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await (workspace.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (workspace.Chat!.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            await attach.Created[^1].TriggerAttached([]);
            Dispatcher.UIThread.RunJobs();
        }

        return (vm, window, permissions);
    }
}
