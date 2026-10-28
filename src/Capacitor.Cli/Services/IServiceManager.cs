using System.Text;

namespace Capacitor.Cli.Services;

/// <summary>Which OS service backend a manager targets.</summary>
enum ServicePlatform { Launchd, Systemd, WindowsScheduledTask, WindowsService }

/// <summary>Lifecycle state of an installed service for one id.</summary>
enum ServiceState { NotInstalled, Installed, Running }

/// <summary>Tri-state result of probing a launchd service via launchctl print.</summary>
enum LabelProbe { Loaded, Absent, Unknown }

/// <summary>A file the manager writes at install time (absolute path + content).</summary>
record GeneratedFile(string Path, string Content);

/// <summary>Status plus the binary path baked into the installed unit (for doctor).</summary>
record ServiceStatus(ServiceState State, string? BinaryPath);

/// <summary>Rich query result: tri-state probe, plist presence, current state, binary path, running job pid,
/// and the loaded job's spawn-type word (launchd only; null when the label is not loaded or the print
/// has no such line).</summary>
record ServiceQuery(LabelProbe Probe, bool UnitPresent, ServiceState State, string? BinaryPath, int? JobPid, string? LoadedSpawnType = null);

/// <summary>
/// Everything needed to render and register one per-user service.
/// <paramref name="ServiceId"/> is the sanitized id (see <c>DaemonStore.Sanitize</c>), used for the
/// filename/label/instance/task AND the daemon <c>--name</c>.
/// </summary>
record ServiceSpec(
    string                              ServiceId,
    string                              DaemonBinaryPath,
    string                              LogPath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string>               ExtraArgs);

/// <summary>How a manager writes a unit to disk. Production is
/// <see cref="ServiceFiles.WriteOwnerOnly"/>; tests inject a spy so the wiring is assertable without
/// registering a real OS service.</summary>
delegate void UnitFileWriter(string path, string content, Encoding? encoding = null);

interface IServiceManager {
    string Describe();
    /// <summary>The directory this manager's units live in, so the location has one owner: the manager
    /// that writes them. <c>daemon doctor</c> audits the path to it for directories other accounts can
    /// write, which is what a unit's own <c>0600</c> cannot speak for.</summary>
    string UnitDirectory { get; }
    IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec);
    IReadOnlyList<string>        ListInstalled();
    /// <summary>Null when the units could not be listed, which <see cref="ListInstalled"/> reports as none.</summary>
    IReadOnlyList<string>?       TryListInstalled() => ListInstalled();
    ServiceStatus                Status(string serviceId);
    /// <summary>The daemon binary the installed unit names, read without asking the OS supervisor where
    /// the unit file says it; null when it cannot be read.</summary>
    string?                      UnitBinary(string serviceId) => Status(serviceId).BinaryPath;
    /// <summary>The environment baked into the installed unit, read from the file this manager wrote;
    /// null when no unit is on disk.</summary>
    IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId);
    ServiceQuery                 Query(string serviceId);
    /// <summary>False when the OS tool refused the registration, with its reason in <paramref name="error"/>.</summary>
    bool Install(ServiceSpec spec, bool startNow, out string? error);
    /// <summary>Write + activate only, no leading bootout — the fresh-install half of <see cref="Install"/>
    /// for the verify engine, which classifies the label itself via <see cref="Query"/> and only calls
    /// this on a positive Absent (so there is nothing to boot out). Managers with no distinct verify path
    /// delegate mechanically to <see cref="Install"/>.</summary>
    void WriteAndBootstrap(ServiceSpec spec);
    /// <summary>True + no plist on disk is the only success. False retains the plist and names the state
    /// in <paramref name="error"/> so an operator can diagnose before retrying.</summary>
    bool Uninstall(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>; no plist mutation either way.</summary>
    bool Start(string serviceId, out string? error);
    /// <summary>False on failure, with the reason in <paramref name="error"/>. The plist is never deleted —
    /// stopping is not uninstalling.</summary>
    bool Stop(string serviceId, out string? error);
    /// <summary>True when installing a new name itself stops the daemon running under the name it replaces,
    /// as with one daemon per user, so nothing can relaunch that old name before it is retired.</summary>
    bool InstallEndsReplacedDaemon => false;
}

/// <summary>
/// The launchctl-invoking operations the <c>--verify</c> transaction drives, each bounded by a
/// per-call <c>timeout</c> carved from the transaction's remaining budget. A launchctl
/// child that exceeds it is tree-killed and mapped to a bounded failure (Query → <see
/// cref="LabelProbe.Unknown"/>; Start/Stop/Uninstall → false; WriteAndBootstrap → throw) so a hung
/// tool can never block the transaction past its deadline. Only <see cref="LaunchdServiceManager"/>
/// implements it — verify is launchd-only; the plain verbs keep the un-timed <see
/// cref="IServiceManager"/> methods, which stay unbounded.
/// </summary>
interface IVerifyServiceManager {
    /// <summary>Where this manager's unit for <paramref name="serviceId"/> lives, so the location has
    /// one owner: the manager that writes it.</summary>
    string                       UnitPath(string serviceId);
    IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec);
    ServiceQuery Query(string serviceId, TimeSpan timeout);
    void         WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout);
    bool         Uninstall(string serviceId, TimeSpan timeout, out string? error);
    bool         Start(string serviceId, TimeSpan timeout, out string? error);
    /// <summary>Bootstrap-only start for the gated path: activates the already-written unit, but
    /// fails — never kickstarts — when the label reads Loaded, closing the race with the gate's own
    /// confirmed-absent check.</summary>
    bool         StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error);
    bool         Stop(string serviceId, TimeSpan timeout, out string? error);
}
