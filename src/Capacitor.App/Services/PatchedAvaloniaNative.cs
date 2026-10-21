namespace Capacitor.App.Services;

/// The macOS native library the app loads in place of the Avalonia.Native package's: the same
/// source at the referenced tag plus Native/AvaloniaNative/automation-leak.patch, which releases
/// the accessibility peers that otherwise keep every detached control, its subtree and its
/// DataContext alive for the life of the process. Null where Avalonia.Native is not the platform.
internal static class PatchedAvaloniaNative {
    public const string FileName = "libAvaloniaNative.patched.dylib";

    public static string? Path => OperatingSystem.IsMacOS() ? System.IO.Path.Combine(AppContext.BaseDirectory, FileName) : null;
}
