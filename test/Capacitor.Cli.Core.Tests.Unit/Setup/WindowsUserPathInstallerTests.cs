using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class WindowsUserPathInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeUserPathStore(string? initial) : IUserPathStore {
        public string? Value = initial;
        public int Appends;
        public Exception? AppendFailure;

        public string? Read() => Value;

        public void Append(string directory) {
            if (AppendFailure is not null) throw AppendFailure;
            Appends++;
            Value = string.IsNullOrEmpty(Value) ? directory : Value + ";" + directory;
        }
    }

    string Target => Path.Combine(Tmp.Path, "app", "kcap.exe");
    string TargetDir => Path.Combine(Tmp.Path, "app");

    [Test]
    public async Task Appends_the_cli_folder_and_reports_installed_once_the_probe_finds_it() {
        var store = new FakeUserPathStore(@"C:\tools");
        var probe = new FakeLoginShellProbe { KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true) };

        var result = await new WindowsUserPathInstaller(store, probe).InstallAsync(Target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(store.Value).IsEqualTo(@"C:\tools;" + TargetDir);
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_folder_already_on_the_user_path_is_not_appended_twice() {
        var store = new FakeUserPathStore(TargetDir.ToUpperInvariant() + @"\");
        var installer = new WindowsUserPathInstaller(store, new FakeLoginShellProbe());

        await Assert.That(installer.Preflight(Target)).IsEqualTo(ShimPreflight.AlreadyInstalled);
        var result = await installer.InstallAsync(Target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(store.Appends).IsEqualTo(0);
    }

    [Test]
    public async Task Preflight_is_installable_when_the_folder_is_absent_and_never_a_conflict() {
        var installer = new WindowsUserPathInstaller(new FakeUserPathStore(null), new FakeLoginShellProbe());

        await Assert.That(installer.Preflight(Target)).IsEqualTo(ShimPreflight.Installable);
    }

    [Test]
    public async Task A_relative_target_fails_without_touching_the_path() {
        var store = new FakeUserPathStore(null);

        var result = await new WindowsUserPathInstaller(store, new FakeLoginShellProbe()).InstallAsync("kcap.exe", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(store.Appends).IsEqualTo(0);
    }

    [Test]
    public async Task A_folder_containing_a_separator_fails_without_touching_the_path() {
        var store = new FakeUserPathStore(null);
        var target = Path.Combine(Tmp.Path, "a;b", "kcap.exe");

        var result = await new WindowsUserPathInstaller(store, new FakeLoginShellProbe()).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(store.Appends).IsEqualTo(0);
    }

    [Test]
    public async Task A_registry_refusal_is_a_failed_outcome_with_its_reason() {
        var store = new FakeUserPathStore(null) { AppendFailure = new UnauthorizedAccessException("denied") };

        var result = await new WindowsUserPathInstaller(store, new FakeLoginShellProbe()).InstallAsync(Target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.Detail).Contains("denied");
    }

    [Test]
    [Arguments(false, ShimOutcome.InstalledButNotOnPath)]
    [Arguments(null, ShimOutcome.Failed)]
    public async Task The_re_probe_decides_the_outcome(bool? onPath, ShimOutcome expected) {
        var probe = new FakeLoginShellProbe { KcapOnPathFreshBehavior = _ => Task.FromResult(onPath) };

        var result = await new WindowsUserPathInstaller(new FakeUserPathStore(null), probe).InstallAsync(Target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(expected);
    }
}
