using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class LocalInstallControllerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static LocalInstallController Controller(FakeKcapCli cli, LocalRoot root, TelemetryProbe probe, bool committedLocal = false) =>
        new(() => cli, root, () => committedLocal, action => action(), probe.Funnel);

    TelemetryProbe Probe() => TelemetryProbe.Live("setup", Config.Root);

    /// Every interrupt request lands on the one token the child was started with, and the settle
    /// waits for the child's own exit rather than completing on the request.
    [Test]
    public async Task Cancel_then_settle_sends_one_interrupt() {
        var signals = 0;
        var tokens = new List<CancellationToken>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli {
            SetupLocalBehavior = async (onLine, interrupt, _) => {
                tokens.Add(interrupt);
                var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (interrupt.Register(() => {
                    Interlocked.Increment(ref signals);
                    interrupted.TrySetResult();
                })) {
                    await interrupted.Task;
                }
                await release.Task;
                onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("cancelled")));
                return new StreamingResult(130, false, []);
            },
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        controller.Interrupt();
        controller.Interrupt();
        var settle = controller.SettleAsync();
        controller.Interrupt();
        await Task.Delay(50);

        await Assert.That(settle.IsCompleted).IsFalse();
        await Assert.That(controller.Running).IsTrue();

        release.SetResult();
        await settle.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(signals).IsEqualTo(1);
        await Assert.That(tokens.Count).IsEqualTo(1);
        await Assert.That(controller.Running).IsFalse();
        await Assert.That(controller.Outcome).IsTypeOf<LocalInstallOutcome.Failed>();
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }

    /// A runner that rolls back on SIGINT and tree-kills on its cancellation token, as ProcessRunner does.
    sealed class RollbackRunner : IProcessRunner {
        public int Interrupts;
        public int Kills;
        public readonly TaskCompletionSource Spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, "", "", false));

        public async Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) {
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var onInterrupt = options.Interrupt.Register(() => {
                Interlocked.Increment(ref Interrupts);
                stopped.TrySetResult(false);
            });
            await using var onKill = ct.Register(() => {
                Interlocked.Increment(ref Kills);
                stopped.TrySetResult(true);
            });
            Spawned.TrySetResult();

            if (await stopped.Task) return new StreamingResult(137, false, []);

            await Task.Delay(20, CancellationToken.None);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("cancelled")));
            return new StreamingResult(130, false, []);
        }
    }

    [Test]
    public async Task A_quit_interrupts_the_running_setup_once_and_never_kills_it() {
        var runner = new RollbackRunner();
        var cli = new KcapCli(runner, "kcap", "daemon-a", "work", terminalPathAsync: null, "https://cap.example.com:443");
        var probe = Probe();
        var controller = new LocalInstallController(
            () => cli, LocalSetupScript.NoSetup(Config), () => false, action => action(), probe.Funnel);
        using var shutdown = new CancellationTokenSource();

        await controller.StartAsync(LocalEmbeddingChoice.Default, shutdown.Token);
        await runner.Spawned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The app cancels its shutdown token before it settles the install.
        await shutdown.CancelAsync();
        await controller.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(runner.Interrupts).IsEqualTo(1);
        await Assert.That(runner.Kills).IsEqualTo(0);
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
        // A quit is not an install failure.
        await Assert.That(probe.Names).IsEquivalentTo(["cli_setup_local_install_started"]);
    }

    [Test]
    public async Task Settle_when_idle_completes_at_once_and_start_while_running_spawns_nothing() {
        var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => exit.Task };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.SettleAsync();
        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        exit.SetResult(new StreamingResult(0, false, []));
        await controller.SettleAsync();

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task No_cli_settles_retryable() {
        var probe = Probe();
        var cli = new FakeKcapCli {
            CliPath = null,
            SetupLocalBehavior = (_, _, _) => Task.FromResult(
                new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")])),
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), probe);

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Retry);
        await Assert.That(failed.Message).IsEqualTo("kcap CLI not found");
        await Assert.That(failed.LockedToLocal).IsFalse();
        await Assert.That(probe.Only("cli_setup_local_install_failed").Properties["code"]!.GetValue<string>()).IsEqualTo("cli_missing");
    }

    [Test]
    public async Task A_throwing_run_settles_retryable_instead_of_hanging() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => throw new InvalidOperationException("spawn refused") };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), probe);

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Retry);
        await Assert.That(failed.Message).Contains("spawn refused");
        await Assert.That(probe.Only("cli_setup_local_install_failed").Properties["code"]!.GetValue<string>()).IsEqualTo("spawn_failed");
    }

    [Test]
    public async Task A_nonzero_exit_without_a_failed_line_reports_no_failure_line() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = (_, _, _) => Task.FromResult(new StreamingResult(3, false, [])) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), probe);

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(probe.Only("cli_setup_local_install_failed").Properties["code"]!.GetValue<string>()).IsEqualTo("no_failure_line");
    }

    /// A child that dies after a line of progress is not ranWithoutOutput, so the stderr tail is the
    /// only account of why it stopped.
    [Test]
    public async Task A_nonzero_exit_without_a_failed_line_keeps_the_stderr_tail() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = (onLine, _, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, """{"v":1,"event":"downloading","bytes":100}"""));
                return Task.FromResult(new StreamingResult(3, false, [new StreamedLine(ProcessStreamKind.Stderr, "Unhandled exception: disk full")]));
            },
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Message).IsEqualTo("Unhandled exception: disk full");
    }

    /// A failed line wins over stderr: it is the CLI's own account.
    [Test]
    public async Task A_failed_line_is_not_replaced_by_stderr() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = (onLine, _, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed("download_failed", "The download stalled.")));
                return Task.FromResult(new StreamingResult(1, false, [new StreamedLine(ProcessStreamKind.Stderr, "noise")]));
            },
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Message).IsEqualTo("The download stalled.");
    }

    [Test]
    public async Task A_failed_run_stops_every_running_row() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = LocalSetupScript.Prints(1,
                """{"v":1,"event":"unpacked"}""", """{"v":1,"event":"starting"}""",
                LocalSetupScript.Failed("start_failed")),
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());
        RowState? firstStartWhenSettled = null;
        controller.Settled += _ => firstStartWhenSettled = controller.Progress.FirstStart.State;

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(controller.Progress.Install.State).IsEqualTo(RowState.Done);
        await Assert.That(controller.Progress.FirstStart.State).IsEqualTo(RowState.Pending);
        await Assert.That(firstStartWhenSettled).IsEqualTo(RowState.Pending);
    }

    [Test]
    public async Task A_failed_download_hides_the_download_bar() {
        var cli = new FakeKcapCli {
            SetupLocalBehavior = LocalSetupScript.Prints(1,
                """{"v":1,"event":"downloading","bytes":100}""", LocalSetupScript.Failed("download_failed")),
        };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(controller.Progress.Download.State).IsEqualTo(RowState.Pending);
    }

    [Test]
    public async Task Started_per_attempt_and_committed_once_with_its_plan() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), probe);
        LocalInstallOutcome? settled = null;
        controller.Settled += o => settled = o;

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(settled).IsEqualTo(controller.Outcome);
        await Assert.That(((LocalInstallOutcome.Installed)controller.Outcome!).ServerUrl).IsEqualTo(LocalSetupScript.ServerUrl);
        await Assert.That(probe.Names).IsEquivalentTo(
            ["cli_setup_local_install_started", "cli_setup_local_install_committed"], CollectionOrdering.Matching);
        await Assert.That(probe.Only("cli_setup_local_install_committed").Properties["plan"]!.GetValue<string>()).IsEqualTo("fresh");
    }

    [Test]
    public async Task A_busy_lock_under_a_pending_setup_settles_locked_and_says_so() {
        var probe = Probe();
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("lifecycle_busy")) };
        var controller = Controller(cli, LocalSetupScript.PendingSetup(Config), probe);

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        var failed = (LocalInstallOutcome.Failed)controller.Outcome!;
        await Assert.That(failed.Affordance).IsEqualTo(LocalInstallAffordance.Busy);
        await Assert.That(failed.LockedToLocal).IsTrue();
        var e = probe.Only("cli_setup_local_install_failed");
        await Assert.That(e.Properties["code"]!.GetValue<string>()).IsEqualTo("lifecycle_busy");
        await Assert.That(e.Properties["locked"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task A_committed_local_profile_locks_a_failure_without_a_journal() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe(), committedLocal: true);

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).LockedToLocal).IsTrue();
    }

    [Test]
    public async Task A_new_run_starts_from_fresh_progress() {
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("download_failed")) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
        await controller.SettleAsync();
        var first = controller.Progress;

        cli.SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed);
        await controller.RetryAsync(CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(controller.Progress).IsNotSameReferenceAs(first);
        await Assert.That(controller.Progress.FailedCode).IsNull();
        await Assert.That(controller.Outcome).IsTypeOf<LocalInstallOutcome.Installed>();
    }

    /// The progress a run reports into knows what it was asked, so a keyword run's Ollama probe does not read as a failure.
    [Test]
    public async Task A_run_left_on_keyword_search_summarises_its_skip_as_that_choice() {
        const string skipped =
            """{"v":1,"event":"embeddings_skipped","code":"ollama_not_found","message":"Ollama is not responding on http://localhost:11434; search works by keyword."}""";
        var cli = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, skipped, LocalSetupScript.Committed) };
        var controller = Controller(cli, LocalSetupScript.NoSetup(Config), Probe());

        await controller.StartAsync(LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.KeywordOnly), CancellationToken.None);
        await controller.SettleAsync();

        await Assert.That(controller.Outcome).IsTypeOf<LocalInstallOutcome.Installed>();
        await Assert.That(controller.Progress.Embeddings.Summary).IsEqualTo("Search by keyword, as chosen.");
    }
}
