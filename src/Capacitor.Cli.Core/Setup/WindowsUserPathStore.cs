using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Capacitor.Cli.Core.Setup;

/// HKCU\Environment\Path. Written through the registry rather than
/// Environment.SetEnvironmentVariable, which stores REG_SZ and so would bake every `%VAR%` entry
/// into a literal path.
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUserPathStore(string keyPath = WindowsUserPathStore.EnvironmentKey) : IUserPathStore {
    public const string EnvironmentKey = "Environment";
    const string ValueName = "Path";

    public string? Read() {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Append(string directory) {
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)) {
            var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var kind = current is null ? RegistryValueKind.ExpandString : key.GetValueKind(ValueName);
            var next = string.IsNullOrEmpty(current) ? directory : current.TrimEnd(';') + ";" + directory;
            key.SetValue(ValueName, next, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        }

        BroadcastEnvironmentChange();
    }

    // Explorer rebuilds the environment it hands new processes only on this broadcast; without it
    // a terminal opened from the Start menu keeps the old PATH until the next sign-in.
    static void BroadcastEnvironmentChange() {
        const nint HwndBroadcast = 0xffff;
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint SendMessageTimeout(
        nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
