using System.Reactive;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Keyword or meaning-based search, with an Ollama model choice beneath the latter.
public sealed class SearchChoiceViewModel : ReactiveObject {
    public const string Explainer = "Choose how to find information saved in kcap.";
    public const string MeaningDetail = "Finds related information even when you use different words. Uses an AI model that runs in Ollama on this Mac.";
    public const string KeywordDetail = "Finds information using matching words. No AI model needed. You can change this later from kcap local in the sidebar.";
    public const string DefaultLabel = $"{LocalEmbeddingChoice.DefaultModel} (about 670 MB)";
    public const string DefaultDetail = "Downloaded into Ollama if it is not there yet.";
    public const string ModelChangeDetail = "Changing the model rebuilds the search index in the background. Your saved information is kept.";

    readonly Func<CancellationToken, Task<bool>> _ollamaAvailable;
    readonly Func<bool> _ollamaInstalled;
    bool _useMeaning, _useOther, _checking, _ollamaInstalledNow;
    // Until someone picks, or a recorded choice is loaded, the selection follows whether Ollama answers.
    bool _followOllama = true;
    bool? _ollamaRunning;
    string _otherModel = "";

    public SearchChoiceViewModel(Func<CancellationToken, Task<bool>>? ollamaAvailable = null, Func<bool>? ollamaInstalled = null) {
        _ollamaAvailable = ollamaAvailable ?? LocalOllamaAvailability.CheckAsync;
        _ollamaInstalled = ollamaInstalled ?? LocalOllamaAvailability.Installed;
        CheckOllamaCommand = ReactiveCommand.CreateFromTask(CheckOllamaAsync);
    }

    /// Changing an installation's search, where a new model rebuilds an index that already exists.
    public bool Reconfiguring { get; init; }

    public bool UseMeaning {
        get => _useMeaning;
        set {
            // A radio echoing a selection back is not a pick.
            if (_useMeaning == value) return;
            _followOllama = false;
            SelectMeaning(value);
        }
    }

    public bool UseKeyword {
        get => !UseMeaning;
        set { if (value) UseMeaning = false; }
    }

    public bool UseDefault {
        get => !_useOther;
        set {
            if (!value) return;
            _useOther = false;
            RestateChoice();
        }
    }

    public bool UseOther {
        get => _useOther;
        set {
            if (!value) return;
            _useOther = true;
            RestateChoice();
        }
    }

    public bool Checking {
        get => _checking;
        private set => this.RaiseAndSetIfChanged(ref _checking, value);
    }

    bool OllamaRunning => _ollamaRunning == true;

    /// A model can only be pulled through a running Ollama; a recorded meaning choice stays selectable.
    public bool MeaningEnabled => OllamaRunning || UseMeaning;

    public bool ModelChoiceVisible => UseMeaning && OllamaRunning;

    /// Stays up through a re-check, so the line does not jump while the button says it is checking.
    public bool OllamaMissing => _ollamaRunning == false;

    public string CheckLabel => Checking ? "Checking…" : "Check again";

    public string? OllamaStatus => _ollamaRunning switch {
        null when Checking => "Looking for Ollama on this Mac…",
        false              => _ollamaInstalledNow
            ? "Ollama is installed but not running. Open it, then check again."
            : "Needs Ollama, a free app that runs AI models on this Mac. Install it from ollama.com, open it, then check again.",
        _ => null,
    };

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
            if (_followOllama && _ollamaRunning is { } running) SelectMeaning(running);
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

    public bool Valid => !UseMeaning || !UseOther || NamedValid;

    /// Shown once something is typed: an empty field only keeps the button disabled.
    public string? OtherModelError => UseMeaning && UseOther && Named.Length > 0 && !NamedValid
        ? "Not an Ollama model name: letters, digits and . _ : / - only, starting with a letter or digit."
        : null;

    public LocalEmbeddingChoice Choice =>
        UseKeyword ? LocalEmbeddingChoice.KeywordOnly
        : UseOther && NamedValid ? new LocalEmbeddingChoice(Named)
        : LocalEmbeddingChoice.Default;

    /// Starts from what an earlier setup recorded, so a plain confirmation changes nothing. With
    /// nothing recorded, the selection follows whether Ollama is running.
    public void Prefill(LocalEmbeddingSetting? recorded) {
        _followOllama = recorded is null;
        switch (recorded) {
            case null:
                _useOther = false;
                SelectMeaning(OllamaRunning);
                break;
            case { Model: null }:
                SelectMeaning(false);
                break;
            case { Model: { } model } when model != LocalEmbeddingChoice.DefaultModel:
                OtherModel = model;
                _useOther  = true;
                SelectMeaning(true);
                break;
            default:
                _useOther = false;
                SelectMeaning(true);
                break;
        }
    }

    void SelectMeaning(bool value) {
        _useMeaning = value;
        RestateChoice();
    }

    void RestateOllama() {
        this.RaisePropertyChanged(nameof(OllamaMissing));
        this.RaisePropertyChanged(nameof(CheckLabel));
        this.RaisePropertyChanged(nameof(OllamaStatus));
        this.RaisePropertyChanged(nameof(MeaningEnabled));
        this.RaisePropertyChanged(nameof(ModelChoiceVisible));
    }

    void RestateChoice() {
        this.RaisePropertyChanged(nameof(UseMeaning));
        this.RaisePropertyChanged(nameof(UseDefault));
        this.RaisePropertyChanged(nameof(UseOther));
        this.RaisePropertyChanged(nameof(UseKeyword));
        this.RaisePropertyChanged(nameof(MeaningEnabled));
        this.RaisePropertyChanged(nameof(ModelChoiceVisible));
        this.RaisePropertyChanged(nameof(Valid));
        this.RaisePropertyChanged(nameof(OtherModelError));
        this.RaisePropertyChanged(nameof(Choice));
    }
}
