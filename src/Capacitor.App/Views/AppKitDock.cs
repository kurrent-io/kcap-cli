using System.Runtime.InteropServices;

namespace Capacitor.App.Views;

public static partial class AppKitDock {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static void SetVisible(bool visible) {
        if (!OperatingSystem.IsMacOS()) return;

        var app = Send(GetClass("NSApplication"), Selector("sharedApplication"));
        nint policy = visible ? 0 : 1; // NSApplicationActivationPolicyRegular / Accessory
        if (Send(app, Selector("activationPolicy")) != policy)
            SetActivationPolicy(app, Selector("setActivationPolicy:"), policy);
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SetActivationPolicy(nint receiver, nint selector, nint policy);
}
