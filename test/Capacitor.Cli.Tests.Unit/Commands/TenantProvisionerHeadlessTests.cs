using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A non-interactive session reaches the create-a-workspace fork, where every way out is a Spectre
/// prompt and Spectre throws rather than returning. Either the two answers arrive as flags, or there
/// is nothing to ask and the run has to say so.
/// </summary>
/// <remarks>These capture Console, which is process-global.</remarks>
[NotInParallel]
public class TenantProvisionerHeadlessTests {
    const string BaseUrl = ProvisioningStubHandler.BaseUrl;

    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null), time: TimeProvider.System);

    /// <summary>Interactivity is injected, not read: the ambient value belongs to whatever host the
    /// suite is running under, so reading it would pass in CI and fail in a developer's terminal.</summary>
    [Test]
    public async Task Declines_instead_of_throwing_when_there_is_no_terminal_to_prompt_on() {
        var provisioner = new SpectreTenantProvisioner(
            new TenantProvisioningClient(new HttpClient(), ClientAgent.Cli), BaseUrl, NoTelemetry.Facade,
            TimeProvider.System,
            isInteractive: () => false);

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Declined);
    }

    /// <summary>Names every route that actually works, and no other: an org admin cannot fix this, so
    /// "ask your admin" would be a dead end dressed as advice.</summary>
    [Test]
    public async Task The_message_offers_the_flags_signup_and_an_existing_workspace() {
        var message = OAuthLoginFlow.WorkspaceCreationNeedsATerminalMessage("https://signup.test");

        await Assert.That(message).Contains("--org");
        await Assert.That(message).Contains("--slug");
        await Assert.That(message).Contains("https://signup.test/signup");
        await Assert.That(message).Contains("--server-url");
        await Assert.That(message).DoesNotContain("admin");
    }

    // --- The answers supplied up front (--org / --slug) ---

    [Test]
    public async Task Provisions_with_no_terminal_when_the_answers_are_supplied() {
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(offer.Tenant!.Slug).IsEqualTo("acme");
        await Assert.That(offer.Tenant.OrganizationId).IsEqualTo("org_123");
    }

    // Reaching Created at all is the assertion: every prompt sits after this branch, and a Spectre
    // prompt in a test host throws rather than returning a default.
    [Test]
    public async Task The_supplied_answers_replace_the_prompts_on_a_terminal_too() {
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => true, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
    }

    [Test]
    public async Task A_taken_slug_ends_the_run_naming_it() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { AvailabilityReason = "taken" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("acme");
        await Assert.That(handler.Paths).DoesNotContain("/api/signup/provision");
    }

    [Test]
    public async Task A_slug_that_cannot_be_checked_ends_the_run_rather_than_guessing() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { AvailabilityStatus = HttpStatusCode.InternalServerError };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("acme");
        await Assert.That(handler.Paths).DoesNotContain("/api/signup/provision");
    }

    [Test]
    public async Task An_unusable_slug_ends_the_run_before_any_network_call() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "Acme Inc!"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("not a valid slug");
        await Assert.That(handler.Paths).IsEmpty();
    }

    // A reserved word fails the same check by a different arm, and the two say different things.
    [Test]
    public async Task A_reserved_slug_says_so_rather_than_calling_it_malformed() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "api"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("is reserved");
        await Assert.That(handler.Paths).IsEmpty();
    }

    [Test]
    [Arguments("reserved", "being provisioned by someone else")]
    [Arguments("blocked",  "is reserved")]
    [Arguments(null,       "is unavailable")]
    public async Task Every_unavailable_reason_reaches_the_log_with_its_own_wording(string? reason, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { AvailabilityAvailable = false, AvailabilityReason = reason };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
    }

    // The refusal has to land on the same stream as the earlier ones, or a script reads two logs.
    [Test]
    [Arguments(HttpStatusCode.Conflict, "taken")]
    [Arguments(HttpStatusCode.BadRequest, "Invalid organization name or slug")]
    public async Task A_refusal_from_the_provision_call_ends_the_run_on_stderr(HttpStatusCode status, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = status };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
    }

    [Test]
    public async Task The_org_and_hostname_are_stated_before_the_workspace_exists() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        await provisioner.OfferCreateAsync(Tokens());

        var written = capture.GetCapturedError();

        await Assert.That(written).Contains("Acme");
        await Assert.That(written).Contains("https://acme.kcap.ai");
    }

    // The wait renders through a Spectre live display, the one part of the poll that wants a
    // terminal, so the path is walked rather than assumed.
    [Test]
    public async Task Waits_out_a_pending_workspace_with_no_terminal_to_render_on() {
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "active" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.Paths).Contains("/api/signup/status");
    }

    // A workspace that failed to build does not exist, and `kcap setup <slug>` reads a positional as
    // an existing server — so the way back has to be the command that creates, not the one that points.
    [Test]
    public async Task A_failed_build_points_back_at_the_command_that_creates() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        var written = capture.GetCapturedError();

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(written).Contains("--slug acme --tier free --no-prompt");
        await Assert.That(written).DoesNotContain("kcap setup acme");
    }

    // Without the tier, following the printed command would quietly ask for a different plan.
    [Test]
    public async Task A_failed_build_retries_on_the_plan_that_was_asked_for() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(capture.GetCapturedError()).Contains("--slug acme --tier team --no-prompt");
    }

    // An organization name is arbitrary text; a quote or a backtick in one rewrites the command it is
    // pasted into, so the suggestion carries a placeholder rather than the name itself.
    [Test]
    public async Task The_retry_command_never_carries_the_organization_name() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("\"; rm -rf /; echo \"", "acme"));

        await provisioner.OfferCreateAsync(Tokens());

        // The echo names the organization because that is a report; the re-run line is a command
        // somebody pastes, and that is the one the name must stay out of.
        var retryLine = capture.GetCapturedError()
            .Split('\n')
            .Single(l => l.Contains("Re-run", StringComparison.Ordinal));

        await Assert.That(retryLine).Contains("--org \"<name>\"");
        await Assert.That(retryLine).DoesNotContain("rm -rf");
    }

    // Without the flag the printed command would hit the gate again; the code itself stays out of a log.
    [Test]
    public async Task A_failed_build_names_the_invite_code_flag_but_never_the_code() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", InviteCode: "SECRET1"));

        await provisioner.OfferCreateAsync(Tokens());

        var retryLine = capture.GetCapturedError()
            .Split('\n')
            .Single(l => l.Contains("Re-run", StringComparison.Ordinal));

        await Assert.That(retryLine).Contains("--tier free --invite-code <code> --no-prompt");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("SECRET1");
    }

    [Test]
    public async Task A_failed_build_with_no_invite_code_names_no_invite_code_flag() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(capture.GetCapturedError()).DoesNotContain("--invite-code");
    }

    [Test]
    public async Task The_requested_tier_reaches_the_provision_call() {
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.ProvisionTiers).IsEquivalentTo(["team"]);
    }

    // The sign-in is already approved by the time the server refuses, so ending the run here would
    // make the user start over for a plan choice that has a perfectly good second answer.
    [Test]
    [Arguments(HttpStatusCode.BadRequest, "work_email_required", "work email")]
    [Arguments(HttpStatusCode.Conflict,   "trial_exists",        "already has a Team trial")]
    public async Task A_refused_trial_falls_back_to_the_free_plan_and_says_why(
            HttpStatusCode status, string reason, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(status, reason)] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.ProvisionTiers).IsEquivalentTo(["team", "free"]);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
        await Assert.That(capture.GetCapturedError()).Contains("Free plan instead");
    }

    // Only the two trial refusals have a Free answer; a taken slug is taken whatever the plan.
    [Test]
    public async Task Any_other_refusal_of_a_trial_ends_the_run_without_retrying() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Conflict, "taken")] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionTiers).IsEquivalentTo(["team"]);
    }

    // Free cloud workspaces close while the invite gate binds a caller. A run that named no plan asks for the
    // Team trial instead; one that named Free is told its choices, not that the slug is taken.
    [Test]
    public async Task An_unnamed_plan_moves_to_the_team_trial_when_free_is_closed() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Conflict, "free_unavailable")] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.ProvisionTiers).IsEquivalentTo(["free", "team"]);
        await Assert.That(capture.GetCapturedError()).Contains("Team trial instead");
    }

    [Test]
    public async Task A_named_free_plan_that_is_closed_says_what_to_choose_instead() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Conflict, "free_unavailable")] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Free));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionTiers).IsEquivalentTo(["free"]);
        await Assert.That(capture.GetCapturedError()).Contains("--tier team --invite-code");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("is taken");
    }

    [Test]
    public async Task A_closed_free_plan_after_a_refused_trial_offers_only_the_local_edition() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler {
            ProvisionReplies = [(HttpStatusCode.BadRequest, "work_email_required"), (HttpStatusCode.Conflict, "free_unavailable")]
        };
        var provisioner = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("--local");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("--tier team");
    }

    // The fallback can land on an account that already owns a Free workspace, which is not a taken slug.
    [Test]
    public async Task A_free_limit_says_the_account_already_has_a_workspace() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler {
            ProvisionReplies = [(HttpStatusCode.Conflict, "trial_exists"), (HttpStatusCode.Conflict, "free_limit")]
        };
        var provisioner = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("already owns a Free workspace");
    }

    [Test]
    public async Task A_slug_already_reserved_to_this_account_still_provisions() {
        using var handler = new ProvisioningStubHandler { AvailabilityAvailable = false, AvailabilityReason = "yours" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
    }

    // --- The invite gate ---

    [Test]
    public async Task Headless_invite_required_ends_the_run_naming_the_request_url() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Forbidden, "invite_required")] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("invite-only");
        await Assert.That(capture.GetCapturedError()).Contains("https://signup.example/get-started#request");
        await Assert.That(capture.GetCapturedError()).Contains("--invite-code");
    }

    // Where to request an invite is the server's to say; guessing a URL would name the wrong host.
    [Test]
    public async Task Invite_required_with_no_request_url_names_no_url() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Forbidden, "invite_required")], OmitRequestUrl = true };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("--invite-code");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("get-started");
    }

    // Passing the flags is the choice, on a terminal too: a prompt here would throw in this host.
    [Test]
    public async Task Supplied_answers_never_prompt_for_an_invite_code_even_on_a_terminal() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Forbidden, "invite_required")] };
        var provisioner   = Provisioner(handler, () => true, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(handler.ProvisionCodes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Headless_sends_the_supplied_invite_code() {
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", InviteCode: "abcd"));

        await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { "abcd" });
    }

    [Test]
    public async Task No_invite_code_sends_no_code_member() {
        using var handler = new ProvisioningStubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { null });
    }

    // The fallback is a second request, and an invite-gated server refuses it without the code.
    [Test]
    public async Task The_free_fallback_carries_the_invite_code_too() {
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(HttpStatusCode.Conflict, "trial_exists")] };
        var provisioner   = Provisioner(handler, () => false,
            new RequestedWorkspace("Acme", "acme", WorkspaceTier.Team, InviteCode: "abcd"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.ProvisionCodes).IsEquivalentTo(new string?[] { "abcd", "abcd" });
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, "invalid_code",   "isn't valid")]
    [Arguments(HttpStatusCode.Conflict,   "code_exhausted", "has been used up")]
    public async Task Headless_refused_code_names_the_reason_and_the_request_url(
            HttpStatusCode status, string reason, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new ProvisioningStubHandler { ProvisionReplies = [(status, reason)] };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme", InviteCode: "ZZZZ"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
        await Assert.That(capture.GetCapturedError()).Contains("get-started#request");
        await Assert.That(handler.ProvisionCodes).Count().IsEqualTo(1);
    }

    static SpectreTenantProvisioner Provisioner(ProvisioningStubHandler handler, Func<bool> isInteractive, RequestedWorkspace requested) =>
        new(new TenantProvisioningClient(new HttpClient(handler, disposeHandler: false), ClientAgent.Cli), BaseUrl,
            NoTelemetry.Facade, TimeProvider.System, isInteractive, requested);
}
