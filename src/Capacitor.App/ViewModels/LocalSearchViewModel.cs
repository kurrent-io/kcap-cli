using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// How the local server searches, beside its version in the rail, and a flyout that changes it by running
/// setup again. An upgrade and a search change cannot run together, so each holds the other off.
/// </summary>
public sealed class LocalSearchViewModel : ReactiveObject, IDisposable {
    public const string BusyText = "Another local setup, upgrade or uninstall is running on this Mac. Try again once it has finished.";

    static readonly string[] Derived = [
        nameof(Running), nameof(StateText), nameof(CurrentText), nameof(CanApply), nameof(ApplyLabel), nameof(Embeddings),
        nameof(FailureText),
    ];

    readonly LocalSearchController _search;
    readonly LocalUpgradeController _upgrade;
    readonly Func<LocalEmbeddingSetting?> _readSetting;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly IScheduler _readScheduler;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _read = new();
    LocalEmbeddingSetting? _current;
    bool _loaded;

    /// <param name="readSetting">The installation's recorded embedding setting; a disk read, run on
    /// <paramref name="readScheduler"/> (the task pool by default), never on the UI thread.</param>
    public LocalSearchViewModel(
            Func<LocalEmbeddingSetting?> readSetting, LocalSearchController search, LocalUpgradeController upgrade,
            Func<Task> reconnect, CancellationToken shutdown, IScheduler? readScheduler = null) {
        _search        = search;
        _upgrade       = upgrade;
        _readSetting   = readSetting;
        _reconnect     = reconnect;
        _shutdown      = shutdown;
        _readScheduler = readScheduler ?? RxSchedulers.TaskpoolScheduler;
        _read.DisposeWith(_disposables);

        Choice = new SearchChoiceViewModel();
        ReadSetting();

        Observable.Switch(search.WhenAnyValue(x => x.Progress)
                .Select(p => p.Embeddings.Changed.Select(_ => Unit.Default).StartWith(Unit.Default)))
            .Merge(search.WhenAnyValue(x => x.Running).Select(_ => Unit.Default))
            .Merge(upgrade.WhenAnyValue(x => x.Running).Select(_ => Unit.Default))
            .Merge(Choice.Changed.Select(_ => Unit.Default))
            .Subscribe(_ => Refresh())
            .DisposeWith(_disposables);

        search.Settled += OnSettled;
        Disposable.Create(() => search.Settled -= OnSettled).DisposeWith(_disposables);

        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync);
    }

    public SearchChoiceViewModel Choice { get; }

    public bool Running => _search.Running;

    public LocalEmbeddingProgress Embeddings => _search.Progress.Embeddings;

    /// The rail's word for it; null until the setting has been read.
    public string? StateText =>
        Running ? "setting up search"
        : !_loaded ? null
        : _current?.Model is not null ? "search by meaning" : "keyword search";

    public string? CurrentText => !_loaded ? null
        : (_current?.Model is { } model ? $"Searches by meaning with {model}." : "Searches by keyword.")
        + (_current is { Pending: true } ? " The change is not applied yet." : "");

    public bool CanApply =>
        _loaded && !Running && !_upgrade.Running && Choice.Valid
     && (Choice.Choice.Model != CurrentModel || _current is null or { Pending: true });

    public string ApplyLabel => Choice.Choice.Model switch {
        null                                  => "Use keyword search",
        _ when CurrentModel is null           => "Turn on search by meaning",
        _                                     => "Use this model",
    };

    public string? FailureText => !Running && _search.Outcome is LocalInstallOutcome.Failed failed
        ? failed.Affordance == LocalInstallAffordance.Busy ? BusyText : TerminalAdvice.Strip(failed.Message) ?? "Setup did not finish."
        : null;

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }

    string? CurrentModel => _current?.Model;

    Task ApplyAsync() => CanApply ? _search.StartAsync(Choice.Choice, _shutdown) : Task.CompletedTask;

    void OnSettled(LocalInstallOutcome outcome) {
        if (outcome is LocalInstallOutcome.Installed) _ = ReconnectAsync();
        ReadSetting();
        Refresh();
    }

    void ReadSetting() =>
        _read.Disposable = Observable.Start(_readSetting, _readScheduler)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(setting => {
                _current = setting;
                _loaded  = true;
                Choice.Prefill(setting);
                Refresh();
            }, ex => Console.Error.WriteLine($"kcap app: reading the local search setting failed: {ex.Message}"));

    // A changed model restarts the server; the lane would find it on its own backoff.
    async Task ReconnectAsync() {
        try {
            await _reconnect();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: reconnecting to the local server failed: {ex.Message}");
        }
    }

    void Refresh() {
        foreach (var name in Derived) this.RaisePropertyChanged(name);
    }

    public void Dispose() => _disposables.Dispose();
}
