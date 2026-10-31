using System.Diagnostics;
using Capacitor.Cli.Core.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeStatusLineCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_posix_command_guards_kcap_and_quotes_the_original() {
        var command = ClaudeStatusLineCommand.For("abc123", "echo 'hi'", posixShell: true);

        await Assert.That(command).IsEqualTo(
            """if command -v kcap >/dev/null 2>&1; then exec kcap statusline --account abc123; else exec sh -c 'echo '\''hi'\'''; fi""");
    }

    [Test]
    public async Task A_posix_command_without_an_original_falls_back_to_a_no_op() {
        await Assert.That(ClaudeStatusLineCommand.For("abc123", null, posixShell: true)).IsEqualTo(
            "if command -v kcap >/dev/null 2>&1; then exec kcap statusline --account abc123; else :; fi");
    }

    [Test]
    public async Task Without_a_posix_shell_the_command_is_plain() {
        await Assert.That(ClaudeStatusLineCommand.For("abc123", "echo hi", posixShell: false)).IsEqualTo("kcap statusline --account abc123");
    }

    [Test]
    [Arguments(true, "echo mine")]
    [Arguments(true, null)]
    [Arguments(false, "echo mine")]
    public async Task Both_shapes_are_recognised_for_any_account(bool posix, string? original) {
        await Assert.That(ClaudeStatusLineCommand.IsOurs(ClaudeStatusLineCommand.For("one", original, posix))).IsTrue();
        await Assert.That(ClaudeStatusLineCommand.IsOurs(ClaudeStatusLineCommand.For("another-id", original, posix))).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("echo kcap statusline --account x")]
    [Arguments("kcap statusline --account")]
    [Arguments("~/.claude/statusline.sh")]
    public async Task Other_commands_are_not_ours(string? command) {
        await Assert.That(ClaudeStatusLineCommand.IsOurs(command)).IsFalse();
    }

    [Test]
    public async Task Off_windows_sh_is_assumed_without_probing() {
        var probed = false;

        var shell = ClaudeStatusLineCommand.PosixShellPath(_ => { probed = true; return null; }, windows: false, _ => { probed = true; return false; });

        await Assert.That(shell).IsEqualTo("sh");
        await Assert.That(probed).IsFalse();
    }

    [Test]
    public async Task On_windows_the_configured_git_bash_is_the_shell() {
        var bash = Tmp.PathTo("Programs", "bash.exe");

        var shell = ClaudeStatusLineCommand.PosixShellPath(n => n == "CLAUDE_CODE_GIT_BASH_PATH" ? bash : null, windows: true, p => p == bash);

        await Assert.That(shell).IsEqualTo(bash);
    }

    [Test]
    public async Task On_windows_bash_beside_git_on_path_is_the_shell() {
        var bash = Tmp.PathTo("Git", "bin", "bash.exe");
        var path = $"{Tmp.PathTo("empty")};{Tmp.PathTo("Git", "cmd")}";
        HashSet<string> existing = [bash, Tmp.PathTo("Git", "cmd", "git.exe")];

        var shell = ClaudeStatusLineCommand.PosixShellPath(n => n == "PATH" ? path : null, windows: true, existing.Contains);

        await Assert.That(shell).IsEqualTo(bash);
    }

    [Test]
    public async Task On_windows_git_without_bash_and_a_missing_configured_bash_give_no_shell() {
        HashSet<string> existing = [Tmp.PathTo("Git", "cmd", "git.exe")];
        var path = Tmp.PathTo("Git", "cmd");

        var shell = ClaudeStatusLineCommand.PosixShellPath(
            n => n switch { "PATH" => path, "CLAUDE_CODE_GIT_BASH_PATH" => Tmp.PathTo("missing.exe"), _ => null }, windows: true, existing.Contains);

        await Assert.That(shell).IsNull();
    }

    public static IEnumerable<Func<(string Original, string Expected)>> Originals() {
        yield return () => ("echo hi # a trailing comment", "hi\n");
        yield return () => ("echo 'single quoted' \"it's\"", "single quoted it's\n");
        yield return () => ("echo one\necho two", "one\ntwo\n");
        yield return () => ("while read -r l; do echo \"got $l\"; done <<'EOF'\nfirst\nEOF\necho after", "got first\nafter\n");
    }

    [Test]
    [MethodDataSource(nameof(Originals))]
    public async Task The_embedded_original_parses_back_out_of_the_guard((string Original, string Expected) c) {
        var found = ClaudeStatusLineCommand.TryOriginalOf(ClaudeStatusLineCommand.For("abc123", c.Original, posixShell: true), out var original);

        await Assert.That(found).IsTrue();
        await Assert.That(original).IsEqualTo(c.Original);
    }

    [Test]
    public async Task A_guard_without_an_original_parses_as_none_and_the_plain_shape_as_unknown() {
        await Assert.That(ClaudeStatusLineCommand.TryOriginalOf(ClaudeStatusLineCommand.For("a", null, true), out var none)).IsTrue();
        await Assert.That(none).IsNull();
        await Assert.That(ClaudeStatusLineCommand.TryOriginalOf(ClaudeStatusLineCommand.For("a", "echo x", false), out _)).IsFalse();
        await Assert.That(ClaudeStatusLineCommand.TryOriginalOf("echo mine", out _)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    [MethodDataSource(nameof(Originals))]
    public async Task Guard_parses_with_sh_n((string Original, string Expected) c) {
        var (exit, _) = await RunSh(["-n", "-c", ClaudeStatusLineCommand.For("abc123", c.Original, posixShell: true)]);

        await Assert.That(exit).IsEqualTo(0);
    }

    [Test, ExcludeOn(OS.Windows)]
    [MethodDataSource(nameof(Originals))]
    public async Task Guard_runs_the_original_when_kcap_is_absent((string Original, string Expected) c) {
        var (exit, stdout) = await RunSh(["-c", ClaudeStatusLineCommand.For("abc123", c.Original, posixShell: true)]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo(c.Expected);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Guard_without_an_original_prints_nothing_when_kcap_is_absent() {
        var (exit, stdout) = await RunSh(["-c", ClaudeStatusLineCommand.For("abc123", null, posixShell: true)]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo("");
    }

    // PATH holds only an `sh` shim, so `command -v kcap` fails and the nested `sh -c` still resolves.
    async Task<(int Exit, string Stdout)> RunSh(string[] args) {
        Tmp.CreateExecutable(Path.Combine("bin", "sh"), "#!/bin/sh\nexec /bin/sh \"$@\"\n");
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["PATH"] = Tmp.PathTo("bin");

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout);
    }
}
