using System.Globalization;
using System.ComponentModel;
using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

public enum HistoryState { Looking, NoHistory, OnlyUnmatched, Unreadable, Repos }

/// <summary>
/// Which harnesses' and repositories' past sessions to upload, and how far back. Discovery runs on
/// entry over every harness on this machine, whichever of them the Harnesses page turned recording on
/// for: importing history and recording from now on are separate choices. Next starts the import and
/// moves on at once, because the import outlives the page. The page leads with what it found and what
/// it will import, and keeps the per-repository choices behind Customize.
/// </summary>
public sealed class HistoryStepViewModel : ReactiveObject, IWizardStep {
    internal const string DocsUrl = "https://github.com/kurrent-io/kcap-cli#configuration";

    readonly IKcapCli       _cli;
    readonly Action<Action> _post;
    readonly TimeProvider   _time;
    readonly Func<bool>     _onLocalPath;
    readonly IImportIntelligence? _intelligence;

    readonly Dictionary<string, HistoryRepoRow> _lastRows = new(StringComparer.OrdinalIgnoreCase);

    bool                     _discovered;
    bool                     _discoveredLocal;
    HistoryState             _state = HistoryState.Looking;
    bool                     _titleLocally;
    bool                     _summariseOutcomes = true;
    bool                     _customizing;
    int                      _unmatched;
    ImportDiscoveryReport?   _report;
    HistoryImportRun?        _run;
    CancellationTokenSource? _discovery;

    public HistoryStepViewModel(
            IKcapCli cli, Action<Action> post, string machineName, TimeProvider time, Func<bool>? onLocalPath = null,
            IImportIntelligence? intelligence = null) {
        _cli          = cli;
        _post         = post;
        _time         = time;
        _onLocalPath  = onLocalPath ?? (() => false);
        _intelligence = intelligence;
        MachineName = machineName;

        ToggleCustomizeCommand = ReactiveCommand.Create(() => { Customizing = !Customizing; });
    }

    public WizardStepId Id         => WizardStepId.Import;
    public string       Title      => "Bring your history with you";
    public string       Eyebrow    => "Your history";
    public bool         Applicable => true;
    public string       SkipLabel  => "Not now";

    /// The local promise holds only while nothing names or summarises the imports: each sends excerpts to a model provider.
    public string Lede => _onLocalPath()
        ? "kcap imports the sessions your harnesses have already written, so your past work is searchable " +
          (Model switch {
              { Mode: IntelligenceModes.Provider, Vendor: var vendor } =>
                  $"from day one. Titles and outcomes send excerpts to {VendorLabel(vendor)}; everything else stays on this machine.",
              { Mode: IntelligenceModes.LocalAgent } =>
                  "from day one. Titles and outcomes run on your own agent, which sends excerpts to its model provider; everything else stays on this machine.",
              _ when Naming =>
                  "from day one. Naming sends excerpts to your agent's model provider; everything else stays on this machine.",
              _ => "from day one. Everything stays on this machine.",
          })
        : "kcap uploads the sessions your harnesses have already written, so your past work is searchable " +
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
            this.RaisePropertyChanged(nameof(SummariseOffered));
            this.RaisePropertyChanged(nameof(PaceLine));
            this.RaisePropertyChanged(nameof(DeclineVisible));
            this.RaisePropertyChanged(nameof(DeclineNote));
            this.RaisePropertyChanged(nameof(CloudDeclineVisible));
            this.RaisePropertyChanged(nameof(LocalDeclineVisible));
            this.RaisePropertyChanged(nameof(DetailsVisible));
            this.RaisePropertyChanged(nameof(FoldersVisible));
            this.RaisePropertyChanged(nameof(SourcesVisible));
            Restate();
        }
    }

    public bool Looking  => State == HistoryState.Looking;
    public bool HasRepos => State == HistoryState.Repos;

    /// The one line an empty arm says instead of a list.
    public string? EmptyMessage => _onLocalPath() ? LocalEmptyMessage : State switch {
        HistoryState.NoHistory =>
            $"No history on {MachineName} to import. New sessions will be stored as they happen.",
        HistoryState.OnlyUnmatched =>
            $"Found {SessionCount(_unmatched)} on {MachineName}, but none matched a repository. Run kcap remap, then kcap import.",
        HistoryState.Unreadable =>
            $"Could not read history on {MachineName}. Try kcap import --discover, then kcap import.",
        _ => null,
    };

    string? LocalEmptyMessage => State switch {
        HistoryState.NoHistory =>
            $"No history on {MachineName} to import. New sessions will be stored as they happen.",
        HistoryState.OnlyUnmatched =>
            $"Found {SessionCount(_unmatched)} on {MachineName}, but none matched a repository. Those sessions stay on {ThisDevice.Name}.",
        HistoryState.Unreadable =>
            $"Could not read history on {MachineName}. Import it later from {SetupLocation.Path}.",
        _ => null,
    };

    /// The local edition has nobody to share with, so its track is skip or import.
    public bool LocalLevels => _discoveredLocal;
    public bool CloudLevels => !_discoveredLocal;

    public string LevelsNote => _discoveredLocal
        ? $"Checked repositories import into kcap on {ThisDevice.Name}"
        : "Checked repositories import with your recording privacy setting. Only me keeps a repository's history private";

    /// The harnesses discovery found repository history from, each one its own choice. Empty when the CLI's report
    /// does not split repositories by harness; the import then reads every harness it counted.
    public IReadOnlyList<HistorySourceOption> Sources { get; private set; } = [];

    public bool SourcesVisible => HasRepos && Sources.Count > 0;

    public IReadOnlyList<HistoryOwnerGroup> Groups { get; private set; } = [];

    readonly List<HistoryWindowOption> _windows = [];

    public IReadOnlyList<HistoryWindowOption> Windows => _windows;

    public string? UnmatchedNote =>
        !HasRepos || _unmatched == 0 ? null
        : _onLocalPath()             ? $"{SessionCount(_unmatched)} on disk don't match a repository. Those sessions stay on {ThisDevice.Name}{FoldersOffered}."
        :                              $"{SessionCount(_unmatched)} on disk don't match a repository. Use kcap remap to link them{FoldersOffered}.";

    string FoldersOffered => Folders.Count > 0 ? ", or check their folders under Customize" : "";

    /// The folders behind the unmatched sessions that can be named, each its own choice apart from the
    /// repositories. Sessions with no folder recorded cannot be imported from here.
    public IReadOnlyList<HistoryFolderRow> Folders { get; private set; } = [];

    public bool FoldersVisible => DetailsVisible && Folders.Count > 0;

    public int SelectedFolderCount => IncludedFolders.Count();

    /// A folder pass names no harness of its own: with every harness unchecked it would take no vendor
    /// flag, which the CLI reads as every harness. A folder none of the checked harnesses wrote to has
    /// nothing to import.
    IEnumerable<HistoryFolderRow> IncludedFolders =>
        Sources.Count > 0 && !Sources.Any(s => s.IsSelected) ? []
        : Folders.Where(f => f.Included && (f.Vendors is null || Share(f.Vendors, Selected) is not null));

    /// Unchecking is a choice about this import, never a setting: recording and the exclusions already
    /// saved go on exactly as before.
    public const string SelectionNote =
        "Unchecking leaves a project out of this import only, and changes nothing about what records from now on. " +
        "A repository in excluded_repos, or a path you ignored, stays out whatever is checked.";

    /// Off until asked for: most people import everything found, so the per-repository choices wait behind Customize.
    public bool Customizing {
        get => _customizing;
        private set {
            this.RaiseAndSetIfChanged(ref _customizing, value);
            this.RaisePropertyChanged(nameof(DetailsVisible));
            this.RaisePropertyChanged(nameof(FoldersVisible));
            this.RaisePropertyChanged(nameof(CustomizeLabel));
        }
    }

    public bool DetailsVisible => HasRepos && Customizing;

    public string CustomizeLabel => Customizing ? "Hide options" : "Customize";

    public ReactiveCommand<Unit, Unit> ToggleCustomizeCommand { get; }

    /// What discovery found, before any choice narrows it.
    public string? FoundLine {
        get {
            var repos = _report?.Repos.Select(r => Project(r, _ => true)).OfType<ImportDiscoveryRepo>().ToList() ?? [];
            if (repos.Count == 0) return null;

            return $"Found {SessionCount(repos.Sum(r => r.Sessions))} in {RepositoryCount(repos.Count)} on {MachineName}";
        }
    }

    /// What Next will import, following the harnesses, repositories and window chosen.
    public string? ImportLine {
        get {
            var total = Groups.Sum(g => g.Repos.Count);
            if (!HasRepos || _windows.Count == 0) return null;
            if (SelectedCount == 0 && SelectedFolderCount == 0) return "Nothing is selected to import";

            var repos = SelectedCount == 0 ? null
                : total == 1             ? "the one repository"
                : SelectedCount == total ? $"all {total.ToString("N0", CultureInfo.CurrentCulture)} repositories"
                :                          $"{SelectedCount.ToString("N0", CultureInfo.CurrentCulture)} of {RepositoryCount(total)}";
            var folders = SelectedFolderCount == 0 ? null : FolderCount(SelectedFolderCount);
            var from    = repos is not null && folders is not null ? $"{repos} and {folders}" : repos ?? folders;
            var window = SelectedWindow;
            var span   = window.Since is null ? "your whole history" : $"the {window.Label.ToLowerInvariant()}";

            return CountFor(window) is { } count
                ? $"kcap imports {count} from {from}, covering {span}"
                : $"kcap imports {span} from {from}";
        }
    }

    /// Who can read a Shared import, said outside Customize because Shared is the cloud default. Once the
    /// import starts the choices are locked, so it stops pointing at them.
    public string? SharingLine =>
        _discoveredLocal || !Groups.SelectMany(g => g.Repos).Any(r => r.Level == ImportLevel.Shared)
            && !Folders.Any(f => f.Level == ImportLevel.Shared) ? null
        : ImportStarted ? "Shared imports follow your recording privacy setting"
        :                 "Shared imports follow your recording privacy setting. Use Customize to keep a repository to Only me";

    /// Titling runs your own agent, so it is a choice only where a harness that titles is imported from, and only while
    /// the server does not title them itself.
    public bool TitlingOffered => HasRepos && _intelligence?.Applied?.ResolvesTitles != true
        && (Sources.Count == 0 || Sources.Any(s => s is { IsSelected: true, Id: HarnessId.Claude or HarnessId.Codex }));

    /// The answer the background intelligence page got, when a model now runs on the server.
    IntelligenceSetupResponse? Model => _intelligence?.Applied is { Mode: not IntelligenceModes.None } applied ? applied : null;

    public bool SummariseOffered => HasRepos && Model is not null;

    /// On until the user turns it off: the server's own default for the local edition.
    public bool SummariseOutcomes {
        get => _summariseOutcomes;
        set => this.RaiseAndSetIfChanged(ref _summariseOutcomes, value);
    }

    /// How long the server's background work on the selected sessions takes at its daily pace.
    public string? PaceLine {
        get {
            if (Model is not { } model || !HasRepos || _windows.Count == 0) return null;

            var counts = SelectedCounts(SelectedWindow.Since);
            if (counts.Count == 0 || counts.Any(c => c is null)) return null;

            var sessions = counts.Sum(c => c!.Value);

            return model.DaysFor(sessions) is { } days and > 0
                ? $"The server works through {SessionCount(sessions)} at up to {model.BacklogSessionTouchesPerDay.ToString("N0", CultureInfo.CurrentCulture)} a day: about {days} day{(days == 1 ? "" : "s")}"
                : null;
        }
    }

    static string VendorLabel(string? vendor) => IntelligenceVendor.All.FirstOrDefault(v => v.Id == vendor)?.Label ?? vendor ?? "your provider";

    /// Off until the user turns it on: naming sends each session's opening excerpts to whichever model
    /// provider their claude or codex uses, which nothing else on this page does.
    public bool TitleLocally {
        get => _titleLocally;
        set {
            this.RaiseAndSetIfChanged(ref _titleLocally, value);
            this.RaisePropertyChanged(nameof(Lede));
        }
    }

    public const string TitlingDisclosure =
        "Your own claude or codex writes each title from the session's first message and reply, so those " +
        "excerpts go to the model provider it uses, on your quota. Unnamed sessions are just as searchable.";

    bool Naming => TitlingOffered && TitleLocally;

    public int SelectedCount => Groups.SelectMany(g => g.Repos).Count(r => r.Level != ImportLevel.Skip);

    public string? NextLabel =>
        Looking                        ? "Looking for history…"
        : Run is { State: ImportRunState.Running or ImportRunState.Finished } ? "Continue"
        : !HasRepos || SelectedCount + SelectedFolderCount == 0 ? "Continue"
        : SelectedFolderCount > 0       ? (SelectedCount + SelectedFolderCount == 1 ? "Import 1 project" : $"Import {SelectedCount + SelectedFolderCount} projects")
        : SelectedCount == 1            ? "Import 1 repository"
        :                                 $"Import {SelectedCount} repositories";

    public bool Skippable => !ImportStarted && (HasRepos || Looking);
    public bool CanContinue => !Looking;

    public bool DeclineVisible => HasRepos;

    public const string CloudDeclineNote =
        "Not now means kcap starts its history from today. Nothing is deleted either way, and you can widen the window later with kcap import.";

    public static string LocalDeclineNote { get; } =
        $"Not now means kcap starts its history from today. Nothing is deleted either way, and you can import more later from {SetupLocation.Path}.";

    public string? DeclineNote => !DeclineVisible ? null : _onLocalPath() ? LocalDeclineNote : CloudDeclineNote;

    // The cloud note names its command in the monospace font, so the view writes that one itself.
    public bool CloudDeclineVisible => DeclineVisible && !_onLocalPath();
    public bool LocalDeclineVisible => DeclineVisible && _onLocalPath();
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
        this.RaisePropertyChanged(nameof(SharingLine));
        this.RaisePropertyChanged(nameof(Skippable));
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(Satisfied));
    }

    HistoryWindowOption SelectedWindow => _windows.FirstOrDefault(w => w.IsSelected) ?? _windows[1];

    public Task OnEnterAsync(CancellationToken ct) {
        // The list the user may already have adjusted stands. A discovery that could not read the disk
        // is asked again.
        if (_discovered && _discoveredLocal == _onLocalPath() && State != HistoryState.Unreadable) {
            // The intelligence page's answer may have changed since.
            Restate();

            return Task.CompletedTask;
        }

        _discovery?.Cancel();
        _discovery?.Dispose();
        _discovery = null;
        _report    = null;
        Groups     = [];
        _lastRows.Clear();
        Sources    = [];
        Folders    = [];
        _windows.Clear();
        _unmatched = 0;
        this.RaisePropertyChanged(nameof(Groups));
        this.RaisePropertyChanged(nameof(Sources));
        this.RaisePropertyChanged(nameof(Folders));
        this.RaisePropertyChanged(nameof(FoldersVisible));
        this.RaisePropertyChanged(nameof(Windows));
        this.RaisePropertyChanged(nameof(UnmatchedNote));
        _discovered      = true;
        _discoveredLocal = _onLocalPath();
        this.RaisePropertyChanged(nameof(LocalLevels));
        this.RaisePropertyChanged(nameof(CloudLevels));
        this.RaisePropertyChanged(nameof(LevelsNote));

        State = HistoryState.Looking;
        var cts = _discovery = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Discovery = DiscoverAsync(cts.Token);

        return Task.CompletedTask;
    }

    internal Task Discovery { get; private set; } = Task.CompletedTask;

    async Task DiscoverAsync(CancellationToken ct) {
        ImportDiscoveryReport? report;
        try {
            // No vendor flag: every harness with history, not only the ones recording from now on.
            report = await _cli.ImportDiscoverAsync([], ct).ConfigureAwait(false);
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

        _report    = report;
        _unmatched = report.UnmatchedSessions;

        // Registry order. A vendor id this build does not know has no flag to import it with.
        Sources = [.. report.Repos
            .SelectMany(r => r.Vendors ?? [])
            .Select(v => (Id: HarnessId.From(v.Vendor), v.Sessions))
            .Where(v => v is { Id: not null, Sessions: > 0 })
            .GroupBy(v => v.Id!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new HistorySourceOption(g.Key, g.Sum(v => v.Sessions)))];
        foreach (var source in Sources) {
            source.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(HistorySourceOption.IsSelected)) Regroup();
            };
        }
        this.RaisePropertyChanged(nameof(Sources));

        Folders = [.. (report.UnmatchedFolders ?? []).Select(Offer).OfType<HistoryFolderRow>()];
        foreach (var folder in Folders) {
            folder.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(HistoryFolderRow.Level)) Restate();
            };
        }
        this.RaisePropertyChanged(nameof(Folders));

        Regroup();
        BuildWindows(report);
        State = Groups.Count > 0 ? HistoryState.Repos
            : _unmatched > 0     ? HistoryState.OnlyUnmatched
            :                      HistoryState.NoHistory;
        this.RaisePropertyChanged(nameof(UnmatchedNote));
    }

    /// A folder as the offered harnesses see it, or null when none of them has a session there. The import
    /// names only the offered harnesses' flags, so one with no checkbox is neither counted nor imported.
    HistoryFolderRow? Offer(ImportDiscoveryFolder folder) {
        if (folder is not { Folder: { } path, Sessions: > 0 }) return null;
        if (Sources.Count == 0 || folder.Vendors is null or { Count: 0 })
            return new HistoryFolderRow(path, folder.Sessions, folder.Windows, null, _discoveredLocal);

        return Share(folder.Vendors, id => Sources.Any(s => s.Id == id)) is { } offered
            ? new HistoryFolderRow(path, offered.Sessions, offered.Windows, folder.Vendors, _discoveredLocal)
            : null;
    }

    bool Selected(HarnessId id) => Sources.Any(s => s.Id == id && s.IsSelected);

    /// The repositories the selected harnesses wrote to. A repository keeps the choices made on it, even
    /// across its harness being unchecked and checked again.
    void Regroup() {
        if (_report is not { } report) return;

        var rows = new List<HistoryRepoRow>();
        foreach (var repo in report.Repos) {
            if (Project(repo, Selected) is not { } projected) continue;

            var row = new HistoryRepoRow(projected, _discoveredLocal);
            if (_lastRows.TryGetValue(row.Slug, out var previous)) row.Adopt(previous);
            _lastRows[row.Slug] = row;
            row.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(HistoryRepoRow.Level)) Restate();
            };
            rows.Add(row);
        }

        // Owners by their most recent repository, repositories in the report's own order.
        Groups = [.. rows
            .GroupBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(r => r.LastSessionAt ?? DateTimeOffset.MinValue))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HistoryOwnerGroup(g.Key, [.. g]))];
        this.RaisePropertyChanged(nameof(Groups));
        Restate();
    }

    /// A repository as the included harnesses see it, or null when none of them has a session there.
    /// A repository the report does not split by harness counts every harness.
    static ImportDiscoveryRepo? Project(ImportDiscoveryRepo repo, Func<HarnessId, bool> included) {
        if (repo.Vendors is null or { Count: 0 }) return repo;
        if (Share(repo.Vendors, included) is not { } share) return null;

        return repo with {
            Sessions      = share.Sessions,
            LastSessionAt = share.LastSessionAt,
            Windows       = share.Windows,
        };
    }

    /// The included harnesses' part of a split, or null when none of them has a session there.
    static (int Sessions, DateTimeOffset? LastSessionAt, IReadOnlyList<ImportDiscoveryWindow>? Windows)? Share(
            IReadOnlyList<ImportDiscoveryVendor> vendors, Func<HarnessId, bool> included) {
        var cells    = vendors.Where(v => HarnessId.From(v.Vendor) is { } id && included(id)).ToList();
        var sessions = cells.Sum(v => v.Sessions);
        if (sessions == 0) return null;

        // A window one harness did not report leaves the sum unknown rather than short.
        var first   = cells[0].Windows ?? [];
        var windows = cells.All(c => c.Windows is { } own && own.Select(w => w.Since).SequenceEqual(first.Select(w => w.Since)))
            ? first.Select((w, i) => new ImportDiscoveryWindow(w.Since, cells.Sum(c => c.Windows![i].Sessions))).ToList()
            : null;

        return (sessions, cells.Max(c => c.LastSessionAt), windows);
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

    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction == WizardNavigation.Next && Looking) return false;
        // One import at a time; one that failed or was stopped can be started again.
        if (direction == WizardNavigation.Next && HasRepos && SelectedCount + SelectedFolderCount > 0
            && Run is not { State: ImportRunState.Running or ImportRunState.Finished }) {
            // Before the first session lands, so the server applies the choice to all of them.
            if (Model is { } model && model.ImportOutcomes != SummariseOutcomes) await SendSummariseAsync(ct).ConfigureAwait(true);

            StartImport();
        }

        return true;
    }

    /// A failed send leaves the server's own choice in place; the import goes ahead either way.
    async Task SendSummariseAsync(CancellationToken ct) {
        try {
            if (await _intelligence!.ApplyImportOutcomesAsync(SummariseOutcomes, ct).ConfigureAwait(true) is not IntelligenceSetupOutcome.Applied)
                Console.Error.WriteLine("kcap: the server did not take the import-outcome choice");
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: could not send the import-outcome choice: {ex.Message}");
        }
    }

    void StartImport() {
        var selected = Groups.SelectMany(g => g.Repos).Where(r => r.Level != ImportLevel.Skip).ToList();
        var window   = SelectedWindow;
        // Only the harnesses left checked, or no flag at all when the report did not name them.
        var vendors  = Sources.Where(s => s.IsSelected).Select(s => s.Id.Flag).ToList();
        var skip     = !Naming;

        List<ImportRequest> passes = [];
        foreach (var level in new[] { ImportLevel.OnlyMe, ImportLevel.Shared }) {
            var repos   = selected.Where(r => r.Level == level).Select(r => r.Slug).ToList();
            var folders = IncludedFolders.Where(f => f.Level == level).Select(f => f.Path).ToList();
            if (repos.Count == 0 && folders.Count == 0) continue;
            passes.Add(new ImportRequest(ImportScopeChoice.Repo, null, vendors, repos, window.Since,
                Private: level == ImportLevel.OnlyMe, SkipTitle: skip, Folders: folders.Count == 0 ? null : folders));
        }

        var counts   = SelectedCounts(window.Since);
        var expected = counts.Any(c => c is null) ? 0 : counts.Sum(c => c!.Value);
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
        this.RaisePropertyChanged(nameof(SelectedFolderCount));
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(FoundLine));
        this.RaisePropertyChanged(nameof(ImportLine));
        this.RaisePropertyChanged(nameof(SharingLine));
        this.RaisePropertyChanged(nameof(TitlingOffered));
        this.RaisePropertyChanged(nameof(SummariseOffered));
        this.RaisePropertyChanged(nameof(PaceLine));
        this.RaisePropertyChanged(nameof(Lede));
    }

    string? CountFor(HistoryWindowOption window) {
        var counts = SelectedCounts(window.Since);
        if (counts.Count == 0) return null;

        return counts.Any(c => c is null) ? null : SessionCount(counts.Sum(c => c!.Value));
    }

    /// Each checked repository's and folder's sessions in the window, null where unknown.
    List<int?> SelectedCounts(DateOnly? since) => [
        .. Groups.SelectMany(g => g.Repos).Where(r => r.Level != ImportLevel.Skip).Select(r => r.SessionsSince(since)),
        .. IncludedFolders.Select(f => FolderSessionsSince(f, since)),
    ];

    /// A folder the report did not split by harness may hold one the import's flags leave out, so its
    /// count is unknown whenever the page offers harnesses at all.
    int? FolderSessionsSince(HistoryFolderRow folder, DateOnly? since) =>
        folder.Vendors is { } vendors ? Share(vendors, Selected)?.Windows?.FirstOrDefault(w => w.Since == since)?.Sessions
        : Sources.Count == 0          ? folder.SessionsSince(since)
        :                               null;

    static string FolderCount(int count) => count == 1 ? "1 folder" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} folders";

    internal static string SessionCount(int count) => count == 1 ? "1 session" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} sessions";

    static string RepositoryCount(int count) => count == 1 ? "1 repository" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} repositories";
}
