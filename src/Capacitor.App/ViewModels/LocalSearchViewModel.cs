using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// How the local server searches, managed in Settings by running setup again.
/// An upgrade and a search change cannot run together, so each holds the other off.
/// </summary>
public sealed class LocalSearchViewModel : ReactiveObject, IDisposable {
    public const string BusyText = "Another local setup, upgrade or uninstall is running on this Mac. Try again once it has finished.";

    static readonly string[] Derived = [
        nameof(Running), nameof(StateText), nameof(CurrentText), nameof(CanApply), nameof(ApplyLabel), nameof(Embeddings),
        nameof(FailureText), nameof(ShowsSettingsLink), nameof(ShowsInlineSettings),
    ];

    readonly LocalSearchController _search;
    readonly LocalUpgradeController _upgrade;
    readonly Func<LocalEmbeddingSetting?> _readSetting;
    readonly Func<bool>? _builtinModelReady;
    readonly Func<CancellationToken, Task<LocalEmbeddingModelManifest?>> _builtinOffer;
    readonly Func<Task> _reconnect;
    readonly CancellationToken _shutdown;
    readonly IScheduler _readScheduler;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _read = new();
    readonly SerialDisposable _offer = new();
    LocalEmbeddingSetting? _current;
    bool _canOpenSettings;
    bool _loaded;
    bool _builtinNeedsRetry;

    /// <param name="readSetting">The installation's recorded embedding setting; a disk read, run on
    /// <paramref name="readScheduler"/> (the task pool by default), never on the UI thread.</param>
    /// <param name="builtinOffer">The built-in model set the installed stack offers, or null; asked again on every reconnect,
    /// which follows an upgrade.</param>
    /// <param name="openSettings">What opens Settings on its Search tab, null while no Settings window can open.</param>
    /// <param name="builtinModelReady">Validates installed model files whenever a built-in setting is read, off the UI thread.</param>
    public LocalSearchViewModel(
            Func<LocalEmbeddingSetting?> readSetting, Func<CancellationToken, Task<LocalEmbeddingModelManifest?>> builtinOffer,
            IObservable<ServerLaneStatus> status, LocalSearchController search,
            LocalUpgradeController upgrade, Func<Task> reconnect, CancellationToken shutdown, IScheduler? readScheduler = null,
            IObservable<Action?>? openSettings = null, Func<bool>? builtinModelReady = null) {
        _search        = search;
        _upgrade       = upgrade;
        _readSetting   = readSetting;
        _builtinModelReady = builtinModelReady;
        _builtinOffer  = builtinOffer;
        _reconnect     = reconnect;
        _shutdown      = shutdown;
        _readScheduler = readScheduler ?? RxSchedulers.TaskpoolScheduler;
        _read.DisposeWith(_disposables);
        _offer.DisposeWith(_disposables);

        Choice = new SearchChoiceViewModel { Reconfiguring = true };
        ReadSetting();
        LookUpOffer();

        Observable.Switch(search.WhenAnyValue(x => x.Progress)
                .Select(p => p.Embeddings.Changed.Select(_ => Unit.Default).StartWith(Unit.Default)))
            .Merge(search.WhenAnyValue(x => x.Running).Select(_ => Unit.Default))
            .Merge(upgrade.WhenAnyValue(x => x.Running).Select(_ => Unit.Default))
            .Merge(Choice.Changed.Select(_ => Unit.Default))
            .Subscribe(_ => Refresh())
            .DisposeWith(_disposables);

        search.Settled += OnSettled;
        Disposable.Create(() => search.Settled -= OnSettled).DisposeWith(_disposables);
        upgrade.Settled += OnUpgradeSettled;
        Disposable.Create(() => upgrade.Settled -= OnUpgradeSettled).DisposeWith(_disposables);

        // A setup run from a terminal restarts the server when it changes the model, so a reconnect rereads the setting.
        status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Skip(1)
            .Where(connected => connected)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => {
                ReadSetting();
                LookUpOffer();
            })
            .DisposeWith(_disposables);

        ApplyCommand        = ReactiveCommand.CreateFromTask(ApplyAsync);
        Action? open = null;
        var settings = openSettings ?? Observable.Return<Action?>(null);
        settings.Subscribe(action => {
            open             = action;
            _canOpenSettings = action is not null;
            Refresh();
        }).DisposeWith(_disposables);
        OpenSettingsCommand = ReactiveCommand.Create(() => open?.Invoke(), settings.Select(action => action is not null));
    }

    public SearchChoiceViewModel Choice { get; }

    public bool Running => _search.Running;

    public LocalEmbeddingProgress Embeddings => _search.Progress.Embeddings;

    /// The current search mode; null until the setting has been read.
    public string? StateText =>
        Running ? "setting up search"
        : !_loaded ? null
        : CurrentByMeaning && !BuiltinNeedsRetry ? "search by meaning" : "keyword search";

    public string? CurrentText => !_loaded ? null
        : BuiltinNeedsRetry ? "Search works by keyword until the built-in model is ready."
        : (_current switch {
            { Model: { } model }                         => $"Searches by meaning with {model}.",
            { Provider: LocalEmbeddingProvider.Builtin } => "Searches by meaning with the built-in model.",
            _                                            => "Searches by keyword unless the server's own settings choose a model.",
        })
        + (_current is { Pending: true } ? " The change is not applied yet." : "");

    public bool CanApply =>
        _loaded && !Running && !_upgrade.Running && Choice.Valid && !Choice.AwaitingOffer
     && (Differs(Choice.Choice) || _current is null or { Pending: true } || BuiltinNeedsRetry && Choice.UseBuiltin);

    public string ApplyLabel => BuiltinNeedsRetry && Choice.UseBuiltin ? "Try again" : "Save";

    public string? FailureText => !Running && _search.Outcome is LocalInstallOutcome.Failed failed
        ? failed.Affordance == LocalInstallAffordance.Busy ? BusyText : TerminalAdvice.Strip(failed.Message) ?? "Setup did not finish."
        : null;

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }

    /// Whether the Search tab can be opened in Settings.
    public bool ShowsSettingsLink => StateText is not null && _canOpenSettings;

    /// Without a profile, the support menu offers the search controls directly.
    public bool ShowsInlineSettings => StateText is not null && !_canOpenSettings;

    bool CurrentByMeaning => _current is { Model: not null } or { Provider: LocalEmbeddingProvider.Builtin };

    bool BuiltinNeedsRetry => _builtinNeedsRetry && _current is { Provider: LocalEmbeddingProvider.Builtin };

    bool Differs(LocalEmbeddingChoice choice) => choice.Provider switch {
        LocalEmbeddingProvider.Builtin => _current?.Provider != LocalEmbeddingProvider.Builtin,
        LocalEmbeddingProvider.Ollama  => _current?.Model != choice.Model,
        _                              => CurrentByMeaning,
    };

    Task ApplyAsync() => CanApply ? _search.StartAsync(Choice.Choice, _shutdown) : Task.CompletedTask;

    void OnSettled(LocalInstallOutcome outcome) {
        if (outcome is LocalInstallOutcome.Installed) {
            if (_search.Progress.Embeddings is { Configured: true } or { SkipCode: "declined" }) _builtinNeedsRetry = false;
            _ = ReconnectAsync();
        }
        ReadSetting();
        Refresh();
    }

    void OnUpgradeSettled(LocalUpgradeOutcome outcome) {
        if (outcome is not (LocalUpgradeOutcome.Upgraded or LocalUpgradeOutcome.Failed { Switched: true })) return;
        _builtinNeedsRetry = _upgrade.Progress.Embeddings.Skipped;
        ReadSetting();
        Refresh();
    }

    void ReadSetting() {
        _read.Disposable = Observable.Start(() => {
                var setting = _readSetting();
                var builtinNeedsRetry = setting is { Provider: LocalEmbeddingProvider.Builtin }
                    ? !_builtinModelReady?.Invoke() : false;
                return (setting, builtinNeedsRetry);
            }, _readScheduler)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => {
                var (setting, builtinNeedsRetry) = snapshot;
                if (builtinNeedsRetry is { } needsRetry) _builtinNeedsRetry = needsRetry;
                // An unchanged setting leaves a choice the user is still making alone.
                if (!_loaded || setting != _current) Choice.Prefill(setting);
                _current = setting;
                _loaded  = true;
                Refresh();
            }, ex => Console.Error.WriteLine($"kcap app: reading the local search setting failed: {ex.Message}"));
    }

    void LookUpOffer() =>
        _offer.Disposable = Observable.FromAsync(_builtinOffer)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Choice.OfferBuiltin, ex => {
                Console.Error.WriteLine($"kcap app: looking up the built-in search model failed: {ex.Message}");
                Choice.OfferBuiltin(null);
            });

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
