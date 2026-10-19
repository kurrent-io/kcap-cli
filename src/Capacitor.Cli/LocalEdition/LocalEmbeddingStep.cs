using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Decides what <c>data/embedding.json</c> should say, pulling and probing the model first, and records it once the
/// caller has acted on it. Nothing here fails the install: without Ollama, or with a model that cannot be pulled or
/// cannot embed, it reports <see cref="LocalInstallEvent.EmbeddingsSkipped"/> and leaves the setting as it was. Only
/// cancellation escapes.
/// </summary>
static class LocalEmbeddingStep {
    /// <returns>The setting to record, or null when the recorded one stands.</returns>
    public static async Task<LocalEmbeddingSetting?> DecideAsync(LocalRoot root, LocalEmbeddingAnswer answer, OllamaClient ollama,
        IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        var current = LocalEmbeddingFile.Read(root);

        if (answer is LocalEmbeddingAnswer.Skip) {
            progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.Declined, "Search by meaning is off; search works by keyword."));

            return current == LocalEmbeddingSetting.Declined ? null : LocalEmbeddingSetting.Declined;
        }

        var named = ((LocalEmbeddingAnswer.Ollama)answer).Model;

        if (named is null && current == LocalEmbeddingSetting.Declined) {
            progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.Declined,
                $"Search by meaning stays off, as an earlier setup chose; --embedding-model <model> turns it on."));

            return null;
        }

        var model = named ?? current?.Model ?? OllamaClient.DefaultModel;
        var url   = ollama.BaseUrl.ToString().TrimEnd('/');

        if (await ollama.VersionAsync(ct) is not { } version)
            return Skipped(progress, LocalEmbeddingSkipCode.OllamaNotFound, $"No Ollama answers on {url}", current);

        progress.Report(new LocalInstallEvent.EmbeddingOllamaFound(url, version));

        try {
            if (!await ollama.HasModelAsync(model, ct)) {
                progress.Report(new LocalInstallEvent.EmbeddingModelPulling(model));
                await ollama.PullAsync(model, new SyncProgress<(long Received, long Total)>(Throttled(model, progress)), ct);
            }
        } catch (OllamaException ex) {
            return Skipped(progress, LocalEmbeddingSkipCode.PullFailed, $"Ollama could not pull {model} ({ex.Message})", current);
        }

        int dimensions;

        try {
            dimensions = await ollama.EmbedProbeAsync(model, ct);
        } catch (OllamaException ex) {
            return Skipped(progress, LocalEmbeddingSkipCode.ModelUnusable, $"{model} could not embed text ({ex.Message})", current);
        }

        progress.Report(new LocalInstallEvent.EmbeddingModelReady(model, dimensions));

        var replaced = current?.Model is { } old && old != model ? old : null;
        progress.Report(new LocalInstallEvent.EmbeddingsConfigured(model, replaced));

        return current?.Model == model ? null : new LocalEmbeddingSetting(model);
    }

    /// <summary>Records <paramref name="setting"/>. A failure is reported, not thrown: the next setup decides again.</summary>
    public static void Record(LocalRoot root, LocalEmbeddingSetting setting, IProgress<LocalInstallEvent> progress) {
        try {
            LocalEmbeddingFile.Write(root, setting);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.WriteFailed,
                $"{LocalEmbeddingFile.PathFor(root)} could not be written ({ex.Message}); run kcap setup --local again."));
        }
    }

    static LocalEmbeddingSetting? Skipped(IProgress<LocalInstallEvent> progress, LocalEmbeddingSkipCode code, string why,
        LocalEmbeddingSetting? current) {
        var outcome = current?.Model is { } model
            ? $"the server keeps embedding with {model}, and searches by keyword while that fails."
            : "search works by keyword.";

        progress.Report(new LocalInstallEvent.EmbeddingsSkipped(code, $"{why}; {outcome}"));

        return null;
    }

    /// <summary>One event per whole percent, so a large pull does not flood the JSON lines.</summary>
    static Action<(long Received, long Total)> Throttled(string model, IProgress<LocalInstallEvent> progress) {
        var last = -1L;

        return p => {
            var percent = p.Total > 0 ? p.Received * 100 / p.Total : 0;
            if (percent == last) return;

            last = percent;
            progress.Report(new LocalInstallEvent.EmbeddingModelProgress(model, p.Received, p.Total));
        };
    }
}
