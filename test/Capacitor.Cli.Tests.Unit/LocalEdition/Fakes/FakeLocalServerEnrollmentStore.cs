using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

sealed class FakeLocalServerEnrollmentStore : ILocalServerEnrollmentStore {
    public LocalServerEnrollment? Current;

    public LocalServerEnrollment? Read() => Current;

    public void Write(LocalServerEnrollment enrollment) => Current = enrollment;

    public void SetStopped(bool stopped) {
        if (Current is not null) Current = Current with { Stopped = stopped };
    }

    public void Delete() => Current = null;
}
