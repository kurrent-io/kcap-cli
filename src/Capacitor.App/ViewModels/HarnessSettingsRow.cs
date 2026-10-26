using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class HarnessSettingsRow : ReactiveObject {
    string? _message;
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
        true => "Recording configured", false => "Recording not configured", null => "Recording configuration unknown",
    };
    internal bool? Wired => _wired;
    public bool IsConfigured => _wired == true;
    internal void SetWired(bool? wired) {
        _wired = wired;
        this.RaisePropertyChanged(nameof(ConnectionLabel));
        this.RaisePropertyChanged(nameof(IsConfigured));
    }
    public string? ConsentNote => Id == HarnessId.Codex ? HarnessRowViewModel.CodexConsentNote : null;
    public string? Message { get => _message; internal set => this.RaiseAndSetIfChanged(ref _message, value); }
}
