using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class SessionNudgesTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string Line =
        "kcap session id: `s1`. Pass it as `session_id` to a kcap MCP tool that cannot resolve the session by itself.";

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    string CodexConfig(params string[] servers) =>
        Tmp.CreateFile("config.toml", string.Concat(servers.Select(s =>
            $"[mcp_servers.kcap-{s}]\ncommand = \"kcap\"\nargs = [\"mcp\", \"{s}\"]\n\n")));

    string? Resolve(string config, Profile? profile = null, PlanEntitlements? plan = null, SessionStartAudience? audience = null) =>
        SessionNudges.Resolve(HarnessId.Codex, "s1", profile, Harnesses, plan ?? PlanEntitlements.Unknown,
            audience ?? SessionStartAudience.Interactive, cwd: null, Home, config);

    static readonly SessionStartAudience Hosted = new(IsHosted: true, IsFlowParticipant: false);

    /// <summary>schedule_wakeup refuses a terminal session, so only a hosted agent is pointed at it.</summary>
    [Test]
    public async Task Only_a_hosted_agent_is_told_to_wait_with_a_wake_up() {
        var config = CodexConfig("workitems", "plans", "flows");

        await Assert.That(Resolve(config, audience: Hosted)).Contains(WakeUpNudgeEmitter.Nudge);
        await Assert.That(Resolve(config)).DoesNotContain("## Waiting");
        await Assert.That(Resolve(config, audience: new(IsHosted: true, IsFlowParticipant: true)) ?? "").DoesNotContain("## Waiting");
    }

    [Test]
    public async Task A_hosted_agent_without_the_flows_server_is_not_told_to_wait_with_a_wake_up() {
        await Assert.That(Resolve(CodexConfig("workitems", "plans"), audience: Hosted)).DoesNotContain("## Waiting");
    }

    /// <summary>The nudge says nothing about the session id, so it must not stand in for the line that does.</summary>
    [Test]
    public async Task A_hosted_agent_with_only_the_flows_server_still_gets_the_session_id_line() {
        var nudges = Resolve(CodexConfig("flows"), audience: Hosted);

        await Assert.That(nudges).IsEqualTo(HarnessNudgeEmitter.Combine(WakeUpNudgeEmitter.Nudge, Line));
    }

    [Test]
    public async Task With_both_nudges_shown_the_line_is_left_out() {
        var nudges = Resolve(CodexConfig("workitems", "plans", "flows"));

        await Assert.That(nudges).IsNotNull();
        await Assert.That(nudges!).Contains("## Work items");
        await Assert.That(nudges).Contains("## Plans");
        await Assert.That(nudges).DoesNotContain("kcap session id:");
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
        await Assert.That(nudges).DoesNotContain("kcap session id:");
    }

    [Test]
    public async Task With_no_kcap_server_registered_nothing_is_said() {
        await Assert.That(Resolve(Tmp.CreateFile("config.toml", "model = \"gpt-5-codex\"\n"))).IsNull();
    }
}
