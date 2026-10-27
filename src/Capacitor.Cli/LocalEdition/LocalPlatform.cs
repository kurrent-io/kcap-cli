using System.Runtime.InteropServices;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The facts about this machine the preflight decides on, behind a record so tests cover the matrix.</summary>
sealed record LocalPlatform(bool IsMacOS, bool IsLinux, bool IsMusl, Architecture Arch) {
    public bool IsWindows { get; init; }

    public static LocalPlatform Current() =>
        new(OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), OperatingSystem.IsLinux() && RuntimeRid.IsMusl(), RuntimeInformation.ProcessArchitecture) {
            IsWindows = OperatingSystem.IsWindows(),
        };

    /// <summary>There is a local edition build for this machine: an Apple silicon Mac. Windows waits on the service
    /// that runs the server unit there.</summary>
    public bool SupportsLocalEdition => IsMacOS && Arch == Architecture.Arm64;

    /// <summary>The runtime identifier a bundle for this machine is published under.</summary>
    public string Rid => $"{(IsWindows ? "win" : IsMacOS ? "osx" : IsMusl ? "linux-musl" : "linux")}-{Arch.ToString().ToLowerInvariant()}";
}
