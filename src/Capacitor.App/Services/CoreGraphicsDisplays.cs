using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.App.Services;

/// <summary>
/// The CoreGraphics active-display list, the one CoreVideo consults when Avalonia.Native registers
/// its display link. A sleeping display stays on the online list; only the active list empties.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class CoreGraphicsDisplays {
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <returns>The active display count, or -1 when the query fails.</returns>
    public static int ActiveCount() {
        try {
            return CGGetActiveDisplayList(0, 0, out var count) == 0 ? (int)count : -1;
        } catch (DllNotFoundException) {
            return -1;
        } catch (EntryPointNotFoundException) {
            return -1;
        }
    }

    [LibraryImport(CoreGraphics, EntryPoint = "CGGetActiveDisplayList")]
    private static partial int CGGetActiveDisplayList(uint maxDisplays, nint activeDisplays, out uint displayCount);
}
