using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// The command line ConPTY hands CreateProcessW. A launch prompt is multi-line and carries quotes
/// and cmd metacharacters, so an npm shim must never be routed through `cmd.exe /c`.
public class ConPtyCommandLineTests {
    const string Prompt = "Fix the bug.\nThen run \"the tests\" & report 100% of %PATH%.";

    static (string, bool) Resolve(string command) => command switch {
        "codex" => (@"C:\npm\codex.cmd", true),
        "node"  => (@"C:\Program Files\nodejs\node.exe", false),
        "tool"  => (@"C:\tools\tool.cmd", true),
        _       => (command, false),
    };

    static NpmCmdShim.Target? Shim(string path) =>
        path == @"C:\npm\codex.cmd" ? new NpmCmdShim.Target("node", @"C:\npm\node_modules\@openai\codex\bin\codex.js") : null;

    [Test]
    public async Task An_npm_shim_starts_node_with_the_script_and_no_cmd_exe() {
        var line = ConPtyProcess.BuildCommandLine("codex", ["--", Prompt], Resolve, Shim).ToString();

        await Assert.That(line).DoesNotContain("cmd.exe");
        await Assert.That(line).StartsWith("\"C:\\Program Files\\nodejs\\node.exe\" C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js -- ");
        await Assert.That(line).EndsWith(ConPtyProcess.QuoteArg(Prompt));
        await Assert.That(line).Contains("\n");
    }

    [Test]
    public async Task A_native_shim_target_is_started_as_itself() {
        var line = ConPtyProcess.BuildCommandLine("codex", ["-v"], Resolve,
            _ => new NpmCmdShim.Target(@"C:\npm\node_modules\x\bin\x.exe", null)).ToString();

        await Assert.That(line).IsEqualTo(@"C:\npm\node_modules\x\bin\x.exe -v");
    }

    [Test]
    public async Task A_plain_exe_is_unchanged() {
        var line = ConPtyProcess.BuildCommandLine(@"C:\bin\claude.exe", ["--", Prompt], Resolve, Shim).ToString();

        await Assert.That(line).IsEqualTo(@"C:\bin\claude.exe -- " + ConPtyProcess.QuoteArg(Prompt));
    }

    [Test]
    public async Task A_non_npm_cmd_keeps_cmd_exe_for_single_line_arguments() {
        var line = ConPtyProcess.BuildCommandLine("tool", ["--flag", "value"], Resolve, Shim).ToString();

        await Assert.That(line).IsEqualTo(@"cmd.exe /c C:\tools\tool.cmd --flag value");
    }

    /// cmd.exe would silently cut the argument at the newline; refusing turns that into a launch
    /// failure the orchestrator surfaces.
    [Test]
    public async Task A_non_npm_cmd_refuses_a_multi_line_argument() {
        await Assert.That(() => ConPtyProcess.BuildCommandLine("tool", ["--", Prompt], Resolve, Shim))
            .Throws<InvalidOperationException>();
    }
}
