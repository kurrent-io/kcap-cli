using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>Which supervisor runs the local server on Windows: <c>kcap-service</c> or the per-user task.</summary>
public class LocalServerServiceSelectionTests {
    readonly FakeLocalServerEnrollmentStore _store = new();
    readonly FakeServicePipe _pipe = new();

    static ServicePipeResponse Advertising(params string[] verbs) => new(true, Capabilities: new ServicePipeCapabilities("1.0.0", 1, verbs));

    /// <summary>A task beside an enrollment would put two supervisors on one root, so a service that is down or
    /// unregistered still keeps it.</summary>
    [Test]
    public async Task An_enrollment_keeps_the_service_whatever_the_service_is_doing() {
        _store.Current = new("s", "r", "c", "e", "l", Stopped: false);

        var uses = LocalServerServices.UsesService(_store, () => false, _pipe);

        await Assert.That(uses).IsTrue();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    [Test]
    public async Task Without_the_service_registered_the_task_runs_it_and_nothing_is_asked() {
        var uses = LocalServerServices.UsesService(_store, () => false, _pipe);

        await Assert.That(uses).IsFalse();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    [Test]
    public async Task A_registered_service_advertising_every_server_verb_runs_it() {
        _pipe.Answer = _ => Advertising(["capabilities", "enroll", "status", .. ServicePipeVerbs.Server]);

        await Assert.That(LocalServerServices.UsesService(_store, () => true, _pipe)).IsTrue();
        await Assert.That(_pipe.Verbs).IsEquivalentTo([ServicePipeVerbs.Capabilities]);
    }

    /// <summary>An older service answers a server verb as unknown; a server handed to it would never start.</summary>
    [Test]
    public async Task A_service_missing_one_server_verb_leaves_the_server_to_the_task() {
        _pipe.Answer = _ => Advertising(["capabilities", "enroll", .. ServicePipeVerbs.Server.Where(v => v != ServicePipeVerbs.ServerUnenroll)]);

        await Assert.That(LocalServerServices.UsesService(_store, () => true, _pipe)).IsFalse();
    }

    [Test]
    public async Task A_registered_service_that_does_not_answer_leaves_the_server_to_the_task() {
        _pipe.Answer = _ => null;

        await Assert.That(LocalServerServices.UsesService(_store, () => true, _pipe)).IsFalse();
    }
}
