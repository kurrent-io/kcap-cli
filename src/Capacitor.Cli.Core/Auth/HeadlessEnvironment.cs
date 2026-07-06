using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.Auth;

public enum OSPlatformKind { Linux, MacOS, Windows, Other }

/// <summary>
/// Heuristic check for whether the current process has access to an interactive
/// desktop browser. Used by <c>OAuthLoginFlow</c> to decide between the localhost
/// browser flow and the device-code fallback.
/// </summary>
public static class HeadlessEnvironment {
    public static bool IsHeadless() => IsHeadless(CurrentEnv(), CurrentPlatform());

    public static bool IsHeadless(IReadOnlyDictionary<string, string?> env, OSPlatformKind platform) {
        if (HasValue(env, "SSH_CONNECTION") || HasValue(env, "SSH_CLIENT")) return true;

        return platform == OSPlatformKind.Linux
         && !HasValue(env, "DISPLAY")
         && !HasValue(env, "WAYLAND_DISPLAY");
    }

    static bool HasValue(IReadOnlyDictionary<string, string?> env, string key)
        => env.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);

    static IReadOnlyDictionary<string, string?> CurrentEnv() {
        var keys = new[] { "SSH_CONNECTION", "SSH_CLIENT", "DISPLAY", "WAYLAND_DISPLAY" };

        return keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
    }

    static OSPlatformKind CurrentPlatform() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return OSPlatformKind.Linux;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return OSPlatformKind.MacOS;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return OSPlatformKind.Windows;

        return OSPlatformKind.Other;
    }
}
