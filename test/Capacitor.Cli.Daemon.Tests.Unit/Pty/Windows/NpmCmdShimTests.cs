using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// Pure string logic, so it runs on every OS: the shims are Windows files but parsing them needs
/// no Windows API.
public class NpmCmdShimTests {
    const string Dir = @"C:\nvm4w\nodejs";

    // cmd-shim's current output, as npm writes it for codex.
    const string ModernShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@openai\codex\bin\codex.js" %*
        """;

    // The older cmd-shim layout some global installs still carry.
    const string LegacyShim = """
        @IF EXIST "%~dp0\node.exe" (
          "%~dp0\node.exe"  "%~dp0\node_modules\npm\bin\npm-cli.js" %*
        ) ELSE (
          @SETLOCAL
          @SET PATHEXT=%PATHEXT:;.JS;=;%
          node  "%~dp0\node_modules\npm\bin\npm-cli.js" %*
        )
        """;

    [Test]
    public async Task Modern_shim_resolves_to_node_and_the_script() {
        var target = NpmCmdShim.Parse(ModernShim, Dir, _ => false);

        await Assert.That(target).IsEqualTo(new NpmCmdShim.Target("node", @"C:\nvm4w\nodejs\node_modules\@openai\codex\bin\codex.js"));
    }

    [Test]
    public async Task A_node_exe_beside_the_shim_is_preferred() {
        var target = NpmCmdShim.Parse(ModernShim, Dir, p => p == @"C:\nvm4w\nodejs\node.exe");

        await Assert.That(target!.Program).IsEqualTo(@"C:\nvm4w\nodejs\node.exe");
    }

    [Test]
    public async Task Legacy_shim_resolves_to_the_same_script() {
        var target = NpmCmdShim.Parse(LegacyShim, Dir + @"\", _ => false);

        await Assert.That(target).IsEqualTo(new NpmCmdShim.Target("node", @"C:\nvm4w\nodejs\node_modules\npm\bin\npm-cli.js"));
    }

    [Test]
    public async Task A_native_target_is_started_directly() {
        const string shim = "@ECHO off\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n";

        var target = NpmCmdShim.Parse(shim, Dir, _ => false);

        await Assert.That(target).IsEqualTo(new NpmCmdShim.Target(@"C:\nvm4w\nodejs\node_modules\@anthropic-ai\claude-code\bin\claude.exe", null));
    }

    [Test]
    [Arguments("@echo off\r\nC:\\tools\\thing.exe %*\r\n")]
    [Arguments("@echo off\r\n\"%dp0%\\bin\\run.sh\" %*\r\n")]
    [Arguments("")]
    public async Task Anything_else_is_not_a_shim(string text) {
        await Assert.That(NpmCmdShim.Parse(text, Dir, _ => false)).IsNull();
    }

    [Test]
    public async Task An_unreadable_file_is_not_a_shim() {
        using var tmp = new TempDir();

        await Assert.That(NpmCmdShim.TryRead(tmp.PathTo("absent.cmd"))).IsNull();
    }
}
