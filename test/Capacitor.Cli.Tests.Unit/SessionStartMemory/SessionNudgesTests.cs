using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class SessionNudgesTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string Line =
        "Kurrent Capacitor session id: `s1`. Pass it as `session_id` to a kcap MCP tool that cannot resolve the session by itself.";

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    string CodexConfig(params string[] servers) =>
        Tmp.CreateFile("config.toml", string.Concat(servers.Select(s =>
            $"[mcp_servers.kcap-{s}]\ncommand = \"kcap\"\nargs = [\"mcp\", \"{s}\"]\n\n")));

    string? Resolve(string config, Profile? profile = null, PlanEntitlements? plan = null) =>
        SessionNudges.Resolve(HarnessId.Codex, "s1", profile, Harnesses, plan ?? PlanEntitlements.Unknown, config);

    [Test]
    public async Task With_both_nudges_shown_the_line_is_left_out() {
        var nudges = Resolve(CodexConfig("workitems", "plans", "flows"));

        await Assert.That(nudges).IsNotNull();
        await Assert.That(nudges!).Contains("## Work items");
        await Assert.That(nudges).Contains("## Plans");
        await Assert.That(nudges).DoesNotContain("Kurrent Capacitor session id:");
    }

    /// <summary>With a nudge shown, the output is the two nudges and nothing more.</summary>
    [Test]
    public async Task With_a_nudge_shown_the_bytes_are_what_the_two_nudges_alone_produce() {
        var config = CodexConfig("workitems", "plans", "flows");

        var expected = HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, PlanEntitlements.Unknown, config),
            PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, config));

        await Assert.That(expected).IsNotNull();
        await Assert.That(Resolve(config)).IsEqualTo(expected);
    }

    [Test]
    public async Task With_both_nudges_opted_out_the_line_states_the_id() {
        var nudges = Resolve(
            CodexConfig("workitems", "plans", "flows"),
            new Profile { DisableWorkItemsNudge = true, DisablePlansNudge = true });

        await Assert.That(nudges).IsEqualTo(Line);
    }

    [Test]
    public async Task With_a_plan_that_has_no_work_items_the_line_states_the_id() {
        var nudges = Resolve(CodexConfig("workitems", "flows"), plan: PlanEntitlements.Parse("work_items=0"));

        await Assert.That(nudges).IsEqualTo(Line);
    }

    [Test]
    public async Task With_only_the_flows_server_registered_the_line_states_the_id() {
        await Assert.That(Resolve(CodexConfig("flows"))).IsEqualTo(Line);
    }

    [Test]
    public async Task With_only_the_plans_nudge_shown_the_line_is_left_out() {
        var nudges = Resolve(CodexConfig("workitems", "plans"), new Profile { DisableWorkItemsNudge = true });

        await Assert.That(nudges).IsNotNull();
        await Assert.That(nudges!).StartsWith("## Plans\n");
        await Assert.That(nudges).DoesNotContain("Kurrent Capacitor session id:");
    }

    [Test]
    public async Task With_no_kcap_server_registered_nothing_is_said() {
        await Assert.That(Resolve(Tmp.CreateFile("config.toml", "model = \"gpt-5-codex\"\n"))).IsNull();
    }
}
