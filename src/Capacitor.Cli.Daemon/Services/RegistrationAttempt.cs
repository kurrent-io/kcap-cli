namespace Capacitor.Cli.Daemon.Services;

/// <summary>One registration's advertisement: what it is held against until a DaemonConnect payload is
/// built, what that payload carries once it is, and whether the server accepted it.</summary>
internal sealed class RegistrationAttempt(UnattendedAdvertisement attempted, Action<UnattendedAdvertisement> accepted) {
    public UnattendedAdvertisement Carried { get; private set; } = attempted;

    public bool Accepted { get; private set; }

    public void Sending(UnattendedAdvertisement payload) => Carried = payload;

    /// <summary>The server accepted <see cref="Carried"/> and dispatches against it from now on.</summary>
    public void Accept() {
        Accepted = true;
        accepted(Carried);
    }
}
