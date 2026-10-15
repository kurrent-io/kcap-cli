namespace Capacitor.Cli;

/// <summary>How the running kcap was installed, which decides how it updates.</summary>
public enum InstallKind {
    /// <summary>Inside the desktop app bundle; the app updates it.</summary>
    App,

    /// <summary>Laid out by the install script; <c>kcap update</c> installs a new version directory.</summary>
    Script,

    /// <summary>A global npm install; the npm launcher drives <c>kcap update</c>.</summary>
    Npm,

    /// <summary>Anything else: a dev build, a copied binary, a package manager we do not drive.</summary>
    Unknown,
}
