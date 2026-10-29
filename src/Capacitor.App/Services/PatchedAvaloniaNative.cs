namespace Capacitor.App.Services;

/// The macOS native library the app loads in place of the Avalonia.Native package's: the same
/// source at the referenced tag plus the patches in Native/AvaloniaNative, which release leaked
/// accessibility peers and keep a resized Metal window from drawing at its old size. Null where
/// Avalonia.Native is not the platform.
internal static class PatchedAvaloniaNative {
    public const string FileName = "libAvaloniaNative.patched.dylib";

    public static string? Path => OperatingSystem.IsMacOS() ? System.IO.Path.Combine(AppContext.BaseDirectory, FileName) : null;
}
