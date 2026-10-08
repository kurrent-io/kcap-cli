using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class ReloadCopyTests {
    static ReloadState State(MutationOutcome outcome) => ReloadState.From(outcome, "alexey", 1)!;

    [Test]
    public async Task A_success_has_no_state() {
        await Assert.That(ReloadState.From(new MutationOutcome.Succeeded(), "alexey", 1)).IsNull();
        await Assert.That(ReloadState.From(new MutationOutcome.SucceededAfterTimeout(), "alexey", 1)).IsNull();
    }

    [Test]
    public async Task Refresh_tokens_and_priority_skews_name_the_daemon() {
        var contended = ReloadCopy.For(State(new MutationOutcome.Failed(1, "contended", RecoverySurface.Attention)));
        await Assert.That(contended).Contains("--name alexey");
        var band = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("background_band")));
        await Assert.That(band).IsEqualTo("The daemon still runs at background priority after the reload. Run `kcap daemon service refresh --name alexey --force` from a terminal and check `kcap daemon status`.");
        var unknown = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("spawn_type_unknown")));
        await Assert.That(unknown).IsEqualTo("The reload finished but the daemon's priority could not be confirmed. Check `kcap daemon status --name alexey`.");
    }

    [Test]
    public async Task Refusals_carry_no_exit_code_and_reload_specific_recovery() {
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention))))
            .IsEqualTo("This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention))))
            .IsEqualTo("This kcap is too old for this app. Update kcap, then press Reload again.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention))))
            .IsEqualTo("kcap CLI not found. Can't manage the daemon from this app.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("not_loaded", RecoverySurface.Attention))))
            .IsEqualTo("The daemon service for alexey is not loaded. Run `kcap daemon service start --name alexey`.");
    }

    [Test]
    public async Task Ownership_and_evidence_legs_use_neutral_wording() {
        var repair = ReloadCopy.For(State(new MutationOutcome.AttentionRepair("running_without_daemon_pid")));
        await Assert.That(repair).IsEqualTo("The daemon service could not be verified (running_without_daemon_pid). Check `kcap daemon status --name alexey`.");
        var skew = ReloadCopy.For(State(new MutationOutcome.AttentionSkew("ownership_mismatch")));
        await Assert.That(skew).DoesNotContain("restart");
        await Assert.That(skew).DoesNotContain("came back");
    }

    [Test]
    [Arguments("unreachable_with_recorded_owner")]
    [Arguments("unreachable")]
    public async Task Unreachable_evidence_legs_render_as_could_not_be_verified(string token) {
        var state = State(new MutationOutcome.AttentionSkew(token));
        await Assert.That(state.Kind).IsEqualTo(ReloadOutcomeKind.Skew);
        await Assert.That(ReloadCopy.For(state))
            .IsEqualTo($"The daemon service could not be verified ({token}). Check `kcap daemon status --name alexey`.");
    }

    [Test]
    public async Task Unknown_tokens_fall_back_with_or_without_an_exit_code() {
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Failed(7, null, RecoverySurface.Attention))))
            .IsEqualTo("The daemon reload for alexey failed (exit 7). Check `kcap daemon status --name alexey`; details are in the app log.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.Refused("something_new", RecoverySurface.Attention))))
            .IsEqualTo("The daemon reload for alexey did not succeed (something_new). Check `kcap daemon status --name alexey`; details are in the app log.");
        await Assert.That(ReloadCopy.For(State(new MutationOutcome.UnconfirmedNoAttach())))
            .IsEqualTo("The daemon reload is not yet confirmed — check `kcap daemon status --name alexey`.");
    }

    [Test]
    public async Task Only_priority_class_tokens_are_resolved_by_a_positive_read() {
        foreach (var token in new[] { "background_band", "spawn_type_unknown", "deferred", "contended", "unverified" })
            await Assert.That(ReloadCopy.ResolvedByPositivePriority(token)).IsTrue();
        foreach (var token in new[] { "reload_unsupported", "not_loaded", "unit_missing", "unit_unreadable", "unit_unsupported", "failed", "ownership_mismatch", "verify_unknown_7", "unconfirmed" })
            await Assert.That(ReloadCopy.ResolvedByPositivePriority(token)).IsFalse();
    }
}
