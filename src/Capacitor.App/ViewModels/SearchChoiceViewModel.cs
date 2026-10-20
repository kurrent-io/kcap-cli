using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Search by meaning with the suggested Ollama model, with another one, or search by keyword only.
public sealed class SearchChoiceViewModel : ReactiveObject {
    public const string Explainer =
        "Search by meaning runs through Ollama on this Mac. The model fixes the shape of what is stored, so " +
        "changing it later means everything is embedded again in the background.";
    public const string DefaultLabel  = $"Use Ollama with {LocalEmbeddingChoice.DefaultModel} (about 670 MB)";
    public const string DefaultDetail = "Downloaded into Ollama if it doesn't have it yet. Without Ollama running, search works by keyword.";
    public const string KeywordDetail = "Search by meaning can be turned on later from Capacitor local in the sidebar.";

    bool _useDefault = true, _useOther, _useKeyword;
    string _otherModel = "";

    public bool UseDefault {
        get => _useDefault;
        set => Pick(ref _useDefault, value);
    }

    public bool UseOther {
        get => _useOther;
        set => Pick(ref _useOther, value);
    }

    public bool UseKeyword {
        get => _useKeyword;
        set => Pick(ref _useKeyword, value);
    }

    public string OtherModel {
        get => _otherModel;
        set {
            this.RaiseAndSetIfChanged(ref _otherModel, value ?? "");
            this.RaisePropertyChanged(nameof(Valid));
            this.RaisePropertyChanged(nameof(OtherModelError));
        }
    }

    public bool Valid => !UseOther || LocalEmbeddingModelName.IsValid(OtherModel.Trim());

    /// Shown once something is typed: an empty field only keeps the button disabled.
    public string? OtherModelError => UseOther && OtherModel.Trim().Length > 0 && !Valid
        ? "Not an Ollama model name: letters, digits and . _ : / - only, starting with a letter or digit."
        : null;

    public LocalEmbeddingChoice Choice =>
        UseKeyword ? LocalEmbeddingChoice.KeywordOnly
        : UseOther && OtherModel.Trim() is var other && LocalEmbeddingModelName.IsValid(other) ? new LocalEmbeddingChoice(other)
        : LocalEmbeddingChoice.Default;

    /// Starts from what an earlier setup recorded, so a plain confirmation changes nothing.
    public void Prefill(LocalEmbeddingSetting? recorded) {
        switch (recorded) {
            case { Model: null }:
                UseKeyword = true;
                break;
            case { Model: { } model } when model != LocalEmbeddingChoice.DefaultModel:
                OtherModel = model;
                UseOther   = true;
                break;
            default:
                UseDefault = true;
                break;
        }
    }

    void Pick(ref bool field, bool value) {
        if (!value || field) return;

        _useDefault = _useOther = _useKeyword = false;
        field = true;
        this.RaisePropertyChanged(nameof(UseDefault));
        this.RaisePropertyChanged(nameof(UseOther));
        this.RaisePropertyChanged(nameof(UseKeyword));
        this.RaisePropertyChanged(nameof(Valid));
        this.RaisePropertyChanged(nameof(OtherModelError));
        this.RaisePropertyChanged(nameof(Choice));
    }
}
