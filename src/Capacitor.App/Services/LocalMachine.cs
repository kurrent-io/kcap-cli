using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// This machine's <see cref="MachineId"/>, the id a session's recording identity names. The app never
/// creates it: a machine whose hooks and daemon have not run yet has recorded nothing, so a null id
/// matches no session. It is read again until present, since a first hook can create it while the app runs.
public sealed class LocalMachine(MachineId machineId) {
    string? _id;

    public string? Id => _id ??= machineId.ReadPersisted();
}
