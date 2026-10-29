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
        "kcap collects pseudonymous usage data, such as which screens and features you use and how setup goes. "
      + "It never includes file paths, prompts or transcript content. It can be linked to your workspace and its creator.";

    public const string NotSaved =
        "Usage data is off until the app quits. The choice could not be saved, so kcap commands still send it. The app sends it again the next time it opens.";

    readonly CliTelemetry _telemetry;
    readonly ConfigRoot   _config;
    bool   _visible;
    string _message = Text;

    public UsageDataNotice(CliTelemetry telemetry, ConfigRoot config) {
        _telemetry = telemetry;
        _config    = config;
        _visible   = telemetry.NoticeDue(config);
        // An environment variable outranks the saved choice, so a Turn off here would not last.
        CanTurnOff = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config)).Reason is not ("KCAP_TELEMETRY" or "DO_NOT_TRACK");
        AcknowledgeCommand = ReactiveCommand.Create(Acknowledge);
        TurnOffCommand     = ReactiveCommand.Create(() => { TurnOff(); }, Observable.Return(CanTurnOff));
    }

    public bool Visible {
        get => _visible;
        private set => this.RaiseAndSetIfChanged(ref _visible, value);
    }

    public string Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool CanTurnOff { get; }

    public ReactiveCommand<Unit, Unit> AcknowledgeCommand { get; }
    public ReactiveCommand<Unit, Unit> TurnOffCommand { get; }

    /// Raised whenever usage data is turned off, from here or from Settings, so an open Settings switch follows.
    public event Action? TurnedOff;

    public void Hide() => Visible = false;

    public void Acknowledge() {
        _telemetry.AcknowledgeNotice(_config);
        Hide();
    }

    /// <returns>Whether the opt-out was saved.</returns>
    public bool TurnOff() => TurnOff(_telemetry, _config, this);

    /// Stops this process first, whose queue the saved flag alone would not stop, then saves the opt-out
    /// every kcap process reads. An unsaved one keeps the notice up, saying so.
    /// <returns>Whether the opt-out was saved.</returns>
    internal static bool TurnOff(CliTelemetry telemetry, ConfigRoot config, UsageDataNotice notice) {
        telemetry.DiscardAndDisable();
        var saved = TelemetryState.SetEnabled(false, config);

        if (saved) notice.Hide();
        else notice.Message = NotSaved;

        notice.TurnedOff?.Invoke();

        return saved;
    }
}
