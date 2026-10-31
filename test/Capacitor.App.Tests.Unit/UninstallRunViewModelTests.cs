using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class UninstallRunViewModelTests {
    sealed class FakeRemoval(List<string> calls) : IAppRemoval {
        public IReadOnlyList<string> LeftLinks = [];
        public bool Trashed = true;
        public bool SupportRemoved = true;
        public Exception? SupportFailure;

        public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) {
            calls.Add("links");
            return Task.FromResult(LeftLinks);
        }

        public bool DeleteSupportFiles() {
            calls.Add("support");
            if (SupportFailure is not null) throw SupportFailure;
            return SupportRemoved;
        }

        public Task<bool> TrashBundleAsync(CancellationToken ct) {
            calls.Add("trash");
            return Task.FromResult(Trashed);
        }

        public void RevealBundle() => calls.Add("reveal");
    }

    static StreamingResult Exit(int code) => new(code, false, []);

    static (UninstallRunViewModel Vm, FakeKcapCli Cli, FakeRemoval Removal, List<string> Calls) Build(bool purgeLocal = false) {
        var calls   = new List<string>();
        var cli     = new FakeKcapCli { UninstallBehavior = _ => Task.FromResult(Exit(0)) };
        var removal = new FakeRemoval(calls);
        var vm      = new UninstallRunViewModel(cli, removal, purgeLocal, () => calls.Add("quit"), action => action());
        return (vm, cli, removal, calls);
    }

    [Test]
    public async Task Preparation_finishes_before_cleanup_and_is_not_repeated_on_retry() {
        var calls = new List<string>();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeKcapCli { UninstallBehavior = _ => { calls.Add("cli"); return Task.FromResult(Exit(1)); } };
        var vm = new UninstallRunViewModel(cli, new FakeRemoval(calls), false, () => calls.Add("quit"), action => action(),
            async () => { calls.Add("prepare"); await prepared.Task; calls.Add("prepared"); });

        var operation = vm.RunAsync();
        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Preparing);
        await Assert.That(vm.IsBusy).IsTrue();
        await Assert.That(calls).IsEquivalentTo(["prepare"]);
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsFalse();
        prepared.SetResult();
        await operation;
        await Assert.That(calls).IsEquivalentTo(["prepare", "prepared", "cli"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        cli.UninstallBehavior = _ => { calls.Add("cli"); return Task.FromResult(Exit(0)); };
        await vm.RetryCommand.Execute();
        await Assert.That(calls).IsEquivalentTo(["prepare", "prepared", "cli", "cli", "links", "support", "trash"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(vm.IsFinished).IsTrue();
    }

    [Test]
    public async Task Failed_preparation_keeps_the_app_and_never_starts_cleanup() {
        var calls = new List<string>();
        var cli = new FakeKcapCli();
        var vm = new UninstallRunViewModel(cli, new FakeRemoval(calls), false, () => { }, action => action(),
            () => Task.FromException(new InvalidOperationException("A local operation could not settle.")));

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(calls).IsEmpty();
        await Assert.That(cli.UninstallPurgeFlags).IsEmpty();
        await Assert.That(vm.DetailsExpanded).IsTrue();
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsTrue();
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsFalse();
    }

    [Test]
    public async Task A_failed_ui_disposal_drains_remaining_services_but_prevents_cleanup() {
        var calls = new List<string>();
        var cli = new FakeKcapCli();
        var vm = new UninstallRunViewModel(cli, new FakeRemoval(calls), false, () => { }, action => action(),
            () => App.DisposeUiForUninstallAsync(
                [System.Reactive.Disposables.Disposable.Create(() => throw new InvalidOperationException("dispose failed")),
                 System.Reactive.Disposables.Disposable.Create(() => calls.Add("remaining-ui"))],
                () => { calls.Add("services"); return ValueTask.CompletedTask; }));

        await vm.RunAsync();

        await Assert.That(calls).IsEquivalentTo(["remaining-ui", "services"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(cli.UninstallPurgeFlags).IsEmpty();
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsFalse();
    }

    [Test]
    public async Task Cli_failure_never_touches_the_bundle() {
        var (vm, cli, _, calls) = Build();
        cli.UninstallBehavior = onLine => {
            onLine(new StreamedLine(ProcessStreamKind.Stderr, "could not stop the daemon"));
            return Task.FromResult(Exit(1));
        };

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(calls).IsEmpty();
        await Assert.That(vm.Lines).Contains("could not stop the daemon");
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsTrue();
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsTrue();
    }

    [Test]
    public async Task Missing_cli_shows_why() {
        var (vm, cli, _, _) = Build();
        cli.UninstallBehavior = _ => Task.FromResult(
            new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]));

        await vm.RunAsync();

        await Assert.That(vm.Lines).Contains("kcap CLI not found");
    }

    [Test]
    public async Task Retry_runs_the_cli_again() {
        var (vm, cli, _, calls) = Build();
        cli.UninstallBehavior = _ => Task.FromResult(Exit(1));
        await vm.RunAsync();

        cli.UninstallBehavior = _ => Task.FromResult(Exit(0));
        await vm.RetryCommand.Execute();

        await Assert.That(cli.UninstallPurgeFlags.Count).IsEqualTo(2);
        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(calls).DoesNotContain("quit");
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsTrue();
    }

    [Test]
    public async Task Leftover_link_is_a_failure_and_keeps_the_bundle() {
        var (vm, _, removal, calls) = Build();
        removal.LeftLinks = ["/usr/local/bin/kcap"];

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(string.Join(",", calls)).IsEqualTo("links");
        await Assert.That(vm.Lines.Any(l => l.Contains("/usr/local/bin/kcap"))).IsTrue();
    }

    [Test]
    public async Task Success_removes_links_then_trashes_and_waits_for_done() {
        var (vm, _, _, calls) = Build();

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(string.Join(",", calls)).IsEqualTo("links,support,trash");
        await Assert.That(vm.IsFinished).IsTrue();
        await Assert.That(vm.Receipt.Select(i => i.Text)).IsEquivalentTo([
            "Removed kcap from your coding agents’ user settings",
            "Removed kcap’s background services",
            "Removed your kcap settings and sign-in",
            "Removed the kcap command the app installed",
            "Moved the kcap app to the Trash",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(vm.Receipt.All(i => i.Done)).IsTrue();
        await Assert.That(vm.DetailsLabel).IsEqualTo("Show what was removed");
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsFalse();
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsTrue();

        await vm.CloseCommand.Execute();

        await Assert.That(string.Join(",", calls)).IsEqualTo("links,support,trash,quit");
    }

    [Test]
    public async Task Trash_failure_reveals_and_waits_for_close() {
        var (vm, _, removal, calls) = Build();
        removal.Trashed = false;

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.TrashFailed);
        await Assert.That(string.Join(",", calls)).IsEqualTo("links,support,trash,reveal");
        await Assert.That(vm.Heading).IsEqualTo("Drag kcap to the Trash to finish");
        await Assert.That(vm.Lines.Any(l => l.Contains("Drag kcap to the Trash to finish"))).IsTrue();
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsFalse();

        await vm.CloseCommand.Execute();

        await Assert.That(calls[^1]).IsEqualTo("quit");
    }

    [Test]
    [NotInParallel]
    public async Task Support_file_failure_is_logged_and_the_run_continues() {
        var (vm, _, removal, calls) = Build();
        removal.SupportFailure = new InvalidOperationException("boom");
        using var stderr = ConsoleOutput.StartErrorCapture();

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(string.Join(",", calls)).IsEqualTo("links,support,trash");
        await Assert.That(vm.Receipt[^1].Done).IsFalse();
    }

    [Test]
    public async Task Support_files_reported_as_left_add_a_note_to_the_receipt() {
        var (vm, _, removal, _) = Build();
        removal.SupportRemoved = false;

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
        await Assert.That(vm.Receipt[^1].Done).IsFalse();
    }

    [Test]
    public async Task Receipt_names_the_deleted_local_server_only_when_the_cli_purged_one() {
        var (nothingFound, _, _, _) = Build(purgeLocal: true);
        var (purged, cli, _, _) = Build(purgeLocal: true);
        cli.UninstallBehavior = onLine => {
            onLine(new StreamedLine(ProcessStreamKind.Stdout, "Purged the local server in /home/me/.local/share/kcap/local."));
            return Task.FromResult(Exit(0));
        };

        await nothingFound.RunAsync();
        await purged.RunAsync();

        await Assert.That(nothingFound.Receipt.Any(i => i.Text.Contains("local server"))).IsFalse();
        await Assert.That(purged.Receipt.Any(i => i.Text.Contains("local server"))).IsTrue();
    }

    [Test]
    public async Task Failure_leaves_the_receipt_empty() {
        var (vm, cli, _, _) = Build();
        cli.UninstallBehavior = _ => Task.FromResult(Exit(1));

        await vm.RunAsync();

        await Assert.That(vm.Receipt).IsEmpty();
        await Assert.That(vm.DetailsLabel).IsEqualTo("Hide details");
    }

    [Test]
    public async Task Close_and_retry_are_disabled_while_running() {
        var (vm, cli, _, _) = Build();
        var exit = new TaskCompletionSource<StreamingResult>();
        cli.UninstallBehavior = _ => exit.Task;

        var run = vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Running);
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsFalse();
        await Assert.That(await vm.RetryCommand.CanExecute.FirstAsync()).IsFalse();

        exit.SetResult(Exit(1));
        await run;
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Passes_purge_local_through(bool purgeLocal) {
        var (vm, cli, _, _) = Build(purgeLocal);

        await vm.RunAsync();

        await Assert.That(cli.UninstallPurgeFlags).IsEquivalentTo(new[] { purgeLocal });
    }
}
