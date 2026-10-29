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
    string? _details;
    bool _isWarning;
    bool? _wired;
    bool _tools;
    bool _setupUnverified;

    internal HarnessSettingsRow(HarnessIdentity identity, DetectedAgent detected, bool? wired, bool tools, bool setupUnverified = false) {
        _wired = wired;
        _tools = tools;
        _setupUnverified = setupUnverified;
        Id = identity.Id;
        Label = identity.Label;
        Signal = detected.BinaryFound ? "Installed on this computer"
            : detected.DesktopAppFound ? "Its desktop app or its app data is on this computer"
            : "Its settings folder is on this computer";
        ConsentNote = Id == HarnessId.Codex
            ? "Also lets Codex's sandbox reach the servers of every kcap profile, so its skills can connect. Disconnecting Codex keeps this access."
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
    public bool SetupUnverified => _setupUnverified;
    public bool NeedsAttention => ConnectionUnknown || SetupUnverified;
    public bool ShowRecordingStatus => IsConfigured && !SetupUnverified;
    /// Tools installed for a tool kcap does not record.
    public bool ToolsOnly => !SetupUnverified && _wired == false && _tools && OffersTools;
    public bool HasStatus => IsConfigured || NeedsAttention || ToolsOnly;

    /// Whether kcap's tools are installed, as far as Settings last set them.
    public bool Tools => ToolsBundled ? IsConfigured : _tools;
    public bool? ToolsSelection => SetupUnverified ? null : Tools;

    public string? ConnectionLabel => SetupUnverified ? "Setup unverified" : _wired switch {
        true => "Recording", null => "Could not check", _ => ToolsOnly ? "Tools only" : null,
    };
    public string? ConnectionTip => SetupUnverified
        ? "The last change could not be verified. Agent tools may be missing. Try the change again."
        : _wired switch {
        true => "kcap records this tool's sessions. Each one goes to the profile set for its repository",
        null => "kcap could not read this tool's settings to tell whether it records",
        _ => ToolsOnly ? "The agent has kcap's tools, but its sessions are not recorded" : null,
    };

    public string RecordLabel => ToolsBundled ? "Session recording and agent tools" : "Session recording";
    public string RecordName => $"Session recording for {Label}";
    public string RecordTip => ToolsBundled
        ? $"kcap records {Label}'s sessions and gives it kcap's agent tools. {Label} installs the two together. The tools let the agent search your recorded sessions and use kcap from its chat."
        : $"kcap records each {Label} session through hooks in its settings.";
    public string ToolsName => $"Agent tools for {Label}";

    public const string ToolsTip =
        "kcap's MCP servers and skills. They let the agent search your recorded sessions and use kcap from its chat.";

    internal void SetWired(bool? wired) {
        _wired = wired;
        Raise();
    }

    internal void SetTools(bool tools) {
        _tools = tools;
        Raise();
    }

    internal void SetSetupUnverified(bool unverified) {
        _setupUnverified = unverified;
        Raise();
    }

    void Raise() {
        this.RaisePropertyChanged(nameof(IsConfigured));
        this.RaisePropertyChanged(nameof(ConnectionUnknown));
        this.RaisePropertyChanged(nameof(SetupUnverified));
        this.RaisePropertyChanged(nameof(NeedsAttention));
        this.RaisePropertyChanged(nameof(ShowRecordingStatus));
        this.RaisePropertyChanged(nameof(ToolsOnly));
        this.RaisePropertyChanged(nameof(HasStatus));
        this.RaisePropertyChanged(nameof(Tools));
        this.RaisePropertyChanged(nameof(ToolsSelection));
        this.RaisePropertyChanged(nameof(ConnectionLabel));
        this.RaisePropertyChanged(nameof(ConnectionTip));
    }

    public string? Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    /// Set when Message reports a failure, which then reads as that failure's detail.
    public string? ErrorTitle { get => _errorTitle; private set => this.RaiseAndSetIfChanged(ref _errorTitle, value); }

    /// The command line's own output, kept behind Show details: it names files and errors a reader rarely needs.
    public string? Details { get => _details; private set => this.RaiseAndSetIfChanged(ref _details, value); }
    /// The callout reports a change that finished with warnings rather than one that failed.
    public bool IsWarning { get => _isWarning; private set => this.RaiseAndSetIfChanged(ref _isWarning, value); }

    internal void Report(string? message, string? errorTitle = null, string? details = null, bool warning = false) {
        Message = message;
        ErrorTitle = errorTitle;
        Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        IsWarning = warning;
    }
}
