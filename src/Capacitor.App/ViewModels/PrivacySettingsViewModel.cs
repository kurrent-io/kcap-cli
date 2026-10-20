using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Settings' Privacy tab: whether this device sends kcap usage data. The switch writes the flag
/// `kcap config set telemetry` writes, so the app and the CLI share it; an environment variable outranks
/// it, and the tab says which.
public sealed class PrivacySettingsViewModel : ReactiveObject {
    internal const string TakesEffectOnRelaunch = "Usage data starts again the next time the app opens.";

    readonly CliTelemetry    _telemetry;
    readonly ConfigRoot      _config;
    readonly UsageDataNotice _notice;
    bool    _send;
    string? _hint;

    public PrivacySettingsViewModel(CliTelemetry telemetry, ConfigRoot config, UsageDataNotice notice) {
        _telemetry = telemetry;
        _config    = config;
        _notice    = notice;

        var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config));
        _send      = decision.Enabled;
        Overridden = decision.Reason is "KCAP_TELEMETRY" or "DO_NOT_TRACK"
            ? $"{decision.Reason} is set in the app's environment, which decides this instead."
            : null;
    }

    public string Description => UsageDataNotice.Text;

    /// Why the switch cannot change anything, or null when it can.
    public string? Overridden { get; }

    public bool CanChange => Overridden is null;

    public string? Hint {
        get => _hint;
        private set => this.RaiseAndSetIfChanged(ref _hint, value);
    }

    public bool SendUsageData {
        get => _send;
        set {
            if (!CanChange || value == _send) return;

            this.RaiseAndSetIfChanged(ref _send, value);

            if (value) {
                // This process's facade is already off; a live one is only built at startup.
                TelemetryState.SetEnabled(true, _config);
                Hint = TakesEffectOnRelaunch;
            } else {
                UsageDataNotice.TurnOff(_telemetry, _config, _notice);
                Hint = null;
            }
        }
    }
}
