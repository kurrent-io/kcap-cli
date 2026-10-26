using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Capacitor.Cli.Services;

/// <summary><c>HKCU\Software\Kurrent\kcap\Daemon</c>, the key <c>kcap-service</c> reads from the user's hive</summary>
[SupportedOSPlatform("windows")]
sealed class RegistryServiceEnrollmentStore : IServiceEnrollmentStore {
    public const string KeyPath = @"Software\Kurrent\kcap\Daemon";

    public ServiceEnrollment? Read() {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key?.GetValue("Name") is not string { Length: > 0 } name || key.GetValue("DaemonPath") is not string { Length: > 0 } daemon) return null;
        return new ServiceEnrollment(
            name, daemon, key.GetValue("Profile") as string,
            key.GetValue("MaxAgents") is int max ? max : null, key.GetValue("EnvironmentFile") as string);
    }

    public void Write(ServiceEnrollment enrollment) {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("Name", enrollment.Name);
        key.SetValue("DaemonPath", enrollment.DaemonPath);
        Set(key, "Profile", enrollment.Profile);
        Set(key, "EnvironmentFile", enrollment.EnvironmentFile);
        if (enrollment.MaxAgents is { } max) key.SetValue("MaxAgents", max, RegistryValueKind.DWord);
        else key.DeleteValue("MaxAgents", throwOnMissingValue: false);
    }

    public void Delete() {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        DeleteIfEmpty(@"Software\Kurrent\kcap");
        DeleteIfEmpty(@"Software\Kurrent");
    }

    static void DeleteIfEmpty(string path) {
        using (var key = Registry.CurrentUser.OpenSubKey(path)) {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    static void Set(RegistryKey key, string name, string? value) {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value);
    }
}
