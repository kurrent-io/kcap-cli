using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerOwnerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Root = "/Users/a/.config/kcap/local";

    UserHome Home => new(Tmp.PathTo("home"));

    LocalServerUnit Unit => field ??= LocalServerUnit.ForInstallation(new LocalRoot(Tmp.PathTo("local")), 51234);

    static Func<string, string[], (int ExitCode, string StdOut, string StdErr)> Scripted(int exit, string stdout, string stderr = "") =>
        (_, _) => (exit, stdout, stderr);

    LocalServerLaunchdService Launchd(Func<string, string[], (int ExitCode, string StdOut, string StdErr)> run) =>
        new(Home, TimeProvider.System, run, uid: 501);

    LocalServerSystemdService Systemd(Func<string, string[], (int ExitCode, string StdOut, string StdErr)> run) =>
        new(Home, TimeProvider.System, run);

    string Plist => LocalServerLaunchdUnit.PlistPath(Home, Unit);

    string UnitFile => LocalServerSystemdUnit.UnitPath(Home, Unit);

    static string Print(string extra) => $"gui/501/io.kurrent.kcap.local = {{\n\tstate = running\n{extra}}}\n";

    [Test]
    public async Task Print_environment_names_the_root() {
        var print = Print($"\tinherited environment = {{\n\t\tKCAP_LOCAL_ROOT => /wrong\n\t}}\n\n\tenvironment = {{\n\t\tKCAP_LOCAL_ROOT => {Root}\n\t\tLocal__Port => 51234\n\t}}\n");

        var owner = Launchd(Scripted(0, print)).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(new OwnerReading.Root(Root));
    }

    [Test]
    public async Task Print_without_the_variable_is_unreadable() {
        var owner = Launchd(Scripted(0, Print("\tenvironment = {\n\t\tLocal__Port => 1\n\t}\n"))).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task The_variable_outside_the_environment_block_is_unreadable() {
        var owner = Launchd(Scripted(0, Print($"\tinherited environment = {{\n\t\tKCAP_LOCAL_ROOT => {Root}\n\t}}\n"))).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_missing_label_is_absent() {
        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(OwnerReading.Absent.Instance);
    }

    [Test]
    public async Task An_unclassifiable_print_failure_is_unreadable() {
        var owner = Launchd(Scripted(1, "", "boom")).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task Plist_names_the_root() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Plist), LocalServerLaunchdUnit.Plist(Unit));

        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root(Unit.Root.Directory));
    }

    [Test]
    public async Task An_unparseable_plist_is_unreadable() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Plist), "<plist><dict>");

        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_plist_without_the_variable_is_unreadable() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Plist),
            "<plist version=\"1.0\"><dict><key>Label</key><string>x</string></dict></plist>");

        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_missing_plist_is_absent() {
        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(OwnerReading.Absent.Instance);
    }

    [Test]
    public async Task Show_environment_names_the_root() {
        var owner = Systemd(Scripted(0, $"LoadState=loaded\nEnvironment=Local__Port=51234 KCAP_LOCAL_ROOT={Root}\n")).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(new OwnerReading.Root(Root));
    }

    [Test]
    public async Task A_quoted_show_value_is_unquoted() {
        var owner = Systemd(Scripted(0, "LoadState=loaded\nEnvironment=\"KCAP_LOCAL_ROOT=/Users/a b/local\" Local__Port=1\n")).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(new OwnerReading.Root("/Users/a b/local"));
    }

    [Test]
    public async Task Show_without_the_variable_is_unreadable() {
        var owner = Systemd(Scripted(0, "LoadState=loaded\nEnvironment=Local__Port=1\n")).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_not_found_unit_is_absent() {
        var owner = Systemd(Scripted(0, "LoadState=not-found\nActiveState=inactive\nEnvironment=\n")).Owner(Unit);

        await Assert.That(owner.Loaded).IsEqualTo(OwnerReading.Absent.Instance);
    }

    [Test]
    public async Task A_failed_show_is_unreadable() {
        var owner = Systemd(Scripted(1, "", "Failed to connect to bus")).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task The_unit_file_names_the_root() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), LocalServerSystemdUnit.Unit(Unit));

        var owner = Systemd(Scripted(0, "LoadState=not-found\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root(Unit.Root.Directory));
    }

    [Test]
    public async Task A_unit_file_without_the_variable_is_unreadable() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), "[Service]\nEnvironment=Local__Port=1\n");

        var owner = Systemd(Scripted(0, "LoadState=not-found\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_missing_unit_file_is_absent() {
        var owner = Systemd(Scripted(0, "LoadState=not-found\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(OwnerReading.Absent.Instance);
    }

    [Test]
    public async Task Install_writes_the_plist_durably() {
        var points = new List<string>();
        using var _ = FaultPoints.Arm(points.Add);

        Launchd((_, args) => args[0] == "print" ? (113, "", "Could not find service") : (0, "", "")).Install(Unit);

        var launchAgents = Path.GetDirectoryName(Plist)!;
        var renamed      = points.IndexOf($"renamed:{Plist}");
        var synced       = points.LastIndexOf($"sync:{launchAgents}");

        await Assert.That(renamed).IsGreaterThanOrEqualTo(0);
        await Assert.That(synced).IsGreaterThan(renamed);
    }

    [Test]
    public async Task Uninstall_reports_the_unlink() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Plist), "x");
        var points = new List<string>();
        using var _ = FaultPoints.Arm(points.Add);

        await new LocalServerLaunchdService(Home, TimeProvider.System, (_, args) => args[0] == "print" ? (113, "", "Could not find service") : (0, "", ""),
            findLive: _ => null, uid: 501).UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(points).Contains($"unlink:{Plist}");
    }

    [Test]
    public async Task A_not_found_unit_that_is_still_active_is_unreadable() {
        var owner = Systemd(Scripted(0, "LoadState=not-found\nActiveState=active\nEnvironment=\n")).Owner(Unit);

        await Assert.That(owner.Loaded).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task The_last_assignment_in_the_unit_file_wins() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), "[Service]\nEnvironment=KCAP_LOCAL_ROOT=/first\nEnvironment=KCAP_LOCAL_ROOT=/second\n");

        var owner = Systemd(Scripted(0, "LoadState=not-found\nActiveState=inactive\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root("/second"));
    }

    [Test]
    public async Task An_empty_environment_line_resets_the_unit_file() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), "[Service]\nEnvironment=KCAP_LOCAL_ROOT=/first\nEnvironment=\n");

        var owner = Systemd(Scripted(0, "LoadState=not-found\nActiveState=inactive\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsTypeOf<OwnerReading.Unreadable>();
    }

    [Test]
    public async Task A_root_with_special_characters_round_trips_through_the_plist() {
        var odd = new LocalServerUnit("/bin/x", new LocalRoot(Tmp.PathTo("a&b <c> \"q\"")), 1);
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Plist), LocalServerLaunchdUnit.Plist(odd));

        var owner = Launchd(Scripted(113, "", "Could not find service")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root(odd.Root.Directory));
    }

    [Test]
    public async Task A_root_with_special_characters_round_trips_through_the_unit_file() {
        var odd = new LocalServerUnit("/bin/x", new LocalRoot(Tmp.PathTo("a&b %c \"q\" \\d")), 1);
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), LocalServerSystemdUnit.Unit(odd));

        var owner = Systemd(Scripted(0, "LoadState=not-found\nActiveState=inactive\n")).Owner(Unit);

        await Assert.That(owner.OnDisk).IsEqualTo(new OwnerReading.Root(odd.Root.Directory));
    }

    [Test]
    public async Task Systemd_uninstall_reports_the_unlink() {
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, UnitFile), "x");
        var points = new List<string>();
        using var _ = FaultPoints.Arm(points.Add);

        await new LocalServerSystemdService(Home, TimeProvider.System, (_, _) => (0, "", ""), findLive: _ => null)
            .UninstallAsync(Unit, CancellationToken.None);

        await Assert.That(points).Contains($"unlink:{UnitFile}");
    }
}
