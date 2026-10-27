using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Decides what <c>data/embedding.json</c> should say, fetching and probing the model first, and records it once the
/// caller has acted on it. Nothing here fails the install: without Ollama, without the built-in model, or with a model
/// that cannot be fetched or cannot embed, it reports <see cref="LocalInstallEvent.EmbeddingsSkipped"/> and leaves the
/// setting as it was. Only cancellation escapes.
/// </summary>
static class LocalEmbeddingStep {
    /// <param name="manifest">The built-in model set the installed version offers; null when it offers none.</param>
    /// <returns>The setting to record, or null when the recorded one stands. A new model is reported configured only
    /// once the caller has applied it, through <see cref="Applied"/>.</returns>
    public static async Task<LocalEmbeddingSetting?> DecideAsync(LocalRoot root, LocalEmbeddingAnswer answer, OllamaClient ollama,
        LocalModelSetDownloader models, LocalEmbeddingModelManifest? manifest, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        var current = LocalEmbeddingFile.Read(root);

        if (current is { Provider: LocalEmbeddingProvider.Unknown }) {
            progress.Report(Unrecognised(root));
            return null;
        }

        switch (answer) {
            case LocalEmbeddingAnswer.Skip:
                progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.Declined, "Search by meaning is off; search works by keyword."));

                return current is { Provider: LocalEmbeddingProvider.None } ? null : LocalEmbeddingSetting.Declined;

            case LocalEmbeddingAnswer.Builtin:
                return await BuiltinAsync(models, manifest, current, progress, ct);

            case LocalEmbeddingAnswer.Ollama { Model: { } named }:
                return await OllamaAsync(ollama, named, current, null, progress, ct);
        }

        switch (current?.Provider) {
            case LocalEmbeddingProvider.None:
                progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.Declined,
                    "Search by meaning stays off, as an earlier setup chose; --builtin-embeddings or --embedding-model <model> turns it on."));
                return null;

            case LocalEmbeddingProvider.Builtin when answer is LocalEmbeddingAnswer.Default:
                return await BuiltinAsync(models, manifest, current, progress, ct);

            case LocalEmbeddingProvider.Ollama:
                return await OllamaAsync(ollama, null, current, null, progress, ct);
        }

        // Nothing is recorded, or Ollama without a model was asked for over a recorded built-in model.
        if (answer is LocalEmbeddingAnswer.Default && manifest is not null) {
            if (await ollama.VersionAsync(ct) is { } version) return await OllamaAsync(ollama, null, current, version, progress, ct);

            return await BuiltinAsync(models, manifest, current, progress, ct);
        }

        return await OllamaAsync(ollama, null, current, null, progress, ct);
    }

    static async Task<LocalEmbeddingSetting?> BuiltinAsync(LocalModelSetDownloader models, LocalEmbeddingModelManifest? manifest,
        LocalEmbeddingSetting? current, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        if (manifest is null)
            return Skipped(progress, LocalEmbeddingSkipCode.BuiltinUnavailable, "This version of kcap local offers no built-in search model, or it could not be looked up", current);

        if (!await FetchBuiltinAsync(models, manifest, current, progress, ct)) return null;

        if (current is { Provider: LocalEmbeddingProvider.Builtin } && current.ModelSetId == manifest.ModelSet) {
            progress.Report(new LocalInstallEvent.EmbeddingBuiltinConfigured());
            return null;
        }

        return LocalEmbeddingSetting.Builtin(manifest.ModelSet);
    }

    /// <summary>Downloads <paramref name="manifest"/>'s model set unless it is already in place and verifies.</summary>
    /// <returns>False when the download failed, which is reported, or the downloader's publishing step refused it.</returns>
    public static async Task<bool> FetchBuiltinAsync(LocalModelSetDownloader models, LocalEmbeddingModelManifest manifest,
        LocalEmbeddingSetting? current, IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        if (models.IsPresent(manifest)) return true;

        progress.Report(new LocalInstallEvent.EmbeddingBuiltinDownloading(manifest.TotalSize));

        try {
            return await models.DownloadAsync(manifest, new SyncProgress<(long Received, long Total)>(Throttled(
                p => new LocalInstallEvent.EmbeddingBuiltinProgress(p.Received, p.Total), progress)), ct);
        } catch (LocalModelDownloadException ex) {
            Skipped(progress, LocalEmbeddingSkipCode.ModelDownloadFailed, $"The built-in search model could not be downloaded ({ex.Message})", current);
            return false;
        }
    }

    /// <param name="version">Ollama's version when the caller already asked it.</param>
    static async Task<LocalEmbeddingSetting?> OllamaAsync(OllamaClient ollama, string? named, LocalEmbeddingSetting? current, string? version,
        IProgress<LocalInstallEvent> progress, CancellationToken ct) {
        var model = named ?? current?.Model ?? OllamaClient.DefaultModel;
        var url   = ollama.BaseUrl.ToString().TrimEnd('/');

        if ((version ?? await ollama.VersionAsync(ct)) is not { } found)
            return Skipped(progress, LocalEmbeddingSkipCode.OllamaNotFound, $"Ollama is not responding on {url}", current,
                current?.Model is null && current?.Provider != LocalEmbeddingProvider.Builtin
                    ? "Start Ollama, then run kcap setup --local again to turn on search by meaning."
                    : null);

        progress.Report(new LocalInstallEvent.EmbeddingOllamaFound(url, found));

        try {
            if (!await ollama.HasModelAsync(model, ct)) {
                progress.Report(new LocalInstallEvent.EmbeddingModelPulling(model));
                await ollama.PullAsync(model, new SyncProgress<(long Received, long Total)>(Throttled(
                    p => new LocalInstallEvent.EmbeddingModelProgress(model, p.Received, p.Total), progress)), ct);
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

        if (current?.Model != model) return LocalEmbeddingSetting.Ollama(model);

        progress.Report(new LocalInstallEvent.EmbeddingsConfigured(model, null));

        return null;
    }

    /// <summary>Records <paramref name="setting"/>. A failure is reported, not thrown: the next setup decides again.</summary>
    /// <returns>False when it could not be written, or when the file records a provider this release does not know and
    /// was left as it is.</returns>
    public static bool Record(LocalRoot root, LocalEmbeddingSetting setting, IProgress<LocalInstallEvent> progress) {
        try {
            if (LocalEmbeddingFile.Write(root, setting)) return true;

            progress.Report(Unrecognised(root));
            return false;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            progress.Report(new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.WriteFailed,
                $"{LocalEmbeddingFile.PathFor(root)} could not be written ({ex.Message}); run kcap setup --local again."));
            return false;
        }
    }

    /// <summary>Reports a setting the unit now serves; a decline was reported when it was decided.</summary>
    public static void Applied(LocalEmbeddingSetting? previous, LocalEmbeddingSetting setting, IProgress<LocalInstallEvent> progress) {
        if (setting.Provider == LocalEmbeddingProvider.Builtin) {
            progress.Report(new LocalInstallEvent.EmbeddingBuiltinConfigured(previous?.Model));
            return;
        }

        if (setting.Model is not { } model) return;

        progress.Report(new LocalInstallEvent.EmbeddingsConfigured(model, previous?.Model is { } old && old != model ? old : null));
    }

    /// <summary>Puts back what <see cref="LocalEmbeddingFile.Read"/> returned before a change, absence included.</summary>
    public static void Restore(LocalRoot root, LocalEmbeddingSetting? previous) {
        if (previous is null) LocalEmbeddingFile.Delete(root);
        else _ = LocalEmbeddingFile.Write(root, previous); // A refusal means the file holds a provider we do not know, which is what Restore wants kept.
    }

    /// <param name="advice">Its own sentence, so the desktop app can drop it as terminal advice.</param>
    static LocalEmbeddingSetting? Skipped(IProgress<LocalInstallEvent> progress, LocalEmbeddingSkipCode code, string why,
        LocalEmbeddingSetting? current, string? advice = null) {
        var outcome = current switch {
            { Model: { } model }                          => $"the server keeps embedding with {model}, and searches by keyword while that fails.",
            { Provider: LocalEmbeddingProvider.Builtin } => "the server keeps the built-in model, and searches by keyword while it is missing.",
            _                                             => "search works by keyword.",
        };

        progress.Report(new LocalInstallEvent.EmbeddingsSkipped(code, advice is null ? $"{why}; {outcome}" : $"{why}; {outcome} {advice}"));

        return null;
    }

    static LocalInstallEvent.EmbeddingsSkipped Unrecognised(LocalRoot root) =>
        new(LocalEmbeddingSkipCode.UnrecognisedProvider,
            $"{LocalEmbeddingFile.PathFor(root)} records a search provider this version of kcap does not know; the recorded choice was left as it is.");

    /// <summary>One event per whole percent, so a large download does not flood the JSON lines.</summary>
    static Action<(long Received, long Total)> Throttled(Func<(long Received, long Total), LocalInstallEvent> report, IProgress<LocalInstallEvent> progress) {
        var last = -1L;

        return p => {
            var percent = p.Total > 0 ? p.Received * 100 / p.Total : 0;
            if (percent == last) return;

            last = percent;
            progress.Report(report(p));
        };
    }
}
