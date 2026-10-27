using System.Reactive.Concurrency;
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
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The windows other than the main one: Settings, Launch approvals and the launch consent prompt.
static partial class DocsShotScenes {
    const string LocalServerUrl = "http://localhost:5000";
    const double SettingsHeight = 760;

    /// The settings window over the Northwind profile, not yet shown, open on the tab named `tab`.
    /// Profiles, Accounts and Notifications are always there; Search only with `localSearch`, the
    /// local edition's built-in embedding model being offered and in use; Privacy only with `privacy`,
    /// usage data being sent.
    public static async Task<DocsShotScene> SettingsAsync(TempConfigRoot config, string tab = "DaemonTab",
            bool localSearch = false, bool privacy = false, double height = SettingsHeight) {
        var scene = new DocsShotScene();
        try {
            await ConfigMutator.MutateAsync(config.Root, c => c with {
                ActiveProfile = "northwind",
                Profiles = new() {
                    ["northwind"] = new Profile { ServerUrl = ServerUrl, Daemon = new DaemonSettings { Name = "maya-mbp", MaxAgents = 5 } },
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
                new("acc-1", "claude", "~/.claude", "~/.claude", "personal", "recording"),
                new("acc-2", "claude", "~/.claude-northwind", "~/.claude-northwind", "northwind", "recording"),
                new("acc-3", "codex", "~/.codex", "~/.codex", "main", "recording"),
            ], [new("claude", "~/.claude", "~/.claude"), new("codex", "~/.codex", "~/.codex")]));
            var notifications = scene.Own(new NotificationSettingsService(config.PathTo("notifications.json")));
            await notifications.SaveAsync(new NotificationPreferences(true, true, false));

            var vm = scene.Own(new SettingsViewModel(new SettingsProfileStore(config.Root, "northwind", ServerUrl),
                service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
                (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
                (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true),
                notificationSettings: notifications, notificationAccess: new FakeDesktopNotificationAccess(DesktopNotificationAccess.Allowed),
                profiles: profiles) {
                Accounts = new AccountsSettingsViewModel(accounts,
                    (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()), "maya-mbp", "northwind",
                    () => [], _ => Task.FromResult<string?>(null), service.Status),
                Search = localSearch ? LocalSearch(scene) : null,
                Privacy = privacy ? Privacy(scene, config) : null,
            });
            if (vm.Search is { } search) scene.Settled = () => !search.Choice.Checking && (search.Choice.OllamaMissing || search.Choice.OllamaEnabled);
            var window = scene.Present(new SettingsWindow { DataContext = vm, Height = height });
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>(tab)
                ?? throw new InvalidOperationException($"Settings has no tab named '{tab}'");
            return scene;
        } catch {
            await scene.DisposeAsync();
            throw;
        }
    }

    static PrivacySettingsViewModel Privacy(DocsShotScene scene, TempConfigRoot config) {
        var telemetry = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), config.Root).Telemetry;
        var privacy = scene.Own(new PrivacySettingsViewModel(telemetry, config.Root, new UsageDataNotice(telemetry, config.Root)));
        // The view model reads the process environment, which a figure must never show.
        if (privacy.Overridden is not null || !privacy.SendUsageData)
            throw new InvalidOperationException($"desktop-settings-privacy needs usage data on and the switch free: {privacy.Overridden ?? "it resolved off"}");
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
