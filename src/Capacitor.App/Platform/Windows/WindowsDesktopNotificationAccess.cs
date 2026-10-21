using System.Diagnostics;
using System.Runtime.Versioning;
using Capacitor.App.Services.Notifications;
using Microsoft.Win32;

namespace Capacitor.App.Platform.Windows;

/// Windows has no permission prompt: an app's toasts are on until the user turns them off, either
/// for this app or for every app. Both switches live in the user's registry hive, and the app's own
/// value is absent until it is first changed. Never NotDetermined, so nothing ever asks.
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopNotificationAccess(string appUserModelId, Func<string, string, object?>? readValue = null)
    : IDesktopNotificationAccess {
    const string AppSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\";
    const string GlobalKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    readonly Func<string, string, object?> _readValue = readValue ?? ReadCurrentUser;

    public Task<DesktopNotificationAccess> GetAsync() => Task.FromResult(Read());

    public Task<DesktopNotificationAccess> RequestAsync() => Task.FromResult(Read());

    public void OpenSystemSettings() {
        try {
            using var _ = Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
        }
    }

    DesktopNotificationAccess Read() {
        try {
            return IsOff(_readValue(GlobalKey, "ToastEnabled")) || IsOff(_readValue(AppSettingsKey + appUserModelId, "Enabled"))
                ? DesktopNotificationAccess.Denied
                : DesktopNotificationAccess.Allowed;
        } catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
            return DesktopNotificationAccess.Unknown;
        }
    }

    static bool IsOff(object? value) => value is int number && number == 0;

    static object? ReadCurrentUser(string keyPath, string valueName) {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }
}
