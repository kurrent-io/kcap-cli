using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capacitor.App.Services.Notifications;

[SupportedOSPlatform("macos")]
internal static partial class MacNotificationInterop {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    static MacNotificationInterop() =>
        _ = NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications");

    public static nint String(string value) => Send(GetClass("NSString"), Selector("stringWithUTF8String:"), value);
    public static string? Text(nint value) => value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Selector("UTF8String")));
    public static string? Error(nint error) => error == 0 ? null : Text(Send(error, Selector("localizedDescription")));
    public static void Release(nint value) { if (value != 0) SendVoid(value, Selector("release")); }

    public static nint Array(IEnumerable<nint> values) {
        var array = Send(GetClass("NSMutableArray"), Selector("array"));
        foreach (var value in values) SendVoid(array, Selector("addObject:"), value);
        return array;
    }

    public sealed class Pool : IDisposable {
        readonly nint _pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        public void Dispose() => Release(_pool);
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetClass(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetProtocol(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Selector(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AllocateClass(nint superclass, string name, nuint extraBytes);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")]
    internal static partial void RegisterClass(nint cls);
    [LibraryImport(ObjC, EntryPoint = "class_addProtocol")]
    internal static partial byte AddProtocol(nint cls, nint protocol);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial byte AddMethod(nint cls, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Send(nint receiver, nint selector, string arg);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector, nint arg1, nint arg2);
}
