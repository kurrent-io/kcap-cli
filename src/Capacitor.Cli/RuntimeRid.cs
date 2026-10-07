using System.Runtime.InteropServices;

namespace Capacitor.Cli;

/// <summary>The canonical <c>os-arch[-musl]</c> RID this process runs as, the form release assets are named by.</summary>
static class RuntimeRid {
    public static string Current() {
        var arch = RuntimeInformation.ProcessArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X64   => "x64",
            var other          => other.ToString().ToLowerInvariant(),
        };
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS())   return $"osx-{arch}";
        return IsMusl() ? $"linux-musl-{arch}" : $"linux-{arch}";
    }

    static bool IsMusl() {
        try {
            return Directory.Exists("/etc/apk")
                || File.Exists("/lib/ld-musl-x86_64.so.1")
                || File.Exists("/lib/ld-musl-aarch64.so.1");
        } catch { return false; }
    }
}
