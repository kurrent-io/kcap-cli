using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class UninstallConfirmationTests {
    const string Attached = "default";

    static UninstallFacts Facts(
        string? local = null, DaemonActivity? activity = null, bool daemonRunning = false,
        string? hosted = null, IReadOnlyList<string>? live = null, bool liveUnknown = false,
        bool othersUnknown = false, params OtherKcapInstall[] others) =>
        new(local, Attached, activity, liveUnknown ? null : live ?? (daemonRunning ? [Attached] : []), hosted, othersUnknown ? null : others);

    [Test]
    public async Task Lists_what_is_removed() {
        var d = UninstallConfirmation.Build(Facts()).Prompt.Disclosure;

        await Assert.That(d).Contains("hooks");
        await Assert.That(d).Contains("background service");
        await Assert.That(d).Contains("settings and sign-in");
        await Assert.That(d).Contains("kcap app");
        await Assert.That(d).Contains("PATH line");
        await Assert.That(d).Contains("shell profile");
    }

    [Test]
    public async Task Local_server_sets_the_warning_and_purge_consent() {
        var r = UninstallConfirmation.Build(Facts(local: "http://localhost:7000"));

        await Assert.That(r.Prompt.Warning).Contains("http://localhost:7000");
        await Assert.That(r.Prompt.Warning).Contains("permanently deleted");
        await Assert.That(r.PurgeLocal).IsTrue();
        await Assert.That(r.Prompt.Kind).IsEqualTo(LifecyclePrompt.KindUninstall);
    }

    [Test]
    public async Task No_local_server_has_no_warning() {
        var r = UninstallConfirmation.Build(Facts());

        await Assert.That(r.Prompt.Warning).IsNull();
        await Assert.That(r.PurgeLocal).IsFalse();
    }

    [Test]
    public async Task Busy_daemon_names_the_agent_count() {
        var d = UninstallConfirmation.Build(Facts(activity: new DaemonActivity(2, true), daemonRunning: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("2 running agents will be ended");
    }

    [Test]
    public async Task One_agent_is_singular() {
        var d = UninstallConfirmation.Build(Facts(activity: new DaemonActivity(1, true), daemonRunning: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("1 running agent will be ended");
    }

    [Test]
    public async Task Idle_daemon_says_nothing_about_agents() {
        var d = UninstallConfirmation.Build(Facts(activity: new DaemonActivity(0, false), daemonRunning: true)).Prompt.Disclosure;

        await Assert.That(d).DoesNotContain("will be ended");
    }

    [Test]
    public async Task Daemon_running_without_a_snapshot_says_its_agents_will_be_ended() {
        var d = UninstallConfirmation.Build(Facts(daemonRunning: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("not answering");
        await Assert.That(d).Contains("will be ended");
    }

    [Test]
    public async Task Another_live_daemon_is_named_and_its_work_ended() {
        var d = UninstallConfirmation.Build(Facts(activity: new DaemonActivity(1, true), live: [Attached, "laptop"])).Prompt.Disclosure;

        await Assert.That(d).Contains("1 running agent will be ended");
        await Assert.That(d).Contains("The kcap daemon 'laptop' is also running. Anything it runs will be ended.");
        await Assert.That(d).DoesNotContain($"'{Attached}'");
    }

    [Test]
    public async Task Another_live_daemon_is_named_when_the_app_is_not_attached() {
        var d = UninstallConfirmation.Build(Facts(live: ["laptop"])).Prompt.Disclosure;

        await Assert.That(d).Contains("'laptop' is also running");
        await Assert.That(d).DoesNotContain("not answering");
    }

    [Test]
    public async Task Unreadable_daemons_keep_the_conservative_line() {
        var d = UninstallConfirmation.Build(Facts(liveUnknown: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("Anything a running kcap daemon is doing will be ended.");
    }

    [Test]
    public async Task Unreadable_daemons_beside_a_known_activity_warn_about_the_others() {
        var d = UninstallConfirmation.Build(Facts(activity: new DaemonActivity(0, false), liveUnknown: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("Anything another kcap daemon is running will be ended.");
    }

    [Test]
    public async Task Unknown_other_installs_get_one_line() {
        var d = UninstallConfirmation.Build(Facts(othersUnknown: true)).Prompt.Disclosure;

        await Assert.That(d).Contains("Any other kcap installed outside this app (npm, Homebrew, a script install) will also stop working.");
    }

    [Test]
    public async Task No_other_installs_say_nothing_about_them() {
        var d = UninstallConfirmation.Build(Facts()).Prompt.Disclosure;

        await Assert.That(d).DoesNotContain("stop working");
    }

    [Test]
    public async Task Hosted_server_says_sent_sessions_stay() {
        var d = UninstallConfirmation.Build(Facts(hosted: "https://kcap.example")).Prompt.Disclosure;

        await Assert.That(d).Contains("https://kcap.example stay there");
    }

    [Test]
    public async Task Other_install_is_named_with_its_command() {
        var d = UninstallConfirmation.Build(Facts(others: [new OtherKcapInstall("/opt/homebrew/bin/kcap", "brew uninstall kcap")])).Prompt.Disclosure;

        await Assert.That(d).Contains("/opt/homebrew/bin/kcap");
        await Assert.That(d).Contains("brew uninstall kcap");
    }
    [Test]
    public async Task Names_the_shell_profile_that_will_be_edited() {
        var facts = Facts() with { PathProfiles = ["/Users/standard/.zprofile"] };
        var disclosure = UninstallConfirmation.Build(facts).Prompt.Disclosure;
        await Assert.That(disclosure).Contains("/Users/standard/.zprofile");
        await Assert.That(disclosure).Contains("PATH line");
    }

    [Test]
    public async Task No_eligible_profiles_says_nothing_about_profile_edits() {
        var disclosure = UninstallConfirmation.Build(Facts() with { PathProfiles = [] }).Prompt.Disclosure;
        await Assert.That(disclosure).DoesNotContain("shell profile");
    }

}
