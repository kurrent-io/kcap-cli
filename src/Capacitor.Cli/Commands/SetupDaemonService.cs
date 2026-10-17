using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;

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

    /// <param name="ladder">Test seam for the ensure ladder; production resolves the real service
    /// manager. Null return means the platform has no service manager at all.</param>
    public static async Task<FirstRunMachineActionResult> EnsureAsync(
            ConfigRoot                      root,
            ProfileContext                  saved,
            UserHome                        home,
            TimeProvider                    time,
            Func<Task<ServiceEnsureJson?>>? ladder = null) {
        try {
            var run = ladder ?? (() => DaemonServiceCommands.FlowEnsureAsync(root, saved, home, time));

            return await run() is { } outcome
                ? EnsureFlowMap.Map(outcome)
                : new FirstRunMachineActionResult(
                      FirstRunMachineActionOutcomes.Refused,
                      FirstRunMachineActionReasons.UnsupportedPlatform);
        } catch (Exception) {
            // Something was attempted, so this is a failure rather than a refusal.
            return new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Failed, null);
        }
    }

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
