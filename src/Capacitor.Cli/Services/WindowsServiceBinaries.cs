using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Capacitor.Cli.Services;

/// <summary>Where <c>kcap-service.exe</c> ships (beside this CLI, in the desktop bundle) and where its elevated
/// install puts it</summary>
[SupportedOSPlatform("windows")]
static class WindowsServiceBinaries {
    public const string ServiceName = "kcap-service";
    const string FileName = "kcap-service.exe";

    public static bool Registered() {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return key is not null;
    }

    /// <summary>Null for an install that does not carry the service, such as npm's</summary>
    public static string? Bundled() {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(path) ? path : null;
    }

    public static string Installed =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "kcap", "service", FileName);

    /// <summary>The bundled binary differs from the installed one; null when either is missing</summary>
    public static bool? UpgradeAvailable() {
        if (Bundled() is not { } bundled || !File.Exists(Installed)) return null;
        try {
            return !Hash(bundled).AsSpan().SequenceEqual(Hash(Installed));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    static byte[] Hash(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }
}
