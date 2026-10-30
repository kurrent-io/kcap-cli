using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudePermissionModeArgsTests {
    [Test]
    [Arguments(new string[0], null)]
    [Arguments(new[] { "--model", "opus", "fix it" }, null)]
    [Arguments(new[] { "--permission-mode", "manual" }, "manual")]
    [Arguments(new[] { "--permission-mode=acceptEdits" }, "acceptEdits")]
    [Arguments(new[] { "--model", "opus", "--permission-mode", "auto", "fix it" }, "auto")]
    [Arguments(new[] { "--model=opus", "--permission-mode", "auto" }, "auto")]
    [Arguments(new[] { "--permission-mode", "auto", "fix it", "--permission-mode", "manual" }, "manual")]
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

    /// A mode flag right after a bare option may be that option's value, as with --system-prompt below,
    /// so it reads as Manual: recording the apparent mode could recover a Manual session as auto.
    [Test]
    [Arguments(new[] { "--permission-mode", "manual", "--system-prompt", "--permission-mode=auto", "fix it" }, "manual")]
    [Arguments(new[] { "--system-prompt", "--permission-mode", "auto" }, "manual")]
    [Arguments(new[] { "--verbose", "--permission-mode", "bypassPermissions" }, "manual")]
    [Arguments(new[] { "-n", "--permission-mode=auto" }, "manual")]
    public async Task A_mode_that_may_be_another_options_value_reads_as_manual(string[] args, string expected) {
        await Assert.That(ClaudePermissionModeArgs.Selected(args)).IsEqualTo(expected);
    }
}
