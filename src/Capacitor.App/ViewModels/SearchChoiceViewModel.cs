using System.Globalization;
using System.Reactive;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Keyword search, or search by meaning through the built-in model or Ollama, with an Ollama model choice beneath the latter.
public sealed class SearchChoiceViewModel : ReactiveObject {
    public const string Explainer = "Choose how to find information saved in kcap.";
    public static readonly string BuiltinDetail = BuiltinDetailFor("about 320 MB");
    public const string BuiltinUnavailable = "Not available. kcap could not find a built-in model for this version.";
    public const string BuiltinLookingUp = "Checking for a built-in model…";
    public static readonly string MeaningDetail = $"Finds related information even when you use different words. Uses an AI model that runs in Ollama on {ThisDevice.Name}.";
    public const string KeywordDetail = "Finds information using matching words. No AI model needed. You can change this later in Settings, under Search.";
    public const string DefaultLabel = $"{LocalEmbeddingChoice.DefaultModel} (about 670 MB)";
    public const string DefaultDetail = "Downloaded into Ollama if it is not there yet.";
    public const string OllamaDownloadUrl = "https://ollama.com/download";
    public const string ModelChangeDetail = "Changing the model rebuilds the search index in the background. Your saved information is kept.";

    enum Mode { Keyword, Builtin, Ollama }

    readonly Func<CancellationToken, Task<bool>> _ollamaAvailable;
    readonly Func<bool> _ollamaInstalled;
    readonly IUrlOpener _opener;
    Mode _mode = Mode.Keyword;
    bool _useOther, _checking, _ollamaInstalledNow;
    // Until someone picks, or a recorded choice is loaded, the selection follows the recommendation: Ollama when it
    // answers, else the built-in model when the stack offers it, else keyword search.
    bool _followRecommendation = true;
    bool _recordedOllama, _recordedBuiltin;
    // Null until looked up, and not offered meanwhile: a stack without the model would refuse the flag.
    bool? _builtinOffered;
    long? _builtinBytes;
    bool? _ollamaRunning;
    string _otherModel = "";

    public SearchChoiceViewModel(
            Func<CancellationToken, Task<bool>>? ollamaAvailable = null, Func<bool>? ollamaInstalled = null, IUrlOpener? opener = null) {
        _ollamaAvailable = ollamaAvailable ?? LocalOllamaAvailability.CheckAsync;
        _ollamaInstalled = ollamaInstalled ?? LocalOllamaAvailability.Installed;
        _opener          = opener ?? new ShellUrlOpener();
        CheckOllamaCommand     = ReactiveCommand.CreateFromTask(CheckOllamaAsync);
        OpenOllamaSiteCommand  = ReactiveCommand.Create(() => _opener.Open(OllamaDownloadUrl));
    }

    /// Changing an installation's search, where a new model rebuilds an index that already exists.
    public bool Reconfiguring { get; init; }

    // A radio echoing a selection back is not a pick.
    public bool UseKeyword {
        get => _mode == Mode.Keyword;
        set { if (value && _mode != Mode.Keyword) Pick(Mode.Keyword); }
    }

    public bool UseBuiltin {
        get => _mode == Mode.Builtin;
        set { if (value && _mode != Mode.Builtin && BuiltinEnabled) Pick(Mode.Builtin); }
    }

    public bool UseOllama {
        get => _mode == Mode.Ollama;
        set { if (value && _mode != Mode.Ollama) Pick(Mode.Ollama); }
    }

    /// Picking a model is a pick of Ollama, so it stops the selection following the recommendation too.
    public bool UseDefault {
        get => !_useOther;
        set {
            if (!value || !_useOther) return;
            _followRecommendation = false;
            _useOther             = false;
            RestateChoice();
        }
    }

    public bool UseOther {
        get => _useOther;
        set {
            if (!value || _useOther) return;
            _followRecommendation = false;
            _useOther             = true;
            RestateChoice();
        }
    }

    public bool Checking {
        get => _checking;
        private set => this.RaiseAndSetIfChanged(ref _checking, value);
    }

    bool OllamaRunning => _ollamaRunning == true;

    /// A recorded built-in model stays selectable, so a switch away from it can be undone.
    public bool BuiltinEnabled => _builtinOffered == true || _recordedBuiltin;

    /// Setup left to decide may pick the built-in model, so an unchosen recommendation is not final until the lookup answers.
    public bool AwaitingOffer => _followRecommendation && _builtinOffered is null;

    public string? BuiltinStatus => _builtinOffered switch {
        _ when _recordedBuiltin => null,
        false                   => BuiltinUnavailable,
        null                    => BuiltinLookingUp,
        _                       => null,
    };

    public string BuiltinDescription => _builtinBytes is { } bytes
        ? BuiltinDetailFor($"about {(bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture)} MB")
        : BuiltinDetail;

    static string BuiltinDetailFor(string size) =>
        $"Finds related information even when you use different words. kcap downloads an AI model ({size}) once and runs it on {ThisDevice.Name}. Nothing else to install.";

    /// What the stack setup will run offers; null when it offers no built-in model or the lookup failed. A pick it can
    /// no longer honour goes back to following the recommendation.
    public void OfferBuiltin(LocalEmbeddingModelManifest? offered) {
        _builtinOffered = offered is not null;
        _builtinBytes   = offered?.TotalSize;
        if (_mode == Mode.Builtin && !BuiltinEnabled) _followRecommendation = true;
        if (_followRecommendation) Select(Recommendation);
        this.RaisePropertyChanged(nameof(BuiltinEnabled));
        this.RaisePropertyChanged(nameof(BuiltinStatus));
        this.RaisePropertyChanged(nameof(BuiltinDescription));
        this.RaisePropertyChanged(nameof(AwaitingOffer));
    }

    Mode Recommendation => OllamaRunning ? Mode.Ollama : _builtinOffered == true ? Mode.Builtin : Mode.Keyword;

    /// A model can only be pulled through a running Ollama; a recorded model stays selectable, so a
    /// switch away from it can be undone while Ollama is stopped.
    public bool OllamaEnabled => OllamaRunning || UseOllama || _recordedOllama;

    public bool ModelChoiceVisible => UseOllama && OllamaRunning;

    /// Stays up through a re-check, so the line does not jump while the button says it is checking.
    public bool OllamaMissing => _ollamaRunning == false;

    public string CheckLabel => Checking ? "Checking…" : "Check again";

    /// The state beside Check again; what search by meaning needs stays in the option's own description.
    public string? OllamaStatus => _ollamaRunning switch {
        null when Checking => "Looking for Ollama…",
        false              => _ollamaInstalledNow ? "Ollama is installed but not running. Open it." : "Ollama is not installed.",
        _ => null,
    };

    public bool OllamaLinkVisible => OllamaMissing && !_ollamaInstalledNow;

    public ReactiveCommand<Unit, Unit> OpenOllamaSiteCommand { get; }

    public ReactiveCommand<Unit, Unit> CheckOllamaCommand { get; }

    async Task CheckOllamaAsync(CancellationToken ct) {
        if (Checking) return;
        Checking = true;
        RestateOllama();
        try {
            _ollamaRunning      = await _ollamaAvailable(ct);
            _ollamaInstalledNow = _ollamaRunning == true || _ollamaInstalled();
        } finally {
            Checking = false;
            if (_followRecommendation && _ollamaRunning is not null) Select(Recommendation);
            RestateOllama();
        }
    }

    public string OtherModel {
        get => _otherModel;
        set {
            this.RaiseAndSetIfChanged(ref _otherModel, value ?? "");
            this.RaisePropertyChanged(nameof(Valid));
            this.RaisePropertyChanged(nameof(OtherModelError));
        }
    }

    string Named => OtherModel.Trim();

    bool NamedValid => LocalEmbeddingModelName.IsValid(Named);

    public bool Valid => !UseOllama || !UseOther || NamedValid;

    /// Shown once something is typed: an empty field only keeps the button disabled.
    public string? OtherModelError => UseOllama && UseOther && Named.Length > 0 && !NamedValid
        ? "Not an Ollama model name. Use letters, digits and . _ : / - and start with a letter or digit."
        : null;

    public LocalEmbeddingChoice Choice => _mode switch {
        _ when _followRecommendation => LocalEmbeddingChoice.Recommended(_mode switch {
            Mode.Ollama  => LocalEmbeddingChoice.Default,
            Mode.Builtin => LocalEmbeddingChoice.Builtin,
            _            => LocalEmbeddingChoice.KeywordOnly,
        }),
        Mode.Keyword                => LocalEmbeddingChoice.KeywordOnly,
        Mode.Builtin                => LocalEmbeddingChoice.Builtin,
        _                           => UseOther && NamedValid ? new LocalEmbeddingChoice(Named) : LocalEmbeddingChoice.Default,
    };

    /// Starts from what an earlier setup recorded, so a plain confirmation changes nothing. With
    /// nothing recorded, the selection follows the recommendation.
    public void Prefill(LocalEmbeddingSetting? recorded) {
        _followRecommendation = recorded is null;
        _recordedOllama       = recorded?.Model is not null;
        _recordedBuiltin      = recorded?.Provider == LocalEmbeddingProvider.Builtin;
        this.RaisePropertyChanged(nameof(BuiltinEnabled));
        this.RaisePropertyChanged(nameof(BuiltinStatus));
        switch (recorded) {
            case null:
                _useOther = false;
                Select(Recommendation);
                break;
            case { Provider: LocalEmbeddingProvider.Builtin }:
                _useOther = false;
                Select(Mode.Builtin);
                break;
            case { Model: null }:
                Select(Mode.Keyword);
                break;
            case { Model: { } model } when model != LocalEmbeddingChoice.DefaultModel:
                OtherModel = model;
                _useOther  = true;
                Select(Mode.Ollama);
                break;
            default:
                _useOther = false;
                Select(Mode.Ollama);
                break;
        }
    }

    void Pick(Mode mode) {
        _followRecommendation = false;
        Select(mode);
    }

    void Select(Mode mode) {
        _mode = mode;
        RestateChoice();
    }

    void RestateOllama() {
        this.RaisePropertyChanged(nameof(OllamaMissing));
        this.RaisePropertyChanged(nameof(CheckLabel));
        this.RaisePropertyChanged(nameof(OllamaStatus));
        this.RaisePropertyChanged(nameof(OllamaLinkVisible));
        this.RaisePropertyChanged(nameof(OllamaEnabled));
        this.RaisePropertyChanged(nameof(ModelChoiceVisible));
    }

    void RestateChoice() {
        this.RaisePropertyChanged(nameof(UseKeyword));
        this.RaisePropertyChanged(nameof(UseBuiltin));
        this.RaisePropertyChanged(nameof(UseOllama));
        this.RaisePropertyChanged(nameof(UseDefault));
        this.RaisePropertyChanged(nameof(UseOther));
        this.RaisePropertyChanged(nameof(OllamaEnabled));
        this.RaisePropertyChanged(nameof(ModelChoiceVisible));
        this.RaisePropertyChanged(nameof(Valid));
        this.RaisePropertyChanged(nameof(OtherModelError));
        this.RaisePropertyChanged(nameof(Choice));
        this.RaisePropertyChanged(nameof(AwaitingOffer));
    }
}
