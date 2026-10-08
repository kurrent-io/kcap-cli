using System.Globalization;
using System.ComponentModel;
using Capacitor.App.Services;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public enum HistoryState { Looking, NoHarness, NoHistory, OnlyUnmatched, Unreadable, Repos }

/// <summary>
/// Which repositories' past sessions to upload, how far each goes, and how far back. Discovery
/// runs on entry for the harnesses the Harnesses page turned recording on for; Next starts the
/// import and moves on at once, because the import outlives the page.
/// </summary>
public sealed class HistoryStepViewModel : ReactiveObject, IWizardStep {
    internal const string DocsUrl = "https://github.com/kurrent-io/kcap-cli#configuration";

    readonly IKcapCli                          _cli;
    readonly Func<IReadOnlyList<HarnessId>>    _inScope;
    readonly Action<Action>                    _post;
    readonly TimeProvider                      _time;

    IReadOnlyList<HarnessId>? _discoveredFor;
    HistoryState              _state = HistoryState.Looking;
    bool                      _titleLocally = true;
    int                       _unmatched;
    HistoryImportRun?         _run;
    CancellationTokenSource?  _discovery;

    public HistoryStepViewModel(
            IKcapCli cli, Func<IReadOnlyList<HarnessId>> inScope, Action<Action> post, string machineName, TimeProvider time) {
        _cli        = cli;
        _inScope    = inScope;
        _post       = post;
        _time       = time;
        MachineName = machineName;
    }

    public WizardStepId Id         => WizardStepId.Import;
    public string       Title      => "Bring your history with you";
    public string       Eyebrow    => "Your history";
    public bool         Applicable => true;
    public string       SkipLabel  => "Not now";

    public string Lede =>
        "Capacitor uploads the transcripts your harnesses have already written, so your past work is searchable " +
        "from day one. Nothing leaves this machine until you choose.";

    public string MachineName { get; }

    public bool Satisfied => _run is { State: ImportRunState.Finished };

    public HistoryState State {
        get => _state;
        private set {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(Looking));
            this.RaisePropertyChanged(nameof(HasRepos));
            this.RaisePropertyChanged(nameof(EmptyMessage));
            this.RaisePropertyChanged(nameof(Skippable));
            this.RaisePropertyChanged(nameof(CanContinue));
            this.RaisePropertyChanged(nameof(TitlingOffered));
            Restate();
        }
    }

    public bool Looking  => State == HistoryState.Looking;
    public bool HasRepos => State == HistoryState.Repos;

    /// The one line an empty arm says instead of a list.
    public string? EmptyMessage => State switch {
        HistoryState.NoHarness =>
            "No recording connections were added in this setup. Go back to connect a harness, or use kcap import in your terminal to import history later.",
        HistoryState.NoHistory =>
            $"There is no history on {MachineName} to bring over, so there is nothing to do here. Everything from now on is recorded as it happens.",
        HistoryState.OnlyUnmatched =>
            $"We found {SessionCount(_unmatched)} on {MachineName}, but could not match any of them to a repository — so there is nothing here to choose between. kcap remap in your terminal links them up, and then kcap import will bring them in.",
        HistoryState.Unreadable =>
            $"We could not read the history on {MachineName}. Run kcap import --discover in your terminal to see why, and kcap import to bring it over later.",
        _ => null,
    };

    /// "from ✓ Claude Code ✓ Codex": the harnesses this page reads from.
    public IReadOnlyList<string> FromLabels { get; private set; } = [];

    public IReadOnlyList<HistoryOwnerGroup> Groups { get; private set; } = [];

    readonly List<HistoryWindowOption> _windows = [];

    public IReadOnlyList<HistoryWindowOption> Windows => _windows;

    public string? UnmatchedNote =>
        HasRepos && _unmatched > 0 ? $"{SessionCount(_unmatched)} on disk don't match a repository. Use kcap remap to link them." : null;

    /// Titling runs your own agent, so it is a choice only where a harness that titles is in scope.
    public bool TitlingOffered => HasRepos && FromHarnesses.Any(h => h is HarnessId.Claude or HarnessId.Codex);

    public bool TitleLocally {
        get => _titleLocally;
        set => this.RaiseAndSetIfChanged(ref _titleLocally, value);
    }

    public int SelectedCount => Groups.SelectMany(g => g.Repos).Count(r => r.Level != ImportLevel.Skip);

    public string? NextLabel =>
        Looking                        ? "Looking for history…"
        : Run is { State: ImportRunState.Running or ImportRunState.Finished } ? "Continue"
        : !HasRepos || SelectedCount == 0 ? "Continue"
        : SelectedCount == 1            ? "Import 1 repository"
        :                                 $"Import {SelectedCount} repositories";

    public bool Skippable => !ImportStarted && (HasRepos || Looking);
    public bool CanContinue => !Looking;

    public bool DeclineVisible => HasRepos;
    public bool ImportStarted => Run is { State: ImportRunState.Running or ImportRunState.Finished };

    /// The import this page started, for the pages after it to report on.
    public HistoryImportRun? Run {
        get => _run;
        private set {
            if (_run is not null) _run.PropertyChanged -= OnRunChanged;
            this.RaiseAndSetIfChanged(ref _run, value);
            if (_run is not null) _run.PropertyChanged += OnRunChanged;
            OnRunChanged(this, new PropertyChangedEventArgs(nameof(HistoryImportRun.State)));
        }
    }

    void OnRunChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(HistoryImportRun.State)) return;
        this.RaisePropertyChanged(nameof(ImportStarted));
        this.RaisePropertyChanged(nameof(Skippable));
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(Satisfied));
    }

    IReadOnlyList<HarnessId> FromHarnesses { get; set; } = [];

    HistoryWindowOption SelectedWindow => _windows.FirstOrDefault(w => w.IsSelected) ?? _windows[1];

    public Task OnEnterAsync(CancellationToken ct) {
        var scope = _inScope();
        // The same harnesses as last time: the list the user may already have adjusted stands. A
        // discovery that could not read the disk is asked again.
        if (_discoveredFor is not null && _discoveredFor.SequenceEqual(scope) && State != HistoryState.Unreadable)
            return Task.CompletedTask;

        _discovery?.Cancel();
        _discovery?.Dispose();
        _discovery = null;
        Groups = [];
        _windows.Clear();
        _unmatched = 0;
        this.RaisePropertyChanged(nameof(Groups));
        this.RaisePropertyChanged(nameof(Windows));
        this.RaisePropertyChanged(nameof(UnmatchedNote));
        _discoveredFor = scope;
        FromHarnesses  = scope;
        FromLabels     = [.. scope.Select(HarnessRegistry.LabelOf)];
        this.RaisePropertyChanged(nameof(FromLabels));
        this.RaisePropertyChanged(nameof(TitlingOffered));

        if (scope.Count == 0) {
            State = HistoryState.NoHarness;
            return Task.CompletedTask;
        }

        State = HistoryState.Looking;
        var cts = _discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Discovery = DiscoverAsync(scope, cts.Token);

        return Task.CompletedTask;
    }

    internal Task Discovery { get; private set; } = Task.CompletedTask;

    async Task DiscoverAsync(IReadOnlyList<HarnessId> scope, CancellationToken ct) {
        ImportDiscoveryReport? report;
        try {
            report = await _cli.ImportDiscoverAsync([.. scope.Select(h => h.Flag)], ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: import discovery failed: {ex.Message}");
            report = null;
        }

        _post(() => {
            if (ct.IsCancellationRequested) return;
            Apply(report);
        });
    }

    void Apply(ImportDiscoveryReport? report) {
        if (report is null) {
            State = HistoryState.Unreadable;
            return;
        }

        _unmatched = report.UnmatchedSessions;

        // Owners by their most recent repository, repositories in the report's own order.
        Groups = [.. report.Repos
            .Select(r => new HistoryRepoRow(r))
            .GroupBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(r => r.LastSessionAt ?? DateTimeOffset.MinValue))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HistoryOwnerGroup(g.Key, [.. g]))];

        foreach (var repo in Groups.SelectMany(g => g.Repos)) {
            repo.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(HistoryRepoRow.Level)) Restate();
            };
        }

        BuildWindows(report);
        this.RaisePropertyChanged(nameof(Groups));
        State = Groups.Count > 0 ? HistoryState.Repos
            : _unmatched > 0     ? HistoryState.OnlyUnmatched
            :                      HistoryState.NoHistory;
        this.RaisePropertyChanged(nameof(UnmatchedNote));
    }

    void BuildWindows(ImportDiscoveryReport report) {
        if (_windows.Count == 0) {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var keys  = FirstRunImportWindows.All;
            for (var i = 0; i < keys.Count; i++) {
                var key   = keys[i];
                var label = key switch {
                    FirstRunImportWindows.Last30 => "Last 30 days",
                    FirstRunImportWindows.Last90 => "Last 90 days",
                    _                            => "Everything",
                };
                // The report's own boundaries, which the CLI drew in the same order: a repository's
                // counts are keyed by them, and they are what --since will select.
                var since  = report.Windows.Count == keys.Count ? report.Windows[i].Since : FirstRunImportWindows.Since(key, today);
                var option = new HistoryWindowOption(key, label, since) { IsSelected = key == FirstRunImportWindows.Last90 };
                option.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(HistoryWindowOption.IsSelected)) Restate();
                };
                _windows.Add(option);
            }
        }

        Restate();
    }

    public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction == WizardNavigation.Next && Looking) return Task.FromResult(false);
        // One import at a time; one that failed or was stopped can be started again.
        if (direction == WizardNavigation.Next && HasRepos && SelectedCount > 0
            && Run is not { State: ImportRunState.Running or ImportRunState.Finished }) StartImport();

        return Task.FromResult(true);
    }

    void StartImport() {
        var selected = Groups.SelectMany(g => g.Repos).Where(r => r.Level != ImportLevel.Skip).ToList();
        var window   = SelectedWindow;
        var vendors  = FromHarnesses.Select(h => h.Flag).ToList();
        var skip     = !(TitlingOffered && TitleLocally);

        List<ImportRequest> passes = [];
        foreach (var level in new[] { ImportLevel.OnlyMe, ImportLevel.Shared }) {
            var repos = selected.Where(r => r.Level == level).Select(r => r.Slug).ToList();
            if (repos.Count == 0) continue;
            passes.Add(new ImportRequest(ImportScopeChoice.Repo, null, vendors, repos, window.Since,
                Private: level == ImportLevel.OnlyMe, SkipTitle: skip));
        }

        var expected = selected.Sum(r => r.SessionsSince(window.Since) ?? 0);
        var caption  = window.Since is null ? "from your whole history" : $"from the {window.Label.ToLowerInvariant()}";
        var run      = new HistoryImportRun(expected, selected.Count, caption, passes.Count, _post);
        Run = run;
        run.Start(_cli, passes);
        this.RaisePropertyChanged(nameof(Satisfied));
    }

    /// Shutdown's half of the run's lifetime: nothing else stops an import once it has started.
    public async Task CancelActiveRunAsync() {
        _discovery?.Cancel();
        if (Run is { } run) await run.CancelAsync().ConfigureAwait(false);
    }

    void Restate() {
        foreach (var window in Windows) window.CountLabel = CountFor(window);
        this.RaisePropertyChanged(nameof(SelectedCount));
        this.RaisePropertyChanged(nameof(NextLabel));
    }

    string? CountFor(HistoryWindowOption window) {
        var selected = Groups.SelectMany(g => g.Repos).Where(r => r.Level != ImportLevel.Skip).ToList();
        if (selected.Count == 0) return null;

        var counts = selected.Select(r => r.SessionsSince(window.Since)).ToList();

        return counts.Any(c => c is null) ? null : SessionCount(counts.Sum(c => c!.Value));
    }

    internal static string SessionCount(int count) => count == 1 ? "1 session" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} sessions";
}
