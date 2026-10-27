using System.Diagnostics;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit;

public class PathShimInstallerTests {
    sealed class FakeProcessRunner : IProcessRunner {
        public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args, options));
            return _step();
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

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

    // --- OsascriptArgs ---

    [Test]
    public async Task OsascriptArgs_passes_the_target_as_the_last_raw_argv_element() {
        var args = PathShimInstaller.OsascriptArgs("/App Space/kcap");

        await Assert.That(args[^1]).IsEqualTo("/App Space/kcap");
        await Assert.That(args[^2]).IsEqualTo("--");
    }

    [Test]
    public async Task OsascriptArgs_script_never_interpolates_the_target() {
        var args = PathShimInstaller.OsascriptArgs("/App Space/kcap");
        // Everything before the "--" separator is the AppleScript source; the target lives only
        // in the argv element after it (asserted separately above).
        var separator = Array.IndexOf(args, "--");
        var scriptSource = string.Join('\n', args[..separator]);

        await Assert.That(scriptSource).Contains("quoted form of item 1 of argv");
        await Assert.That(scriptSource).Contains("mkdir -p /usr/local/bin");
        await Assert.That(scriptSource).Contains("ln -s ");
        await Assert.That(scriptSource).DoesNotContain("-f");
        await Assert.That(scriptSource).DoesNotContain("/App Space/kcap");
    }

    // --- PosixQuote ---

    [Test]
    public async Task PosixQuote_escapes_single_quotes() {
        await Assert.That(PathShimInstaller.PosixQuote("a'b")).IsEqualTo("'a'\"'\"'b'");
    }

    [Test]
    public async Task PosixQuote_round_trips_through_a_real_shell() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX shell");

        const string input = "a'b \"c\" \\d";
        var quoted = PathShimInstaller.PosixQuote(input);

        var psi = new ProcessStartInfo("/bin/sh", ["-c", $"printf %s {quoted}"]) {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(output).IsEqualTo(input);
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

    [Test]
    public async Task InstallAsync_cancel_is_detected_via_negative_128_in_stderr() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Cancelled);
    }

    // Regression: a genuine failure's shell error text can embed the (unescaped, by the shell's
    // OWN error reporting — not our argv passing) target path verbatim. A target path that just
    // happens to contain the bare substring "-128" (e.g. a PR/build-numbered directory) must not
    // be misread as AppleScript's "(-128)" cancellation marker — that would silently discard the
    // real failure's Detail and SudoFallback.
    [Test]
    public async Task InstallAsync_bare_negative_128_substring_without_parens_is_not_treated_as_cancel() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("app-128", "kcap");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", $"ln: {target}: Permission denied", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.SudoFallback).IsEqualTo(
            "sudo mkdir -p /usr/local/bin && sudo ln -s " + PathShimInstaller.PosixQuote(target) + " /usr/local/bin/kcap");
    }

    [Test]
    public async Task InstallAsync_other_failure_reports_the_exact_sudo_fallback() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "administrator privileges denied", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.SudoFallback).IsEqualTo(
            "sudo mkdir -p /usr/local/bin && sudo ln -s " + PathShimInstaller.PosixQuote(target) + " /usr/local/bin/kcap");
    }

    [Test]
    public async Task InstallAsync_success_then_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(dest),
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
    }

    [Test]
    public async Task InstallAsync_success_then_probe_false_is_installed_but_not_on_path() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(false) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
    }

    // An unknown re-probe is NOT the same as "not on PATH": asserting the PATH's contents from a
    // probe that returned nothing would be a guess, so the link is reported Failed with a
    // "could not re-verify" detail rather than a fabricated diagnosis.
    [Test]
    public async Task InstallAsync_success_then_probe_null_fails_closed_with_could_not_reverify_detail() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(null) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(result.Detail).Contains("could not re-verify");
        await Assert.That(result.SudoFallback).IsNull();
    }

    [Test]
    public async Task InstallAsync_rejects_a_target_with_a_newline_without_calling_the_runner() {
        var runner = new FakeProcessRunner();
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await installer.InstallAsync("/usr/local/bin/ev\nil", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_conflict_preflight_fails_without_calling_the_runner() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(dest, "pre-existing, not a symlink");
        var runner = new FakeProcessRunner();
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe());

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_already_installed_preflight_succeeds_without_calling_the_runner() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(dest),
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Regression: AlreadyInstalled used to short-circuit straight to Installed on the symlink
    // alone (spec §5 forbids exactly that — "never report success on the symlink alone"). The
    // symlink resolving to our target says nothing about whether the login shell's PATH actually
    // includes /usr/local/bin, so AlreadyInstalled must run the SAME post-install probe the
    // freshly-linked branch does.
    [Test]
    public async Task InstallAsync_already_installed_then_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(dest),
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
    }

    // Regression: ShimOfferCoordinator's offer decision already consumed
    // KcapOnPathAsync (caching its "absent" answer) before ever calling InstallAsync — the
    // post-install probe must not just replay that stale cached answer.
    [Test]
    public async Task InstallAsync_success_stale_cached_false_but_fresh_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, "", "", false));
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(false), // the stale, already-cached pre-install answer
            KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathFreshBehavior = _ => Task.FromResult<string?>(dest), // the real post-install state
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task InstallAsync_already_installed_stale_cached_false_but_fresh_probe_true_is_installed() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(false),
            KcapOnPathFreshBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathFreshBehavior = _ => Task.FromResult<string?>(dest),
        };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(runner.Calls).IsEmpty();
        await Assert.That(probe.KcapOnPathForceRefreshCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task InstallAsync_already_installed_then_probe_false_is_installed_but_not_on_path() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var dest = tmp.PathTo("kcap");
        var target = tmp.PathTo("target-cli");
        File.WriteAllText(target, "cli");
        File.CreateSymbolicLink(dest, target);
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(false) };
        var installer = new PathShimInstaller(runner, probe);

        var result = await Install(installer, dest, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.InstalledButNotOnPath);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_links_into_a_writable_stable_path_folder_without_a_prompt() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string localBin = tmp.CreateDir(".local", "bin");
        var link = Path.Combine(localBin, "kcap");
        var target = tmp.CreateFile("target-cli");
        var runner = new FakeProcessRunner();
        var probe = ResolvingTo(link, $"relative/bin:{localBin}/");
        var installer = new PathShimInstaller(runner, probe, home: new UserHome(tmp.Path));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
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
        var probe = ResolvingTo(link, $"{shims}:{localBin}");
        var installer = new PathShimInstaller(new FakeProcessRunner(), probe, home: new UserHome(tmp.Path));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

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
        var probe = ResolvingTo(link, $"{localBin}:{bin}");
        var installer = new PathShimInstaller(new FakeProcessRunner(), probe, home: new UserHome(tmp.Path));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(File.ReadAllText(foreign)).IsEqualTo("someone else's");
        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
    }

    /// Another kcap the terminal still prefers means the link did nothing, so it is taken back.
    [Test]
    public async Task InstallAsync_removes_a_shadowed_link_and_falls_back_to_the_admin_prompt() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        string localBin = tmp.CreateDir(".local", "bin");
        var target = tmp.CreateFile("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var probe = ResolvingTo("/somewhere/else/kcap", localBin);
        var installer = new PathShimInstaller(runner, probe, home: new UserHome(tmp.Path));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Cancelled);
        await Assert.That(new FileInfo(Path.Combine(localBin, "kcap")).Exists).IsFalse();
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
    }

    // --- Without admin rights ---

    [Test]
    public async Task InstallAsync_without_admin_links_into_local_bin_and_adds_the_profile_line() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var target = tmp.CreateFile("target-cli");
        var runner = new FakeProcessRunner();
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(tmp.PathTo(".local", "bin", "kcap")),
        };
        var installer = new PathShimInstaller(runner, probe, home: home, profile: new ShellProfile(home, "/bin/zsh"),
            mayElevate: _ => Task.FromResult(false));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
        await Assert.That(File.ReadAllText(tmp.PathTo(".zprofile"))).Contains(ShellProfile.Begin);
        await Assert.That(runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InstallAsync_with_admin_keeps_the_prompt_and_leaves_the_profile_alone() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var target = tmp.CreateFile("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "execution error: User canceled. (-128)", false));
        var installer = new PathShimInstaller(runner, new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(true));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Cancelled);
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
    }

    [Test]
    public async Task InstallAsync_without_admin_never_replaces_another_kcap_in_local_bin() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var target = tmp.CreateFile("target-cli");
        var foreign = tmp.CreateFile([".local", "bin", "kcap"], "someone else's");
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));

        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Failed);
        await Assert.That(File.ReadAllText(foreign)).IsEqualTo("someone else's");
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
    }

    /// The user could not have replaced the admin folder's entry anyway.
    [Test]
    public async Task InstallAsync_without_admin_is_not_blocked_by_another_kcap_in_the_admin_folder() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");

        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var target = tmp.CreateFile("target-cli");
        var adminLink = tmp.CreateFile("admin-kcap", "someone else's");
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(tmp.PathTo(".local", "bin", "kcap")),
        };
        var installer = new PathShimInstaller(new FakeProcessRunner(), probe, home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));

        var result = await Install(installer, adminLink, target, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
        await Assert.That(File.ReadAllText(adminLink)).IsEqualTo("someone else's");
    }

    [Test]
    public async Task Disclosure_shows_the_profile_line_before_it_is_written() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/bin/zsh"));

        await Assert.That(installer.Disclosure).Contains($"export PATH=\"$PATH:{ShellProfile.Folder(home)}\" (in ~{Path.DirectorySeparatorChar}.zprofile)");
    }

    [Test]
    public async Task DescribeAsync_names_a_writable_folder_already_on_the_terminal_path() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX access(2)");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var bin = tmp.CreateDir("bin");
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>($"/usr/bin:{bin}") },
            home: home, profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{Path.DirectorySeparatorChar}bin"]);
        await Assert.That(Text(spans)).DoesNotContain("admin");
        await Assert.That(File.Exists(Path.Combine(bin, "kcap"))).IsFalse();
    }

    [Test]
    public async Task DescribeAsync_warns_an_admin_of_the_password_prompt() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();
        using var system = new TempDir();
        var home = new UserHome(tmp.Path);
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), system.PathTo("kcap"), home,
            new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(true));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([system.Path]);
        await Assert.That(Text(spans)).Contains("admin password");
        await Assert.That(Text(spans)).DoesNotContain(".zprofile");
    }

    [Test]
    public async Task DescribeAsync_without_admin_names_the_local_folder_and_the_profile() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        var sep = Path.DirectorySeparatorChar;
        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{sep}.local{sep}bin", $"~{sep}.zprofile"]);
        await Assert.That(Text(spans)).DoesNotContain("admin");
        await Assert.That(File.Exists(tmp.PathTo(".zprofile"))).IsFalse();
    }

    [Test]
    public async Task DescribeAsync_names_an_admin_link_it_would_leave_alone() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        using var system = new TempDir();
        var home = new UserHome(tmp.Path);
        var adminLink = system.CreateFile("kcap", "someone else's");
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), adminLink, home,
            new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(true));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(Text(spans)).Contains("belongs to another install");
        await Assert.That(Text(spans)).DoesNotContain("admin password");
    }

    [Test]
    public async Task DescribeAsync_without_admin_names_a_local_link_it_would_leave_alone() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        tmp.CreateFile([".local", "bin", "kcap"], "someone else's");
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        var sep = Path.DirectorySeparatorChar;
        await Assert.That(spans.Where(s => s.Code).Select(s => s.Text)).IsEquivalentTo([$"~{sep}.local{sep}bin{sep}kcap"]);
        await Assert.That(Text(spans)).DoesNotContain(".zprofile");
    }

    [Test]
    public async Task DescribeAsync_without_admin_on_an_unsupported_shell_promises_no_prompt() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX shell profile paths");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, "/opt/homebrew/bin/fish"), mayElevate: _ => Task.FromResult(false));

        var spans = await installer.DescribeAsync(tmp.PathTo("target-cli"), CancellationToken.None);

        await Assert.That(Text(spans)).Contains("can't set PATH for this shell");
        await Assert.That(Text(spans)).DoesNotContain("admin password");
    }

    static string Text(IEnumerable<DisclosureSpan> spans) => string.Concat(spans.Select(s => s.Text));

    [Test]
    [Arguments("staff everyone localaccounts admin _appserverusr\n", true)]
    [Arguments("staff everyone localaccounts _lpoperator\n", false)]
    public async Task IsAdminAsync_reads_the_admin_group(string groups, bool admin) {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(0, groups, "", false));

        await Assert.That(await PathShimInstaller.IsAdminAsync(runner, CancellationToken.None)).IsEqualTo(admin);
    }

    /// An unanswered question keeps the prompt an admin can complete rather than editing a profile.
    [Test]
    public async Task IsAdminAsync_assumes_admin_when_id_fails() {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "id: boom", false));

        await Assert.That(await PathShimInstaller.IsAdminAsync(runner, CancellationToken.None)).IsTrue();
    }


    [Test]
    public async Task An_existing_admin_link_off_path_still_gets_the_user_fallback() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.Path);
        var target = tmp.CreateFile("target-cli");
        var admin = tmp.PathTo("admin-kcap");
        File.CreateSymbolicLink(admin, target);
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(File.Exists(tmp.PathTo(".zprofile"))),
            KcapPathBehavior = _ => Task.FromResult<string?>(tmp.PathTo(".local", "bin", "kcap")),
        };
        var installer = new PathShimInstaller(new FakeProcessRunner(), probe, home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));
        var result = await Install(installer, admin, target, CancellationToken.None);
        await Assert.That(result.Outcome).IsEqualTo(ShimOutcome.Installed);
        await Assert.That(new FileInfo(tmp.PathTo(".local", "bin", "kcap")).LinkTarget).IsEqualTo(target);
        await Assert.That(new FileInfo(admin).LinkTarget).IsEqualTo(target);
    }

    [Test]
    [Arguments("/bin/fish", "safe")]
    [Arguments("/bin/zsh", "a!b")]
    public async Task Unsupported_profile_fallback_does_not_create_a_link(string shell, string folder) {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        using var tmp = new TempDir();
        var home = new UserHome(tmp.CreateDir(folder));
        var installer = new PathShimInstaller(new FakeProcessRunner(), new FakeLoginShellProbe(), home: home,
            profile: new ShellProfile(home, shell), mayElevate: _ => Task.FromResult(false));
        var result = await Install(installer, tmp.PathTo("admin-kcap"), tmp.CreateFile("target-cli"), CancellationToken.None);
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
        var home = new UserHome(tmp.Path);
        var link = tmp.PathTo(".local", "bin", "kcap");
        var earlier = tmp.CreateFile("earlier-kcap");
        var probe = new FakeLoginShellProbe {
            KcapOnPathFreshBehavior = _ => Task.FromResult(onPath),
            KcapPathBehavior = _ => Task.FromResult<string?>(link),
            KcapPathFreshBehavior = _ => Task.FromResult(command is null ? null : command == "installed" ? link : earlier),
        };
        var installer = new PathShimInstaller(new FakeProcessRunner(), probe, home: home,
            profile: new ShellProfile(home, "/bin/zsh"), mayElevate: _ => Task.FromResult(false));
        var result = await Install(installer, tmp.PathTo("admin-kcap"), tmp.CreateFile("target-cli"), CancellationToken.None);
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
        var home = new UserHome(tmp.Path);
        var localBin = tmp.CreateDir(".local", "bin").Path;
        var link = Path.Combine(localBin, "kcap");
        var target = tmp.CreateFile("target-cli");
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessResult(1, "", "User canceled. (-128)", false));
        var probe = ResolvingTo(link.ToUpperInvariant(), throughProfile ? "/usr/bin:/bin" : localBin);
        var installer = new PathShimInstaller(runner, probe, home: home,
            profile: throughProfile ? new ShellProfile(home, "/bin/zsh") : null,
            mayElevate: _ => Task.FromResult(false));
        var result = await Install(installer, tmp.PathTo("admin-kcap"), target, CancellationToken.None);
        var expected = OperatingSystem.IsMacOS() ? ShimOutcome.Installed
            : throughProfile ? ShimOutcome.InstalledButNotOnPath : ShimOutcome.Cancelled;
        await Assert.That(result.Outcome).IsEqualTo(expected);
        if (OperatingSystem.IsMacOS())
            await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
    }

    static FakeLoginShellProbe ResolvingTo(string resolved, string terminalPath) => new() {
        TerminalPathBehavior = _ => Task.FromResult<string?>(terminalPath),
        KcapOnPathBehavior   = _ => Task.FromResult<bool?>(true),
        KcapPathBehavior     = _ => Task.FromResult<string?>(resolved),
    };

    // Drives InstallAsync against an arbitrary temp `destination` (never the real
    // /usr/local/bin/kcap) via the internal destination-overriding overload.
    static Task<ShimResult> Install(PathShimInstaller installer, string destination, string target, CancellationToken ct) =>
        installer.InstallAsync(target, destination, ct);
}
