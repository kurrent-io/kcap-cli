using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Notifications;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The windows other than the main one: Settings, Launch approvals and the launch consent prompt.
static partial class DocsShotScenes {
    const string LocalServerUrl = "http://localhost:5000";
    const double SettingsWidth = 860;

    /// The settings window over the Northwind profile, not yet shown, open on the page named `tab` and
    /// sized to fit it. Privacy, Daemon, Notifications, Accounts and Profiles are always there; Search
    /// only with `localSearch`, the local edition's built-in embedding model being offered and in use; Usage data
    /// only with `usageData`, usage data being sent; Harnesses only with `harnesses`, Claude Code and
    /// Codex wired and Cursor found but not; About only with `about`.
    public static async Task<DocsShotScene> SettingsAsync(TempConfigRoot config, string tab, double height,
            bool localSearch = false, bool usageData = false, bool harnesses = false, bool about = false,
            double width = SettingsWidth) {
        var scene = new DocsShotScene();
        try {
            await ConfigMutator.MutateAsync(config.Root, c => c with {
                ActiveProfile = "northwind",
                Profiles = new() {
                    ["northwind"] = new Profile {
                        ServerUrl = ServerUrl, Daemon = new DaemonSettings { Name = "maya-mbp", MaxAgents = 5 },
                        DefaultVisibility = "private", AllowedRepos = ["northwind/payments-api", "northwind/storefront"],
                        AllowedPaths = ["~/code"], ExcludedPaths = ["~/code/scratch"],
                    },
                    ["local"] = new Profile { ServerUrl = LocalServerUrl, AuthProvider = new AuthProviderStamp(AuthProvider.None, LocalServerUrl) },
                },
            });
            var tokens = AuthFixtures.NewTokenStore(config.Root);
            await tokens.SaveAsync("northwind", new StoredTokens {
                AccessToken = "docs-shot", ExpiresAt = DateTimeOffset.UtcNow.AddHours(8), Provider = AuthProvider.GitHubApp,
                ServerUrl = ServerUrl, GitHubUsername = "maya-chen",
            });
            var profiles = new ProfilesSettingsViewModel(config.Root, tokens,
                new OnboardingGate(config.Root, tokens, ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System), "northwind",
                (_, _, _) => Task.CompletedTask, (_, _) => Task.FromResult(false));
            await profiles.RefreshAsync();

            var service = new FakeDaemonClientService();
            Northwind(service);
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1", "accounts/1"]));
            var accounts = new FakeAccountsService();
            accounts.Publish(new AccountsSnapshotDto("maya-mbp", 3, [
                Account("acc-1", "claude", "~/.claude", "personal", Usage(ClaudeStatusLineReading.LimitId, 34, 82)),
                Account("acc-2", "claude", "~/.claude-northwind", "northwind", Usage(ClaudeStatusLineReading.LimitId, 12, 41)),
                Account("acc-3", "codex", "~/.codex", "main", Usage(CodexRateLimits.DefaultLimitId, 27, 58)),
            ], [new("claude", "~/.claude", "~/.claude"), new("codex", "~/.codex", "~/.codex")]));
            var notifications = scene.Own(new NotificationSettingsService(config.PathTo("notifications.json")));
            await notifications.SaveAsync(new NotificationPreferences(true, true, false));
            var store = new SettingsProfileStore(config.Root, "northwind", ServerUrl);
            var harnessPage = harnesses ? await Harnesses(store) : null;

            var vm = scene.Own(new SettingsViewModel(store,
                service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
                (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true),
                notificationSettings: notifications, notificationAccess: new FakeDesktopNotificationAccess(DesktopNotificationAccess.Allowed),
                profiles: profiles, harnesses: harnessPage) {
                Accounts = new AccountsSettingsViewModel(accounts,
                    (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()), "maya-mbp", "northwind",
                    () => [], _ => Task.FromResult<string?>(null), service.Status, usageClock: Observable.Return(Now)),
                Search = localSearch ? LocalSearch(scene) : null,
                Privacy = usageData ? UsageData(scene, config) : null,
                About = about ? await About(service) : null,
            });
            if (vm.Search is { } search) scene.Settled = () => !search.Choice.Checking && (search.Choice.OllamaMissing || search.Choice.OllamaEnabled);
            var window = scene.Present(new SettingsWindow { DataContext = vm, Width = width, Height = height });
            var page = window.FindControl<TabItem>(tab) ?? throw new InvalidOperationException($"Settings has no page named '{tab}'");
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            // The Profiles page is a hidden tab, which the tab strip deselects while it builds itself.
            tabs.SelectedItem = page;
            window.Opened += (_, _) => tabs.SelectedItem = page;
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    static AccountViewDto Account(string id, string vendor, string directory, string label, AccountReadingDto reading) =>
        new(id, vendor, directory, directory, label, "recording", SignIn: SignInStates.SignedIn, Generation: 1, Reading: reading);

    /// A reading taken five minutes ago: the five-hour and weekly windows at the given use, each
    /// resetting within a day so the figure reads the same in every time zone.
    static AccountReadingDto Usage(string limitId, double fiveHour, double week) =>
        new(1, [
            new(limitId, 300, fiveHour, Now.AddHours(2).AddMinutes(40), Now.AddMinutes(-5)),
            new(limitId, 10080, week, Now.AddHours(19).AddMinutes(15), Now.AddMinutes(-5)),
        ], null, null, null);

    /// Owned by the settings view model, which disposes it.
    static async Task<HarnessesSettingsViewModel> Harnesses(SettingsProfileStore store) {
        IReadOnlyDictionary<HarnessId, DetectedAgent> found = new Dictionary<HarnessId, DetectedAgent> {
            [HarnessId.Claude] = new(true, true),
            [HarnessId.Codex] = new(true, true),
            [HarnessId.Cursor] = new(true, true),
        };
        var harnesses = new HarnessesSettingsViewModel(store, new FakeKcapCli(), _ => Task.FromResult(found),
            isWired: id => id == HarnessId.Claude || id == HarnessId.Codex);
        await harnesses.RescanAsync();
        if (harnesses.Rows.Count != 3)
            throw new InvalidOperationException($"desktop-settings-harnesses found {harnesses.Rows.Count} tools, not 3: {harnesses.Message}");
        return harnesses;
    }

    static async Task<AboutSettingsViewModel> About(FakeDaemonClientService service) {
        var about = new AboutSettingsViewModel(service, _ => Task.FromResult<string?>("1.4.0"));
        await about.LoadAsync();
        return about;
    }

    static PrivacySettingsViewModel UsageData(DocsShotScene scene, TempConfigRoot config) {
        var telemetry = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), config.Root).Telemetry;
        var privacy = scene.Own(new PrivacySettingsViewModel(telemetry, config.Root, new UsageDataNotice(telemetry, config.Root)));
        // The view model reads the process environment, which a figure must never show.
        if (privacy.Overridden is not null || !privacy.SendUsageData)
            throw new InvalidOperationException($"desktop-settings-usage-data needs usage data on and the switch free: {privacy.Overridden ?? "it resolved off"}");
        return privacy;
    }

    static LocalSearchViewModel LocalSearch(DocsShotScene scene) {
        var cli = new FakeKcapCli();
        LocalUpgradeController? upgrade = null;
        var search = new LocalSearchController(() => cli, action => action(), held: () => upgrade?.Running == true);
        upgrade = new LocalUpgradeController(() => cli, action => action(), held: () => search.Running);
        LocalEmbeddingModelManifest builtin = new("builtin-minilm", 1, new('e', 64), [new("model.onnx", new('f', 64), 90L * 1024 * 1024)]);
        var lane = scene.Own(new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connected)));
        return scene.Own(new LocalSearchViewModel(() => LocalEmbeddingSetting.Builtin(builtin.ModelSet),
            _ => Task.FromResult<LocalEmbeddingModelManifest?>(builtin), lane, search, upgrade, () => Task.CompletedTask,
            CancellationToken.None, ImmediateScheduler.Instance));
    }

    /// The Launch approvals window with its decision log read: the rules and prompts that settled
    /// recent launches on Maya's machine.
    public static async Task<DocsShotScene> LaunchApprovalsAsync() {
        var scene = new DocsShotScene();
        try {
            static ConsentDecisionRecord Decision(int minutesAgo, string requester, string display, string kind, string repo, string outcome, string source) =>
                new(Now.AddMinutes(-minutesAgo).ToString("O"), $"agent-{minutesAgo}", requester, false, kind, repo, "claude", outcome, source, display);
            IReadOnlyList<ConsentDecisionRecord> log = [
                Decision(4, "sam", "Sam Reyes", "review-flow", StorefrontRepo, "allowed", "prompt_user"),
                Decision(26, "dana", "Dana Okafor", "review-flow", PaymentsRepo, "allowed", "rule[0]"),
                Decision(75, "sam", "Sam Reyes", "agent", PaymentsRepo, "denied", "rule[1]"),
                Decision(190, "dana", "Dana Okafor", "review", StorefrontRepo, "denied", "prompt_timeout"),
                Decision(320, "maya", "Maya Chen", "agent", PaymentsRepo, "allowed", "owner"),
            ];
            var activity = scene.Own(new ActivityViewModel(() => new ConsentLogReadResult(log, true), () => "docs", new FakeTicker()));
            activity.RequestRefresh();
            await (activity.PendingRefreshForTesting ?? Task.CompletedTask);
            scene.Present(new LaunchApprovalsWindow { DataContext = activity });
            scene.Defer(() => activity.PendingRefreshForTesting ?? Task.CompletedTask);
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    /// The prompt asking Maya to allow a review flow Sam requested on the storefront.
    public static async Task<DocsShotScene> ConsentPromptAsync() {
        var scene = new DocsShotScene();
        try {
            var consent = scene.Own(new FakeConsentService());
            consent.Add(ConsentEntries.Entry(requestId: "c1", promptId: "p1", requester: "sam", requesterDisplay: "Sam Reyes",
                kind: "review-flow", repoPath: StorefrontRepo, vendor: "claude", requestedAt: Now.AddSeconds(-12), timeoutSeconds: 60));
            scene.Present(new ConsentPromptWindow {
                DataContext = new ConsentPromptViewModel(consent, new AppNotifier(), new FakeTicker(), new FakeTimeProvider(Now), CancellationToken.None),
            });
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }
}
