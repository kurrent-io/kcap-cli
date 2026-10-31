using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>The usage-data question <c>kcap setup --local</c> asks before anything is sent. Neither answer is
/// pre-selected; a run that cannot ask saves nothing, and the local edition reads no choice as don't share.</summary>
static class LocalUsageDataQuestion {
    public const string Explanation =
        "Your sessions stay on this machine either way. Usage data is pseudonymous events about which commands and features "
      + "you use and how setup goes, never file paths, prompts or transcript content. Nothing is sent unless you say yes.";

    public const string Question = "Share usage data with kcap?";

    public const string Shared    = "Usage data: shared. kcap config set telemetry off stops it.";
    public const string NotShared = "Usage data: not shared. kcap config set telemetry on shares it.";
    public const string NotSaved  = "Usage data: not shared. The choice could not be saved, so nothing is sent.";

    /// <summary>No choice saved and no environment variable deciding. A choice that cannot be read is not asked
    /// again: it may be an opt-out.</summary>
    public static bool Due(ConfigRoot config) =>
        Due(TelemetryState.TryRead(config), TelemetrySettings.Resolve(null, UnsavedConsent.Withhold));

    internal static bool Due(TelemetryStateFile? saved, TelemetryDecision unsaved) =>
        saved is { Enabled: null } && unsaved.Reason == TelemetrySettings.LocalReason;

    public static bool Ask() {
        AnsiConsole.MarkupLine($"  {Markup.Escape(Explanation)}");

        var answer = AnsiConsole.Prompt(new TextPrompt<string>(Markup.Escape(Question), StringComparer.OrdinalIgnoreCase)
            .AddChoice("yes")
            .AddChoice("no"));

        return string.Equals(answer.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Saves the answer every kcap process reads, and lets this run send what it held only on a saved yes.</summary>
    /// <returns>The line that reports what was saved.</returns>
    public static string Record(bool share, CliTelemetry telemetry, ConfigRoot config) {
        if (!share) {
            telemetry.DiscardAndDisable();

            return TelemetryState.SetEnabled(false, config) ? NotShared : NotSaved;
        }

        if (!TelemetryState.SetEnabled(true, config)) {
            telemetry.DiscardAndDisable();

            return NotSaved;
        }

        telemetry.OptedIn(config);

        return Shared;
    }
}
