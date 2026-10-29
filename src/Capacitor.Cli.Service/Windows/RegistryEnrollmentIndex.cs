using System.Runtime.Versioning;
using Capacitor.Cli.Service.Sessions;
using Microsoft.Win32;

namespace Capacitor.Cli.Service.Windows;

[SupportedOSPlatform("windows")]
sealed class RegistryEnrollmentIndex : IEnrollmentIndex {
    const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public bool Add(string sid) {
        using var index = Registry.LocalMachine.CreateSubKey(ServiceIdentity.IndexKey);
        using (var existing = index.OpenSubKey(sid)) {
            if (existing is not null) return false;
        }
        index.CreateSubKey(sid).Dispose();
        return true;
    }

    public void Remove(string sid) {
        using var index = Registry.LocalMachine.OpenSubKey(ServiceIdentity.IndexKey, writable: true);
        index?.DeleteSubKeyTree(sid, throwOnMissingSubKey: false);
    }

    public void PruneDeparted() {
        using var index = Registry.LocalMachine.OpenSubKey(ServiceIdentity.IndexKey, writable: true);
        if (index is null) return;
        using var profiles = Registry.LocalMachine.OpenSubKey(ProfileList);
        foreach (var sid in index.GetSubKeyNames()) {
            using var profile = profiles?.OpenSubKey(sid);
            if (profile is null) index.DeleteSubKeyTree(sid, throwOnMissingSubKey: false);
        }
    }

    public bool IsEmpty {
        get {
            using var index = Registry.LocalMachine.OpenSubKey(ServiceIdentity.IndexKey);
            return index is null || index.SubKeyCount == 0;
        }
    }
}
