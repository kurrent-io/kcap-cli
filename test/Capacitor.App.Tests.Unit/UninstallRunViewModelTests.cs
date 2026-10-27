using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class UninstallRunViewModelTests {
    sealed class FakeRemoval(List<string> calls) : IAppRemoval {
        public IReadOnlyList<string> LeftLinks = [];
        public bool Trashed = true;
        public Exception? SupportFailure;

        public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) {
            calls.Add("links");
            return Task.FromResult(LeftLinks);
        }

        public void DeleteSupportFiles() {
            calls.Add("support");
            if (SupportFailure is not null) throw SupportFailure;
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
        await Assert.That(calls).Contains("quit");
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
    public async Task Success_removes_links_then_trashes_then_quits() {
        var (vm, _, _, calls) = Build();

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Finished);
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
        await Assert.That(string.Join(",", calls)).IsEqualTo("links,support,trash,quit");
    }

    [Test]
    public async Task Unexpected_failure_never_stays_running() {
        var removal = new FakeRemoval([]);
        var cli     = new FakeKcapCli { UninstallBehavior = _ => Task.FromResult(Exit(0)) };
        var vm      = new UninstallRunViewModel(cli, removal, false, () => throw new InvalidOperationException("boom"), action => action());

        await vm.RunAsync();

        await Assert.That(vm.State).IsEqualTo(UninstallRunState.Failed);
        await Assert.That(await vm.CloseCommand.CanExecute.FirstAsync()).IsTrue();
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
