using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

static class NativeTestHostProcess {
    public static Process Start(string mode) {
        var dll = ResolveDll();
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {mode}") {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start NativeTestHost");
    }

    // Derived from this assembly's own bin/<Config>/<TFM>/ rather than hardcoded, so CI and a local
    // build resolve the sibling's output the same way. A custom -o/OutDir breaks the derivation.
    static string ResolveDll() {
        var dir      = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm      = Path.GetFileName(dir);
        var config   = Path.GetFileName(Path.GetDirectoryName(dir)!);
        var testRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var hostDll  = Path.Combine(testRoot, "Capacitor.Cli.Tests.Unit.NativeTestHost", "bin", config, tfm,
            "Capacitor.Cli.Tests.Unit.NativeTestHost.dll");

        if (!File.Exists(hostDll))
            throw new InvalidOperationException($"NativeTestHost not built at {hostDll} — build Capacitor.slnx first");

        return hostDll;
    }
}
