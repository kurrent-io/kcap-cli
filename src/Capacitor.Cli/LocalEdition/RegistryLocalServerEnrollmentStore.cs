using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Capacitor.Cli.LocalEdition;

/// <summary><c>HKCU\Software\Kurrent\kcap\LocalServer</c>, beside the daemon's enrollment. The service reads it only
/// while impersonating the user; only the CLI writes it</summary>
[SupportedOSPlatform("windows")]
sealed class RegistryLocalServerEnrollmentStore : ILocalServerEnrollmentStore {
    public const string KeyPath = @"Software\Kurrent\kcap\LocalServer";

    public LocalServerEnrollment? Read() {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key is null) return null;

        return Text(key, "ServerPath") is { } server && Text(key, "Root") is { } root && Text(key, "CliPath") is { } cli
            && Text(key, "EnvironmentFile") is { } env && Text(key, "LogFile") is { } log
            ? new LocalServerEnrollment(server, root, cli, env, log, key.GetValue("Stopped") is int stopped && stopped != 0)
            : null;
    }

    public void Write(LocalServerEnrollment enrollment) {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("ServerPath", enrollment.ServerPath);
        key.SetValue("Root", enrollment.Root);
        key.SetValue("CliPath", enrollment.CliPath);
        key.SetValue("EnvironmentFile", enrollment.EnvironmentFile);
        key.SetValue("LogFile", enrollment.LogFile);
        key.SetValue("Stopped", enrollment.Stopped ? 1 : 0, RegistryValueKind.DWord);
    }

    public void SetStopped(bool stopped) {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.SetValue("Stopped", stopped ? 1 : 0, RegistryValueKind.DWord);
    }

    public void Delete() {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        DeleteIfEmpty(@"Software\Kurrent\kcap");
        DeleteIfEmpty(@"Software\Kurrent");
    }

    static string? Text(RegistryKey key, string name) => key.GetValue(name) is string { Length: > 0 } value ? value : null;

    static void DeleteIfEmpty(string path) {
        using (var key = Registry.CurrentUser.OpenSubKey(path)) {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
