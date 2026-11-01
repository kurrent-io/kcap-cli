using System.Text.Json;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Core.Tests.Unit.Handoff;

public class HandoffWireTests {
    const string ServerShapedLaunch = """
        {"agent_id":"a1","handoff":{
          "start":{"source_session_id":"s0","start_sha":"abc123","checkout":"Detached","branch":"feat/x",
                   "local_branch_sha":"def456","source":"Branch","detach_reason":"LocalBranchDiverged",
                   "pull_request_number":7,"head_is_fork":false,"head_owner":null,"head_repo":null},
          "source":{"session_id":"s0","title":"T","owner_display":"Ann","repo_owner":"o","repo_name":"r",
                   "branch":"feat/x","pull_request_number":7,"pull_request_title":"PR","first_prompt":"first","last_prompt":"last"},
          "session_commits":["c1","c2"]}}
        """;

    [Test]
    public async Task DaemonConnect_advertises_handoff_launches() {
        var connect = new DaemonConnect("n", "p", [], 1, [], SupportsHandoffLaunch: true);

        var json = JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);

        await Assert.That(json).Contains("\"supports_handoff_launch\":true");
    }

    [Test]
    public async Task A_launch_command_round_trips_its_handoff_block() {
        var command = JsonSerializer.Deserialize(ServerShapedLaunch, CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(command.Handoff).IsNotNull();
        await Assert.That(command.Handoff!.Start.StartSha).IsEqualTo("abc123");
        await Assert.That(command.Handoff.Start.PullRequestNumber).IsEqualTo(7);
        await Assert.That(command.Handoff.Source.LastPrompt).IsEqualTo("last");
        await Assert.That(command.Handoff.SessionCommits).IsNotNull();
        await Assert.That(command.Handoff.SessionCommits!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_launch_command_without_handoff_reads_null() {
        var command = JsonSerializer.Deserialize("""{"agent_id":"a1"}""", CapacitorJsonContext.Default.LaunchAgentCommand);

        await Assert.That(command.Handoff).IsNull();
    }

    [Test]
    [Arguments("CreateBranch", HandoffCheckout.CreateBranch)]
    [Arguments("CheckoutBranch", HandoffCheckout.CheckoutBranch)]
    [Arguments("FastForwardBranch", HandoffCheckout.FastForwardBranch)]
    [Arguments("Detached", HandoffCheckout.Detached)]
    public async Task ToStart_maps_every_shape(string checkout, HandoffCheckout expected) {
        var wire = new HandoffLaunchStart("s0", "abc", checkout, "feat/x", "def", "ForkPullRequest", "ForkHead", 7, true, "o", "r");

        var start = wire.ToStart();

        await Assert.That(start).IsEqualTo(new HandoffStart(
            HandoffSource.ForkPullRequest, expected, "abc", "feat/x", HandoffDetachReason.ForkHead, "def"));
    }

    [Test]
    [Arguments("Bogus", "Branch", "None")]
    [Arguments("Detached", "Bogus", "None")]
    [Arguments("Detached", "Branch", "Bogus")]
    public async Task ToStart_refuses_an_unknown_name(string checkout, string source, string reason) {
        var wire = new HandoffLaunchStart("s0", "abc", checkout, null, null, source, reason, null, false, null, null);

        await Assert.That(wire.ToStart()).IsNull();
    }

    [Test]
    public async Task A_takeover_admission_reads_the_servers_snake_case_shape() {
        var admission = JsonSerializer.Deserialize(
            """{"token":"t","refusal":null,"lease_seconds":60}""", CapacitorJsonContext.Default.HandoffTakeoverAdmission);

        await Assert.That(admission).IsEqualTo(new HandoffTakeoverAdmission("t", null, 60));
    }

    [Test]
    public async Task The_begin_request_goes_out_as_one_snake_case_object() {
        var json = JsonSerializer.Serialize(
            new HandoffTakeoverBeginRequest("src", "succ"), CapacitorJsonContext.Default.HandoffTakeoverBeginRequest);

        await Assert.That(json).IsEqualTo("""{"source_session_id":"src","successor_session_id":"succ"}""");
    }
}
