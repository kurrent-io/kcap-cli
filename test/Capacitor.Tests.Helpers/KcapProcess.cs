using System.Diagnostics;
using System.Reflection;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Starts the built <c>kcap</c> binary from its own output directory, which the Helpers project
/// stamps in at compile time. Both contexts are required rather than optional: a child inherits
/// nothing in-process, and unpinned it finds the real daemons and config directories.
///
/// <para>Server selection is cleared for the same reason it is pinned: the child resolves
/// <c>KCAP_URL</c> and <c>KCAP_PROFILE</c> at its own composition root, so a value exported by
/// whoever ran the suite would aim it at a server the test never named. Cleared rather than
/// pinned, because a caller that wants one sets it on the returned info.</para>
/// </summary>
public static class KcapProcess {
    public static string BinaryPath { get; } = Path.Combine(
        typeof(KcapProcess).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "KcapBinaryDir").Value!,
        OperatingSystem.IsWindows() ? "kcap.exe" : "kcap");

    /// <summary>The caller adds its own working directory and environment.</summary>
    public static ProcessStartInfo StartInfo(DaemonStore store, ConfigRoot config, params ReadOnlySpan<string> args) {
        var psi = new ProcessStartInfo(BinaryPath) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment[DaemonStore.DaemonsDirEnvVar] = store.Directory;
        psi.Environment[ConfigRoot.ConfigDirEnvVar]   = config.Directory;
        psi.Environment.Remove(ProfileOverrides.UrlVar);
        psi.Environment.Remove(ProfileOverrides.ProfileVar);

        return psi;
    }
}
