using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class UninstallConfirmationTests {
    static UninstallFacts Facts(
        string? local = null, DaemonActivity? activity = null, bool daemonRunning = false,
        string? hosted = null, params OtherKcapInstall[] others) =>
        new(local, activity, daemonRunning, hosted, others);

    [Test]
    public async Task Lists_what_is_removed() {
        var d = UninstallConfirmation.Build(Facts()).Prompt.Disclosure;

        await Assert.That(d).Contains("hooks");
        await Assert.That(d).Contains("background service");
        await Assert.That(d).Contains("settings and sign-in");
        await Assert.That(d).Contains("kcap app");
    }

    [Test]
    public async Task Local_server_sets_the_warning_and_the_purge_arg() {
        var r = UninstallConfirmation.Build(Facts(local: "http://localhost:7000"));

        await Assert.That(r.Prompt.Warning).Contains("http://localhost:7000");
        await Assert.That(r.Prompt.Warning).Contains("permanently deleted");
        await Assert.That(r.RelaunchArgs.SequenceEqual(["--uninstall", "--purge-local"])).IsTrue();
        await Assert.That(r.Prompt.Kind).IsEqualTo(LifecyclePrompt.KindUninstall);
    }

    [Test]
    public async Task No_local_server_has_no_warning() {
        var r = UninstallConfirmation.Build(Facts());

        await Assert.That(r.Prompt.Warning).IsNull();
        await Assert.That(r.RelaunchArgs.SequenceEqual(["--uninstall"])).IsTrue();
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
}
