using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Setup's ensure after a rename: the service the profile's previous name left installed is retired with
/// the install, never a unit that is not there, and a refused retire still installs the new name.
/// </summary>
public class RetiringEnsureTests {
    const string NewId = "mac-1";
    const string OldId = "mac";

    static readonly ServiceQuery Absent    = new(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
    static readonly ServiceQuery Installed = new(LabelProbe.Loaded, true, ServiceState.Running, "/b/kcap-daemon", 42);
    static readonly ServiceQuery Unknown   = new(LabelProbe.Unknown, false, ServiceState.NotInstalled, null, null);

    static readonly ServiceEnsureJson InstalledResult = new(NewId, "not_installed", "install", "installed", Verified: true);
    static readonly ServiceEnsureJson RetireRefused   =
        new(NewId, "not_installed", "install", "refused", null, "verify_retire_refused", Verified: true);

    sealed class Ladder(params ServiceEnsureJson[] results) {
        public List<string?> Retires { get; } = [];

        public Task<ServiceEnsureJson> Run(string? retire) {
            Retires.Add(retire);

            return Task.FromResult(results[Math.Min(Retires.Count, results.Length) - 1]);
        }
    }

    /// <summary>Answers each query for the old id from <paramref name="answers"/> in turn; any other id
    /// fails the test, so nothing but the old id can be read as a retire candidate.</summary>
    static Func<string, ServiceQuery> OldUnit(params ServiceQuery[] answers) {
        var calls = 0;

        return id => id == OldId
            ? answers[Math.Min(calls++, answers.Length - 1)]
            : throw new InvalidOperationException($"queried {id}");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("Mac-1")]
    public async Task Without_a_different_previous_name_nothing_is_retired(string? previousName) {
        var ladder = new Ladder(InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(
            NewId, previousName, canRetire: true, OldUnit(Installed), ladder.Run);

        await Assert.That(ladder.Retires).IsEquivalentTo(new string?[] { null });
        await Assert.That(outcome.Retired).IsNull();
        await Assert.That(outcome.LeftInstalled).IsNull();
    }

    /// <summary>A manually started daemon has no unit, so the old name having no service retires
    /// nothing.</summary>
    [Test]
    public async Task A_previous_name_with_no_service_retires_nothing() {
        var ladder = new Ladder(InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(NewId, OldId, true, OldUnit(Absent), ladder.Run);

        await Assert.That(ladder.Retires).IsEquivalentTo(new string?[] { null });
        await Assert.That(outcome.Retired).IsNull();
        await Assert.That(outcome.LeftInstalled).IsNull();
    }

    /// <summary>The raw saved name is retired by its service id, the same sanitization every unit
    /// bakes.</summary>
    [Test]
    public async Task The_previous_service_is_retired_with_the_install() {
        var ladder = new Ladder(InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(
            NewId, " MAC ", true, OldUnit(Installed, Absent), ladder.Run);

        await Assert.That(ladder.Retires).IsEquivalentTo(new string?[] { OldId });
        await Assert.That(outcome.Result).IsEqualTo(InstalledResult);
        await Assert.That(outcome.Retired).IsEqualTo(OldId);
        await Assert.That(outcome.LeftInstalled).IsNull();
    }

    /// <summary>A refused retire changed nothing, so the new name still gets its service and the old one
    /// is reported rather than silently kept.</summary>
    [Test]
    public async Task A_refused_retire_installs_without_it_and_reports_the_old_service() {
        var ladder = new Ladder(RetireRefused, InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(
            NewId, OldId, true, OldUnit(Installed, Installed), ladder.Run);

        await Assert.That(ladder.Retires).IsEquivalentTo(new string?[] { OldId, null });
        await Assert.That(outcome.Result).IsEqualTo(InstalledResult);
        await Assert.That(outcome.LeftInstalled).IsEqualTo(OldId);
        await Assert.That(outcome.Retired).IsNull();
    }

    [Test]
    public async Task A_platform_that_cannot_retire_reports_the_old_service() {
        var ladder = new Ladder(InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(
            NewId, OldId, canRetire: false, OldUnit(Installed, Installed), ladder.Run);

        await Assert.That(ladder.Retires).IsEquivalentTo(new string?[] { null });
        await Assert.That(outcome.LeftInstalled).IsEqualTo(OldId);
    }

    /// <summary>An unreadable probe afterwards proves neither, so neither is claimed.</summary>
    [Test]
    public async Task An_unknown_probe_after_the_install_claims_nothing() {
        var ladder = new Ladder(InstalledResult);

        var outcome = await DaemonServiceCommands.RetiringEnsureAsync(
            NewId, OldId, true, OldUnit(Installed, Unknown), ladder.Run);

        await Assert.That(outcome.Retired).IsNull();
        await Assert.That(outcome.LeftInstalled).IsNull();
    }
}
