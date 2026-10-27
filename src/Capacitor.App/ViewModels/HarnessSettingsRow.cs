using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One coding tool as Settings shows it. What kcap set up for it is the computer's, not a profile's:
/// the hooks it installs carry no profile, and each session picks one when it starts.
public sealed class HarnessSettingsRow : ReactiveObject {
    string? _message;
    string? _errorTitle;
    bool? _wired;
    bool _tools;

    internal HarnessSettingsRow(HarnessIdentity identity, DetectedAgent detected, bool? wired, bool tools, string? server = null) {
        _wired = wired;
        _tools = tools;
        Id = identity.Id;
        Label = identity.Label;
        Signal = detected.BinaryFound ? "Found on your terminal PATH" : "Harness configuration found";
        ConsentNote = Id == HarnessId.Codex
            ? $"Also opens Codex's sandbox network to {(string.IsNullOrEmpty(server) ? "this app's server" : server)}, the server of the profile this app uses, so its skills can reach it."
            : null;
    }

    public HarnessId Id { get; }
    public string Label { get; }
    public string Signal { get; }
    public string? ConsentNote { get; }

    /// Claude Code and Codex install their tools with recording, so one choice covers both.
    public bool ToolsBundled => HarnessInstallOptions.ToolsBundled(Id);
    /// Grok loads the tools set up for Claude Code; kcap installs none for it.
    public bool OffersTools => !ToolsBundled && Id != HarnessId.Grok;
    public bool RecordOnly => !ToolsBundled && !OffersTools;

    internal bool? Wired => _wired;
    public bool IsConfigured => _wired == true;
    public bool ConnectionUnknown => _wired is null;
    /// Tools installed for a tool kcap does not record.
    public bool ToolsOnly => _wired == false && _tools && OffersTools;
    public bool HasStatus => IsConfigured || ConnectionUnknown || ToolsOnly;

    /// Whether kcap's tools are installed, as far as Settings last set them.
    public bool Tools => ToolsBundled ? IsConfigured : _tools;

    public string? ConnectionLabel => _wired switch {
        true => "Recording", null => "Couldn't check", _ => ToolsOnly ? "Tools only" : null,
    };
    public string? ConnectionTip => _wired switch {
        true => "kcap records this tool's sessions; each one goes to the profile its repository resolves to",
        null => "kcap could not read this tool's settings to tell whether it records",
        _ => ToolsOnly ? "The agent has kcap's tools, but its sessions are not recorded" : null,
    };

    public string RecordLabel => ToolsBundled ? "Session recording and agent tools" : "Session recording";
    public string RecordName => $"Session recording for {Label}";
    public string RecordTip => ToolsBundled
        ? $"kcap records {Label}'s sessions and gives it kcap's agent tools; {Label} installs the two together. The tools let the agent search your recorded sessions and use kcap from its chat."
        : $"kcap records each {Label} session through hooks in its settings.";
    public string ToolsName => $"Agent tools for {Label}";

    public const string ToolsTip =
        "kcap's MCP servers and skills: they let the agent search your recorded sessions and use kcap from its chat.";

    internal void SetWired(bool? wired) {
        _wired = wired;
        Raise();
    }

    internal void SetTools(bool tools) {
        _tools = tools;
        Raise();
    }

    void Raise() {
        this.RaisePropertyChanged(nameof(IsConfigured));
        this.RaisePropertyChanged(nameof(ConnectionUnknown));
        this.RaisePropertyChanged(nameof(ToolsOnly));
        this.RaisePropertyChanged(nameof(HasStatus));
        this.RaisePropertyChanged(nameof(Tools));
        this.RaisePropertyChanged(nameof(ConnectionLabel));
        this.RaisePropertyChanged(nameof(ConnectionTip));
    }

    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    /// Set when Message reports a failure, which then reads as that failure's detail.
    public string? ErrorTitle { get => _errorTitle; private set => this.RaiseAndSetIfChanged(ref _errorTitle, value); }

    internal void Report(string? message, string? errorTitle = null) {
        Message = message;
        ErrorTitle = errorTitle;
    }
}
