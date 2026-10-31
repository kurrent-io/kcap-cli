using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli;

/// Where this CLI came from. A bundled CLI is updated by the app, a snap by snapd, a script install by
/// `kcap update` itself, and an npm install by the npm launcher, so update and re-install surfaces follow this answer.
public static class InstallProvenance {
    static readonly Lazy<InstallKind> Cached = new(() => Detect(Environment.ProcessPath, File.Exists));

    public static InstallKind Kind() => Cached.Value;

    /// <summary>Shipped with the desktop app, in its bundle or its snap: kcap never updates itself, and an npm
    /// nudge would point at a channel the app's own may lag.</summary>
    public static bool IsAppBundled() => Kind() is InstallKind.App or InstallKind.Snap;

    /// <summary>The sentence that follows "'kcap' is not on PATH." For the desktop app's CLI that is the
    /// app's own PATH fix: reinstalling from npm would put a second, separate CLI beside it.</summary>
    public static string NotOnPathHint() =>
        NotOnPathHint(Kind(), OperatingSystem.IsWindows(), Environment.ProcessPath, SnapLayout.OfRunningBinary());

    internal static string NotOnPathHint(InstallKind kind, bool windows, string? processPath, SnapLayout? snap = null) =>
        kind == InstallKind.App
            ? $"Put the desktop app's kcap on PATH: choose \"Install command-line tool\" from the kcap icon in the {(windows ? "system tray" : "menu bar")}"
              + (string.IsNullOrEmpty(processPath) ? "" : $", or run: {ShellPath(processPath, windows)} daemon shim ensure")
              + ". Then open a new terminal."
            : ReinstallHint(kind, windows, snap);

    /// <summary>The sentence telling the user how to install kcap again the way it was installed, for an
    /// install that is missing its own files.</summary>
    public static string ReinstallHint() =>
        ReinstallHint(Kind(), OperatingSystem.IsWindows(), SnapLayout.OfRunningBinary());

    internal static string ReinstallHint(InstallKind kind, bool windows, SnapLayout? snap = null) =>
        kind == InstallKind.App
            ? "Re-install the kcap desktop app."
            : "Re-install kcap: " + ReinstallCommand(kind, windows, snap);

    // PowerShell, the default Windows terminal shell, runs a quoted path only behind its call operator.
    static string ShellPath(string path, bool windows) =>
        !path.Any(char.IsWhiteSpace) ? path
        : windows ? $"& \"{path}\""
        : $"\"{path}\"";

    /// <summary>The command that installs kcap again the way it was installed. The desktop app's CLI has
    /// none, so it reaches users only through <see cref="NotOnPathHint()"/> and <see cref="ReinstallHint()"/>.</summary>
    internal static string ReinstallCommand(InstallKind kind, bool windows, SnapLayout? snap = null) => kind switch {
        InstallKind.Script when windows      => "irm https://www.kurrent.io/install.ps1 | iex",
        InstallKind.Script                   => "curl -fsSL https://www.kurrent.io/install | bash",
        // Callers mostly show this when `kcap` is not on PATH, which for a snap means /snap/bin is missing from
        // it; a damaged snap's missing plugin still needs the reinstall. One line that pastes into a shell as is.
        // A local install is replaced from a file: removing it first leaves nothing if the Store has no kcap.
        InstallKind.Snap when snap is { IsLocal: true } =>
            "export PATH=\"/snap/bin:$PATH\"  # or reinstall: sudo snap install --dangerous --classic <file>.snap",
        InstallKind.Snap when snap is not null =>
            $"export PATH=\"/snap/bin:$PATH\"  # or reinstall: sudo snap remove {snap.Name} && sudo snap install {snap.Name} --classic",
        _                                    => "npm install -g @kurrent/kcap",
    };

    internal static InstallKind Detect(string? processPath, Func<string, bool> fileExists) {
        if (IsAppBundled(processPath, fileExists) || IsWindowsAppInstall(processPath, fileExists)) return InstallKind.App;
        if (SnapLayout.FromBinary(processPath) is not null) return InstallKind.Snap;
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

    /// <summary>The Windows desktop app's Velopack install: the CLI sits beside <c>kcap-desktop.exe</c> in
    /// <c>current</c>, under the root that holds Velopack's <c>Update.exe</c>.</summary>
    internal static bool IsWindowsAppInstall(string? processPath, Func<string, bool> fileExists) {
        var current = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(current) || !string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase))
            return false;

        var root = Path.GetDirectoryName(current);
        return !string.IsNullOrEmpty(root)
            && fileExists(Path.Combine(root, "Update.exe"))
            && fileExists(Path.Combine(current, "kcap-desktop.exe"));
    }
}
