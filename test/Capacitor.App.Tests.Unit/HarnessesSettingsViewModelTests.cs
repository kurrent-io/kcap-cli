using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class HarnessesSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SettingsProfileStore Seed() {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example" }
        } });
        return new SettingsProfileStore(Config.Root, "work", "https://work.example");
    }

    [Test]
    public Task Rescan_detects_new_harnesses_and_connect_handles_failure_then_retry() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var wired = false;
        var detected = new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, false) };
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(detected), isWired: _ => wired);
        await vm.RescanAsync();
        await Assert.That(vm.Rows.Count).IsEqualTo(1);
        var row = vm.Rows[0];
        await Assert.That(row.Signal).Contains("PATH");
        await Assert.That(row.ConnectionLabel).IsEqualTo("Recording not configured");
        await Assert.That(row.ConsentNote).IsNotNull();
        cli.PluginInstallAsync("--codex", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new ProcessResult(1, "", "installation failed", false), new ProcessResult(0, "", "", false));
        await vm.ConnectAsync(row);
        await Assert.That(row.Message).IsEqualTo("installation failed");
        wired = true;
        await vm.ConnectAsync(row);
        await Assert.That(row.Message!).Contains("Setup completed");
        await Assert.That(row.ConnectionLabel).IsEqualTo("Recording configured");
        detected[HarnessId.Cursor] = new(false, true);
        await vm.RescanAsync();
        await Assert.That(vm.Rows.Count).IsEqualTo(2);
        await Assert.That(vm.Rows.Single(r => r.Id == HarnessId.Cursor).Signal).Contains("configuration");
    });

    [Test]
    public Task Setup_and_rescan_do_not_overlap_and_timeout_is_not_success() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        var completion = new TaskCompletionSource<ProcessResult>();
        cli.PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>()).Returns(completion.Task);
        var scans = 0;
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => {
            scans++;
            return Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Claude] = new(true, true)
            });
        });
        await vm.RescanAsync();
        var operation = vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(vm.Busy).IsTrue();
        await vm.RescanAsync();
        await vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(scans).IsEqualTo(1);
        completion.SetResult(new ProcessResult(0, "", "", true));
        await operation;
        await Assert.That(vm.Rows[0].Message!).Contains("timed out");
        await Assert.That(vm.Busy).IsFalse();
        await cli.Received(1).PluginInstallAsync(HarnessId.Claude.PluginInstallFlag, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });

    [Test]
    public Task Repointed_profile_refuses_setup_before_spawning_cli() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = Substitute.For<IKcapCli>();
        using var vm = new HarnessesSettingsViewModel(Seed(), cli, _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
            new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Codex] = new(true, true) }));
        await vm.RescanAsync();
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { ServerUrl = "https://replacement.example" }
        } });
        await vm.ConnectAsync(vm.Rows[0]);
        await Assert.That(vm.Rows[0].Message!).Contains("profile changed");
        await cli.DidNotReceive().PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>());
    });
}
