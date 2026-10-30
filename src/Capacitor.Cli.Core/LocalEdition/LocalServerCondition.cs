namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What the files under the local root say about the installation, read without the service manager or the
/// network. Only <see cref="Installed"/> is something a start can bring up.</summary>
public enum LocalServerCondition {
    /// <summary>No installation was ever recorded under this root.</summary>
    NotInstalled,

    /// <summary>Purged: the registry retired it and names no active installation.</summary>
    Purged,

    /// <summary>Uninstalled without a purge: the registry and the data remain, the server's files do not.</summary>
    Uninstalled,

    /// <summary>The registry names it, but its server binary is missing.</summary>
    FilesMissing,

    /// <summary>Another kcap holds the lifecycle lock: a start, stop, restart, upgrade, setup or uninstall is running.</summary>
    Changing,

    /// <summary>A lifecycle command stopped part way; the next one finishes or undoes it first.</summary>
    Interrupted,

    /// <summary>The registry or the journal cannot be read, which is evidence of nothing.</summary>
    Unreadable,

    /// <summary>Installed with its files in place. Whether it runs is the service manager's and the server's to say.</summary>
    Installed,

    /// <summary>Installed, but another program holds its port, so no start can bring it up. Never read from the files
    /// alone: it takes an answer on the port that the kcap server never gives, or a listener the OS names as another
    /// program.</summary>
    PortTaken,
}
