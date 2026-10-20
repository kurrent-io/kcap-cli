using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

/// The `embeddings_*` lines of one setup run: the Ollama model pull and whether the step ended in search by
/// meaning or by keyword. A skip never fails the run.
public sealed class LocalEmbeddingProgress : ReactiveObject {
    long _bytes;
    double? _pullFraction;
    bool _pulling;
    string? _model, _configuredModel, _replacedModel, _skipCode, _skipMessage;

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

    public bool Configured => ConfiguredModel is not null;

    public bool Skipped => SkipCode is not null;

    /// How search works once the step ended; null while it runs or when it never reported.
    public string? Summary => ConfiguredModel is { } model
        ? $"Search by meaning with {model}."
        : SkipCode switch {
            null       => null,
            "declined" => "Search by keyword, as chosen.",
            _          => TerminalAdvice.Strip(SkipMessage) ?? "Search by keyword.",
        };

    public string? ReembedNote => ConfiguredModel is { } model && ReplacedModel is { } replaced
        ? $"Everything stored was embedded with {replaced}, so it is embedded again with {model} in the background."
        : null;

    public string? PullText => Pulling && Model is { } model
        ? PullFraction is { } f ? $"Downloading {model}… {(int)Math.Round(f * 100)}%" : $"Downloading {model}…"
        : null;

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
            case "embeddings_configured":
                Model           = root.Str("model") ?? Model;
                ReplacedModel   = root.Str("replaced_model");
                ConfiguredModel = Model;
                End(RowState.Done);
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
