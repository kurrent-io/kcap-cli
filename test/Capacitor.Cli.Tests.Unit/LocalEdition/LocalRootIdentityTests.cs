using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalRootIdentityTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static OwnerReading.Root At(string path) => new(path);

    const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    static void SkipUnlessModesEnforced() =>
        Skip.When(OperatingSystem.IsWindows() || Environment.UserName == "root", "directory modes are not enforced");

    static OwnerReading Gone => OwnerReading.Absent.Instance;

    [Test]
    public async Task Ordinally_equal_resolved_paths_are_same() {
        var root = new LocalRoot(Tmp.CreateDir("root"));

        await Assert.That(LocalRootIdentity.Compare(root, root.Directory)).IsEqualTo(RootMatch.Same);
    }

    [Test]
    public async Task A_symlinked_ancestor_alias_is_same_via_resolution() {
        Skip.When(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");

        var real = Tmp.CreateDir("real");
        Directory.CreateSymbolicLink(Tmp.PathTo("alias"), real);
        var root = new LocalRoot(Path.Combine(real, "local"));
        Directory.CreateDirectory(root.Directory);

        await Assert.That(LocalRootIdentity.Compare(root, Path.Combine(Tmp.PathTo("alias"), "local"))).IsEqualTo(RootMatch.Same);
    }

    [Test]
    public async Task A_nonexistent_other_is_different() {
        var root = new LocalRoot(Tmp.CreateDir("root"));

        await Assert.That(LocalRootIdentity.Compare(root, Tmp.PathTo("nowhere"))).IsEqualTo(RootMatch.Different);
    }

    [Test]
    public async Task A_distinct_existing_directory_is_different() {
        var root = new LocalRoot(Tmp.CreateDir("root"));
        var other = Tmp.CreateDir("other");

        await Assert.That(LocalRootIdentity.Compare(root, other)).IsEqualTo(RootMatch.Different);
        await Assert.That(Directory.GetFileSystemEntries(root.Directory)).IsEmpty();
    }

    [Test]
    public async Task A_case_variant_goes_to_the_probe() {
        var root = new LocalRoot(Tmp.CreateDir("Root"));

        Skip.When(!Directory.Exists(Tmp.PathTo("root")), "the volume is case-sensitive");

        await Assert.That(LocalRootIdentity.Compare(root, Tmp.PathTo("root"))).IsEqualTo(RootMatch.Same);
    }

    [Test]
    [ParallelLimiter<SubprocessLimit>]
    public async Task Case_only_roots_on_a_case_sensitive_volume_are_different() {
        Skip.When(!OperatingSystem.IsMacOS(), "needs hdiutil");

        // Spotlight and fseventsd read these markers at mount, so the volume never gets a busy holder.
        var seed = Tmp.CreateDir("seed");
        seed.CreateFile(".metadata_never_index");
        seed.CreateFile(".fseventsd/no_log");

        var image = Tmp.PathTo("cs.dmg");
        var mount = Tmp.CreateDir("m");
        string? device = null;

        try {
            Skip.When(Run("hdiutil", "create", "-srcfolder", seed, "-format", "UDRW", "-size", "20m", "-fs", "Case-sensitive APFS", "-volname", "kcs", image) != 0, "hdiutil create failed");
            var (attachExit, attachOutput) = RunWithOutput("hdiutil", "attach", "-nobrowse", "-noautoopen", "-owners", "off", "-mountpoint", mount, image);
            Skip.When(attachExit != 0, "hdiutil attach failed");
            // The first device row names the image's whole disk; detaching that releases the image, not just its volume.
            device = attachOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(line => line.Split((char[])[' ', '\t'], 2)[0])
                         .FirstOrDefault(WholeDisk.IsMatch) ?? mount;

            var upper = Directory.CreateDirectory(Path.Combine(mount, "Root")).FullName;
            Directory.CreateDirectory(Path.Combine(mount, "root"));
            var root = new LocalRoot(upper);

            await Assert.That(LocalRootIdentity.Compare(root, Path.Combine(mount, "root"))).IsEqualTo(RootMatch.Different);
        } finally {
            if (device is not null) await DetachAsync(device, mount);
        }
    }

    // Cleanup, not what the test pins: on a loaded runner Spotlight or fseventsd can hold a fresh volume busy past
    // every forced detach, and failing the test there would report a passing comparison as a failure.
    static async Task DetachAsync(string device, string mount) {
        foreach (var pause in (int[])[0, 1, 2, 4, 8]) {
            await Task.Delay(TimeSpan.FromSeconds(pause));
            if (Run("hdiutil", "detach", "-force", device) == 0) return;
        }

        if (Run("diskutil", "unmount", "force", mount) == 0) Run("hdiutil", "detach", "-force", device);
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_root_is_unknown() {
        SkipUnlessModesEnforced();

        var rootDir = Tmp.CreateDir("root");
        var other   = Tmp.CreateDir("other");
        var root    = new LocalRoot(rootDir);

        File.SetUnixFileMode(rootDir, UnixFileMode.None);

        try {
            await Assert.That(LocalRootIdentity.Compare(root, other)).IsEqualTo(RootMatch.Unknown);
        } finally {
            File.SetUnixFileMode(rootDir, Open);
        }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_other_is_unknown() {
        SkipUnlessModesEnforced();

        var root  = new LocalRoot(Tmp.CreateDir("root"));
        var other = Tmp.CreateDir("other");

        File.SetUnixFileMode(other, UnixFileMode.None);

        try {
            await Assert.That(LocalRootIdentity.Compare(root, other)).IsEqualTo(RootMatch.Unknown);
        } finally {
            File.SetUnixFileMode(other, Open);
        }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_other_under_an_unreadable_parent_is_unknown() {
        SkipUnlessModesEnforced();

        var root   = new LocalRoot(Tmp.CreateDir("root"));
        var parent = Tmp.CreateDir("parent");
        Directory.CreateDirectory(Path.Combine(parent, "child"));

        File.SetUnixFileMode(parent, UnixFileMode.None);

        try {
            await Assert.That(LocalRootIdentity.Compare(root, Path.Combine(parent, "child"))).IsEqualTo(RootMatch.Unknown);
        } finally {
            File.SetUnixFileMode(parent, Open);
        }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Decide_unknown_when_a_root_reading_cannot_be_compared() {
        SkipUnlessModesEnforced();

        var rootDir = Tmp.CreateDir("root");
        var other   = Tmp.CreateDir("other");
        var root    = new LocalRoot(rootDir);

        File.SetUnixFileMode(rootDir, UnixFileMode.None);

        try {
            await Assert.That(LocalRootIdentity.Decide(root, new(Gone, At(other)))).IsEqualTo(OwnerDecision.Unknown);
        } finally {
            File.SetUnixFileMode(rootDir, Open);
        }
    }

    [Test]
    public async Task Decide_ours_when_every_reading_is_the_root() {
        var root = new LocalRoot(Tmp.CreateDir("root"));

        await Assert.That(LocalRootIdentity.Decide(root, new(At(root.Directory), At(root.Directory)))).IsEqualTo(OwnerDecision.Ours);
        await Assert.That(LocalRootIdentity.Decide(root, new(At(root.Directory), Gone))).IsEqualTo(OwnerDecision.Ours);
    }

    [Test]
    public async Task Decide_not_ours_when_nothing_is_recorded() {
        var root = new LocalRoot(Tmp.CreateDir("root"));

        await Assert.That(LocalRootIdentity.Decide(root, new(Gone, Gone))).IsEqualTo(OwnerDecision.NotOurs);
    }

    [Test]
    public async Task Decide_not_ours_when_every_reading_is_elsewhere() {
        var root  = new LocalRoot(Tmp.CreateDir("root"));
        var other = Tmp.CreateDir("other");
        var third = Tmp.CreateDir("third");

        await Assert.That(LocalRootIdentity.Decide(root, new(At(other), Gone))).IsEqualTo(OwnerDecision.NotOurs);
        await Assert.That(LocalRootIdentity.Decide(root, new(At(other), At(third)))).IsEqualTo(OwnerDecision.NotOurs);
    }

    [Test]
    public async Task Decide_unknown_when_readings_disagree_or_one_is_unreadable() {
        var root  = new LocalRoot(Tmp.CreateDir("root"));
        var other = Tmp.CreateDir("other");

        await Assert.That(LocalRootIdentity.Decide(root, new(At(root.Directory), At(other)))).IsEqualTo(OwnerDecision.Unknown);
        await Assert.That(LocalRootIdentity.Decide(root, new(new OwnerReading.Unreadable("denied"), Gone))).IsEqualTo(OwnerDecision.Unknown);
    }

    // A whole disk, not one of its slices (/dev/disk4s1): detaching the slice can leave the image attached.
    static readonly Regex WholeDisk = new(@"^/dev/disk\d+$");

    static int Run(string file, params string[] args) => RunWithOutput(file, args).Exit;

    static (int Exit, string Output) RunWithOutput(string file, params string[] args) {
        try {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            // Both drained at once: a child that fills one pipe while the other is read to its end would block forever.
            var output = p.StandardOutput.ReadToEndAsync();
            var error  = p.StandardError.ReadToEndAsync();
            Task.WaitAll(output, error);
            p.WaitForExit();

            return (p.ExitCode, output.Result);
        } catch (System.ComponentModel.Win32Exception) {
            return (-1, "");
        }
    }
}
