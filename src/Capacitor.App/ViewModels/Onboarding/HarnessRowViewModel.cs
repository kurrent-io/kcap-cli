using System.Reactive;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One detected harness on the Harnesses page: whether to record it, whether to give it the tools,
/// and how its install went.
public sealed class HarnessRowViewModel : ReactiveObject {
    internal const string CodexConsentNote = "Also opens Codex's sandbox network to your server, so its skills can reach it.";

    bool               _record;
    bool               _tools;
    AgentInstallStatus _status = AgentInstallStatus.NotRun;
    string?            _message;

    internal HarnessRowViewModel(
            AgentVendor vendor, DetectedAgent detected, bool declined, Func<HarnessRowViewModel, Task> retry,
            IObservable<bool> canRetry) {
        Id       = vendor.Id;
        Label    = vendor.Label;
        Flag     = vendor.Flag;
        Declined = declined;
        // The launch signal wins: "on your PATH" is the stronger claim when both fired.
        Signal   = detected.BinaryFound ? "on your PATH" : "config found";

        _record = !declined;
        _tools  = !declined;

        RetryCommand = ReactiveCommand.CreateFromTask(() => retry(this), canRetry);
    }

    internal HarnessId Id   { get; }
    internal string?   Flag { get; }

    public string Label    { get; }
    public string Signal   { get; }
    public bool   Declined { get; }

    public string SignalLine => Declined ? $"{Signal} · you turned this down before" : Signal;

    /// Claude Code and Codex install their tools with capture; the two cannot be answered apart.
    public bool ToolsBundled => Id == HarnessId.Claude || Id == HarnessId.Codex;

    public string? ConsentNote => Id == HarnessId.Codex ? CodexConsentNote : null;

    public bool Record {
        get => _record;
        set {
            this.RaiseAndSetIfChanged(ref _record, value);
            if (ToolsBundled) Tools = value;
            this.RaisePropertyChanged(nameof(Selected));
        }
    }

    public bool Tools {
        get => _tools;
        set {
            this.RaiseAndSetIfChanged(ref _tools, value);
            this.RaisePropertyChanged(nameof(Selected));
        }
    }

    public bool Selected => Record || Tools;

    public AgentInstallStatus Status {
        get => _status;
        internal set {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(Failed));
            this.RaisePropertyChanged(nameof(Succeeded));
            this.RaisePropertyChanged(nameof(Installing));
        }
    }

    public string? Message {
        get => _message;
        internal set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool Failed     => Status == AgentInstallStatus.Failed;
    public bool Succeeded  => Status == AgentInstallStatus.Succeeded;
    public bool Installing => Status == AgentInstallStatus.Installing;

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    /// What `plugin install` takes after the vendor flag for this row's answer. Tools are the MCP
    /// servers and what steers the agent to them, so declining them skips all three where a vendor
    /// installs them.
    internal IReadOnlyList<string> InstallOptions =>
        ToolsBundled         ? []
        : Record && !Tools   ? [.. ToolParts.Select(part => $"--skip-{Id.VendorId}-{part}")]
        : !Record && Tools   ? ["--tools-only"]
        :                      [];

    // Cursor and Kiro carry no instructions block of their own.
    IEnumerable<string> ToolParts =>
        Id == HarnessId.Cursor || Id == HarnessId.Kiro ? ["mcp", "skills"] : ["mcp", "skills", "instructions"];
}
