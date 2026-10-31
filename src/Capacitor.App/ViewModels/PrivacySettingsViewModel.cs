using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Settings' Privacy tab: whether this device sends kcap usage data. The switch writes the flag
/// `kcap config set telemetry` writes, so the app and the CLI share it; an environment variable outranks
/// it, and the tab says which.
public sealed class PrivacySettingsViewModel : ReactiveObject, IDisposable {
    internal const string TakesEffectOnRelaunch = "Usage data starts again the next time the app opens.";
    internal const string CouldNotSave          = "Could not save this choice.";

    readonly CliTelemetry    _telemetry;
    readonly ConfigRoot      _config;
    readonly UsageDataNotice _notice;
    bool    _send;
    string? _hint;

    public PrivacySettingsViewModel(CliTelemetry telemetry, ConfigRoot config, UsageDataNotice notice) {
        _telemetry = telemetry;
        _config    = config;
        _notice    = notice;
        _notice.TurnedOff += OnTurnedOff;

        // A choice that cannot be read sends nothing, so the switch shows it off; no choice reads as the edition says.
        var decision = TelemetrySettings.Resolve(TelemetryState.TryRead(config) is { } saved ? saved.Enabled : false, telemetry.Unsaved);
        _send      = decision.Enabled;
        Overridden = decision.Reason is "KCAP_TELEMETRY" or "DO_NOT_TRACK"
            ? $"{decision.Reason} is set where kcap was started, so it decides this. Unset it and relaunch kcap to choose here."
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

            if (value) {
                // A facade that is off stays off until the app opens again; only one still waiting for a choice resumes now.
                var saved = TelemetryState.SetEnabled(true, _config);
                // Raised either way: the switch already moved, and an unsaved choice must move it back.
                if (saved) {
                    _send = true;
                    _telemetry.OptedIn(_config);
                }
                this.RaisePropertyChanged();
                Hint = saved ? TakesEffectOnRelaunch : CouldNotSave;
            } else {
                this.RaiseAndSetIfChanged(ref _send, false);
                Hint = UsageDataNotice.TurnOff(_telemetry, _config, _notice) ? null : UsageDataNotice.NotSaved;
            }
        }
    }

    void OnTurnedOff() => this.RaiseAndSetIfChanged(ref _send, false, nameof(SendUsageData));

    public void Dispose() => _notice.TurnedOff -= OnTurnedOff;
}
