using System.ComponentModel;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The local edition's question of who runs the server's background model calls. The local agents offered are the
/// harnesses ticked on the Harnesses page that the machine can run headless, by a connected daemon's own list, else a
/// probe of this machine. Next posts the answer through the CLI and holds the page on a refusal or an unreachable
/// server; not now stores nothing, so coming back through setup never clears an earlier answer.
/// </summary>
public sealed class IntelligenceStepViewModel : ReactiveObject, IWizardStep, IImportIntelligence {
    public const string Explanation =
        "kcap titles your sessions, says what each one achieved and runs evals in the background. Choose what runs those " +
        "model calls: your own coding agent on this machine, or a provider with your API key.";

    readonly IKcapCli                                    _cli;
    readonly Action<Action>                              _post;
    readonly Func<bool>                                  _onLocalPath;
    readonly Func<IReadOnlyList<HarnessId>>              _ticked;
    readonly Func<CancellationToken, Task<string[]>>     _headless;

    IReadOnlyList<IntelligenceOption> _options = [];
    IntelligenceVendor                _vendor  = IntelligenceVendor.Anthropic;
    string                            _model   = IntelligenceVendor.Anthropic.DefaultModel;
    string                            _apiKey  = "";
    string?                           _error;
    bool                              _busy;
    bool                              _loading;
    IntelligenceSetupResponse?        _applied;
    (IntelligenceAnswer Answer, string? Model, string? Key)? _sent;

    public IntelligenceStepViewModel(
            IKcapCli cli, Action<Action> post, Func<bool> onLocalPath, Func<IReadOnlyList<HarnessId>> ticked,
            Func<CancellationToken, Task<string[]>> headless) {
        _cli         = cli;
        _post        = post;
        _onLocalPath = onLocalPath;
        _ticked      = ticked;
        _headless    = headless;
    }

    public WizardStepId Id          => WizardStepId.Intelligence;
    public string       Title       => "Who runs background intelligence?";
    public string       Eyebrow     => "Background intelligence";
    public string?      Lede        => Explanation;
    public bool         Applicable  => true;
    public bool         PassThrough => !_onLocalPath();
    public string       SkipLabel   => "Not now";
    public string?      NextLabel   => Busy ? "Checking…" : null;

    public bool Satisfied => Applied is not null;

    /// <summary>What the server applied, for the Import page; null when nothing was.</summary>
    public IntelligenceSetupResponse? Applied {
        get => _applied;
        private set {
            this.RaiseAndSetIfChanged(ref _applied, value);
            this.RaisePropertyChanged(nameof(Satisfied));
        }
    }

    public IReadOnlyList<IntelligenceOption> Options {
        get => _options;
        private set => this.RaiseAndSetIfChanged(ref _options, value);
    }

    /// <summary>The page's harness read, done once it completes.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public bool IsLoading {
        get => _loading;
        private set {
            this.RaiseAndSetIfChanged(ref _loading, value);
            this.RaisePropertyChanged(nameof(CanContinue));
        }
    }

    IntelligenceOption? Selected => Options.FirstOrDefault(o => o.IsSelected);

    public bool ProviderSelected => Selected?.Token == IntelligenceOption.ProviderToken;

    public IReadOnlyList<IntelligenceVendor> Vendors => IntelligenceVendor.All;

    public IntelligenceVendor SelectedVendor {
        get => _vendor;
        set {
            if (value == _vendor) return;
            this.RaiseAndSetIfChanged(ref _vendor, value);
            Model = value.DefaultModel;
            this.RaisePropertyChanged(nameof(KeyHint));
        }
    }

    public string Model {
        get => _model;
        set => this.RaiseAndSetIfChanged(ref _model, value);
    }

    public string ApiKey {
        get => _apiKey;
        set {
            this.RaiseAndSetIfChanged(ref _apiKey, value);
            this.RaisePropertyChanged(nameof(CanContinue));
        }
    }

    public string KeyHint => $"Your {SelectedVendor.Label} API key. It is stored on the local server, never sent anywhere else.";

    public string? Error {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(NextLabel));
            this.RaisePropertyChanged(nameof(CanContinue));
        }
    }

    public bool CanContinue => !Busy && !IsLoading && (!ProviderSelected || !string.IsNullOrWhiteSpace(ApiKey));

    public Task OnEnterAsync(CancellationToken ct) {
        if (!_onLocalPath() || Options.Count > 0) return Task.CompletedTask;

        IsLoading = true;
        Loading   = LoadAsync(ct);

        return Task.CompletedTask;
    }

    async Task LoadAsync(CancellationToken ct) {
        string[] headless;
        try {
            headless = await _headless(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: headless harness read failed: {ex.Message}");
            headless = [];
        }

        _post(() => Offer(headless));
    }

    void Offer(string[] headless) {
        var ticked = _ticked().Select(id => id.VendorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agents = headless.Where(ticked.Contains).Select(IntelligenceOption.LocalAgent);

        List<IntelligenceOption> options = [.. agents, IntelligenceOption.Provider(), IntelligenceOption.NotNow()];
        options[0].IsSelected = options[0].IsLocalAgent;
        if (!options[0].IsLocalAgent) options[^1].IsSelected = true;

        foreach (var option in options) {
            option.PropertyChanged += OnOptionChanged;
        }

        Options   = options;
        IsLoading = false;
        this.RaisePropertyChanged(nameof(ProviderSelected));
    }

    void OnOptionChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(IntelligenceOption.IsSelected) || sender is not IntelligenceOption { IsSelected: true } picked) return;

        foreach (var other in Options.Where(o => o != picked)) other.IsSelected = false;
        Error = null;
        this.RaisePropertyChanged(nameof(ProviderSelected));
        this.RaisePropertyChanged(nameof(CanContinue));
    }

    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction != WizardNavigation.Next || !_onLocalPath()) return true;
        if (Busy || IsLoading) return false;

        if (Selected is not { } selected || selected.Token == IntelligenceAnswer.NoneToken) return true;

        var answer = selected.IsLocalAgent ? IntelligenceAnswer.LocalAgent(selected.Token) : IntelligenceAnswer.Provider(SelectedVendor);
        var model  = answer.Vendor is { } vendor && Model.Trim() is { Length: > 0 } typed && typed != vendor.DefaultModel ? typed : null;
        var key    = answer.Vendor is null ? null : ApiKey.Trim();

        return await SendAsync(answer, model, key, null, ct).ConfigureAwait(true);
    }

    public async Task<IntelligenceSetupOutcome?> ApplyImportOutcomesAsync(bool importOutcomes, CancellationToken ct) {
        if (_sent is not { } sent) return null;

        var outcome = await _cli.LocalIntelligenceAsync(sent.Answer, sent.Model, sent.Key, importOutcomes, ct).ConfigureAwait(false);
        if (outcome is IntelligenceSetupOutcome.Applied { Response: var applied }) _post(() => Applied = applied);

        return outcome;
    }

    async Task<bool> SendAsync(IntelligenceAnswer answer, string? model, string? key, bool? importOutcomes, CancellationToken ct) {
        Busy  = true;
        Error = null;

        try {
            switch (await _cli.LocalIntelligenceAsync(answer, model, key, importOutcomes, ct).ConfigureAwait(true)) {
                case IntelligenceSetupOutcome.Applied { Response: var applied }:
                    _sent   = (answer, model, key);
                    Applied = applied;
                    return true;
                case IntelligenceSetupOutcome.Refused { Error: var code }:
                    Error = IntelligenceRefusals.Describe(code);
                    return false;
                case IntelligenceSetupOutcome.Failed { Problem: var problem }:
                    Error = $"Could not reach the local server: {problem}";
                    return false;
                default:
                    return false;
            }
        } finally {
            Busy = false;
        }
    }
}
