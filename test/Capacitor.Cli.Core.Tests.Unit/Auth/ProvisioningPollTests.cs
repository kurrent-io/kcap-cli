using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// Pure classification of a status-poll result into what the interactive poll should do next.
// Kept out of SpectreTenantProvisioner (which is Spectre-coupled and untestable) so every
// branch — including the silent-spin cases the old poll ignored — is covered.
public class ProvisioningPollTests {
    [Test]
    public async Task Active_with_org_id_completes() =>
        await Assert.That(ProvisioningPoll.Classify(200, "active", "org_1")).IsEqualTo(PollVerdict.Active);

    [Test]
    public async Task Active_without_org_id_is_a_distinct_terminal_verdict() =>
        await Assert.That(ProvisioningPoll.Classify(200, "active", null)).IsEqualTo(PollVerdict.ActiveNoOrg);

    [Test]
    public async Task Active_with_empty_org_id_is_ActiveNoOrg() =>
        await Assert.That(ProvisioningPoll.Classify(200, "active", "")).IsEqualTo(PollVerdict.ActiveNoOrg);

    [Test]
    public async Task Failed_state_is_terminal() =>
        await Assert.That(ProvisioningPoll.Classify(200, "failed", null)).IsEqualTo(PollVerdict.Failed);

    [Test]
    public async Task Provisioning_state_keeps_waiting() =>
        await Assert.That(ProvisioningPoll.Classify(200, "provisioning", null)).IsEqualTo(PollVerdict.Wait);

    [Test]
    public async Task Reserved_state_keeps_waiting() =>
        await Assert.That(ProvisioningPoll.Classify(200, "reserved", null)).IsEqualTo(PollVerdict.Wait);

    [Test]
    public async Task Forbidden_is_terminal() =>
        await Assert.That(ProvisioningPoll.Classify(403, null, null)).IsEqualTo(PollVerdict.Forbidden);

    [Test]
    public async Task NotFound_is_terminal() =>
        await Assert.That(ProvisioningPoll.Classify(404, null, null)).IsEqualTo(PollVerdict.NotFound);

    [Test]
    // The token source refreshes proactively, so a lone 401 is tolerated — the next tick carries a
    // fresh token rather than aborting a legitimate provisioning on a spurious/edge auth blip.
    public async Task Unauthorized_keeps_waiting() =>
        await Assert.That(ProvisioningPoll.Classify(401, null, null)).IsEqualTo(PollVerdict.Wait);

    [Test]
    public async Task Transport_failure_keeps_waiting() =>
        await Assert.That(ProvisioningPoll.Classify(0, null, null)).IsEqualTo(PollVerdict.Wait);

    [Test]
    public async Task Server_error_keeps_waiting() =>
        await Assert.That(ProvisioningPoll.Classify(503, null, null)).IsEqualTo(PollVerdict.Wait);

    /// <summary>Only a status the server answered with marks a workspace as still being built; an
    /// outage must not keep a removed workspace in discovery.</summary>
    [Test]
    [Arguments(200, "provisioning", true)]
    [Arguments(200, "reserved",     true)]
    [Arguments(200, "active",       false)]
    [Arguments(200, "failed",       false)]
    [Arguments(200, null,           false)]
    [Arguments(404, null,           false)]
    [Arguments(503, null,           false)]
    [Arguments(0,   null,           false)]
    public async Task IsPending_needs_an_answered_in_progress_state(int status, string? state, bool expected) =>
        await Assert.That(ProvisioningPoll.IsPending(status, state)).IsEqualTo(expected);
}
