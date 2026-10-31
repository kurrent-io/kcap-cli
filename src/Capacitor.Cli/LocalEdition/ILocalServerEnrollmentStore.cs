namespace Capacitor.Cli.LocalEdition;

interface ILocalServerEnrollmentStore {
    /// <summary>Null when there is no enrollment, or one missing a value the service needs</summary>
    LocalServerEnrollment? Read();

    void Write(LocalServerEnrollment enrollment);

    void SetStopped(bool stopped);

    void Delete();
}
