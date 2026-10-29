using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Services;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Setup's daemon service: installed or started on every run, once setup has written its own settings.
///
/// <para><b>Deliberately after the settings write, never in the browser leg's poll loop.</b> A service
/// unit bakes the profile name, the expected server URL and the daemon name, so acting earlier installs a
/// unit for the profile this process started with — the wrong server after a login that adopted another
/// tenant, and a name the user is about to stop using.</para>
///
/// <para>The profile handed in is therefore the one setup just <i>wrote</i>, not a re-resolution of it:
/// CLI, environment and repository precedence can each land somewhere other than what this run
/// chose.</para>
/// </summary>
static class SetupDaemonService {
    /// <summary>Bounded, because the request stays outstanding when nothing lands and the screen then
    /// says it asked for ever. There is no next tick here to retry on, unlike the poll loop's.</summary>
    const int ReportAttempts = 3;

    /// <param name="previousName">The daemon name the profile carried before this run saved its own.</param>
    /// <param name="ladder">Test seam for the ensure ladder; production resolves the real service
    /// manager. Null return means the platform has no service manager at all.</param>
    /// <returns>What the ensure did, and a line naming the old daemon's service when a rename retired it
    /// or left it installed.</returns>
    public static async Task<(FirstRunMachineActionResult Result, string? RenameLine)> EnsureAsync(
            ConfigRoot               root,
            ProfileContext           saved,
            UserHome                 home,
            TimeProvider             time,
            string?                  previousName = null,
            Func<Task<FlowEnsure?>>? ladder       = null) {
        try {
            var run = ladder ?? (() => DaemonServiceCommands.FlowEnsureAsync(root, saved, home, time, previousName));

            return await run() is { } outcome
                ? (EnsureFlowMap.Map(outcome.Result), RenameLine(outcome))
                : (new FirstRunMachineActionResult(
                       FirstRunMachineActionOutcomes.Refused,
                       FirstRunMachineActionReasons.UnsupportedPlatform), null);
        } catch (Exception) {
            // Something was attempted, so this is a failure rather than a refusal.
            return (new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Failed, null), null);
        }
    }

    public static bool IsUp(FirstRunMachineActionResult result) => result.Outcome is
        FirstRunMachineActionOutcomes.Enabled or FirstRunMachineActionOutcomes.EnabledUnverified or FirstRunMachineActionOutcomes.AlreadyEnabled;

    /// <summary>Gives an installed service the search path setup sees when it finds a harness the unit's
    /// path does not. Returns the line to print, or null when nothing changed.</summary>
    /// <param name="refresh">Test seam for the rewrite; production goes through the service manager.</param>
    public static async Task<string?> RefreshPathAsync(
            ConfigRoot                   root,
            ProfileContext               saved,
            UserHome                     home,
            TimeProvider                 time,
            HarnessRegistry              harnesses,
            Func<Task<PathRefresh?>>?    refresh = null) {
        try {
            var run = refresh ?? (() => DaemonServiceCommands.FlowRefreshPathAsync(root, saved, home, time, harnesses));

            return PathLine(await run());
        } catch (Exception ex) {
            return PathLine(new(UnitRefresh.Failed, ex.Message));
        }
    }

    internal static string? PathLine(PathRefresh? refresh) => refresh?.Outcome switch {
        null or UnitRefresh.Current => null,
        UnitRefresh.Reloaded =>
            "  [dim]Restarted the daemon service with your current PATH, so it finds the harnesses setup found.[/]",
        UnitRefresh.NotLoaded =>
            "  [dim]The daemon service now has your current PATH; the daemon picks up the harnesses setup found when it next starts.[/]",
        UnitRefresh.Deferred =>
            "  [yellow]![/] The daemon was busy, so it keeps its old PATH and may not find every harness setup found. Run setup again once it is idle.",
        UnitRefresh.Contended or UnitRefresh.Unverified =>
            "  [yellow]![/] The daemon service could not be checked just now, so it keeps its old PATH and may not find every harness setup found. Run setup again shortly.",
        _ =>
            "  [yellow]![/] The daemon service could not be given your current PATH, so it may not find every harness setup found"
          + (refresh.Error is { } error ? $" ({Markup.Escape(error)})" : "")
          + ". Reinstall it with [cyan]kcap daemon service install --replace --verify[/]",
    };

    static string? RenameLine(FlowEnsure outcome) => outcome switch {
        { Retired: { } retired } when FlowEnsure.IsUp(outcome.Result) =>
            $"  [dim]Removed the service for the daemon's previous name, {Markup.Escape(retired)}.[/]",
        { Retired: { } retired } =>
            $"  [yellow]![/] Removed the service for the daemon's previous name, {Markup.Escape(retired)}, "
          + "but the new one did not start, so no daemon service is running on this machine.",
        { LeftInstalled: { } left } =>
            $"  [yellow]![/] The service for the daemon's previous name, {Markup.Escape(left)}, is still installed. "
          + $"Remove it with [cyan]kcap daemon service uninstall --name {Markup.Escape(left)}[/]",
        _ => null,
    };

    /// <summary>Answers a daemon-service request the browser left outstanding with what
    /// <see cref="EnsureAsync"/> did. Nothing outstanding reports nothing.</summary>
    public static async Task ReportAsync(
            IFirstRunFlowChannel        channel,
            string                      serverUrl,
            string                      flowId,
            FirstRunMachineActionResult result,
            CancellationToken           ct = default) {
        // Polled fresh rather than read off the leg's last view: the browser can still be open, so the
        // press can land after the leg returned.
        var poll = await channel.PollAsync(serverUrl, flowId, ct);

        if (poll.Body is not { } view) return;

        var outstanding = FirstRunFlowOutcomes.MachineActions(view)
            .FirstOrDefault(a => a.Capability == FirstRunMachineCapabilities.DaemonService);

        if (outstanding.Capability is null) return;

        for (var attempt = 0; attempt < ReportAttempts; attempt++) {
            var reported = await channel.ReportMachineActionAsync(
                serverUrl, flowId,
                new ReportFirstRunMachineActionRequest {
                    Capability  = outstanding.Capability,
                    RequestedAt = outstanding.RequestedAt,
                    Outcome     = result.Outcome,
                    Reason      = result.Reason
                },
                ct);

            if (reported.Recorded) break;
        }
    }
}
