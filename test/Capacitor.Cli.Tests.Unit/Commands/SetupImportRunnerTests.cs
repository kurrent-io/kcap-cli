using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupImportRunnerTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SetupImportRunner Runner(ProfileContext profiles) => new(
        Config.Root, Home, TestHarnesses.Under(Home),
        new ChosenServerHttp(Config.Root, profiles, ProfileOverrides.None, MachineAuth.None),
        new GitProviderRouter(), TimeProvider.System);

    [Test]
    public async Task Discovery_over_an_empty_home_reports_a_result_and_no_fault() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var discovery = await Runner(profiles).DiscoverAsync(profiles);

        await Assert.That(discovery.Fault).IsNull();
        await Assert.That(discovery.Result).IsNotNull();
        await Assert.That(discovery.Result!.Summary.Repos).IsEmpty();
    }

    [Test]
    public async Task A_run_over_an_empty_home_is_complete_with_an_empty_selection() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var run = await Runner(profiles).RunAsync(new ImportInvocation(
            new ImportScope.All(), MaxSessions: 5, CurrentRepo: null, DefaultVisibility: "private",
            AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: false, Profiles: profiles));

        await Assert.That(run.Fault).IsNull();
        await Assert.That(run.Selection).IsEqualTo(ImportRunSelection.Empty);
        await Assert.That(run.Outcome!.Partition).IsEqualTo(ImportRunPartition.Empty);
    }

    // Claude's per-file and per-project-dir scans all fail open (a hostile transcript or a locked
    // sub-directory is swallowed so one bad file cannot abort the whole scan) — so the only
    // unguarded read left is the top-level `Directory.GetDirectories` in `DiscoverTranscripts`,
    // which is what this test locks out.
    [Test]
    public async Task A_run_that_cannot_scan_its_projects_directory_surfaces_the_fault_without_propagating() {
        if (OperatingSystem.IsWindows()) return;

        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);
        var projects = Path.Combine(Home.Path, ".claude", "projects");
        Directory.CreateDirectory(projects);
        File.SetUnixFileMode(projects, UnixFileMode.None);

        try {
            var run = await Runner(profiles).RunAsync(new ImportInvocation(
                new ImportScope.All(), MaxSessions: null, CurrentRepo: null, DefaultVisibility: "private",
                AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: true, Profiles: profiles));

            await Assert.That(run.Fault).IsNotNull();
        } finally {
            File.SetUnixFileMode(projects,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
