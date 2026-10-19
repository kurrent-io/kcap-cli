using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Brings the per-user unit to a state serving this root, deciding under the unit lock who owns it. The
/// unit is per user, so it is installed only into an empty slot or over this root's own job; one that is not provably
/// this root's is left alone.</summary>
static class LocalOwnUnit {
    /// <summary>True when the unit had to be installed. An owner that is not provably this root's throws what
    /// <paramref name="refuse"/> builds.</summary>
    public static async Task<bool> EnsureAsync(LocalRoot root, LocalServerUnit unit, ILocalServerService service, UserHome home,
        Func<LocalInstallFailure> refuse) {
        using var _ = await LocalUnitLock.AcquireAsync(home, LocalUnitLock.Wait, TimeProvider.System, CancellationToken.None);

        return Ensure(root, unit, service, refuse, startPresent: false);
    }

    /// <summary>For a caller already holding the unit lock. With <paramref name="startPresent"/> an own unit that is
    /// already installed is started too; with <paramref name="replace"/> it is rewritten, which also starts it.</summary>
    public static bool Ensure(LocalRoot root, LocalServerUnit unit, ILocalServerService service, Func<LocalInstallFailure> refuse, bool startPresent,
        bool replace = false) {
        var owner = service.Owner(unit);
        var empty = owner is { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent };

        if (!empty && LocalRootIdentity.Decide(root, owner) != OwnerDecision.Ours) throw refuse();

        if (!empty && !replace && service.IsInstalled(unit)) {
            if (startPresent) service.Start(unit);
            return false;
        }

        service.Install(unit);
        return true;
    }
}
