using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The User-Agent a signup request carries, <c>kcap/1.2.3 (macos-arm64; desktop)</c>, from which kcap-web
/// tells a Desktop signup from a CLI one and names the machine it came from. kcap-web parses it strictly
/// and drops the whole value on any mismatch, so a change to this format must change its parser too.
/// </summary>
public sealed record ClientAgent(ClientSurface Surface) {
    public static readonly ClientAgent Cli     = new(ClientSurface.Cli);
    public static readonly ClientAgent Desktop = new(ClientSurface.Desktop);

    // The machine's architecture, not the process's: a translated x64 build on an arm64 Mac is still an arm64 machine.
    public string UserAgent { get; } = Format(
        CapacitorVersion.CurrentDisplay(), OsName(), RuntimeInformation.OSArchitecture, Surface);

    public static string Format(string version, string os, Architecture arch, ClientSurface surface) =>
        $"kcap/{version} ({os}-{arch.ToString().ToLowerInvariant()}; {surface.ToString().ToLowerInvariant()})";

    static string OsName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";
}
