using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli;

/// Where this CLI came from. A bundled CLI is updated by the app, a script install by `kcap update`
/// itself, and an npm install by the npm launcher, so update and re-install surfaces follow this answer.
public static class InstallProvenance {
    static readonly Lazy<InstallKind> Cached = new(() => Detect(Environment.ProcessPath, File.Exists));

    public static InstallKind Kind() => Cached.Value;

    public static bool IsAppBundled() => Kind() == InstallKind.App;

    /// <summary>The command that installs kcap again the way it was installed.</summary>
    public static string ReinstallCommand() => ReinstallCommand(Kind(), OperatingSystem.IsWindows());

    internal static string ReinstallCommand(InstallKind kind, bool windows) => kind switch {
        InstallKind.Script when windows => "irm https://www.kurrent.io/install.ps1 | iex",
        InstallKind.Script              => "curl -fsSL https://www.kurrent.io/install | bash",
        _                               => "npm install -g @kurrent/kcap",
    };

    internal static InstallKind Detect(string? processPath, Func<string, bool> fileExists) {
        if (IsAppBundled(processPath, fileExists)) return InstallKind.App;
        if (ScriptInstallLayout.FromBinary(processPath, fileExists) is not null) return InstallKind.Script;

        return IsNpm(processPath, fileExists) ? InstallKind.Npm : InstallKind.Unknown;
    }

    /// <summary>The platform binary under <c>node_modules/@kurrent/kcap-&lt;platform&gt;/bin</c>, with the
    /// <c>@kurrent/kcap</c> launcher either beside its package or around it (npm nests the optional
    /// dependency inside the launcher's package on some layouts).</summary>
    static bool IsNpm(string? processPath, Func<string, bool> fileExists) {
        var bin = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(bin)) return false;

        return fileExists(Path.GetFullPath(Path.Combine(bin, "..", "..", "kcap", "bin", "kcap.js")))
            || fileExists(Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", "bin", "kcap.js")));
    }

    internal static bool IsAppBundled(string? processPath, Func<string, bool> fileExists) {
        if (string.IsNullOrEmpty(processPath)) return false;

        var macos = Path.GetDirectoryName(processPath);
        if (macos is null || Path.GetFileName(macos) != "MacOS") return false;

        var contents = Path.GetDirectoryName(macos);
        if (contents is null || Path.GetFileName(contents) != "Contents") return false;

        var bundle = Path.GetDirectoryName(contents);
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return false;

        return fileExists(Path.Combine(contents, "Info.plist"));
    }
}
