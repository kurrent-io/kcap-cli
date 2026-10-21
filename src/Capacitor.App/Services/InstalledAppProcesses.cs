using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Capacitor.App.Services;

internal static partial class InstalledAppProcesses {
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal static int[]? Find(string root) {
        string[] names = ["kcap-desktop", "Kurrent Capacitor"];
        var executables = names.Select(name => Path.Combine(root, "Contents", "MacOS", name)).ToArray();
        var matches = new List<int>();
        var unknown = false;
        foreach (var process in names.SelectMany(Process.GetProcessesByName)) {
            using (process) {
                if (process.Id == Environment.ProcessId) continue;
                try {
                    if (process.HasExited) continue;
                    if (process.MainModule?.FileName is not { } executable) unknown = true;
                    else if (executables.Contains(executable)) matches.Add(process.Id);
                } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {
                    unknown = true;
                }
            }
        }
        return unknown ? null : matches.ToArray();
    }

    internal static async Task<bool> QuitAsync(string root, TimeProvider time, CancellationToken ct,
            Func<string, int[]?>? find = null, Func<int, bool>? quit = null) {
        find ??= Find;
        quit ??= RequestQuit;
        ct.ThrowIfCancellationRequested();
        if (find(root) is not { } pids) return false;
        foreach (var pid in pids) {
            ct.ThrowIfCancellationRequested();
            if (pid == Environment.ProcessId || !quit(pid)) {
                if (find(root) is not { } remaining || remaining.Contains(pid)) return false;
            }
        }
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) {
            if (find(root) is not { } remaining) return false;
            if (remaining.Length == 0) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct);
        }
        return false;
    }

    static bool RequestQuit(int pid) {
        if (!OperatingSystem.IsMacOS() || pid <= 0 || pid == Environment.ProcessId) return false;
        var pool = Send(GetClass("NSAutoreleasePool"), Selector("new"));
        try {
            var app = FindApplication(GetClass("NSRunningApplication"), Selector("runningApplicationWithProcessIdentifier:"), pid);
            return app != 0 && Terminate(app, Selector("terminate")) != 0;
        } finally { Send(pool, Selector("drain")); }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint FindApplication(nint receiver, nint selector, int pid);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte Terminate(nint receiver, nint selector);
}
