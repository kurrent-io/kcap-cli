using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiRoundCeilingTests {
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(600);

    sealed class Rig : IDisposable {
        public readonly FakeTimeProvider Time = new();
        public int Expired;
        public readonly PiRoundCeiling Ceiling;
        public Rig() => Ceiling = new PiRoundCeiling(Limit, Time, () => Expired++);
        public void Dispose() => Ceiling.Dispose();
    }

    [Test]
    public async Task One_round_arms_on_write_and_disarms_on_settle() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        await Assert.That(r.Ceiling.IsArmed).IsTrue();

        r.Ceiling.Response("1", accepted: true);
        r.Ceiling.AgentStarted();
        r.Ceiling.UserEcho();
        r.Ceiling.AgentSettled();

        await Assert.That(r.Ceiling.IsArmed).IsFalse();
        await Assert.That(r.Ceiling.InvariantHolds).IsTrue();
    }

    [Test]
    public async Task A_round_that_never_settles_expires_once() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        r.Ceiling.Response("1", true);
        r.Ceiling.AgentStarted();
        r.Ceiling.UserEcho();

        r.Time.Advance(Limit + TimeSpan.FromSeconds(1));

        await Assert.That(r.Expired).IsEqualTo(1);
    }

    [Test]
    public async Task A_round_queued_mid_turn_gets_its_own_full_budget_from_its_echo() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1"); r.Ceiling.Response("1", true); r.Ceiling.AgentStarted(); r.Ceiling.UserEcho();

        r.Time.Advance(TimeSpan.FromSeconds(500));
        r.Ceiling.PromptWriting("2"); r.Ceiling.Response("2", true);
        r.Time.Advance(TimeSpan.FromSeconds(50));
        r.Ceiling.UserEcho();                                  // round 2 starts at t=550
        r.Time.Advance(TimeSpan.FromSeconds(500));             // t=1050: past round 1's deadline, inside round 2's

        await Assert.That(r.Expired).IsEqualTo(0);

        r.Time.Advance(TimeSpan.FromSeconds(101));             // t=1151 > 550+600
        await Assert.That(r.Expired).IsEqualTo(1);
    }

    [Test]
    public async Task A_settle_handled_after_a_new_prompts_write_rearms_instead_of_disarming() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1"); r.Ceiling.Response("1", true); r.Ceiling.AgentStarted(); r.Ceiling.UserEcho();

        r.Ceiling.PromptWriting("2");      // the sender still sees the old round as busy
        r.Ceiling.AgentSettled();          // the old round's settle, handled late

        await Assert.That(r.Ceiling.IsArmed).IsTrue();
        await Assert.That(r.Ceiling.InvariantHolds).IsTrue();

        r.Time.Advance(Limit + TimeSpan.FromSeconds(1));
        await Assert.That(r.Expired).IsEqualTo(1);            // prompt 2 wedged before its echo: still guarded
    }

    [Test]
    public async Task Two_writes_before_any_start_with_the_second_rejected_leaves_the_first_guarded() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        r.Ceiling.PromptWriting("2");
        r.Ceiling.Response("2", accepted: false);

        await Assert.That(r.Ceiling.IsArmed).IsTrue();
        await Assert.That(r.Ceiling.InvariantHolds).IsTrue();
    }

    [Test]
    public async Task A_settle_between_acceptance_and_echo_rearms() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        r.Ceiling.Response("1", true);
        r.Ceiling.AgentSettled();          // belongs to the previous busy period

        await Assert.That(r.Ceiling.IsArmed).IsTrue();
    }

    [Test]
    public async Task A_lone_rejected_prompt_disarms() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        r.Ceiling.Response("1", accepted: false);

        await Assert.That(r.Ceiling.IsArmed).IsFalse();
    }

    [Test]
    public async Task A_failed_write_leaves_no_phantom_work() {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        r.Ceiling.PromptWriteFailed("1");

        r.Time.Advance(Limit + TimeSpan.FromSeconds(1));

        await Assert.That(r.Ceiling.IsArmed).IsFalse();
        await Assert.That(r.Expired).IsEqualTo(0);
    }

    [Test]
    [Arguments("inFlight")]
    [Arguments("owed")]
    [Arguments("running")]
    public async Task Going_terminal_clears_everything_and_no_timeout_fires_afterwards(string populated) {
        var r = new Rig();
        r.Ceiling.PromptWriting("1");
        if (populated != "inFlight") r.Ceiling.Response("1", true);
        if (populated == "running") { r.Ceiling.AgentStarted(); r.Ceiling.UserEcho(); }

        r.Ceiling.Terminal();
        r.Time.Advance(Limit + TimeSpan.FromSeconds(1));

        await Assert.That(r.Ceiling.IsArmed).IsFalse();
        await Assert.That(r.Expired).IsEqualTo(0);
    }

    [Test]
    public async Task A_response_for_an_id_it_never_saw_changes_nothing() {
        var r = new Rig();
        r.Ceiling.Response("kcap-init-state", accepted: true);

        await Assert.That(r.Ceiling.IsArmed).IsFalse();
        await Assert.That(r.Ceiling.InvariantHolds).IsTrue();
    }

    // The ceiling's Expire fires onExpired outside its own lock, so a teardown can run in the gap. The
    // reap gate's seal is what makes that race benign: a sealed gate refuses the stale timeout.
    [Test]
    public async Task A_ceiling_timeout_after_the_gate_seals_publishes_no_verdict() {
        var time = new FakeTimeProvider();
        var gate = new ReapVerdictGate(() => true, NullLogger.Instance);
        using var ceiling = new PiRoundCeiling(Limit, time,
            () => gate.TryStartReap("pi_reviewer_turn_timeout", () => Task.CompletedTask));
        ceiling.PromptWriting("1");                 // outstanding work: the timer is armed

        _ = gate.Seal();                            // ordinary teardown wins the race
        time.Advance(Limit + TimeSpan.FromSeconds(1));

        await Assert.That(gate.ReadVerdict()).IsNull();
    }

    [Test]
    public async Task A_ceiling_timeout_before_any_seal_publishes_the_verdict() {
        var time = new FakeTimeProvider();
        var gate = new ReapVerdictGate(() => true, NullLogger.Instance);
        using var ceiling = new PiRoundCeiling(Limit, time,
            () => gate.TryStartReap("pi_reviewer_turn_timeout", () => Task.CompletedTask));
        ceiling.PromptWriting("1");

        time.Advance(Limit + TimeSpan.FromSeconds(1));   // Expire wins before any seal

        await Assert.That(gate.ReadVerdict()!.Reason).IsEqualTo("pi_reviewer_turn_timeout");
    }

    [Test]
    public async Task The_invariant_holds_after_every_transition_of_a_long_script() {
        var r = new Rig();
        Action[] script = [
            () => r.Ceiling.PromptWriting("1"), () => r.Ceiling.Response("1", true), r.Ceiling.AgentStarted,
            r.Ceiling.UserEcho, () => r.Ceiling.PromptWriting("2"), () => r.Ceiling.Response("2", true),
            r.Ceiling.UserEcho, r.Ceiling.AgentSettled, () => r.Ceiling.PromptWriting("3"),
            () => r.Ceiling.PromptWriteFailed("3"), () => r.Ceiling.PromptWriting("4"),
            () => r.Ceiling.Response("4", false), r.Ceiling.Terminal,
        ];

        foreach (var step in script) {
            step();
            await Assert.That(r.Ceiling.InvariantHolds).IsTrue();
        }
    }
}
