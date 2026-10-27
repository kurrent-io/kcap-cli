using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class HarnessSettingsRow : ReactiveObject {
    string? _message;
    string? _errorTitle;
    bool? _wired;
    internal HarnessSettingsRow(HarnessIdentity identity, DetectedAgent detected, bool? wired) {
        _wired = wired;
        Id = identity.Id;
        Label = identity.Label;
        Signal = detected.BinaryFound ? "Found on your terminal PATH" : "Harness configuration found";
    }
    public HarnessId Id { get; }
    public string Label { get; }
    public string Signal { get; }
    public string ConnectionLabel => _wired switch {
        true => "Connected", false => "Not connected", null => "Couldn't check",
    };
    public string ConnectionTip => _wired switch {
        true => "kcap records this tool's sessions to this profile",
        false => "kcap is not recording this tool",
        null => "kcap could not read this tool's settings to tell whether it is connected",
    };
    public bool IsNotConfigured => _wired == false;
    public bool ConnectionUnknown => _wired is null;
    internal bool? Wired => _wired;
    public bool IsConfigured => _wired == true;
    /// What activating the checkbox does now, for a screen reader.
    public string ToggleActionName => IsConfigured ? $"Disconnect {Label} from kcap" : $"Connect {Label} to kcap";
    internal void SetWired(bool? wired) {
        _wired = wired;
        this.RaisePropertyChanged(nameof(ConnectionLabel));
        this.RaisePropertyChanged(nameof(IsConfigured));
        this.RaisePropertyChanged(nameof(ToggleActionName));
        this.RaisePropertyChanged(nameof(ConnectionTip));
        this.RaisePropertyChanged(nameof(IsNotConfigured));
        this.RaisePropertyChanged(nameof(ConnectionUnknown));
    }
    public string? ConsentNote => Id == HarnessId.Codex ? HarnessRowViewModel.CodexConsentNote : null;
    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    /// Set when Message reports a failure, which then reads as that failure's detail.
    public string? ErrorTitle { get => _errorTitle; private set => this.RaiseAndSetIfChanged(ref _errorTitle, value); }

    internal void Report(string? message, string? errorTitle = null) {
        Message = message;
        ErrorTitle = errorTitle;
    }
}
