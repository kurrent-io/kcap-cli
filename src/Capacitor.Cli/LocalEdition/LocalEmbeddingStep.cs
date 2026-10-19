using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Brings <c>data/embedding.json</c> to what the answer asks, pulling and probing the model first. Nothing here fails
/// the install: without Ollama, or with a model that cannot be pulled or cannot embed, it reports
/// <see cref="LocalInstallEvent.EmbeddingsSkipped"/> and leaves the file as it was. Only cancellation escapes.
/// </summary>
static class LocalEmbeddingStep {
    /// <returns>True when the file changed, so a running server must be restarted to see it.</returns>
    public static async Task<bool> RunAsync(LocalRoot root, LocalEmbeddingAnswer answer, OllamaClient ollama, IProgress<LocalInstallEvent> progress,
        CancellationToken ct) {
        var current = LocalEmbeddingFile.ReadModel(root);

        if (answer is LocalEmbeddingAnswer.Skip) return Decline(root, current, progress);

        var model = ((LocalEmbeddingAnswer.Ollama)answer).Model ?? current ?? OllamaClient.DefaultModel;
        var url   = ollama.BaseUrl.ToString().TrimEnd('/');

        if (await ollama.VersionAsync(ct) is not { } version)
            return Skipped(progress, LocalEmbeddingSkipCode.OllamaNotFound, $"No Ollama answers on {url}", current);

        progress.Report(new LocalInstallEvent.EmbeddingOllamaFound(url, version));

        int dimensions;

        try {
            if (!await ollama.HasModelAsync(model, ct)) {
                progress.Report(new LocalInstallEvent.EmbeddingModelPulling(model));
                await ollama.PullAsync(model, new SyncProgress<(long Received, long Total)>(Throttled(model, progress)), ct);
            }
        } catch (OllamaException ex) {
            return Skipped(progress, LocalEmbeddingSkipCode.PullFailed, $"Ollama could not pull {model} ({ex.Message})", current);
        }

        try {
            dimensions = await ollama.EmbedProbeAsync(model, ct);
        } catch (OllamaException ex) {
            return Skipped(progress, LocalEmbeddingSkipCode.ModelUnusable, $"{model} could not embed text ({ex.Message})", current);
        }

        progress.Report(new LocalInstallEvent.EmbeddingModelReady(model, dimensions));

        if (current == model) {
            progress.Report(new LocalInstallEvent.EmbeddingsConfigured(model, null));
            return false;
        }

        try {
            LocalEmbeddingFile.Write(root, model);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Skipped(progress, LocalEmbeddingSkipCode.WriteFailed, $"{LocalEmbeddingFile.PathFor(root)} could not be written ({ex.Message})", current);
        }

        progress.Report(new LocalInstallEvent.EmbeddingsConfigured(model, current));

        return true;
    }

    static bool Decline(LocalRoot root, string? current, IProgress<LocalInstallEvent> progress) {
        if (current is not null) {
            try {
                LocalEmbeddingFile.Delete(root);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return Skipped(progress, LocalEmbeddingSkipCode.WriteFailed,
                    $"{LocalEmbeddingFile.PathFor(root)} could not be removed ({ex.Message})", current);
            }
        }

        progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.Declined, "Semantic search is off; search works by keyword."));

        return current is not null;
    }

    static bool Skipped(IProgress<LocalInstallEvent> progress, LocalEmbeddingSkipCode code, string why, string? current) {
        var outcome = current is null
            ? "search works by keyword."
            : $"the server keeps embedding with {current}, and searches by keyword while that fails.";

        progress.Report(new LocalInstallEvent.EmbeddingsSkipped(code, $"{why}; {outcome}"));

        return false;
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
