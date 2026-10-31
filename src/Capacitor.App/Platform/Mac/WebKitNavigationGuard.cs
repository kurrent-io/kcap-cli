using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Mac;

/// Cancels a navigation whose URL System.Uri cannot parse before the WebView package sees it. The
/// package's navigation delegate calls new Uri(url) inside a native callback, where a
/// UriFormatException ends the process, and WebKit accepts URLs System.Uri refuses (mailto:%,
/// foo://a%zz/): one click on such a link in a page would kill the app. The guard replaces the
/// package delegate class's decide-policy method, once per process, with one that cancels those
/// and hands every other request to the package's own.
public static unsafe partial class WebKitNavigationGuard {
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string PackageDelegateClass = "ManagedWKNavigationDelegate";
    const long PolicyCancel = 0;
    // A block's invoke pointer follows its isa, flags and reserved fields.
    const int BlockInvokeOffset = 16;

    static readonly Lock Gate = new();
    static nint _packageDecide;

    /// False when the guard is not in front of this web view's navigation delegate, and the web view
    /// must not load a page.
    public static bool TryInstall(nint wkWebView) {
        if (!OperatingSystem.IsMacOS() || wkWebView == 0) return false;
        var cls = GetClass(PackageDelegateClass);
        if (cls == 0) return false;
        var navigationDelegate = Send(wkWebView, Selector("navigationDelegate"));
        if (navigationDelegate == 0 || SendBool(navigationDelegate, Selector("isKindOfClass:"), cls) == 0) return false;
        lock (Gate) {
            if (_packageDecide != 0) return true;
            var method = GetInstanceMethod(cls, Selector("webView:decidePolicyForNavigationAction:decisionHandler:"));
            if (method == 0) return false;
            delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void> decide = &Decide;
            _packageDecide = SetImplementation(method, (nint)decide);
            return _packageDecide != 0;
        }
    }

    /// The test the package's delegate fails: new Uri(url) throws exactly when this is false.
    public static bool Parses(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out _);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Decide(nint self, nint selector, nint webView, nint action, nint decisionHandler) {
        bool parses;
        try {
            parses = Parses(RequestUrl(action));
        } catch {
            parses = false;
        }
        var packageDecide = Volatile.Read(ref _packageDecide);
        if (parses && packageDecide != 0) {
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)packageDecide)(self, selector, webView, action, decisionHandler);
            return;
        }
        var invoke = *(nint*)(decisionHandler + BlockInvokeOffset);
        ((delegate* unmanaged[Cdecl]<nint, long, void>)invoke)(decisionHandler, PolicyCancel);
    }

    static string? RequestUrl(nint action) {
        var request = Send(action, Selector("request"));
        var url = request == 0 ? 0 : Send(request, Selector("URL"));
        var text = url == 0 ? 0 : Send(url, Selector("absoluteString"));
        var utf8 = text == 0 ? 0 : Send(text, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "class_getInstanceMethod")]
    private static partial nint GetInstanceMethod(nint cls, nint selector);

    [LibraryImport(ObjC, EntryPoint = "method_setImplementation")]
    private static partial nint SetImplementation(nint method, nint implementation);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector, nint argument);
}
