namespace Capacitor.Cli.Service.Sessions;

/// <summary>The machine's record of who enrolled, which the service can read while those users are signed
/// out and their hives are unloaded. An empty index after pruning means the last user has gone</summary>
public interface IEnrollmentIndex {
    /// <summary>False when the user was already indexed</summary>
    bool Add(string sid);
    void Remove(string sid);

    /// <summary>Drops users whose Windows profile has been deleted</summary>
    void PruneDeparted();

    bool IsEmpty { get; }
}
