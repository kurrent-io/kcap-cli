namespace Capacitor.Cli.Services;

interface IServiceEnrollmentStore {
    ServiceEnrollment? Read();
    void Write(ServiceEnrollment enrollment);
    void Delete();
}
