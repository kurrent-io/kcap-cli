using Capacitor.App.Services;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class UninstallFactsSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UninstallFactsSource Source(
        Func<string?>? local = null, Func<DaemonActivity?>? activity = null, Func<IReadOnlyList<string>>? live = null,
        Func<string?>? hosted = null, Func<CancellationToken, Task<string?>>? terminalPath = null, TextWriter? log = null) => new(
        local ?? (() => null), "default", activity ?? (() => null), live ?? (() => []), hosted ?? (() => null),
        terminalPath ?? (_ => Task.FromResult<string?>(null)), Tmp.GetResolvedPath("Kcap.app"),
        TimeSpan.FromMilliseconds(100), TimeProvider.System, log ?? TextWriter.Null);

    [Test, ExcludeOn(OS.Windows)]
    public async Task Gathers_every_fact() {
        Tmp.CreateExecutable("bin/kcap", "#!/bin/sh\n");
        var bin = Tmp.GetResolvedPath("bin");

        var facts = await Source(
            local: () => "http://127.0.0.1:5100", activity: () => new DaemonActivity(2, true), live: () => ["default", "laptop"],
            hosted: () => "https://kcap.example", terminalPath: _ => Task.FromResult<string?>(bin)).GatherAsync(CancellationToken.None);

        await Assert.That(facts.LocalServerUrl).IsEqualTo("http://127.0.0.1:5100");
        await Assert.That(facts.Activity).IsEqualTo(new DaemonActivity(2, true));
        await Assert.That(facts.LiveDaemons).IsEquivalentTo(["default", "laptop"]);
        await Assert.That(facts.HostedServerUrl).IsEqualTo("https://kcap.example");
        await Assert.That(facts.OtherInstalls!.Select(o => o.Path)).IsEquivalentTo([Path.Combine(bin, "kcap")]);
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
    public async Task Unreadable_daemons_are_unknown_not_none() {
        var facts = await Source(live: () => throw new IOException("daemons unreadable")).GatherAsync(CancellationToken.None);

        await Assert.That(facts.LiveDaemons).IsNull();
    }

    [Test]
    public async Task A_terminal_path_that_never_answers_leaves_other_installs_unknown() {
        var facts = await Source(terminalPath: _ => new TaskCompletionSource<string?>().Task).GatherAsync(CancellationToken.None);

        await Assert.That(facts.OtherInstalls).IsNull();
    }

    [Test]
    public async Task A_terminal_path_that_fails_leaves_other_installs_unknown() {
        var facts = await Source(terminalPath: _ => Task.FromException<string?>(new InvalidOperationException("no shell")))
            .GatherAsync(CancellationToken.None);

        await Assert.That(facts.OtherInstalls).IsNull();
    }

    [Test]
    public async Task A_terminal_path_without_kcap_finds_none() {
        var bin = Tmp.CreateDir("empty-bin").Path;

        var facts = await Source(terminalPath: _ => Task.FromResult<string?>(bin)).GatherAsync(CancellationToken.None);

        await Assert.That(facts.OtherInstalls).IsNotNull();
        await Assert.That(facts.OtherInstalls!.Count).IsEqualTo(0);
    }
    [Test]
    public async Task Gathers_profile_edits_for_the_confirmation() {
        var source = Source() with { PathProfiles = () => ["/Users/standard/.zprofile"] };
        var facts = await source.GatherAsync(CancellationToken.None);
        await Assert.That(facts.PathProfiles).IsEquivalentTo(["/Users/standard/.zprofile"]);
    }

    [Test]
    public async Task Unreadable_profile_facts_keep_the_conservative_disclosure() {
        var source = Source() with { PathProfiles = () => throw new IOException("unreadable") };
        var facts = await source.GatherAsync(CancellationToken.None);
        await Assert.That(UninstallConfirmation.Build(facts).Prompt.Disclosure).Contains("shell profile");
    }

}
