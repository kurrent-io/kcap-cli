using System.Text.Json;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The JSON lines the desktop app reads from <c>kcap setup --local --json</c>, <c>kcap local upgrade --json</c> and
/// <c>kcap local fetch --json</c>.</summary>
static class LocalSetupJsonLines {
    public static string Line(LocalInstallEvent e) => Render(e switch {
        LocalInstallEvent.PreflightPassed p  => new() { Event = "preflight_passed", Plan = p.Plan.ToString().ToLowerInvariant(), Version = p.Version, Root = p.Root },
        LocalInstallEvent.Recovering r       => new() { Event = "recovering", Operation = r.Operation, Phase = r.Phase },
        LocalInstallEvent.Recovered r        => new() { Event = "recovered", Operation = r.Operation, Phase = r.Phase },
        LocalInstallEvent.PortChosen p       => new() { Event = "port_chosen", Port = p.Port },
        LocalInstallEvent.Downloading d      => new() { Event = "downloading", Url = d.Url, Bytes = d.Bytes },
        LocalInstallEvent.DownloadProgress d => new() { Event = "download_progress", Received = d.Received, Bytes = d.Bytes },
        LocalInstallEvent.Verified v         => new() { Event = "verified", Sha256 = v.Sha256 },
        LocalInstallEvent.Unpacked u         => new() { Event = "unpacked", Path = u.Path },
        LocalInstallEvent.Starting s         => new() { Event = "starting", Unit = s.Unit },
        LocalInstallEvent.Ready r            => new() { Event = "ready", ServerUrl = r.ServerUrl },
        LocalInstallEvent.Committing         => new() { Event = "committing" },
        LocalInstallEvent.Reused r           => new() { Event = "reused", ServerUrl = r.ServerUrl, InstalledVersion = r.InstalledVersion },
        LocalInstallEvent.RollingBack r      => new() { Event = "rolling_back", Reason = r.Reason },
        LocalInstallEvent.RolledBack         => new() { Event = "rolled_back" },
        LocalInstallEvent.Committed c        => new() { Event = "committed", ServerUrl = c.ServerUrl, InstallationId = c.InstallationId, Port = c.Port, Version = c.Version },
        LocalInstallEvent.UpgradePlanned u   => new() { Event = "upgrade_planned", FromVersion = u.From, Version = u.To },
        LocalInstallEvent.Stopping           => new() { Event = "stopping" },
        LocalInstallEvent.Stopped            => new() { Event = "stopped" },
        LocalInstallEvent.Switched s         => new() { Event = "switched", Version = s.Version },
        LocalInstallEvent.Restarting r       => new() { Event = "restarting", Version = r.Version },
        LocalInstallEvent.Upgraded u         => new() { Event = "upgraded", ServerUrl = u.ServerUrl, FromVersion = u.From, Version = u.To },
        LocalInstallEvent.NothingToDo n      => new() { Event = "nothing_to_do", Reason = n.Reason.Wire(), Version = n.Version, InstalledVersion = n.InstalledVersion },
        LocalInstallEvent.EmbeddingOllamaFound o   => new() { Event = "embeddings_ollama_found", Url = o.Url, Version = o.Version },
        LocalInstallEvent.EmbeddingModelPulling p  => new() { Event = "embeddings_model_pulling", Model = p.Model },
        LocalInstallEvent.EmbeddingModelProgress p => new() { Event = "embeddings_model_progress", Model = p.Model, Received = p.Received, Bytes = p.Bytes },
        LocalInstallEvent.EmbeddingModelReady r    => new() { Event = "embeddings_model_ready", Model = r.Model, Dimensions = r.Dimensions },
        LocalInstallEvent.EmbeddingsConfigured c   => new() { Event = "embeddings_configured", Provider = "ollama", Model = c.Model, ReplacedModel = c.Replaced },
        LocalInstallEvent.EmbeddingsSkipped k      => new() { Event = "embeddings_skipped", Code = k.Code.Wire(), Message = k.Message },
        LocalInstallEvent.EmbeddingBuiltinDownloading d => new() { Event = "embeddings_builtin_downloading", Bytes = d.Bytes },
        LocalInstallEvent.EmbeddingBuiltinProgress p    => new() { Event = "embeddings_builtin_progress", Received = p.Received, Bytes = p.Bytes },
        LocalInstallEvent.EmbeddingBuiltinConfigured c  => new() { Event = "embeddings_configured", Provider = "builtin", ReplacedModel = c.Replaced },
        LocalInstallEvent.WindowsServiceDeclined      => new() { Event = "windows_service_declined" },
        LocalInstallEvent.WindowsServiceUnavailable u => new() { Event = "windows_service_unavailable", Message = u.Message },
        LocalInstallEvent.Fetched f                   => new() { Event = "fetched", Version = f.Version, Path = f.Path },
        _                                    => throw new ArgumentOutOfRangeException(nameof(e), e, null),
    });

    public static string Failed(LocalInstallFailureCode code, string message, bool? switched = null) =>
        Render(new() { Event = "failed", Code = code.Wire(), Message = message, Switched = switched });

    /// <summary>The failure an exception escaping the installer stands for. The install itself throws only
    /// <see cref="LocalInstallFailure"/>; anything else came from building the installer or its preflight.</summary>
    public static LocalInstallFailure Classify(Exception ex) =>
        ex as LocalInstallFailure ?? new(LocalInstallFailureCode.StartFailed, ex.Message);

    /// <summary>The setup exit a failure stands for: the two codes that mean nothing was attempted are refusals.</summary>
    public static int Exit(LocalInstallFailureCode code) =>
        code is LocalInstallFailureCode.AnswerRequired or LocalInstallFailureCode.InvalidArguments ? SetupExit.Refused : SetupExit.Failed;

    public static string Render(LocalSetupLine line) => JsonSerializer.Serialize(line, LocalSetupJsonContext.Default.LocalSetupLine);
}
