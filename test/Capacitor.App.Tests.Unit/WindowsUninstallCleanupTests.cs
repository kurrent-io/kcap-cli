using Capacitor.App.Platform.Windows;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Velopack kills the before-uninstall hook at 30 s, so the steps' budgets have to fit inside it with the PATH
/// removal still to run.
public class WindowsUninstallCleanupTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly List<string> _steps = [];
    readonly List<TimeSpan> _budgets = [];

    void Run(string cli, LocalRoot local) =>
        WindowsUninstallCleanup.Run(cli, Tmp.PathTo("app"), local, (_, args, budget) => {
            _steps.Add(string.Join(' ', args));
            _budgets.Add(budget);
        }, () => _steps.Add("path"));

    [Test]
    [Arguments("local/install.json")]
    [Arguments("local/journal.json")]
    public async Task A_local_installation_is_uninstalled_after_the_daemon_and_before_the_path(string evidence) {
        var cli = Tmp.CreateFile("app/bin/kcap.exe");
        Tmp.CreateFile(evidence, "{}");

        Run(cli, new LocalRoot(Tmp.PathTo("local")));

        await Assert.That(_steps).IsEquivalentTo([
            $"daemon service uninstall --under {Tmp.PathTo("app")}",
            "local uninstall --yes --force --quick",
            "path",
        ], CollectionOrdering.Matching);
        await Assert.That(_budgets).IsEquivalentTo([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)], CollectionOrdering.Matching);
        await Assert.That(_budgets.Aggregate(TimeSpan.Zero, (sum, b) => sum + b)).IsLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Without_a_local_installation_only_the_daemon_and_the_path_are_removed() {
        var cli = Tmp.CreateFile("app/bin/kcap.exe");

        Run(cli, new LocalRoot(Tmp.PathTo("local")));

        await Assert.That(_steps).IsEquivalentTo([$"daemon service uninstall --under {Tmp.PathTo("app")}", "path"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_missing_cli_still_removes_the_path() {
        Tmp.CreateFile("local/install.json", "{}");

        Run(Tmp.PathTo("app", "bin", "kcap.exe"), new LocalRoot(Tmp.PathTo("local")));

        await Assert.That(_steps).IsEquivalentTo(["path"], CollectionOrdering.Matching);
    }
}
