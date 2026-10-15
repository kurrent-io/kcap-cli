using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.App.Services;

/// <summary>
/// Asks CoreVideo for the same display link Avalonia.Native registers during setup, so the answer is
/// the precondition itself rather than a proxy for it: a sleeping display stays on the online list,
/// and a wedged WindowServer can leave the active list populated while the link is still refused.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class CoreVideoDisplayLink {
    const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    /// <returns>0 when a display link can be created now, CoreVideo's code when it is refused
    /// (-6661 with no active display), null when the binding itself failed.</returns>
    public static int? Probe() {
        try {
            var code = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
            if (code == 0 && link != 0) CVDisplayLinkRelease(link);
            return code;
        } catch (DllNotFoundException) {
            return null;
        } catch (EntryPointNotFoundException) {
            return null;
        }
    }

    [LibraryImport(CoreVideo)]
    private static partial int CVDisplayLinkCreateWithActiveCGDisplays(out nint displayLink);

    [LibraryImport(CoreVideo)]
    private static partial void CVDisplayLinkRelease(nint displayLink);
}
