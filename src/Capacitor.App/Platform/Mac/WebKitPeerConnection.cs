using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Mac;

/// Turns RTCPeerConnection off for a WKWebView. A page's content policy cannot stop WebRTC: its
/// ICE traffic is not a fetch, so connect-src never sees it. The switch is WebKit's private
/// WKPreferences._peerConnectionEnabled; the web view's configuration is a copy but its preferences
/// object is the live one, and the change applies to documents loaded after it.
public static partial class WebKitPeerConnection {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// False when the switch is missing or did not take, and the web view must not load a page.
    public static bool TryDisable(nint wkWebView) {
        if (!OperatingSystem.IsMacOS() || wkWebView == 0) return false;

        var configuration = Send(wkWebView, Selector("configuration"));
        var preferences = configuration == 0 ? 0 : Send(configuration, Selector("preferences"));
        var setter = Selector("_setPeerConnectionEnabled:");
        if (preferences == 0 || SendBool(preferences, Selector("respondsToSelector:"), setter) == 0) return false;

        SetBool(preferences, setter, 0);
        return SendBool(preferences, Selector("_peerConnectionEnabled")) == 0;
    }

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SetBool(nint receiver, nint selector, byte value);
}
