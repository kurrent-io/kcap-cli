using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class InstallStepAutoRetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);

    /// Fails to start the first <paramref name="failures"/> runs, then installs.
    static FakeKcapCli FailingToStart(int failures) {
        var run = 0;
        var fail = LocalSetupScript.Prints(1, LocalSetupScript.Failed("start_failed", "The local server did not answer on its port."));
        var ok   = LocalSetupScript.Prints(0, LocalSetupScript.Committed);
        return new FakeKcapCli { SetupLocalBehavior = (line, a, b) => ++run <= failures ? fail(line, a, b) : ok(line, a, b) };
    }

    InstallStepViewModel Step(FakeKcapCli cli, FakeTimeProvider time, Func<IReadOnlyList<string>, FeedbackMailOutcome>? mail = null) {
        var controller = new LocalInstallController(() => cli, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel);
        return new InstallStepViewModel(controller, () => "default", () => "default", () => true,
            _ => Task.FromResult<LocalEmbeddingModelManifest?>(null), time: time, mailSupport: mail);
    }

    static async Task StartAsync(InstallStepViewModel install) {
        await install.OnEnterAsync(CancellationToken.None);
        await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
    }

    static async Task ElapseGapAsync(FakeTimeProvider time) {
        time.Advance(Gap);
        for (var i = 0; i < 5; i++) {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }

    [Test]
    public async Task A_start_failure_retries_on_its_own_and_never_shows_the_failure() {
        var (failureShownWhileRetrying, status, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var time = new FakeTimeProvider();
            var install = Step(FailingToStart(3), time);
            await StartAsync(install);
            var shown = install.FailureVisible;
            var status = install.RetryStatus;
            for (var i = 0; i < 3; i++) await ElapseGapAsync(time);

            return (shown, status, install.Satisfied);
        });

        await Assert.That(failureShownWhileRetrying).IsFalse();
        await Assert.That(status).Contains("1 of 10");
        await Assert.That(satisfied).IsTrue();
    }

    /// The first failure and ten retries: eleven starts, all failed, before the page gives up.
    [Test]
    public async Task Ten_failed_retries_show_the_error_and_offer_the_support_email() {
        IReadOnlyList<string>? mailed = null;
        var (message, tryAgain, email, note) = await AvaloniaSession.DispatchAsync(async () => {
            var time = new FakeTimeProvider();
            var install = Step(FailingToStart(int.MaxValue), time, failures => {
                mailed = failures;
                return new FeedbackMailOutcome(new FeedbackMailDraft("mailto:team@kcap.ai", CutShort: false), Opened: true, Copied: true);
            });
            await StartAsync(install);
            for (var i = 0; i < InstallStepViewModel.AutoRetries; i++) await ElapseGapAsync(time);
            var shown = (install.FailureVisible ? install.FailureMessage : null, install.TryAgainVisible, install.EmailSupportVisible);
            await install.EmailSupportCommand.Execute().ToTask();

            return (shown.Item1, shown.TryAgainVisible, shown.EmailSupportVisible, install.SupportNote);
        });

        await Assert.That(message).IsEqualTo(InstallStepViewModel.GaveUpText);
        await Assert.That(tryAgain).IsFalse();
        await Assert.That(email).IsTrue();
        await Assert.That(mailed!.Count).IsEqualTo(InstallStepViewModel.AutoRetries + 1);
        await Assert.That(note).Contains("team@kcap.ai");
    }

    [Test]
    public async Task Cancel_between_attempts_stops_retrying_and_offers_try_again() {
        var (retrying, tryAgain, starts) = await AvaloniaSession.DispatchAsync(async () => {
            var time = new FakeTimeProvider();
            var runs = 0;
            var fail = LocalSetupScript.Prints(1, LocalSetupScript.Failed("start_failed"));
            var cli = new FakeKcapCli { SetupLocalBehavior = (l, a, b) => { runs++; return fail(l, a, b); } };
            var install = Step(cli, time);
            await StartAsync(install);
            await install.CancelCommand.Execute().ToTask();
            await ElapseGapAsync(time);

            return (install.AutoRetrying, install.TryAgainVisible, runs);
        });

        await Assert.That(retrying).IsFalse();
        await Assert.That(tryAgain).IsTrue();
        await Assert.That(starts).IsEqualTo(1);
    }

    [Test]
    public async Task The_support_report_carries_each_attempt_and_the_server_log_tail() {
        var root = new LocalRoot(Config.PathTo("local"));
        var logs = Directory.CreateDirectory(Path.Combine(root.Directory, "logs"));
        await File.WriteAllLinesAsync(Path.Combine(logs.FullName, "server-20261030.log"),
            [.. Enumerable.Range(1, 250).Select(i => $"line {i}")]);

        var report = InstallSupportReport.Build(["first", "second"], root);

        await Assert.That(report).Contains("1. first");
        await Assert.That(report).Contains("2. second");
        await Assert.That(report).Contains("line 250");
        await Assert.That(report).DoesNotContain("line 50\n");
    }
}
