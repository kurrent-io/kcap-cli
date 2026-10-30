using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit;

public class PathShimInstallerTests {
    // --- Preflight ---

    [Test]
    public async Task Preflight_absent_destination_is_installable() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Installable);
    }

    [Test]
    public async Task Preflight_symlink_to_target_is_already_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, target);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.AlreadyInstalled);
    }

    [Test]
    public async Task Preflight_symlink_to_elsewhere_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        var elsewhere = tmp.PathTo("elsewhere-cli");
        File.WriteAllText(elsewhere, "other");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, elsewhere);

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_regular_file_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.PathTo("kcap");
        File.WriteAllText(dest, "not a symlink");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_directory_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.CreateDir("kcap");

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    [Test]
    public async Task Preflight_broken_symlink_is_conflict() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.PathTo("target-cli");
        var dest = tmp.PathTo("kcap");
        File.CreateSymbolicLink(dest, tmp.PathTo("does-not-exist"));

        await Assert.That(PathShimInstaller.Preflight(dest, target)).IsEqualTo(ShimPreflight.Conflict);
    }

    // --- LooksLikeTarget ---

    [Test]
    public async Task LooksLikeTarget_rejects_newline() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/usr/local/bin/ev\nil")).IsFalse();
    }

    [Test]
    public async Task LooksLikeTarget_rejects_carriage_return() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/usr/local/bin/ev\ril")).IsFalse();
    }

    [Test]
    public async Task LooksLikeTarget_accepts_a_plain_path() {
        await Assert.That(PathShimInstaller.LooksLikeTarget("/App Space/kcap")).IsTrue();
    }

    // --- InstallAsync ---

    static PathShimInstaller Installer(TempDir tmp, ILoginShellProbe probe, string? shell = "/bin/zsh") {
        var home = new UserHome(tmp.Path);
        return new PathShimInstaller(probe, home, shell is null ? null : new ShellProfile(home, shell));
    }

    static FakeLoginShellProbe ResolvingTo(string resolved, string terminalPath) => new() {
        TerminalPathBehavior = _ => Task.FromResult<string?>(terminalPath),
        KcapOnPathBehavior   = _ => Task.FromResult<bool?>(true),
        KcapPathBehavior     = _ => Task.FromResult<string?>(resolved),
    };

    [Test]
    public async Task InstallAsync_rejects_a_target_with_a_newline_without_linking() {
        using var tmp = new TempDir();

        var result = await Installer(tmp, new FakeLoginShellProbe()).InstallAsync("/usr/local/bin/ev\nil", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(Directory.Exists(tmp.PathTo(".local"))).IsFalse();
    }

    [Test]
    public async Task InstallAsync_links_into_a_writable_stable_path_folder_and_leaves_the_profile_alone() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string localBin = tmp.CreateDir(".local", "bin");
        var link = Path.Combine(localBin, "kcap");
        var target = tmp.CreateFile("target-cli");

        var result = await Installer(tmp, ResolvingTo(link, $"relative/bin:{localBin}/")).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
    }

    /// A version manager's folder is writable and early on PATH, but is replaced on the next switch.
    [Test]
    public async Task InstallAsync_never_links_into_a_folder_outside_the_stable_set() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string shims = tmp.CreateDir(".pyenv", "shims");
        string localBin = tmp.CreateDir(".local", "bin");
        var link = Path.Combine(localBin, "kcap");
        var target = tmp.CreateFile("target-cli");

        var result = await Installer(tmp, ResolvingTo(link, $"{shims}:{localBin}")).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(File.Exists(Path.Combine(shims, "kcap"))).IsFalse();
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
    }

    [Test]
    public async Task InstallAsync_skips_a_stable_folder_holding_another_kcap() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string localBin = tmp.CreateDir(".local", "bin");
        var foreign = tmp.CreateFile([".local", "bin", "kcap"], "someone else's");
        string bin = tmp.CreateDir("bin");
        var link = Path.Combine(bin, "kcap");
        var target = tmp.CreateFile("target-cli");

        var result = await Installer(tmp, ResolvingTo(link, $"{localBin}:{bin}")).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(File.ReadAllText(foreign)).IsEqualTo("someone else's");
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
    }

    /// Another kcap the terminal still prefers means the link did nothing, so it is taken back.
    [Test]
    public async Task InstallAsync_removes_a_shadowed_link_and_falls_back_to_local_bin() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string bin = tmp.CreateDir("bin");
        var target = tmp.CreateFile("target-cli");

        var result = await Installer(tmp, ResolvingTo("/somewhere/else/kcap", bin)).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
        await Assert.That(result.Detail).Contains("/somewhere/else/kcap");
        await Assert.That(new FileInfo(Path.Combine(bin, "kcap")).Exists).IsFalse();
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
    }

    /// A fresh Mac: nothing on PATH is the user's to write, which is where the admin prompt used to be.
    [Test]
    public async Task InstallAsync_with_no_writable_path_folder_links_into_local_bin_and_adds_the_profile_line() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.CreateFile("target-cli");
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(tmp.PathTo(".local", "bin", "kcap")),
        };

        var result = await Installer(tmp, probe).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
        await Assert.That(File.ReadAllText(tmp.PathTo(".zprofile"))).Contains(ShellProfile.Begin);
    }

    [Test]
    public async Task InstallAsync_never_replaces_another_kcap_in_local_bin() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.CreateFile("target-cli");
        var foreign = tmp.CreateFile([".local", "bin", "kcap"], "someone else's");

        var result = await Installer(tmp, new FakeLoginShellProbe()).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.Detail).Contains("left untouched");
        await Assert.That(File.ReadAllText(foreign)).IsEqualTo("someone else's");
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
        await Assert.That(Installer(tmp, new FakeLoginShellProbe()).Preflight(target)).IsEqualTo(ShimPreflight.Conflict);
    }

    // Without a profile (Linux) nothing puts ~/.local/bin on PATH, so the probe decides alone.
    [Test]
    public async Task InstallAsync_without_a_profile_reports_a_local_link_the_shell_cannot_see() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.CreateFile("target-cli");
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(false) };

        var result = await Installer(tmp, probe, shell: null).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
        await Assert.That(result.Detail).Contains($"export PATH=\"{tmp.PathTo(".local", "bin")}:$PATH\"");
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
    }

    // An unknown re-probe is not "not on PATH": the link is reported Failed rather than diagnosed.
    [Test]
    public async Task InstallAsync_then_probe_null_fails_closed_with_could_not_reverify_detail() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(null) };

        var result = await Installer(tmp, probe, shell: null).InstallAsync(tmp.CreateFile("target-cli"), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.Detail).Contains("could not re-verify");
    }

    /// The offer decision already cached "absent" before the install; the post-install probe must not replay it.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InstallAsync_reprobes_instead_of_trusting_the_cached_answer(bool alreadyLinked) {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var target = tmp.CreateFile("target-cli");
        var link = tmp.PathTo(".local", "bin", "kcap");
        if (alreadyLinked) {
            tmp.CreateDir(".local", "bin");
            File.CreateSymbolicLink(link, target);
        }
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(false),
            KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathFreshBehavior = _ => Task.FromResult<string?>(link),
        };

        var result = await Installer(tmp, probe, shell: null).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("/bin/fish", "safe")]
    [Arguments("/bin/zsh", "a!b")]
    public async Task Unsupported_profile_fallback_does_not_create_a_link(string shell, string folder) {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.CreateDir(folder));
        var installer = new PathShimInstaller(new FakeLoginShellProbe(), home, new ShellProfile(home, shell));

        var result = await installer.InstallAsync(tmp.CreateFile("target-cli"), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(Directory.Exists(Path.Combine(home.Path, ".local"))).IsFalse();
    }

    [Test]
    [Arguments("earlier", true, ShimOutcome.InstalledButNotOnPath)]
    [Arguments(null, true, ShimOutcome.Failed)]
    [Arguments("installed", true, ShimOutcome.Installed)]
    [Arguments("installed", null, ShimOutcome.Failed)]
    public async Task Profile_fallback_verifies_the_fresh_resolved_command(string? command, bool? onPath, ShimOutcome expected) {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var link = tmp.PathTo(".local", "bin", "kcap");
        var earlier = tmp.CreateFile("earlier-kcap");
        var probe = new FakeLoginShellProbe {
            KcapOnPathFreshBehavior = _ => Task.FromResult(onPath),
            KcapPathBehavior = _ => Task.FromResult<string?>(link),
            KcapPathFreshBehavior = _ => Task.FromResult(command is null ? null : command == "installed" ? link : earlier),
        };

        var result = await Installer(tmp, probe).InstallAsync(tmp.CreateFile("target-cli"), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(expected);
        if (command == "earlier") {
            await Assert.That(result.Detail).Contains(earlier);
            await Assert.That(File.Exists(earlier)).IsTrue();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resolved_link_casing_uses_the_host_path_rules(bool throughProfile) {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var localBin = tmp.CreateDir(".local", "bin").Path;
        var link = Path.Combine(localBin, "kcap");
        var target = tmp.CreateFile("target-cli");
        var probe = ResolvingTo(link.ToUpperInvariant(), throughProfile ? "/usr/bin:/bin" : localBin);

        var result = await Installer(tmp, probe, throughProfile ? "/bin/zsh" : null).InstallAsync(target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(OperatingSystem.IsMacOS() ? ShimOutcome.Installed : ShimOutcome.InstalledButNotOnPath);
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
    }

    // --- Disclosure ---

    [Test]
    public async Task Disclosure_shows_the_profile_line_before_it_is_written_and_never_a_password() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);

        var installer = Installer(tmp, new FakeLoginShellProbe());

        await Assert.That(installer.Disclosure).Contains($"export PATH=\"$PATH:{ShellProfile.Folder(home)}\" (in ~{Path.DirectorySeparatorChar}.zprofile)");
        await Assert.That(installer.Disclosure).DoesNotContain("password");
    }

    [Test]
    public async Task DescribeAsync_names_a_writable_folder_already_on_the_terminal_path() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX access(2)");
        using var tmp = new TempDir();
        var bin = tmp.CreateDir("bin");
        var probe = new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>($"/usr/bin:{bin}") };

        var spans = await Installer(tmp, probe).DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{Path.DirectorySeparatorChar}bin"]);
        await Assert.That(File.Exists(Path.Combine(bin, "kcap"))).IsFalse();
    }

    [Test]
    public async Task DescribeAsync_names_the_local_folder_and_the_profile() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();

        var spans = await Installer(tmp, new FakeLoginShellProbe()).DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        var sep = Path.DirectorySeparatorChar;
        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{sep}.local{sep}bin", $"~{sep}.zprofile"]);
        await Assert.That(Text(spans)).DoesNotContain("password");
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
    }

    [Test]
    public async Task DescribeAsync_without_a_profile_names_only_the_local_folder() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();

        var spans = await Installer(tmp, new FakeLoginShellProbe(), shell: null).DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        var sep = Path.DirectorySeparatorChar;
        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{sep}.local{sep}bin"]);
    }

    [Test]
    public async Task DescribeAsync_names_a_local_link_it_would_leave_alone() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        tmp.CreateFile([".local", "bin", "kcap"], "someone else's");

        var spans = await Installer(tmp, new FakeLoginShellProbe()).DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        var sep = Path.DirectorySeparatorChar;
        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{sep}.local{sep}bin{sep}kcap"]);
        await Assert.That(Text(spans)).Contains("belongs to another install");
    }

    [Test]
    public async Task DescribeAsync_on_an_unsupported_shell_asks_the_user_to_set_path() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();

        var spans = await Installer(tmp, new FakeLoginShellProbe(), "/opt/homebrew/bin/fish").DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(Text(spans)).Contains("can't set PATH for this shell");
    }

    static string Text(IEnumerable<DisclosureSpan> spans) => string.Concat(spans.Select(s => s.Text));
}
