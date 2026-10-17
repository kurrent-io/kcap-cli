using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalUnitLockTests {
    [TempHome] public required TempHome Home { get; init; }

    Task<LocalUnitLock> Acquire(TimeSpan wait) => LocalUnitLock.AcquireAsync(Home, wait, TimeProvider.System, CancellationToken.None);

    [Test]
    public async Task A_second_holder_waits_then_fails_naming_the_unit() {
        using var held = await Acquire(TimeSpan.Zero);

        var ex = await Assert.That(async () => (await Acquire(TimeSpan.FromMilliseconds(300))).Dispose()).Throws<LocalLifecycleException>();

        await Assert.That(ex!.Message).Contains("Another kcap is changing the local server unit");
    }

    [Test]
    public async Task Release_lets_the_next_holder_in() {
        (await Acquire(TimeSpan.Zero)).Dispose();

        using var next = await Acquire(TimeSpan.Zero);

        await Assert.That(File.Exists(Home.PathTo(".config", "kcap", "local-unit.lock"))).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task The_lock_ignores_the_local_root_override() {
        using var _ = EnvScope.Exclusive(LocalRoot.EnvVar, Home.PathTo("elsewhere", "root"));

        using var held = await Acquire(TimeSpan.Zero);

        await Assert.That(File.Exists(Home.PathTo(".config", "kcap", "local-unit.lock"))).IsTrue();
        await Assert.That(Directory.Exists(Home.PathTo("elsewhere"))).IsFalse();
    }
}
