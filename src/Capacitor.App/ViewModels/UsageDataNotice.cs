using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The one-time disclosure of the app's usage data, shown until the user acknowledges it here or a kcap
/// command has already shown it on this device. One instance serves every window that shows it.
public sealed class UsageDataNotice : ReactiveObject {
    public const string Text =
        "kcap collects pseudonymous usage data: which screens and features you use and how setup goes, never "
      + "file paths, prompts or transcript content. It can be associated with your workspace and its creator.";

    bool _visible;

    public UsageDataNotice(CliTelemetry telemetry, ConfigRoot config) {
        _visible   = telemetry.NoticeDue(config);
        // An environment variable outranks the saved choice, so a Turn off here would not last.
        CanTurnOff = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config)).Reason is not ("KCAP_TELEMETRY" or "DO_NOT_TRACK");
        AcknowledgeCommand = ReactiveCommand.Create(() => {
            telemetry.AcknowledgeNotice(config);
            Hide();
        });
        TurnOffCommand = ReactiveCommand.Create(() => TurnOff(telemetry, config, this), Observable.Return(CanTurnOff));
    }

    public bool Visible {
        get => _visible;
        private set => this.RaiseAndSetIfChanged(ref _visible, value);
    }

    public string Message => Text;

    public bool CanTurnOff { get; }

    public ReactiveCommand<Unit, Unit> AcknowledgeCommand { get; }
    public ReactiveCommand<Unit, Unit> TurnOffCommand { get; }

    public void Hide() => Visible = false;

    /// Persists the opt-out every kcap process reads, and drops what this process has queued, which the
    /// persisted flag alone would not stop.
    internal static void TurnOff(CliTelemetry telemetry, ConfigRoot config, UsageDataNotice notice) {
        TelemetryState.SetEnabled(false, config);
        telemetry.DiscardAndDisable();
        notice.Hide();
    }
}
