using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudePermissionModeArgsTests {
    [Test]
    [Arguments(new string[0], null)]
    [Arguments(new[] { "--model", "opus", "fix it" }, null)]
    [Arguments(new[] { "--permission-mode", "manual" }, "manual")]
    [Arguments(new[] { "--permission-mode=acceptEdits" }, "acceptEdits")]
    [Arguments(new[] { "--model", "opus", "--permission-mode", "auto", "fix it" }, "auto")]
    [Arguments(new[] { "--permission-mode", "auto", "--permission-mode", "manual" }, "manual")]
    [Arguments(new[] { "--permission-mode", "plan" }, "plan")]
    [Arguments(new[] { "--permission-mode" }, null)]
    [Arguments(new[] { "--", "--permission-mode", "auto" }, null)]
    public async Task Reads_the_mode_the_user_args_select(string[] args, string? expected) {
        await Assert.That(ClaudePermissionModeArgs.Selected(args)).IsEqualTo(expected);
    }

    /// Claude still accepts its older spelling of Manual, which the product's policy does not.
    [Test]
    public async Task The_legacy_default_spelling_reads_as_manual() {
        await Assert.That(ClaudePermissionModeArgs.Selected(["--permission-mode", "default"])).IsEqualTo("manual");
    }
}
