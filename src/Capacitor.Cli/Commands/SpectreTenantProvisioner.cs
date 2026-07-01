using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

// Interactive create-a-tenant flow for `kcap setup` when WorkOS discovery finds
// none. Prompts, provisions via kcap-web, polls until live. OWNS all user-facing
// messaging for its non-Created outcomes.
public sealed class SpectreTenantProvisioner(TenantProvisioningClient client, string baseUrl) : ITenantProvisioner {
    const int PollIntervalMs = 4000;
    const int MaxPolls       = 150; // ~10 minutes (server budget is 15)

    public async Task<ProvisionOffer> OfferCreateAsync(string workosAccessToken, CancellationToken ct = default) {
        AnsiConsole.MarkupLine("  [yellow]No Capacitor tenant is linked to your account.[/]");
        var create = AnsiConsole.Prompt(new ConfirmationPrompt("  Create one now?") { DefaultValue = true });
        if (!create) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            return ProvisionOffer.Declined;
        }

        var orgName = AnsiConsole.Prompt(
            new TextPrompt<string>("  Organization name:").Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("Enter a name") : ValidationResult.Success()));

        var slug = await PromptSlugAsync(orgName, workosAccessToken, ct);
        if (slug is null) return ProvisionOffer.Declined;

        var origin = $"https://{slug}.kcap.ai";
        var confirm = AnsiConsole.Prompt(
            new ConfirmationPrompt($"  Create tenant [cyan]{Markup.Escape(orgName)}[/] at [cyan]{origin}[/]?") { DefaultValue = true });
        if (!confirm) {
            AnsiConsole.MarkupLine("  [dim]No tenant created.[/]");
            return ProvisionOffer.Declined;
        }

        var outcome = await client.ProvisionAsync(baseUrl, workosAccessToken, orgName, slug, ct);
        switch (outcome.StatusCode) {
            case 200 when outcome.Body?.WorkosOrgId is { Length: > 0 } orgId:
                return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, outcome.Body.Url ?? origin));
            case 202 or 200:
                return await PollAsync(workosAccessToken, slug, orgName, origin, ct);
            case 400:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason400(outcome.Body?.Reason)}");
                return ProvisionOffer.Failed;
            case 409:
                AnsiConsole.MarkupLine($"  [red]✗[/] {Reason409(outcome.Body?.Reason, slug)}");
                return ProvisionOffer.Failed;
            default:
                AnsiConsole.MarkupLine($"  [red]✗[/] Provisioning failed (HTTP {outcome.StatusCode}). Try again later.");
                return ProvisionOffer.Failed;
        }
    }

    async Task<string?> PromptSlugAsync(string orgName, string token, CancellationToken ct) {
        var suggestion = SlugValidator.Derive(orgName);
        while (true) {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("  Workspace URL slug:")
                    .DefaultValue(suggestion.Length > 0 ? suggestion : "")
                    .ShowDefaultValue());
            var slug = SlugValidator.Canonicalize(input);

            var check = SlugValidator.Validate(slug);
            if (!check.Ok) {
                AnsiConsole.MarkupLine(check.Reason == "blocked"
                    ? $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another."
                    : "  [yellow]![/] Use lowercase letters, digits and single hyphens (no leading/trailing hyphen), max 40 chars.");
                continue;
            }

            var avail = await AnsiConsole.Status().StartAsync($"Checking {slug}.kcap.ai…",
                async _ => await client.CheckAvailabilityAsync(baseUrl, token, slug, ct));

            if (avail is null) {
                AnsiConsole.MarkupLine("  [yellow]![/] Couldn't check availability. Try again.");
                continue;
            }
            if (avail.Available || avail.Reason == "yours") return slug;

            AnsiConsole.MarkupLine(avail.Reason switch {
                "reserved" => $"  [yellow]![/] '{Markup.Escape(slug)}' is being provisioned by someone else — pick another.",
                "taken"    => $"  [yellow]![/] '{Markup.Escape(slug)}' is taken — pick another.",
                "blocked"  => $"  [yellow]![/] '{Markup.Escape(slug)}' is reserved — pick another.",
                _          => $"  [yellow]![/] '{Markup.Escape(slug)}' is unavailable — pick another."
            });
        }
    }

    async Task<ProvisionOffer> PollAsync(string token, string slug, string orgName, string origin, CancellationToken ct) {
        return await AnsiConsole.Status().StartAsync($"Provisioning {slug}.kcap.ai — this can take a few minutes…", async _ => {
            for (var i = 0; i < MaxPolls; i++) {
                await Task.Delay(PollIntervalMs, ct);
                var status = await client.GetStatusAsync(baseUrl, token, slug, ct);
                switch (status?.State) {
                    case "active" when status.WorkosOrgId is { Length: > 0 } orgId:
                        return ProvisionOffer.Created(new ProvisionedTenant(orgId, slug, orgName, status.Url ?? origin));
                    case "failed":
                        AnsiConsole.MarkupLine("  [red]✗[/] Provisioning failed. Re-run [cyan]kcap setup " + Markup.Escape(slug) + "[/] to retry.");
                        return ProvisionOffer.Failed;
                }
            }
            AnsiConsole.MarkupLine($"  [yellow]![/] Still provisioning. Re-run [cyan]kcap setup {Markup.Escape(slug)}[/] once it's ready.");
            return ProvisionOffer.InProgress;
        });
    }

    static string Reason400(string? reason) => reason switch {
        "disposable_email" => "Provisioning requires a non-disposable email address.",
        "blocked"          => "That slug is reserved. Pick another and re-run.",
        _                  => "Invalid organization name or slug."
    };

    static string Reason409(string? reason, string slug) => reason switch {
        "owned_by_other" => $"'{slug}' is owned by someone else. Pick another and re-run.",
        _                => $"'{slug}' is already taken. Pick another and re-run."
    };
}
