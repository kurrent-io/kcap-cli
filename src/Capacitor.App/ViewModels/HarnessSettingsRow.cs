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
        true => "Connected · sessions are recorded", false => "Not connected", null => "Could not check the connection",
    };
    internal bool? Wired => _wired;
    public bool IsConfigured => _wired == true;
    internal void SetWired(bool? wired) {
        _wired = wired;
        this.RaisePropertyChanged(nameof(ConnectionLabel));
        this.RaisePropertyChanged(nameof(IsConfigured));
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
