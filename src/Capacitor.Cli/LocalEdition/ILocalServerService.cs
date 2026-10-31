namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The OS service that runs the local server: launchd on macOS, systemd on Linux, <c>kcap-service</c> or a
/// Scheduled Task on Windows. Every call runs under the
/// lifecycle lease. Failures throw <see cref="Core.LocalEdition.LocalLifecycleException"/> naming what is
/// left.
///
/// <para>A stop persists: the unit is disabled as well as stopped, so neither its restart policy nor the
/// next login starts it again until <see cref="Start"/>. That is what lets a transaction switch versions or
/// remove data with nothing able to start the server under it.</para>
/// </summary>
interface ILocalServerService {
    string Describe { get; }

    /// <summary>The unit's name in this service manager, as messages name it.</summary>
    string Name(LocalServerUnit unit) => unit.Label;

    /// <summary>Writes the unit, replacing an older one, and starts it. Does not wait for readiness.</summary>
    void Install(LocalServerUnit unit);

    /// <summary>Enables and starts an installed unit.</summary>
    void Start(LocalServerUnit unit);

    /// <summary>Disables and stops the unit, then waits until nothing of the stack is alive.</summary>
    Task StopAsync(LocalServerUnit unit, CancellationToken ct);

    /// <summary>A verified stop, then the unit file removed.</summary>
    Task UninstallAsync(LocalServerUnit unit, CancellationToken ct);

    /// <summary>Asks the server to stop, gives it about <paramref name="grace"/>, then removes the unit whether or not
    /// the stack is down, so it fits an app uninstaller's deadline. Nothing is verified: the caller checks what is left.</summary>
    Task UninstallWithinAsync(LocalServerUnit unit, TimeSpan grace, CancellationToken ct) => UninstallAsync(unit, ct);

    /// <summary>Where the unit's definition lives on disk, present or not.</summary>
    string UnitFile(LocalServerUnit unit);

    bool IsInstalled(LocalServerUnit unit);

    /// <summary>The root the loaded job and the unit file each name; the unit is per user, so they may name
    /// another installation's.</summary>
    UnitOwner Owner(LocalServerUnit unit);

    /// <summary>Reads only; whoever the unit serves.</summary>
    UnitActivity Activity(LocalServerUnit unit);
}
