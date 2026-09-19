using System.Runtime.InteropServices;

namespace Capacitor.App.Views;

public static partial class AppKitAccessibility {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static bool ReduceTransparency() {
        if (!OperatingSystem.IsMacOS()) return false;

        var workspace = Send(GetClass("NSWorkspace"), Selector("sharedWorkspace"));
        return SendBool(workspace, Selector("accessibilityDisplayShouldReduceTransparency")) != 0;
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBool(nint receiver, nint selector);
}
