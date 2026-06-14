using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class RunAgentArgsTests {
    [Test]
    public async Task Splits_kcap_flags_from_passthrough_at_double_dash() {
        var a = RunAgentArgs.Parse(["claude", "--worktree", "--name", "dev", "--", "--model", "opus", "fix"]);
        await Assert.That(a.Vendor).IsEqualTo("claude");
        await Assert.That(a.Worktree).IsTrue();
        await Assert.That(a.DaemonName).IsEqualTo("dev");
        await Assert.That(a.Passthrough).IsEquivalentTo(new[] { "--model", "opus", "fix" });
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Default_is_in_place_with_no_passthrough() {
        var a = RunAgentArgs.Parse(["codex"]);
        await Assert.That(a.Vendor).IsEqualTo("codex");
        await Assert.That(a.Worktree).IsFalse();
        await Assert.That(a.Passthrough).IsEmpty();
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Empty_args_is_an_error() {
        await Assert.That(RunAgentArgs.Parse([]).Error).IsNotNull();
    }

    [Test]
    public async Task Unknown_kcap_flag_before_dash_is_an_error() {
        var a = RunAgentArgs.Parse(["claude", "--model", "opus"]);
        await Assert.That(a.Error).IsNotNull();
    }

    [Test]
    public async Task Share_is_not_a_flag_sharing_is_a_ui_action() {
        // Sharing is server/UI-authoritative (AI-861 tracks a future `kcap share` command),
        // so --share is just an unknown run-agent flag and is rejected.
        await Assert.That(RunAgentArgs.Parse(["claude", "--share"]).Error).IsNotNull();
    }

    [Test]
    public async Task Empty_passthrough_after_dash_is_allowed() {
        var a = RunAgentArgs.Parse(["claude", "--"]);
        await Assert.That(a.Error).IsNull();
        await Assert.That(a.Passthrough).IsEmpty();
    }
}
