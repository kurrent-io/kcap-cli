using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

sealed class FakeServiceEnrollmentStore : IServiceEnrollmentStore {
    public ServiceEnrollment? Current;

    public ServiceEnrollment? Read() => Current;
    public void Write(ServiceEnrollment enrollment) => Current = enrollment;
    public void Delete() => Current = null;
}
