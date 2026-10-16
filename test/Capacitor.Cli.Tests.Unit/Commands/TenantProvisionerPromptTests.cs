using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The prompted create-a-workspace path when kcap-web gates provisioning on an invite code. Each test
/// answers the fork, organization, slug, plan and confirmation prompts before reaching the gate.
/// </summary>
/// <remarks>These swap AnsiConsole.Console, which is process-global.</remarks>
[NotInParallel]
public class TenantProvisionerPromptTests {
    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null), time: TimeProvider.System);

    // Create, "Acme", the derived slug, Free, confirm.
    static ScriptedConsole UpToTheGate() =>
        new ScriptedConsole().Enter().Line("Acme").Enter().Down().Enter().Enter();

    [Test]
    public async Task A_prompted_code_reaches_the_next_provision_call() {
        using var console = UpToTheGate().Line("abcd");
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Forbidden, "invite_required")] };

        var offer = await Provisioner(handler).OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { null, "abcd" });
        await Assert.That(console.Text).Contains("invite-only");
    }

    [Test]
    public async Task An_empty_answer_ends_the_run_naming_the_request_url() {
        using var console = UpToTheGate().Enter();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Forbidden, "invite_required")] };

        var offer = await Provisioner(handler).OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { null });
        await Assert.That(console.Text).Contains("get-started#request");
    }

    [Test]
    public async Task The_third_wrong_code_ends_the_run() {
        using var console = UpToTheGate().Line("c1").Line("c2").Line("c3");
        using var handler = new ProvisioningStubHandler {
            ProvisionReplies = [
                (HttpStatusCode.Forbidden,  "invite_required"),
                (HttpStatusCode.BadRequest, "invalid_code"),
                (HttpStatusCode.Conflict,   "code_exhausted"),
                (HttpStatusCode.BadRequest, "invalid_code"),
            ]
        };

        var offer = await Provisioner(handler).OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { null, "c1", "c2", "c3" });
        await Assert.That(console.Text).Contains("isn't valid");
    }

    // A server that answers every supplied code with "invite required" must not re-prompt forever.
    [Test]
    public async Task Invite_required_after_a_supplied_code_counts_toward_the_cap() {
        using var console = UpToTheGate().Line("c1").Line("c2").Line("c3");
        using var handler = new ProvisioningStubHandler {
            ProvisionReplies = [
                (HttpStatusCode.Forbidden, "invite_required"),
                (HttpStatusCode.Forbidden, "invite_required"),
                (HttpStatusCode.Forbidden, "invite_required"),
                (HttpStatusCode.Forbidden, "invite_required"),
            ]
        };

        var offer = await Provisioner(handler).OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { null, "c1", "c2", "c3" });
    }

    static SpectreTenantProvisioner Provisioner(ProvisioningStubHandler handler) =>
        new(new TenantProvisioningClient(new HttpClient(handler, disposeHandler: false), ClientAgent.Cli), ProvisioningStubHandler.BaseUrl,
            NoTelemetry.Facade, TimeProvider.System, isInteractive: () => true);
}
