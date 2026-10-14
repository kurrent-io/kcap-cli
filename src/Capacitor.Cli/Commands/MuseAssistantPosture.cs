using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup --muse-assistant</c> forces. Muse Assistant is not a coding harness —
/// it reads <c>~/workspace/skills</c> and its own connectors — so nothing is wired into the Codex the
/// image ships; and every host a run contacts is named up front, because Muse's egress filter asks
/// the user to approve each one in the chat and answers an unapproved host with a placeholder
/// address.</summary>
internal static class MuseAssistantPosture {
    public const string Flag              = "--muse-assistant";
    public const string DefaultVisibility = "private";

    public const string SkippedHarnessesLine =
        "  [dim]· Muse Assistant: harness wiring skipped — Muse reads ~/workspace/skills and its own connectors, and the image's Codex is not Muse's[/]";

    public const string AdviceLine =
        "  [dim]This looks like a Muse Assistant VM. Re-run with --muse-assistant to skip harness wiring and keep sessions private.[/]";

    public const string SummaryValue = "Muse Assistant (no harness wiring; sessions private by default)";

    public static string? ArgumentError(bool tenantGiven) =>
        tenantGiven
            ? null
            : $"{Flag} signs in by device code with no terminal prompt, so it needs the workspace: kcap setup <tenant> {Flag}";

    public static CodingAgentsStep.Options Apply(CodingAgentsStep.Options options) => options with {
        InstallAgents               = false,
        NoPrompt                    = true,
        SkipClaude                  = true,
        SkipCodex                   = true,
        SkipCodexNetworkAccess      = true,
        SkipCursor                  = true,
        SkipCursorMcp               = true,
        SkipCopilot                 = true,
        SkipCopilotMcp              = true,
        SkipCopilotInstructions     = true,
        SkipGemini                  = true,
        SkipGeminiMcp               = true,
        SkipGeminiInstructions      = true,
        SkipKiro                    = true,
        SkipKiroMcp                 = true,
        SkipKiroSkills              = true,
        SkipPi                      = true,
        SkipPiMcp                   = true,
        SkipPiInstructions          = true,
        SkipOpenCode                = true,
        SkipOpenCodeMcp             = true,
        SkipOpenCodeInstructions    = true,
        SkipAntigravity             = true,
        SkipAntigravityMcp          = true,
        SkipAntigravityInstructions = true,
        SkipAntigravitySkills       = true,
        SkipMistralVibe             = true,
        SkipMistralVibeMcp          = true,
        SkipGrok                    = true,
        SkipMuse                    = true,
        SkipMuseMcp                 = true
    };

    public static string HostOf(string serverArg) {
        var text = serverArg.Contains("://", StringComparison.Ordinal) ? serverArg : "https://" + serverArg;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host : serverArg;
    }

    /// <summary>The hosts contacted before the server names its sign-in provider.</summary>
    public static IReadOnlyList<string> ApprovalHosts(string serverArg) => Distinct(HostOf(serverArg), CliTelemetry.EndpointHost);

    /// <summary>The hosts the device sign-in for <paramref name="provider"/> contacts. WorkOS also
    /// serves its verification page from the kcap sign-in domain.</summary>
    public static IReadOnlyList<string> SignInHosts(string provider) => provider switch {
        AuthProvider.WorkOS    => Distinct(HostOf(WorkOSClient.ApiBase), HostOf(MachineAuth.DefaultTokenUrl)),
        AuthProvider.GitHubApp => Distinct(HostOf(GitHubOAuthClient.ApiBase)),
        _                      => []
    };

    static List<string> Distinct(params string[] hosts) => hosts.Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IEnumerable<string> HostNoticeLines(IReadOnlyList<string> hosts) {
        yield return "  Muse Assistant: the first contact with each host below raises an approval card in the user's chat.";
        yield return "  Ask the user to choose \"Always allow this site\" on each card:";

        foreach (var host in hosts) yield return $"    {host}";
    }

    public static IEnumerable<string> SignInNoticeLines(IReadOnlyList<string> hosts) {
        if (hosts.Count == 0) yield break;

        yield return "  Muse Assistant: signing in contacts these hosts next; ask the user to approve their cards too:";

        foreach (var host in hosts) yield return $"    {host}";
    }

    public static IEnumerable<string> UnreachableHintLines(IReadOnlyList<string> hosts) {
        yield return "  On Muse Assistant an unapproved host answers with a placeholder address, so \"cannot reach\" here";
        yield return $"  usually means not yet approved. Ask the user to approve the cards for {string.Join(", ", hosts)},";
        yield return "  then run the same command again.";
    }

    /// <summary>Only for a device sign-in that did not complete. That one result also covers a refused
    /// device-code request, so each remedy is conditional on the cause the line above names.</summary>
    public static IEnumerable<string> LoginFailedHintLines(AuthResult result, string provider) {
        if (result is not AuthResult.Failed { Reason: AuthFailureReason.SigninDenied }) yield break;

        var hosts = SignInHosts(provider);

        if (hosts.Count > 0)
            yield return $"  On Muse Assistant, if a card for {string.Join(", ", hosts)} is still waiting, ask the user to approve it, then run the same command again.";

        yield return "  If the code expired or was declined, run the same command again for a fresh one; any other reason above needs its own fix first.";
    }
}
