using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class PlansNudgeEmitterTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Build_returns_null_for_a_missing_oversized_or_unsafe_session_id() {
        await Assert.That(PlansNudgeEmitter.Build(null)).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("  ")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build(new string('a', 257))).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc`x`")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc\ndef")).IsNull();
    }

    [Test]
    public async Task Build_is_two_sentences_naming_the_four_tools_and_the_session_id() {
        var nudge = PlansNudgeEmitter.Build("s1")!;

        await Assert.That(nudge).StartsWith("## Plans\n");
        await Assert.That(nudge).Contains("declare_plan_document");
        await Assert.That(nudge).Contains("set_plan_tasks");
        await Assert.That(nudge).Contains("update_plan_task");
        await Assert.That(nudge).Contains("get_plan");
        await Assert.That(nudge).Contains("`s1`");

        var body = nudge["## Plans\n".Length..];
        await Assert.That(body.Split(". ").Length).IsEqualTo(2);
        await Assert.That(body).EndsWith(".");
    }

    static string CodexConfigWithPlans(TempDir tmp) =>
        tmp.CreateFile("config.toml", "[mcp_servers.kcap-plans]\ncommand = \"kcap\"\nargs = [\"mcp\", \"plans\"]\n");

    [Test]
    public async Task Resolve_returns_null_when_opted_out() {
        using var tmp = new TempDir();
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: true, Harnesses, CodexConfigWithPlans(tmp))).IsNull();
    }

    [Test]
    public async Task Resolve_returns_the_nudge_when_codex_registers_kcap_plans() {
        using var tmp = new TempDir();
        var nudge = PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, CodexConfigWithPlans(tmp));
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("`s1`");
    }

    [Test]
    public async Task Resolve_returns_null_when_codex_registers_only_workitems() {
        using var tmp = new TempDir();
        var config = tmp.CreateFile("config.toml", "[mcp_servers.kcap-workitems]\ncommand = \"kcap\"\nargs = [\"mcp\", \"workitems\"]\n");
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, config)).IsNull();
    }
}
