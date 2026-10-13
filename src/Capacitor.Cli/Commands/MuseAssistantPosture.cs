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
        SkipGrok                    = true
    };

    public static string HostOf(string serverArg) {
        var text = serverArg.Contains("://", StringComparison.Ordinal) ? serverArg : "https://" + serverArg;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host : serverArg;
    }

    public static IReadOnlyList<string> ApprovalHosts(string serverArg, AuthEndpoints endpoints) {
        var hosts = new List<string>();

        void Add(string host) {
            if (host.Length > 0 && !hosts.Contains(host)) hosts.Add(host);
        }

        Add(HostOf(serverArg));
        Add(CliTelemetry.EndpointHost);
        Add(HostOf(endpoints.ProxyUrl));
        Add(HostOf(MachineAuth.DefaultTokenUrl));

        return hosts;
    }

    public static IEnumerable<string> HostNoticeLines(IReadOnlyList<string> hosts) {
        yield return "  Muse Assistant: the first contact with each host below raises an approval card in the user's chat.";
        yield return "  Ask the user to choose \"Always allow this site\" on each card; this run finishes once they have:";

        foreach (var host in hosts) yield return $"    {host}";
    }

    public static IEnumerable<string> UnreachableHintLines(IReadOnlyList<string> hosts) {
        yield return "  On Muse Assistant an unapproved host answers with a placeholder address, so \"cannot reach\" here";
        yield return $"  usually means not yet approved. Ask the user to approve the cards for {string.Join(", ", hosts)},";
        yield return "  then run the same command again.";
    }
}
