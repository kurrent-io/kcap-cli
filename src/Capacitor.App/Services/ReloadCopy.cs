using System.Collections.Frozen;

namespace Capacitor.App.Services;

/// The failure line under the rail's Reload button. Built from the outcome kind and token, naming the
/// daemon and the terminal command, and never a detail the outcome does not carry.
public static class ReloadCopy {
    static readonly FrozenSet<string> PriorityClass =
        new[] { "background_band", "spawn_type_unknown", "deferred", "contended", "unverified" }.ToFrozenSet(StringComparer.Ordinal);

    static readonly FrozenSet<string> EvidenceLegs = new[] {
        "stale_txn_marker", "running_without_daemon_pid", "daemon_running_outside_service", "ownership_mismatch",
        "ownership_unknown", "instance_pid_mismatch", "instance_changed_during_classification", "server_or_name_mismatch",
        "pre_slice_evidence", "identity_inconsistent", "missing_capability_consent_3", "daemon_below_floor",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// A positive passive read clears only a failure whose sole complaint was the priority state or a
    /// transient inability to act.
    public static bool ResolvedByPositivePriority(string token) => PriorityClass.Contains(token);

    public static string For(ReloadState state) {
        var name  = state.DaemonName;
        var check = $"Check `kcap daemon status --name {name}`";
        var force = $"`kcap daemon service refresh --name {name} --force`";

        if (state.Kind == ReloadOutcomeKind.Unconfirmed)
            return $"The daemon reload is not yet confirmed — check `kcap daemon status --name {name}`.";

        if (state.Kind == ReloadOutcomeKind.Refused) {
            return state.Token switch {
                "reload_unsupported" => "This kcap CLI cannot reload the daemon service. Update kcap, then press Reload again.",
                "cli_below_floor"    => "This kcap is too old for this app. Update kcap, then press Reload again.",
                _ => App.AttentionCopyFor(state.Token) ?? $"The daemon reload for {name} did not succeed ({state.Token}). {check}; details are in the app log.",
            };
        }

        if (state.Kind is ReloadOutcomeKind.Skew or ReloadOutcomeKind.Repair) {
            if (state.Token == "background_band")
                return $"The daemon still runs at background priority after the reload. Run {force} from a terminal and check `kcap daemon status`.";
            if (state.Token == "spawn_type_unknown")
                return $"The reload finished but the daemon's priority could not be confirmed. {check}.";
            if (EvidenceLegs.Contains(state.Token))
                return $"The daemon service could not be verified ({state.Token}). {check}.";
        }

        var refresh = state.Token switch {
            "deferred"         => $"The daemon did not accept the restart. Run {force} from a terminal.",
            "contended"        => $"Another service operation is in progress for {name}. Try Reload again shortly, or run {force}.",
            "not_loaded"       => $"The daemon service for {name} is not loaded. Run `kcap daemon service start --name {name}`.",
            "unverified"       => $"launchd's state for {name} could not be verified. {check}.",
            "unit_missing"     => $"No service unit is installed for {name}. Run `kcap daemon service install --name {name}`.",
            "unit_unreadable"  => $"The service unit for {name} cannot be read. {check}.",
            "unit_unsupported" => $"The service unit for {name} cannot be brought to Standard. Reinstall it with `kcap daemon service install --replace --verify --name {name}`.",
            "failed"           => $"The daemon reload for {name} failed. {check}; details are in the app log.",
            _                  => null,
        };
        if (refresh is not null) return refresh;

        return state.ExitCode is { } exit
            ? $"The daemon reload for {name} failed (exit {exit}). {check}; details are in the app log."
            : $"The daemon reload for {name} did not succeed ({state.Token}). {check}; details are in the app log.";
    }
}
