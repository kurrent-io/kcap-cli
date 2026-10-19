using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>What a local install reports as it goes, in order. The front ends render these as terminal lines or JSON
/// events.</summary>
abstract record LocalInstallEvent {
    LocalInstallEvent() { }

    public sealed record PreflightPassed(PlanHint Plan, string Version, string Root) : LocalInstallEvent;

    public sealed record Recovering(string Operation, string Phase) : LocalInstallEvent;

    public sealed record Recovered(string Operation, string Phase) : LocalInstallEvent;

    public sealed record PortChosen(int Port) : LocalInstallEvent;

    public sealed record Downloading(string Url, long Bytes) : LocalInstallEvent;

    public sealed record DownloadProgress(long Received, long Bytes) : LocalInstallEvent;

    public sealed record Verified(string Sha256) : LocalInstallEvent;

    public sealed record Unpacked(string Path) : LocalInstallEvent;

    public sealed record Starting(string Unit) : LocalInstallEvent;

    public sealed record Ready(string ServerUrl) : LocalInstallEvent;

    public sealed record Committing : LocalInstallEvent;

    public sealed record Reused(string ServerUrl, string InstalledVersion) : LocalInstallEvent;

    public sealed record Committed(string ServerUrl, string InstallationId, int Port, string Version) : LocalInstallEvent;

    public sealed record RollingBack(string Reason) : LocalInstallEvent;

    public sealed record EmbeddingOllamaFound(string Url, string Version) : LocalInstallEvent;

    public sealed record EmbeddingModelPulling(string Model) : LocalInstallEvent;

    public sealed record EmbeddingModelProgress(string Model, long Received, long Bytes) : LocalInstallEvent;

    /// <summary>The model is in Ollama and embedded a probe string into <paramref name="Dimensions"/> numbers.</summary>
    public sealed record EmbeddingModelReady(string Model, int Dimensions) : LocalInstallEvent;

    /// <summary>The server embeds with <paramref name="Model"/>. <paramref name="Replaced"/> names a different model
    /// configured before, whose vectors no longer match.</summary>
    public sealed record EmbeddingsConfigured(string Model, string? Replaced) : LocalInstallEvent;

    public sealed record EmbeddingsSkipped(LocalEmbeddingSkipCode Code, string Message) : LocalInstallEvent;

    public sealed record RolledBack : LocalInstallEvent;
}
