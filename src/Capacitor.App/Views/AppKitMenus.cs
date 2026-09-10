using System.Runtime.InteropServices;

namespace Capacitor.App.Views;

/// The AppKit menu calls Avalonia does not make. It never hands its menus to the windowsMenu and
/// helpMenu slots, and only a registered Window menu gets Move to a display, the tiling items and
/// the window list, and only a registered Help menu gets the search field.
public static partial class AppKitMenus {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// Registers the current main menu's submenus with these titles; one that is missing is skipped.
    public static void Adopt(string windowTitle, string helpTitle) {
        if (!OperatingSystem.IsMacOS()) return;

        var app = SharedApplication();
        var mainMenu = Send(app, Selector("mainMenu"));
        if (mainMenu == 0) return;

        var windowMenu = Submenu(mainMenu, windowTitle);
        if (windowMenu != 0) SendVoid(app, Selector("setWindowsMenu:"), windowMenu);

        var helpMenu = Submenu(mainMenu, helpTitle);
        if (helpMenu != 0) SendVoid(app, Selector("setHelpMenu:"), helpMenu);
    }

    /// Filled from the bundle's Info.plist, so a build run outside a bundle shows no version.
    public static void ShowAboutPanel() {
        if (!OperatingSystem.IsMacOS()) return;

        SendVoid(SharedApplication(), Selector("orderFrontStandardAboutPanel:"), 0);
    }

    static nint SharedApplication() => Send(GetClass("NSApplication"), Selector("sharedApplication"));

    static nint Submenu(nint menu, string title) {
        var item = Send(menu, Selector("itemWithTitle:"), Send(GetClass("NSString"), Selector("stringWithUTF8String:"), title));
        return item == 0 ? 0 : Send(item, Selector("submenu"));
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Send(nint receiver, nint selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector, nint argument);
}
