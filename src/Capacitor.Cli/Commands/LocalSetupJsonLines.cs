using System.Text.Json;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>The JSON lines the desktop wizard reads from <c>kcap setup --local --json</c>.</summary>
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
        _                                    => throw new ArgumentOutOfRangeException(nameof(e), e, null),
    });

    public static string Failed(LocalInstallFailureCode code, string message) =>
        Render(new() { Event = "failed", Code = code.Wire(), Message = message });

    /// <summary>The failure an exception escaping the installer stands for. The install itself throws only
    /// <see cref="LocalInstallFailure"/>; anything else came from building the installer or its preflight.</summary>
    public static LocalInstallFailure Classify(Exception ex) =>
        ex as LocalInstallFailure ?? new(LocalInstallFailureCode.StartFailed, ex.Message);

    static string Render(LocalSetupLine line) => JsonSerializer.Serialize(line, LocalSetupJsonContext.Default.LocalSetupLine);
}
