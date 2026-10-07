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

    UpdateCommand Command(ScriptReleaseFixture f, List<string> refreshed, Func<string, int>? refreshExit = null) {
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
            RunRefresh   = exe => { refreshed.Add(exe); return Task.FromResult(refreshExit?.Invoke(exe) ?? 0); },
        };
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
        await Assert.That(refreshed).IsEquivalentTo([f.Layout.CurrentBin("kcap")]);
        await Assert.That(f.Requests).Contains("/download/cli/channels/latest.json");
        await Assert.That(output.GetCapturedOutput()).Contains("kcap updated to 999.0.0.");
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
        await Assert.That(output.GetCapturedError()).Contains("run `kcap refresh` to retry");
    }
}
