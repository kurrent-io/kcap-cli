using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap update</c> on a script install reads the installer's channel manifest instead of npm,
/// installs the newer release itself and then runs the NEW binary's <c>kcap refresh</c>.
/// </summary>
// Bare: the command writes to Console.
[NotInParallel]
public class UpdateCommandScriptInstallTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    ServiceProvider? _sp;

    public void Dispose() => _sp?.Dispose();

    UpdateCommand Command(ScriptReleaseFixture f, List<string> refreshed, Func<string, int>? refreshExit = null, int upgradeExit = LocalUpgradeCommand.NothingToDo) {
        var profiles = Resolutions.Of(new Profile(), "script-update", null);
        var services = new ServiceCollection();
        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer("", Config.Root, profiles));
        services.AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None);
        _sp = services.BuildServiceProvider();

        var releases = f.Client();

        return new UpdateCommand(
            Config.Root, profiles, new NpmRegistryClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") }),
            _sp.GetRequiredService<CapacitorServer>(), _sp.GetRequiredService<ICapacitorHttpClient>(), TimeProvider.System,
            releases, new ScriptUpdater(releases), InstallKind.Script) {
            ScriptLayout = f.Layout,
            Rid          = ScriptReleaseFixture.Rid,
            RunRefresh      = exe => { refreshed.Add($"refresh:{exe}"); return Task.FromResult(refreshExit?.Invoke(exe) ?? 0); },
            RunLocalUpgrade = exe => { refreshed.Add($"local upgrade:{exe}"); return Task.FromResult(upgradeExit); },
        };
    }

    [Test]
    [Arguments(false, LocalUpgradeCommand.Upgraded, 0)]
    [Arguments(false, LocalUpgradeCommand.NotPublished, 0)]
    [Arguments(false, LocalUpgradeCommand.Failed, 1)]
    [Arguments(true, LocalUpgradeCommand.Failed, 0)]
    public async Task A_current_cli_retries_the_local_stack_unless_only_checking(bool checkOnly, int upgradeExit, int expectedExit) {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("0.0.1");
        var calls = new List<string>();
        using var output = ConsoleOutput.StartFullCapture();

        var exit = await Command(f, calls, upgradeExit: upgradeExit).HandleAsync(checkOnly ? ["--check"] : []);

        await Assert.That(exit).IsEqualTo(expectedExit);
        await Assert.That(calls).IsEquivalentTo(checkOnly ? [] : new[] { $"local upgrade:{f.Layout.CurrentBin("kcap")}" });
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.0.0");
    }

    [Test]
    public async Task A_newer_channel_release_is_installed_and_refreshed_by_the_new_binary() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("999.0.0");
        var refreshed = new List<string>();

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, refreshed).HandleAsync([]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/999.0.0");
        await Assert.That(refreshed).IsEquivalentTo([$"refresh:{f.Layout.CurrentBin("kcap")}", $"local upgrade:{f.Layout.CurrentBin("kcap")}"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(f.Requests).Contains("/download/cli/channels/latest.json");
        await Assert.That(output.GetCapturedOutput()).Contains("kcap updated to 999.0.0.");
    }

    /// <summary>The CLI is updated either way; the exit says the stack did not follow it.</summary>
    [Test]
    [Arguments(LocalUpgradeCommand.Upgraded, 0)]
    [Arguments(LocalUpgradeCommand.NotPublished, 0)]
    [Arguments(LocalUpgradeCommand.Failed, 1)]
    [Arguments(LocalUpgradeCommand.FailedAfterSwitch, 1)]
    public async Task A_failed_local_upgrade_fails_the_update(int upgradeExit, int exit) {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("999.0.0");

        using var output = ConsoleOutput.StartFullCapture();

        await Assert.That(await Command(f, [], upgradeExit: upgradeExit).HandleAsync([])).IsEqualTo(exit);
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/999.0.0");
    }

    /// <summary>An install from the beta channel keeps updating from it while the profile names none.</summary>
    [Test]
    public async Task The_channel_the_install_recorded_is_the_default() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.RecordChannel("beta");
        f.Publish("999.0.0", channel: "beta");

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, []).HandleAsync(["--check"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(f.Requests).Contains("/download/cli/channels/beta.json");
        await Assert.That(f.Requests).DoesNotContain("/download/cli/channels/latest.json");
    }

    [Test]
    public async Task An_explicit_stable_flag_overrides_the_recorded_channel() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.RecordChannel("beta");
        f.Publish("999.0.0");

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, []).HandleAsync(["--check", "--stable"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(f.Requests).Contains("/download/cli/channels/latest.json");
    }

    /// <summary>The switch is recorded where the next run reads it, even when nothing newer installs.</summary>
    [Test]
    public async Task A_beta_switch_is_recorded_in_the_marker() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.0.0", channel: "beta");

        using var output = ConsoleOutput.StartFullCapture();
        await Command(f, []).HandleAsync(["--check", "--beta"]);

        await Assert.That(f.Layout.RecordedChannel()).IsEqualTo("beta");
    }

    [Test]
    public async Task The_check_reads_the_channel_manifest_not_npm() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("999.0.0");

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, []).HandleAsync(["--check"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output.GetCapturedOutput()).Contains("\"latest\":\"999.0.0\"");
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.0.0");
    }

    /// <summary>A failed verification is a failed update, and no refresh runs against a binary that was
    /// never installed.</summary>
    [Test]
    public async Task A_checksum_mismatch_fails_the_update_and_refreshes_nothing() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("999.0.0", manifestSha: new string('b', 64));
        var refreshed = new List<string>();

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, refreshed).HandleAsync([]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output.GetCapturedError()).Contains("Checksum mismatch");
        await Assert.That(refreshed).IsEmpty();
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.0.0");
    }

    /// <summary>The binary is in place once the switch happened; a refresh that fails says how to retry.</summary>
    [Test]
    public async Task A_failed_refresh_still_reports_the_update() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("999.0.0");

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await Command(f, [], _ => 1).HandleAsync([]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(output.GetCapturedError()).Contains("Run `kcap refresh` to retry");
    }
}
