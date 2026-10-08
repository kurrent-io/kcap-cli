using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class ApplicationsLauncherTests {
    const string Installed = "/Applications/Kurrent Capacitor.app";

    sealed class Runner : IProcessRunner {
        public string[]? Args;
        public ProcessResult Result = new(0, "", "", false);
        public Action? OnOpen;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Args = args;
            OnOpen?.Invoke();
            return Task.FromResult(Result);
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task Starts_a_new_instance_and_requires_the_installed_process() {
        var running = false;
        var runner = new Runner { OnOpen = () => running = true };
        var launcher = new ApplicationsLauncher(runner, _ => running, TimeProvider.System);

        await Assert.That(await launcher.OpenAsync(Installed, CancellationToken.None)).IsTrue();
        await Assert.That(runner.Args).IsEquivalentTo(["-n", Installed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Existing_installed_process_is_not_duplicated() {
        var runner = new Runner();
        var launcher = new ApplicationsLauncher(runner, _ => true, TimeProvider.System);

        await Assert.That(await launcher.OpenAsync(Installed, CancellationToken.None)).IsTrue();
        await Assert.That(runner.Args).IsEquivalentTo([Installed], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Successful_open_without_an_installed_process_does_not_allow_quitting() {
        var runner = new Runner();
        var time = new FakeTimeProvider();
        var calls = 0;
        var launcher = new ApplicationsLauncher(runner, _ => {
            if (++calls > 1) time.Advance(TimeSpan.FromSeconds(10));
            return false;
        }, time);

        await Assert.That(await launcher.OpenAsync(Installed, CancellationToken.None)).IsFalse();
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(0, true)]
    public async Task Failed_or_timed_out_open_does_not_allow_quitting(int exit, bool timedOut) {
        var runner = new Runner { Result = new(exit, "", "", timedOut) };
        var launcher = new ApplicationsLauncher(runner, _ => true, TimeProvider.System);
        await Assert.That(await launcher.OpenAsync(Installed, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Unknown_running_state_does_not_start_a_duplicate_or_allow_quitting() {
        var runner = new Runner();
        var launcher = new ApplicationsLauncher(runner, _ => null, TimeProvider.System);
        await Assert.That(await launcher.OpenAsync(Installed, CancellationToken.None)).IsFalse();
        await Assert.That(runner.Args).IsNull();
    }
}
