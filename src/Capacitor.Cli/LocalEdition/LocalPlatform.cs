using System.Runtime.InteropServices;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The facts about this machine the preflight decides on, behind a record so tests cover the matrix.</summary>
sealed record LocalPlatform(bool IsMacOS, bool IsLinux, bool IsMusl, Architecture Arch) {
    public static LocalPlatform Current() =>
        new(OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), OperatingSystem.IsLinux() && RuntimeRid.IsMusl(), RuntimeInformation.ProcessArchitecture);
}
