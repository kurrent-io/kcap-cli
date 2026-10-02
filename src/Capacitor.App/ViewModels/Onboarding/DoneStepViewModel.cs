using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The last page: what this machine is set up to do, said by its outcome rather than as a checklist.
/// The headline follows what actually happened; while the history import runs, its figures fill in
/// here and the page can be left at any time — the import carries on in the main window.
/// </summary>
public sealed class DoneStepViewModel : ReactiveObject, IWizardStep {
    readonly Func<DoneFacts> _facts;
    readonly IUrlOpener?     _opener;

    DoneFacts _current = DoneFacts.Empty;

    public DoneStepViewModel(Func<DoneFacts> facts, IUrlOpener? opener = null) {
        _facts  = facts;
        _opener = opener;

        OpenWorkspaceCommand = ReactiveCommand.Create(OpenWorkspace);
    }

    public WizardStepId Id         => WizardStepId.Done;
    public string       Eyebrow    => "Ready";
    public bool         Applicable => true;
    public bool         Satisfied  => true; // a summary step is never itself incomplete
    public string?      NextLabel  => "Open Capacitor";

    HistoryImportRun? Import => _current.Import;

    bool Imported => Import is { Imported: > 0 } || Import is { Running: true };

    bool CapturePromised => _current.Recording.Count > 0 && !_current.PathHazard;

    public string Title =>
        Imported          ? "Your work so far"
        : CapturePromised ? "From now on, your agents remember"
        :                   "Nothing is being recorded yet";

    public string Lede =>
        Import is null           ? $"Capacitor keeps what your coding agents do on {_current.MachineName}, and gives them tools to search it back — so the next time you hit something, what you already worked out is there to find."
        : Import.Running         ? "What has landed is already searchable, and the rest is still uploading in the background."
        : Import.Imported > 0    ? "Your agents can search all of it — ask one what you have already tried, and it looks here first."
        :                          "Nothing has landed yet. It may still be uploading — this fills in on its own.";

    public bool ShowsFigures => Import is not null;

    public bool Running => Import is { Running: true };

    public string LandedValue => Import?.Imported.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    /// "/ 324" when the selection's count is an honest denominator, "sessions" otherwise.
    public string LandedUnit => Import is { Expected: > 0 } run && run.Imported <= run.Expected
        ? $"/ {run.Expected.ToString("N0", CultureInfo.CurrentCulture)}"
        : "sessions";

    public string LandedCaption => Import?.WindowCaption ?? "";

    public string RepositoriesValue => Import?.Repositories.ToString(CultureInfo.CurrentCulture) ?? "—";

    public string RepositoriesCaption => Running ? "keeps uploading after you open Capacitor" : "imported from this machine";

    public string RepositoriesUnit => Import is { Repositories: 1 } ? "repository" : "repositories";

    /// The landed count only moves when a pass ends, so the bar shows passes rather than inventing sessions.
    public double ProgressValue => Import is { Passes: > 0 } run ? 100.0 * run.PassesDone / run.Passes : 0;

    public bool FailedVisible => Import is { Running: false, Failed: > 0 };

    public string FailedTitle => Import is { Failed: 1 } ? "1 session failed to upload" : $"{Import?.Failed ?? 0} sessions failed to upload";

    public string FailedBody =>
        $"{(Import is { Failed: 1 } ? "It is" : "They are")} still on {_current.MachineName}. Run kcap import to try again.";

    public bool NothingToCountVisible => Import is null && CapturePromised;

    public string? FooterLine =>
        _current.Recording.Count == 0 ? "Turn on a harness in Settings to start recording."
        : Import is null              ? "kcap import brings your existing history over whenever you want."
        :                               null;

    public IReadOnlyList<CaptureItem> Capture => [.. _current.Recording.Select(label => new CaptureItem(label, _current.PathHazard))];

    public bool CaptureVisible => _current.Recording.Count > 0;

    public bool PathHazardVisible => _current.PathHazard && _current.Recording.Count > 0;

    public bool CodexNoteVisible => _current.CodexRecording;

    public string DaemonLine => _current.DaemonRunning
        ? $"Running as a service on {_current.MachineName}. It is reachable now, and after a restart."
        : "The daemon is not running yet. Turn it on from Settings.";

    public bool WorkspaceVisible => !string.IsNullOrEmpty(_current.WorkspaceUrl);

    public ReactiveCommand<Unit, Unit> OpenWorkspaceCommand { get; }

    /// Re-read on every entry: Back-then-forward can change what the earlier pages did.
    public Task OnEnterAsync(CancellationToken ct) {
        if (_current.Import is INotifyPropertyChanged before) before.PropertyChanged -= OnImportChanged;
        _current = _facts();
        if (_current.Import is INotifyPropertyChanged after) after.PropertyChanged += OnImportChanged;
        Restate();

        return Task.CompletedTask;
    }

    // Next finishes the wizard — the shell's own last-step handling raises CloseRequested.
    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);

    void OnImportChanged(object? sender, PropertyChangedEventArgs e) => Restate();

    void OpenWorkspace() {
        if (_current.WorkspaceUrl is not { Length: > 0 } url || _opener is null) return;
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open the workspace: {ex.Message}");
        }
    }

    void Restate() => this.RaisePropertyChanged(string.Empty);
}
