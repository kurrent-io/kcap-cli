using System.Runtime.InteropServices;

namespace Capacitor.App.Services.Update;

public static class UpdateFeed {
    public const string OverrideVariable = "KCAP_APP_UPDATE_URL";

    /// The feed for the platform this build runs on. Each channel is published under its own RID,
    /// and a package applied from another channel's feed would replace the app with a foreign build.
    public static string BaseUrl { get; } = BaseUrlFor(Channel(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), ChannelArchitecture(OperatingSystem.IsWindows())));

    /// On Windows the process architecture, not the OS one: an x64 build emulated on Windows on ARM
    /// must stay on the x64 feed it was installed from.
    public static Architecture ChannelArchitecture(bool isWindows) =>
        isWindows ? RuntimeInformation.ProcessArchitecture : RuntimeInformation.OSArchitecture;

    public static string BaseUrlFor(string channel) => $"https://www.kurrent.io/download/desktop/{channel}/";

    public static string Channel(bool isWindows, bool isLinux, Architecture architecture) {
        var arch = architecture == Architecture.Arm64 ? "arm64" : "x64";
        if (isWindows) return $"win-{arch}";
        if (isLinux) return $"linux-{arch}";
        return $"osx-{arch}";
    }

    public static string Resolve(Func<string, string?> getEnv) {
        var overrideUrl = getEnv(OverrideVariable);
        return string.IsNullOrWhiteSpace(overrideUrl) ? BaseUrl : overrideUrl.Trim();
    }
}
