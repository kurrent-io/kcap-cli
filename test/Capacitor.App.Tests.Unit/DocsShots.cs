using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

/// Renders the desktop app's docs figures into KCAP_SHOTS_OUT, which must be the absolute path of an
/// existing directory; skipped when it is unset.
[NotInParallel("AvaloniaSession")]
public sealed class DocsShots {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int MaxPumps = 200;

    static string OutDir {
        get {
            var dir = Environment.GetEnvironmentVariable(DocsShotScenes.OutEnvVar);
            Skip.When(dir is null,
                $"Docs screenshot harness: set {DocsShotScenes.OutEnvVar} to the absolute path of an existing directory to render the figures");
            if (!Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                throw new InvalidOperationException(
                    $"{DocsShotScenes.OutEnvVar} is '{dir}', which is not the absolute path of an existing directory");
            return dir;
        }
    }

    /// Renders the scene to the file the calling test's DocsShot attribute names, with no text caret.
    static async Task CaptureAsync(Func<Task<DocsShotScene>> build, string sentinelControlName,
            Action<Window>? afterShow = null, [CallerMemberName] string test = "") {
        var name = typeof(DocsShots).GetMethod(test)?.GetCustomAttribute<DocsShotAttribute>()?.Name
            ?? throw new InvalidOperationException($"{test} captures a docs figure but carries no DocsShot attribute");
        var path = Path.Combine(OutDir, $"{name}-dark.png");
        await AvaloniaSession.DispatchAsync(async () => {
            await using var scene = await build();
            var window = scene.Window;
            window.Show();
            window.SetRenderScaling(2);
            afterShow?.Invoke(window);
            for (var i = 0; i < MaxPumps && !(Ready(window, sentinelControlName) && scene.Settled()); i++) {
                Pump(window);
                await Task.Delay(5);
            }
            if (Find(window, sentinelControlName) is null)
                throw new TimeoutException($"{name}: no control named '{sentinelControlName}' appeared");
            if (!Ready(window, sentinelControlName))
                throw new TimeoutException($"{name}: '{sentinelControlName}' is present but never became ready");
            if (!scene.Settled())
                throw new TimeoutException($"{name}: the scene never settled");
            // Deactivating the window keeps the caret; only dropping focus hides it. An open flyout
            // holds focus on a button and closes when focus leaves it, so only a text box loses it.
            if (window.FocusManager?.GetFocusedElement() is TextBox) window.FocusManager.Focus(null);
            Pump(window);
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException($"{name}: the window rendered no frame to capture");
            frame.Save(path, new PngBitmapEncoderOptions());
            return true;
        });
    }

    static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    static Control? Find(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    static bool Ready(Window window, string name) => Find(window, name) switch {
        TextBlock text => !string.IsNullOrEmpty(text.Text),
        TextBox box => !string.IsNullOrEmpty(box.Text),
        { } c => c.IsEffectivelyVisible,
        _ => false
    };

    [Test]
    public async Task The_scene_names_match_the_docs_manifest() {
        await Assert.That(DocsShotScenes.Names).IsEquivalentTo([
            "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
            "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-wizard-history", "desktop-local-recovery",
            "desktop-launcher", "desktop-pull-request", "desktop-work-context", "desktop-rail-grouped",
            "desktop-settings-notifications", "desktop-settings-accounts", "desktop-settings-profiles", "desktop-settings-search",
            "desktop-launch-approvals", "desktop-consent-prompt", "desktop-wizard-agents", "desktop-wizard-visibility",
            "desktop-settings-privacy", "desktop-wizard-usage-data", "desktop-settings-usage-data", "desktop-settings-harnesses",
            "desktop-settings-about"]);
    }

    [Test]
    public async Task Every_scene_has_exactly_one_test() {
        var named = typeof(DocsShots).GetMethods()
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<DocsShotAttribute>()?.Name)
            .OfType<string>().ToArray();
        await Assert.That(named).IsEquivalentTo(DocsShotScenes.Names);
    }

    [Test, DocsShot("desktop-main")]
    public async Task Main_window_with_rail_and_a_working_chat_tab() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(), "ToolRowLabel");
    }

    [Test, DocsShot("desktop-permission-card")]
    public async Task A_permission_request_waits_in_the_tab() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(pendingIds: new HashSet<string> { "a1" },
            permissionRequests: [PermissionEntries.Entry("r1", "a1", toolName: "Bash",
                toolInputJson: """{"command":"git push origin feat/gateway-retry"}""")]), "PermissionCard");
    }

    [Test, DocsShot("desktop-help-menu")]
    public async Task The_help_and_support_flyout_is_open() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(openFeedback: _ => { }, requestSetup: () => { }, openSession: false),
            "RailHelpDocsButton", afterShow: OpenHelpFlyoutInsideTheWindow);
    }

    [Test, DocsShot("desktop-settings")]
    public async Task The_settings_window_shows_the_profile_and_daemon() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "DaemonTab", height: 560), "NameInput");
    }

    [Test, DocsShot("desktop-wizard-edition")]
    public async Task The_setup_wizard_offers_the_cloud_and_local_editions() {
        await CaptureAsync(() => DocsShotScenes.WizardAsync(Config, WizardStepId.Edition), "CloudEditionButton");
    }

    [Test, DocsShot("desktop-wizard-sign-in")]
    public async Task The_setup_wizard_asks_to_sign_in_with_a_work_account() {
        await CaptureAsync(() => DocsShotScenes.WizardAsync(Config, WizardStepId.SignIn), "WorkAccountButton");
    }

    [Test, DocsShot("desktop-wizard-history")]
    public async Task The_setup_wizard_offers_to_import_the_history_it_found() {
        await CaptureAsync(() => DocsShotScenes.WizardHistoryAsync(Config), "FoundLineText");
    }

    [Test, DocsShot("desktop-wizard-agents")]
    public async Task The_setup_wizard_lists_the_agents_it_found_with_record_and_tools() {
        await CaptureAsync(() => DocsShotScenes.WizardHarnessesAsync(Config, localEdition: true), "HarnessLabelText");
    }

    [Test, DocsShot("desktop-wizard-visibility")]
    public async Task The_setup_wizard_asks_who_can_read_new_sessions() {
        await CaptureAsync(() => DocsShotScenes.WizardHarnessesAsync(Config), "PrivacyCard");
    }

    [Test, DocsShot("desktop-wizard-usage-data")]
    public async Task The_setup_wizard_asks_to_share_usage_data() {
        await CaptureAsync(() => DocsShotScenes.WizardUsageDataAsync(Config), "ShareUsageOption");
    }

    [Test, DocsShot("desktop-local-recovery")]
    public async Task The_launcher_offers_to_start_the_local_server() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(openSession: false, localServerDown: true), "StartLocalServerButton");
    }

    [Test, DocsShot("desktop-launcher")]
    public async Task The_launcher_holds_a_goal_with_every_chip_chosen() {
        await CaptureAsync(DocsShotScenes.LauncherAsync, "GoalInput");
    }

    [Test, DocsShot("desktop-pull-request")]
    public async Task The_pull_request_tab_shows_its_checks_and_offers_a_fix() {
        await CaptureAsync(DocsShotScenes.PullRequestAsync, "FixPullRequestButton");
    }

    [Test, DocsShot("desktop-work-context")]
    public async Task The_work_context_shows_parts_people_pull_requests_and_plan() {
        await CaptureAsync(DocsShotScenes.WorkContextAsync, "PlanTaskList");
    }

    [Test, DocsShot("desktop-rail-grouped")]
    public async Task The_rail_groups_live_and_ended_sessions_by_repository() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(openSession: false, moreSessions: true), "SessionMeta");
    }

    [Test, DocsShot("desktop-settings-notifications")]
    public async Task The_settings_window_shows_the_notification_switches() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "NotificationsTab", height: 520), "PermissionNotificationsToggle");
    }

    [Test, DocsShot("desktop-settings-accounts")]
    public async Task The_settings_window_lists_accounts_with_a_row_menu_open() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "AccountsTab", height: 920, width: 960), "AddAccountButton",
            afterShow: window => OpenFlyoutInsideTheWindow(window, window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("accountMenu") && AutomationProperties.GetName(b) == "Actions for northwind"), "desktop-settings-accounts"));
    }

    [Test, DocsShot("desktop-settings-profiles")]
    public async Task The_settings_window_lists_the_profiles() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "ProfilesTab", height: 520), "ProfilesTitleText");
    }

    [Test, DocsShot("desktop-settings-search")]
    public async Task The_settings_window_offers_the_built_in_search_model() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "SearchTab", height: 580, localSearch: true), "SearchCurrentText");
    }

    [Test, DocsShot("desktop-settings-privacy")]
    public async Task The_settings_window_shows_who_reads_new_sessions_and_the_recording_rules() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "CaptureTab", height: 1060), "AllowedReposInput");
    }

    [Test, DocsShot("desktop-settings-usage-data")]
    public async Task The_settings_window_shows_the_usage_data_switch_on() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "PrivacyTab", height: 520, usageData: true), "UsageDataSwitch");
    }

    [Test, DocsShot("desktop-settings-harnesses")]
    public async Task The_settings_window_lists_the_coding_tools_kcap_records() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "HarnessesTab", height: 660, harnesses: true), "HarnessSettingsRows");
    }

    [Test, DocsShot("desktop-settings-about")]
    public async Task The_settings_window_shows_the_installed_versions() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config, "AboutTab", height: 520, about: true), "AboutCliVersionText");
    }

    [Test, DocsShot("desktop-launch-approvals")]
    public async Task The_launch_approvals_window_lists_recent_decisions() {
        await CaptureAsync(DocsShotScenes.LaunchApprovalsAsync, "ActivityItems");
    }

    [Test, DocsShot("desktop-consent-prompt")]
    public async Task The_consent_prompt_asks_to_allow_a_requested_launch() {
        await CaptureAsync(DocsShotScenes.ConsentPromptAsync, "AllowOnceButton");
    }

    static void OpenHelpFlyoutInsideTheWindow(Window window) =>
        OpenFlyoutInsideTheWindow(window, window.FindDescendantOfType<SessionRailView>()!.FindControl<Button>("RailHelpButton")!, "desktop-help-menu");

    static void OpenFlyoutInsideTheWindow(Window window, Button button, string figure) {
        ((Flyout)button.Flyout!).ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var presenter = window.FindDescendantOfType<FlyoutPresenter>()
            ?? throw new InvalidOperationException($"{figure}: the flyout has no presenter in the window");
        var bounds = new Rect(presenter.Bounds.Size).TransformToAABB(presenter.TransformToVisual(window)!.Value);
        var frame = new Rect(window.Bounds.Size);
        if (!frame.Contains(bounds))
            throw new InvalidOperationException(
                $"{figure}: the flyout at {bounds} overflows the window {frame} " +
                $"(left {Math.Max(0, -bounds.X)}, top {Math.Max(0, -bounds.Y)}, " +
                $"right {Math.Max(0, bounds.Right - frame.Width)}, bottom {Math.Max(0, bounds.Bottom - frame.Height)})");
    }
}
