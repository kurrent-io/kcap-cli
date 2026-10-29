using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

// Create-a-tenant flow for `kcap setup` when WorkOS discovery finds none. Prompts (or takes the two
// answers up front), provisions via kcap-web, polls until live. OWNS all user-facing messaging for
// its non-Created outcomes.
/// <param name="requested">
/// Supplied by <c>--org</c>/<c>--slug</c>/<c>--tier</c>/<c>--invite-code</c>. Present means no prompt
/// is raised at all, so this is the only route through the fork that a session with no terminal can take.
/// </param>
/// <param name="isInteractive">
/// Test seam. The ambient value is a property of the host the suite runs under, so a test that read it
/// directly would pass in CI and fail in a developer's terminal.
/// </param>
public sealed class SpectreTenantProvisioner(
        TenantProvisioningClient client,
        string                   baseUrl,
        CliTelemetry             telemetry,
        TimeProvider             time,
        Func<bool>?              isInteractive = null,
        RequestedWorkspace?      requested = null) : ITenantProvisioner {
    const int PollIntervalMs = 4000;
    const int MaxPolls       = 150; // ~10 minutes (server budget is 15)

    const int MaxInviteCodeAttempts = 3;

    const string CreateChoice   = "Create a new workspace";
    const string ExistingChoice = "I already have a workspace";
    const string CancelChoice   = "Cancel";

    const string TeamChoice = "Team trial (30 days, no credit card)";
    const string FreeChoice = "Free";

    public async Task<ProvisionOffer> OfferCreateAsync(WorkOSTokenSource tokens, CancellationToken ct = default) {
        // Says what was actually established, not more: single sign-on returned nothing. Claiming
        // "no tenant is linked to your account" is the very falsehood this prompt exists to stop —
        // a GitHub-App workspace IS linked to the user and simply cannot appear in this lane.
        Note("Single sign-on found no kcap workspace for your account.",
             markup: "[yellow]Single sign-on found no kcap workspace for your account.[/]");
        Note("A workspace that signs in with the GitHub App won't appear here.");

        // The answers are already in hand, so nothing is offered and nothing is asked — including on
        // a terminal that could have been asked. Passing the flags IS the choice.
        if (requested is { } want) return await CreateRequestedAsync(want, tokens, ct);

        // Every way out of this fork is a prompt, so with no terminal there is nothing to offer, and
        // Spectre throws NotSupportedException from inside a prompt rather than returning. Deliberately
        // fires no funnel event: nothing was offered, and recording a decline would attribute to the
        // user a choice they were never shown.
        if (!(isInteractive ?? (() => AnsiConsole.Profile.Capabilities.Interactive))()) {
            // Console rather than AnsiConsole, alone in this class: Spectre hard-wraps at the profile
            // width, which breaks `kcap setup <slug>` across a line and hands the reader a command that
            // does not survive being copied. stderr also matches the non-zero exit this leads to.
            Console.Error.WriteLine();
            Console.Error.WriteLine(OAuthLoginFlow.WorkspaceCreationNeedsATerminalMessage(baseUrl));

            return ProvisionOffer.Declined;
        }

        PromptHygiene.DiscardTypeAhead();
        telemetry.Funnel.WorkspaceOffered();

        // Three ways out, not two: discovery finding nothing does NOT mean the user has no
        // workspace, so offering only "create one" sends an existing member off to make a second.
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("  How would you like to continue?")
                .AddChoices(CreateChoice, ExistingChoice, CancelChoice));

        if (choice == CancelChoice) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            telemetry.Funnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        if (choice == ExistingChoice) {
            var workspace = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace slug or URL:").Validate(v =>
                    string.IsNullOrWhiteSpace(v)
                        ? ValidationResult.Error("Enter a workspace slug (e.g. acme) or a full server URL")
                        : ValidationResult.Success()));

            telemetry.Funnel.WorkspaceRedirected();
            return ProvisionOffer.ExistingWorkspace(workspace.Trim());
        }

        var orgName = AnsiConsole.Prompt(
            new TextPrompt<string>("  Organization name:").Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("Enter a name") : ValidationResult.Success()));

        var slug = await PromptSlugAsync(orgName, tokens, ct);
        if (slug is null) {
            telemetry.Funnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        // Team first, and safe to pick: an account that cannot have the trial falls back to Free.
        var tier = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("  Plan:")
                .AddChoices(TeamChoice, FreeChoice)) == TeamChoice ? WorkspaceTier.Team : WorkspaceTier.Free;

        var origin = $"https://{slug}.kcap.ai";
        var confirm = AnsiConsole.Prompt(
            new ConfirmationPrompt($"  Create tenant [cyan]{Markup.Escape(orgName)}[/] at [cyan]{origin}[/] on {PlanName(tier)}?") { DefaultValue = true });
        if (!confirm) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            telemetry.Funnel.WorkspaceDeclined();
            return ProvisionOffer.Declined;
        }

        return await ProvisionAsync(orgName, slug, origin, tier, tokens, ct, tierChosen: true);
    }

    /// <summary>
    /// The scripted counterpart to the prompts. A slug that is invalid or taken cannot be re-asked
    /// for, so it ends the run with the slug named rather than looping.
    /// </summary>
    async Task<ProvisionOffer> CreateRequestedAsync(
            RequestedWorkspace want, WorkOSTokenSource tokens, CancellationToken ct) {
        var slug  = SlugValidator.Canonicalize(want.Slug);
        var check = SlugValidator.Validate(slug);

        if (!check.Ok) {
            Fail(SlugRejection(slug, check.Reason, "pass a different --slug"), check.Reason!);

            return ProvisionOffer.Failed;
        }

        var avail = await client.CheckAvailabilityAsync(baseUrl, await tokens.GetAsync(ct), slug, ct);

        if (avail is null) {
            Fail($"Couldn't check whether '{slug}' is available. Re-run once you can reach {baseUrl}.",
                 "availability_unreachable");

            return ProvisionOffer.Failed;
        }

        // "yours" is available for this purpose: the slug is already reserved to this account, so
        // provisioning it is a resume rather than a collision.
        if (!avail.Available && avail.Reason != "yours") {
            Fail(SlugRejection(slug, avail.Reason, "pass a different --slug"), avail.Reason ?? "unavailable");

            return ProvisionOffer.Failed;
        }

        // The interactive path confirms the name and hostname before committing to them. Nothing can
        // be confirmed here, so the same two values are at least stated before the workspace exists.
        var tier = want.Tier ?? WorkspaceTier.Free;
        Note($"Creating {want.OrgName} at {want.Origin} on {PlanName(tier)}…");

        return await ProvisionAsync(want.OrgName, slug, want.Origin, tier, tokens, ct, tierChosen: want.Tier is not null);
    }

    /// <summary>
    /// Why a slug cannot be used, shared by the prompt loop and the scripted path so one wording
    /// serves both. <paramref name="tail"/> is what the reader should do, which differs: the loop is
    /// about to ask again, the scripted run is about to end.
    /// </summary>
    internal static string SlugRejection(string slug, string? reason, string tail) => reason switch {
        "invalid"  => $"'{slug}' is not a valid slug. Use lowercase letters, digits and single hyphens (no leading/trailing hyphen), max 40 chars.",
        "reserved" => $"'{slug}' is being provisioned by someone else — {tail}.",
        "blocked"  => $"'{slug}' is reserved — {tail}.",
        "taken"    => $"'{slug}' is taken — {tail}.",
        _          => $"'{slug}' is unavailable — {tail}."
    };

    // Scripted runs render through stderr and plain text: the reader is a log, and a failure that
    // lands on stdout beside the success output is one a script cannot separate. Markup.Escape is
    // for the other arm only - Spectre parses markup, Console does not.
    internal bool Scripted => requested is not null;

    // What the reader should do about a refused slug, which is where the two modes genuinely differ.
    string RetryTail => Scripted ? "pass a different --slug" : "pick another and re-run";

    /// <param name="markup">
    /// Pre-marked-up copy for the terminal, where a plain string would lose styling the escape would
    /// otherwise have to strip. Supplying it makes escaping the caller's job.
    /// </param>
    void Fail(string plain, string funnelReason, string? markup = null) {
        if (Scripted) Console.Error.WriteLine($"  ✗ {plain}");
        else          AnsiConsole.MarkupLine($"  [red]✗[/] {markup ?? Markup.Escape(plain)}");

        telemetry.Funnel.WorkspaceFailed(funnelReason);
    }

    void Note(string plain, string? markup = null) {
        if (Scripted) Console.Error.WriteLine($"  {plain}");
        else          AnsiConsole.MarkupLine($"  [dim]{markup ?? Markup.Escape(plain)}[/]");
    }

    void Retry(string plain, string? markup = null) {
        if (Scripted) Console.Error.WriteLine($"  ! {plain}");
        else          AnsiConsole.MarkupLine($"  [yellow]![/] {markup ?? Markup.Escape(plain)}");
    }

    static string PlanName(WorkspaceTier tier) => tier == WorkspaceTier.Team ? "the Team trial" : "the Free plan";

    /// <param name="tierChosen">False when no plan was named: a Free refusal while Free cloud workspaces are closed
    /// then asks for the Team trial instead.</param>
    async Task<ProvisionOffer> ProvisionAsync(
            string orgName, string slug, string origin, WorkspaceTier tier, WorkOSTokenSource tokens, CancellationToken ct,
            bool tierChosen) {
        telemetry.Funnel.WorkspaceRequested();

        var code       = requested?.InviteCode;
        var wrongCodes = 0;
        // Why the Team trial was refused this run, so a closed Free plan does not send the user back to it.
        string? teamRefused = null;

        while (true) {
            var outcome = await client.ProvisionAsync(
                baseUrl, await tokens.GetAsync(ct), orgName, slug, tier, telemetry.Join.Current, code, ct);

            if (tier == WorkspaceTier.Free && !tierChosen && IsFreeUnavailable(outcome)) {
                Retry("Free cloud workspaces aren't open right now. Asking for the Team trial instead.");
                tier    = WorkspaceTier.Team;
                outcome = await client.ProvisionAsync(
                    baseUrl, await tokens.GetAsync(ct), orgName, slug, tier, telemetry.Join.Current, code, ct);
            }

            // Both refusals land before kcap-web reserves anything, so asking again for Free is a fresh
            // request rather than a resume. The user has already approved a sign-in to get this far.
            if (tier == WorkspaceTier.Team && TeamRefusal(outcome) is { } refusal) {
                teamRefused = refusal;
                Retry($"{refusal} Creating the workspace on the Free plan instead.");
                tier    = WorkspaceTier.Free;
                outcome = await client.ProvisionAsync(
                    baseUrl, await tokens.GetAsync(ct), orgName, slug, tier, telemetry.Join.Current, code, ct);
            }

            switch (outcome.StatusCode) {
                case 200 when outcome.Body?.WorkosOrgId is { Length: > 0 } orgId:
                    telemetry.Funnel.WorkspaceProvisioned();
                    return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, outcome.Body.Url ?? origin));
                case 202 or 200:
                    return await PollAsync(tokens, slug, orgName, origin, tier, ct);
                case 403 when outcome.Body?.Reason == "invite_required": {
                    var requestUrl = outcome.Body.RequestUrl;

                    // Passing the flags means no prompt is raised, so the code has to come as a flag too.
                    if (Scripted) {
                        Fail(requestUrl is { Length: > 0 }
                                 ? $"kcap is invite-only right now. Request an invite at {requestUrl}, then re-run with --invite-code <code>."
                                 : "kcap is invite-only right now. Re-run with --invite-code <code>.",
                             "invite_required");
                        return ProvisionOffer.Failed;
                    }

                    // A server that keeps asking for a code it was just given must not re-prompt forever.
                    if (code is not null && ++wrongCodes >= MaxInviteCodeAttempts) {
                        Fail($"That invite code wasn't accepted.{RequestAt(requestUrl, u => $" Request an invite at {u}.")}", "invite_required");
                        return ProvisionOffer.Failed;
                    }

                    Note($"kcap is invite-only right now. Enter your invite code{RequestAt(requestUrl, u => $", or request one at {u}")}.");
                    code = PromptInviteCode();

                    if (code is null) {
                        Fail(RunAgainLine(requestUrl), "invite_required");
                        return ProvisionOffer.Failed;
                    }

                    continue;
                }
                case 400 when outcome.Body?.Reason == "invalid_code":
                case 409 when outcome.Body?.Reason == "code_exhausted": {
                    var reason     = outcome.Body.Reason;
                    var requestUrl = outcome.Body.RequestUrl;
                    var why        = reason == "invalid_code" ? "That invite code isn't valid." : "That invite code has been used up.";

                    if (Scripted) {
                        Fail(requestUrl is { Length: > 0 }
                                 ? $"{why} Request an invite at {requestUrl}, or pass a different --invite-code."
                                 : $"{why} Pass a different --invite-code.",
                             reason);
                        return ProvisionOffer.Failed;
                    }

                    if (++wrongCodes >= MaxInviteCodeAttempts) {
                        Fail($"{why}{RequestAt(requestUrl, u => $" Request an invite at {u}.")}", reason);
                        return ProvisionOffer.Failed;
                    }

                    Retry($"{why} Try another code{RequestAt(requestUrl, u => $", or request an invite at {u}")}.");
                    code = PromptInviteCode();

                    if (code is null) {
                        Fail(RunAgainLine(requestUrl), reason);
                        return ProvisionOffer.Failed;
                    }

                    continue;
                }
                case 400:
                    Fail(Reason400(outcome.Body?.Reason, slug, RetryTail), outcome.Body?.Reason ?? "invalid_request");
                    return ProvisionOffer.Failed;
                case 409 when IsFreeUnavailable(outcome):
                    Fail(teamRefused is not null
                             ? $"Free cloud workspaces aren't open right now either. {(Scripted ? "Re-run with --local" : "Run `kcap setup --local`")} to run kcap on this machine."
                             : Scripted
                                 ? "Free cloud workspaces aren't open right now. Re-run with --tier team --invite-code <code>, or with --local to run kcap on this machine."
                                 : "Free cloud workspaces aren't open right now. Re-run and choose the Team trial, which needs an invite code, or run `kcap setup --local` to run kcap on this machine.",
                         "free_unavailable");
                    return ProvisionOffer.Failed;
                case 409:
                    Fail(Reason409(outcome.Body?.Reason, slug, RetryTail), outcome.Body?.Reason ?? "conflict");
                    return ProvisionOffer.Failed;
                case 0:
                    Fail("Couldn't reach the provisioning service. Check your connection and try again.", "unreachable");
                    return ProvisionOffer.Failed;
                default:
                    Fail($"Provisioning failed (HTTP {outcome.StatusCode}). Try again later.", $"http_{outcome.StatusCode}");
                    return ProvisionOffer.Failed;
            }
        }
    }

    // The unscripted path reaches provisioning only after its own prompts, so a terminal is there.
    static string? PromptInviteCode() {
        // Anything typed while the provision call was out would otherwise be read as the code.
        PromptHygiene.DiscardTypeAhead();
        var answer = AnsiConsole.Prompt(new TextPrompt<string>("  Invite code:").AllowEmpty()).Trim();

        return answer.Length == 0 ? null : answer;
    }

    // Where to request an invite is the server's to say; with no URL the clause is left out.
    static string RequestAt(string? requestUrl, Func<string, string> clause) =>
        requestUrl is { Length: > 0 } ? clause(requestUrl) : "";

    static string RunAgainLine(string? requestUrl) => requestUrl is { Length: > 0 }
        ? $"Request an invite at {requestUrl} and run kcap setup again."
        : "Run kcap setup again once you have an invite code.";

    async Task<string?> PromptSlugAsync(string orgName, WorkOSTokenSource tokens, CancellationToken ct) {
        var suggestion = SlugValidator.Derive(orgName);
        while (true) {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace URL slug:")
                    .DefaultValue(suggestion.Length > 0 ? suggestion : "")
                    .ShowDefaultValue());
            var slug = SlugValidator.Canonicalize(input);

            var check = SlugValidator.Validate(slug);
            if (!check.Ok) {
                Retry(SlugRejection(slug, check.Reason, "pick another"));
                continue;
            }

            var avail = await AnsiConsole.Status().StartAsync($"Checking {slug}.kcap.ai…",
                async _ => await client.CheckAvailabilityAsync(baseUrl, await tokens.GetAsync(ct), slug, ct));

            if (avail is null) {
                Retry("Couldn't check availability. Try again.");
                continue;
            }
            if (avail.Available || avail.Reason == "yours") return slug;

            Retry(SlugRejection(slug, avail.Reason, "pick another"));
        }
    }

    async Task<ProvisionOffer> PollAsync(
            WorkOSTokenSource tokens, string slug, string orgName, string origin, WorkspaceTier tier, CancellationToken ct) {
        // Two different re-runs. A workspace still being built will exist, so naming it is right; one
        // that failed does not, and `kcap setup <slug>` treats a positional as an existing server and
        // never reaches creation at all. The scripted forms name --no-prompt, without which the first
        // prompt after discovery throws on the session the guidance was printed to.
        var waitPlain    = Scripted ? $"Re-run kcap setup {slug} --no-prompt" : $"Re-run kcap setup {slug}";
        var waitMarkup   = $"Re-run [cyan]kcap setup {Markup.Escape(slug)}[/]";
        // The slug is safe to embed — it is validated down to [a-z0-9-] — but an organization name is
        // arbitrary text, and a quote or a backtick in one turns a command meant for copying into a
        // different command. The reader knows their own name; the shape is what they need.
        // The code is a credential, so the command names the flag and leaves its value to the reader.
        var inviteFlag   = requested?.InviteCode is null ? "" : " --invite-code <code>";
        var createPlain  = Scripted
            ? $"Re-run kcap setup --org \"<name>\" --slug {slug} --tier {tier.Wire()}{inviteFlag} --no-prompt"
            : "Re-run kcap setup";
        var createMarkup = Scripted
            ? $"Re-run [cyan]kcap setup --org \"<name>\" --slug {Markup.Escape(slug)} --tier {tier.Wire()}{Markup.Escape(inviteFlag)} --no-prompt[/]"
            : "Re-run [cyan]kcap setup[/]";
        // The live display is a terminal affordance; on a scripted run its fallback renderer would put
        // a hundred-odd progress lines on stdout, which is the stream that run's failures avoid.
        return Scripted
            ? await PollLoopAsync(_ => { })
            : await AnsiConsole.Status().StartAsync(
                $"Provisioning {slug}.kcap.ai — this can take a few minutes…",
                async ctx => await PollLoopAsync(text => ctx.Status = text));

        async Task<ProvisionOffer> PollLoopAsync(Action<string> setStatus) {
            for (var i = 0; i < MaxPolls; i++) {
                await Task.Delay(TimeSpan.FromMilliseconds(PollIntervalMs), time, ct);
                var status = await client.GetStatusAsync(baseUrl, await tokens.GetAsync(ct), slug, ct);

                switch (ProvisioningPoll.Classify(status.StatusCode, status.Body?.State, status.Body?.WorkosOrgId)) {
                    case PollVerdict.Active:
                        telemetry.Funnel.WorkspaceProvisioned();
                        return ProvisionOffer.Created(new ProvisionedTenant(status.Body!.WorkosOrgId!, slug, orgName, status.Body.Url ?? origin));
                    case PollVerdict.ActiveNoOrg:
                        Fail($"{slug}.kcap.ai is live but isn't linked to an organization. Contact support.", "active_no_org");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Failed:
                        Fail($"Provisioning failed. {createPlain} to retry.", "provisioning_failed",
                             markup: $"Provisioning failed. {createMarkup} to retry.");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Forbidden:
                        Fail($"Verify your email address. {createPlain} to try again.", "forbidden",
                             markup: $"Verify your email address. {createMarkup} to try again.");
                        return ProvisionOffer.Failed;
                    case PollVerdict.NotFound:
                        Fail($"'{slug}' isn't linked to your account. {createPlain}.", "not_found",
                             markup: $"'{Markup.Escape(slug)}' isn't linked to your account. {createMarkup}.");
                        return ProvisionOffer.Failed;
                    case PollVerdict.Wait:
                        // Surface liveness so an elapsed timer never reads as a frozen CLI.
                        setStatus($"Provisioning {slug}.kcap.ai — waiting for it to come online… ({i + 1}/{MaxPolls})");
                        break;
                }
            }
            Retry($"Still provisioning. {waitPlain} once it's ready.",
                  markup: $"Still provisioning. {waitMarkup} once it's ready.");
            telemetry.Funnel.WorkspaceFailed("poll_timeout");
            return ProvisionOffer.InProgress(slug);
        }
    }

    static bool IsFreeUnavailable(ProvisionOutcome outcome) => outcome is { StatusCode: 409, Body.Reason: "free_unavailable" };

    static string? TeamRefusal(ProvisionOutcome outcome) => (outcome.StatusCode, outcome.Body?.Reason) switch {
        (400, "work_email_required") => "The Team trial needs a work email, and this account uses a personal address.",
        (409, "trial_exists")        => "Your organization already has a Team trial.",
        _                            => null
    };

    // The availability check and the provision call refuse a slug for the same reasons in different
    // vocabularies; both reach one reader in one run, so both speak through SlugRejection.
    static string Reason400(string? reason, string slug, string tail) => reason switch {
        "disposable_email" => "Provisioning requires a non-disposable email address.",
        "flagged"          => "We couldn't create this workspace. Email team@kcap.ai if you think this is a mistake.",
        "blocked"          => SlugRejection(slug, "blocked", tail),
        _                  => "Invalid organization name or slug."
    };

    static string Reason409(string? reason, string slug, string tail) => reason switch {
        "owned_by_other" => SlugRejection(slug, "reserved", tail),
        "free_limit"     => "This account already owns a Free workspace. Email team@kcap.ai if you need another.",
        _                => SlugRejection(slug, "taken", tail)
    };
}
