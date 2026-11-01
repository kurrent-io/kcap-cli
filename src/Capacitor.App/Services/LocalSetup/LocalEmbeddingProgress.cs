using System.Globalization;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// The `embeddings_*` lines of one setup run: the Ollama model pull or the built-in model download, and whether the
/// step ended in search by meaning or by keyword. A skip never fails the run. When the run was asked for keyword
/// search, even as an unchosen recommendation that passes no flag, a skip reads as that choice rather than as the
/// Ollama probe setup makes without a flag.
public sealed class LocalEmbeddingProgress(LocalEmbeddingChoice? asked = null) : ReactiveObject {
    long _bytes;
    double? _pullFraction;
    bool _pulling, _builtin;
    string? _model, _configuredModel, _replacedModel, _skipCode, _skipMessage;

    const string BuiltinName = "the built-in model";
    const string KeywordAsChosen = "Search by keyword, as chosen.";

    public InstallRow Row { get; } = new();

    /// The model the step is working on, from its latest line.
    public string? Model {
        get => _model;
        private set => this.RaiseAndSetIfChanged(ref _model, value);
    }

    public bool Pulling {
        get => _pulling;
        private set => this.RaiseAndSetIfChanged(ref _pulling, value);
    }

    public double? PullFraction {
        get => _pullFraction;
        private set => this.RaiseAndSetIfChanged(ref _pullFraction, value);
    }

    public string? ConfiguredModel {
        get => _configuredModel;
        private set => this.RaiseAndSetIfChanged(ref _configuredModel, value);
    }

    /// The model `ConfiguredModel` replaced: everything stored is embedded again in the background.
    public string? ReplacedModel {
        get => _replacedModel;
        private set => this.RaiseAndSetIfChanged(ref _replacedModel, value);
    }

    public string? SkipCode {
        get => _skipCode;
        private set => this.RaiseAndSetIfChanged(ref _skipCode, value);
    }

    public string? SkipMessage {
        get => _skipMessage;
        private set => this.RaiseAndSetIfChanged(ref _skipMessage, value);
    }

    /// The step works on, or ended with, the built-in model rather than an Ollama model.
    public bool Builtin {
        get => _builtin;
        private set => this.RaiseAndSetIfChanged(ref _builtin, value);
    }

    public bool Configured => ConfiguredModel is not null;

    public bool Skipped => SkipCode is not null;

    /// How search works once the step ended; null while it runs or when it never reported.
    public string? Summary => ConfiguredModel is { } model
        ? $"Search by meaning with {model}."
        : SkipCode switch {
            null                                 => null,
            "declined"                           => KeywordAsChosen,
            _ when asked is { ByMeaning: false } => KeywordAsChosen,
            _                                    => TerminalAdvice.Strip(SkipMessage) ?? "Search by keyword.",
        };

    public string? ReembedNote => ConfiguredModel switch {
        null                                => null,
        var model when ReplacedModel is { } replaced
            => $"The search index is being rebuilt with {model} in the background after switching from {replaced}. Your saved information is kept.",
        var model                           => $"The search index for saved information is being built with {model} in the background.",
    };

    public string? PullText {
        get {
            if (!Pulling) return null;

            var text = Builtin ? $"Downloading the built-in search model{BuiltinSize}…"
                : Model is { } model ? $"Downloading the Ollama model {model}…"
                : null;

            return text is null || PullFraction is not { } f ? text : $"{text} {(int)Math.Round(f * 100)}%";
        }
    }

    string BuiltinSize => _bytes > 0 ? $" ({(_bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture)} MB)" : "";

    /// True when the line was an `embeddings_*` event.
    public bool Apply(LocalSetupLine line) {
        var root = line.Root;
        switch (line.Event) {
            case "embeddings_ollama_found":
                Begin();
                break;
            case "embeddings_model_pulling":
                Begin();
                Model        = root.Str("model") ?? Model;
                _bytes       = 0;
                Pulling      = true;
                PullFraction = null;
                break;
            case "embeddings_model_progress":
                Begin();
                Model = root.Str("model") ?? Model;
                if (root.Num("bytes") is > 0 and var total) _bytes = total;
                Pulling      = true;
                PullFraction = _bytes > 0 ? Math.Clamp((double)(root.Num("received") ?? 0) / _bytes, 0, 1) : null;
                break;
            case "embeddings_model_ready":
                Begin();
                Model   = root.Str("model") ?? Model;
                Pulling = false;
                break;
            case "embeddings_builtin_downloading":
            case "embeddings_builtin_progress":
                Begin();
                Builtin = true;
                if (root.Num("bytes") is > 0 and var size) _bytes = size;
                Pulling      = true;
                PullFraction = _bytes > 0 && line.Event == "embeddings_builtin_progress"
                    ? Math.Clamp((double)(root.Num("received") ?? 0) / _bytes, 0, 1)
                    : null;
                break;
            case "embeddings_configured" when root.Str("provider") == "builtin":
                Builtin         = true;
                Model           = BuiltinName;
                ReplacedModel   = root.Str("replaced_model");
                ConfiguredModel = BuiltinName;
                SkipCode        = null;
                SkipMessage     = null;
                End(RowState.Done);
                break;
            case "embeddings_configured":
                Builtin         = false;
                Model           = root.Str("model") ?? Model;
                ReplacedModel   = root.Str("replaced_model");
                ConfiguredModel = Model;
                SkipCode        = null;
                SkipMessage     = null;
                End(RowState.Done);
                break;
            // The unit serves a configured model before setup records it, so a failed record leaves it serving.
            case "embeddings_skipped" when Configured:
                break;
            case "embeddings_skipped":
                SkipMessage = root.Str("message");
                SkipCode    = root.Str("code") ?? "unknown";
                End(RowState.Skipped);
                break;
            default:
                return false;
        }
        Restate();
        return true;
    }

    /// The run moved past the step without its end line.
    public void Close() {
        if (Row.State != RowState.Running) return;

        End(RowState.Done);
        Restate();
    }

    /// A run that settled without success leaves nothing in flight.
    public void Halt() {
        Pulling = false;
        if (Row.State == RowState.Running) Row.State = RowState.Pending;
        Restate();
    }

    void Begin() {
        if (Row.State == RowState.Pending) Row.State = RowState.Running;
    }

    void End(RowState state) {
        Pulling   = false;
        Row.State = state;
    }

    void Restate() {
        this.RaisePropertyChanged(nameof(Configured));
        this.RaisePropertyChanged(nameof(Skipped));
        this.RaisePropertyChanged(nameof(Summary));
        this.RaisePropertyChanged(nameof(ReembedNote));
        this.RaisePropertyChanged(nameof(PullText));
    }
}
