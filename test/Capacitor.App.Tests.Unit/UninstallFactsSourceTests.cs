using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class UninstallFactsSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UninstallFactsSource Source(
        Func<string?>? local = null, Func<DaemonActivity?>? activity = null, Func<bool>? running = null,
        Func<string?>? hosted = null, Func<CancellationToken, Task<string?>>? terminalPath = null, TextWriter? log = null) => new(
        local ?? (() => null), activity ?? (() => null), running ?? (() => false), hosted ?? (() => null),
        terminalPath ?? (_ => Task.FromResult<string?>(null)), Tmp.GetResolvedPath("Kcap.app"),
        TimeSpan.FromMilliseconds(100), TimeProvider.System, log ?? TextWriter.Null);

    [Test]
    public async Task Gathers_every_fact() {
        if (OperatingSystem.IsWindows()) return;
        Tmp.CreateExecutable("bin/kcap", "#!/bin/sh\n");
        var bin = Tmp.GetResolvedPath("bin");

        var facts = await Source(
            local: () => "http://127.0.0.1:5100", activity: () => new DaemonActivity(2, true), running: () => true,
            hosted: () => "https://kcap.example", terminalPath: _ => Task.FromResult<string?>(bin)).GatherAsync(CancellationToken.None);

        await Assert.That(facts.LocalServerUrl).IsEqualTo("http://127.0.0.1:5100");
        await Assert.That(facts.Activity).IsEqualTo(new DaemonActivity(2, true));
        await Assert.That(facts.DaemonRunning).IsTrue();
        await Assert.That(facts.HostedServerUrl).IsEqualTo("https://kcap.example");
        await Assert.That(facts.OtherInstalls.Select(o => o.Path)).IsEquivalentTo([Path.Combine(bin, "kcap")]);
    }

    [Test]
    public async Task A_fact_that_throws_is_logged_and_left_out() {
        var log = new StringWriter();

        var facts = await Source(
            local: () => throw new IOException("endpoints unreadable"), hosted: () => "https://kcap.example",
            log: log).GatherAsync(CancellationToken.None);

        await Assert.That(facts.LocalServerUrl).IsNull();
        await Assert.That(facts.HostedServerUrl).IsEqualTo("https://kcap.example");
        await Assert.That(log.ToString()).Contains("endpoints unreadable");
    }

    [Test]
    public async Task A_terminal_path_that_never_answers_lists_no_other_installs() {
        var facts = await Source(terminalPath: _ => new TaskCompletionSource<string?>().Task).GatherAsync(CancellationToken.None);

        await Assert.That(facts.OtherInstalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_terminal_path_that_fails_lists_no_other_installs() {
        var facts = await Source(terminalPath: _ => Task.FromException<string?>(new InvalidOperationException("no shell")))
            .GatherAsync(CancellationToken.None);

        await Assert.That(facts.OtherInstalls.Count).IsEqualTo(0);
    }
}
